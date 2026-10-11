using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.TuiAotPrototype1143;

internal sealed record SourceContext
{
    private static readonly StringComparer DirectoryComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public string Endpoint { get; }
    public string Workspace { get; }
    public string Tab { get; }
    public string Pane { get; }
    public string Cwd { get; }

    [JsonConstructor]
    public SourceContext(string endpoint, string workspace, string tab, string pane, string cwd)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(tab) || string.IsNullOrWhiteSpace(pane) || string.IsNullOrWhiteSpace(cwd))
            throw new ArgumentException("Captured source requires endpoint, workspace, tab, pane and cwd.");
        Endpoint = endpoint; Workspace = workspace; Tab = tab; Pane = pane;
        // Lexical filesystem canonicalization only: preserve drive/UNC roots and never resolve
        // links, change source ownership, or normalize opaque host identifiers.
        Cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
    }

    public bool Equals(SourceContext? other) => other is not null &&
        Endpoint == other.Endpoint && Workspace == other.Workspace && Tab == other.Tab && Pane == other.Pane &&
        DirectoryComparer.Equals(Cwd, other.Cwd);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Endpoint, StringComparer.Ordinal); hash.Add(Workspace, StringComparer.Ordinal);
        hash.Add(Tab, StringComparer.Ordinal); hash.Add(Pane, StringComparer.Ordinal);
        hash.Add(Cwd, DirectoryComparer);
        return hash.ToHashCode();
    }
}
internal sealed record ViewSnapshot(int Pid, SourceContext Source, string View, int Selected, int? Detail, int Scroll,
    int Width, int Height, long Generation, long Request, bool Loaded, int Loads, int LateDropped, int Canceled,
    int Keys, int Mouse, int Resizes, int Reflows, string[] VisibleLines, string Message);
internal sealed record ControlRequest(string Token, SourceContext Source, string Command, string? View = null);
internal sealed record ControlReply(bool Ok, string? Error, ViewSnapshot? Snapshot);
internal sealed record Registration(int Version, string Pipe, string Token, int Pid, long StartedUtcTicks, string HostPane, SourceContext Source);

[JsonSourceGenerationOptions(WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SourceContext))]
[JsonSerializable(typeof(ViewSnapshot))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlReply))]
[JsonSerializable(typeof(Registration))]
internal sealed partial class PrototypeJsonContext : JsonSerializerContext { }

internal sealed class ControlServer : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Browser browser;
    private readonly Registration record;
    private readonly string path;
    private readonly Task worker;
    private TaskCompletionSource closeDelivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool closeRequested;

    internal ControlServer(Browser browser, SourceContext source)
    {
        this.browser = browser;
        path = LocalControl.PathFor(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        record = new Registration(1, "twig-aot1143-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Environment.GetEnvironmentVariable("HERDR_PANE_ID") ?? "", source);
        var staging = path + "." + record.Token + ".tmp";
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, record, PrototypeJsonContext.Default.Registration);
            File.Move(staging, path, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        worker = Serve();
    }

    private async Task Serve()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(record.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var json = await LocalControl.ReadFrame(pipe, deadline.Token);
                    var request = JsonSerializer.Deserialize(json, PrototypeJsonContext.Default.ControlRequest)
                        ?? throw new InvalidDataException("Empty control request.");
                    ControlReply reply;
                    if (!LocalControl.EqualToken(request.Token, record.Token)) reply = new ControlReply(false, "Wrong prototype instance token.", null);
                    else
                    {
                        var applied = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (request.Command == "close")
                        {
                            closeDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            closeRequested = true;
                        }
                        browser.Events.Enqueue(() =>
                        {
                            if (deadline.IsCancellationRequested) { applied.TrySetCanceled(); return; }
                            try { applied.TrySetResult(browser.Apply(request)); }
                            catch (ArgumentException ex) { applied.TrySetResult(new ControlReply(false, ex.Message, browser.Snapshot())); }
                        });
                        reply = await applied.Task.WaitAsync(deadline.Token);
                    }
                    await LocalControl.WriteFrame(pipe, JsonSerializer.Serialize(reply, PrototypeJsonContext.Default.ControlReply), deadline.Token);
                    if (request.Command == "close")
                    {
                        closeDelivered.TrySetResult();
                        if (!reply.Ok) closeRequested = false;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or InvalidDataException or ArgumentException)
                {
                    // Bad, disconnected or slow clients do not close or replace a live UI.
                    if (closeRequested) closeDelivered.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (closeRequested) await closeDelivered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        }
        finally
        {
            stop.Cancel();
            try { await worker; }
            finally
            {
                LocalControl.RemoveOwned(path, record);
                stop.Dispose();
            }
        }
    }
}

internal static class LocalControl
{
    internal static string PathFor(SourceContext source)
    {
        var identity = source.Endpoint + "\0" + source.Workspace + "\0" + source.Tab;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var root = Environment.GetEnvironmentVariable("TWIG_AOT1143_STATE_DIR") ?? Path.Combine(Path.GetTempPath(), "twig-tui-aot-1143-control");
        return Path.Combine(root, hash + ".json");
    }

    internal static Registration? Read(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 16384) throw new InvalidDataException("Oversized prototype registration; not reclaimed.");
        Registration record;
        try
        {
            record = JsonSerializer.Deserialize(stream, PrototypeJsonContext.Default.Registration)
                ?? throw new InvalidDataException("Invalid prototype registration; not reclaimed.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Malformed prototype registration; preserved, not reclaimed.", ex);
        }
        // Nullable annotations do not constrain JSON. Validate presence before accessing members;
        // registrations are native-host-only (standalone panel never creates one).
        if (record.Version != 1 || record.Pid <= 0 || record.StartedUtcTicks <= 0 ||
            record.Token is not { Length: 64 } || record.Pipe is not { Length: 45 } ||
            string.IsNullOrWhiteSpace(record.HostPane) || record.Source is null)
            throw new InvalidDataException("Incomplete prototype registration; preserved, not reclaimed.");
        var source = record.Source;
        if (string.IsNullOrWhiteSpace(source.Endpoint) || string.IsNullOrWhiteSpace(source.Workspace) ||
            string.IsNullOrWhiteSpace(source.Tab) || string.IsNullOrWhiteSpace(source.Pane) || string.IsNullOrWhiteSpace(source.Cwd) ||
            !Path.IsPathFullyQualified(source.Endpoint) || !Path.IsPathFullyQualified(source.Cwd) ||
            !record.Token.All(Uri.IsHexDigit) || !record.Pipe.StartsWith("twig-aot1143-", StringComparison.Ordinal) ||
            !record.Pipe[13..].All(Uri.IsHexDigit))
            throw new InvalidDataException("Unrecognized prototype registration; preserved, not reclaimed.");
        return record;
    }

    internal static bool Alive(Registration record)
    {
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Cannot establish peer liveness; registration is not stale.", ex); }
    }

    internal static bool EqualToken(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    internal static void RemoveOwned(string path, Registration record)
    {
        var current = Read(path);
        if (current is not null && EqualToken(current.Token, record.Token) && current.Pid == record.Pid && current.StartedUtcTicks == record.StartedUtcTicks)
            File.Delete(path);
    }

    internal static async Task<ControlReply> Send(Registration record, SourceContext source, string command, string? view = null)
    {
        if (record.Source != source) throw new InvalidOperationException("Existing prototype belongs to a different captured source; refusing retarget.");
        if (!Alive(record)) throw new InvalidOperationException("Prototype process is closed/replaced; stale registration is not a live endpoint.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var pipe = new NamedPipeClientStream(".", record.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await WriteFrame(pipe, JsonSerializer.Serialize(new ControlRequest(record.Token, source, command, view), PrototypeJsonContext.Default.ControlRequest), timeout.Token);
        var reply = JsonSerializer.Deserialize(await ReadFrame(pipe, timeout.Token), PrototypeJsonContext.Default.ControlReply)
            ?? throw new InvalidDataException("Empty control reply.");
        if (!reply.Ok) throw new InvalidOperationException(reply.Error);
        if (reply.Snapshot is null || reply.Snapshot.Source != source || reply.Snapshot.Pid != record.Pid)
            throw new InvalidDataException("Control reply failed captured-source/process checks.");
        return reply;
    }

    // Length prefix, bounded before allocation; the pipe is local/current-user-only, not an ADO authority channel.
    internal static async Task<string> ReadFrame(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > 65536) throw new InvalidDataException("Invalid control frame size.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        return Encoding.UTF8.GetString(body);
    }

    internal static async Task WriteFrame(Stream stream, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > 65536) throw new InvalidDataException("Oversized control reply.");
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
