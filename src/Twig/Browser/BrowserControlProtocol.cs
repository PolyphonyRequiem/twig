using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twig.Browser;

internal sealed record BrowserSource(string Socket, string Workspace, string Tab, string Pane, string Cwd)
{
    internal static BrowserSource Create(string socket, string workspace, string tab, string pane, string cwd)
    {
        BrowserControlProtocol.Text(socket, 4096); BrowserControlProtocol.Text(workspace, 256);
        BrowserControlProtocol.Text(tab, 256); BrowserControlProtocol.Text(pane, 256); BrowserControlProtocol.Text(cwd, 4096);
        if (!Path.IsPathFullyQualified(socket) || !Path.IsPathFullyQualified(cwd)) throw new InvalidDataException("Captured source requires absolute socket and cwd paths.");
        return new(socket, workspace, tab, pane, Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)));
    }

    internal bool Matches(BrowserSource source) => Socket == source.Socket && Workspace == source.Workspace && Tab == source.Tab && Pane == source.Pane &&
        string.Equals(Cwd, source.Cwd, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed record BrowserHostRegistration(string Pipe, string Token, int Pid, string BirthId, string HostPane, BrowserSource Source, string LaunchNonce);
internal sealed record BrowserControlRequest(string Token, string Nonce, int Pid, string BirthId, string HostPane, BrowserSource Source, BrowserIntent Intent);
internal sealed record BrowserControlReply(string Nonce, int Pid, string BirthId, string HostPane, BrowserSource Source, BrowserIntentResult Result);
internal sealed record BrowserLaunchTicket(string Nonce, int Pid, string BirthId, long Expires, string HostPane, BrowserSource Source);

/// <summary>Explicit UTF-8 writer/reader, with no reflection serializer or second source-generated context.</summary>
internal static class BrowserControlProtocol
{
    private const int Version = 4;
    internal const int MaximumFrame = 65536;
    internal static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal static string NewPipeName() => "twa" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    internal static bool EqualToken(string left, string right) => left.Length == 64 && right.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    internal static void Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid or oversized native host context.");
    }

    internal static void Token(string value)
    { if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid native control token/nonce."); }

    internal static string PathFor(string root, BrowserSource source)
        => Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Socket + "\0" + source.Workspace + "\0" + source.Tab))) + ".json");

    // Linux Process.StartTime reconstructs a wall-clock boot offset per runtime;
    // independent processes disagree even for the same live PID. Kernel start ticks
    // and the boot UUID identify its lifetime without a tolerance or PID-only fallback.
    internal static string ProcessBirthId(int pid)
    {
        var (birthId, live) = ProcessIdentity(pid);
        if (!live) throw new ArgumentException("The native process has already exited.", nameof(pid));
        return birthId;
    }

    internal static bool Alive(int pid, string birthId)
    {
        try
        {
            var (current, live) = ProcessIdentity(pid);
            return live && string.Equals(current, birthId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
        { return false; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { throw new IOException("Cannot establish native peer liveness; no live registration is reclaimed.", ex); }
    }

    private static readonly string LinuxBootId = OperatingSystem.IsLinux()
        ? Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()).ToString("N") : "";

    private static (string BirthId, bool Live) ProcessIdentity(int pid)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        if (!OperatingSystem.IsLinux())
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos"
                : throw new PlatformNotSupportedException("Native process birth identity is unsupported on this platform.");
            return (string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{platform}:{process.StartTime.ToUniversalTime().Ticks}"), !process.HasExited);
        }

        var stat = File.ReadAllText(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/proc/{pid}/stat"));
        // comm may contain spaces and parentheses; fields begin after its final ')'.
        var end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 >= stat.Length) throw new InvalidDataException("Malformed native process identity; no registration is reclaimed.");
        var fields = stat.AsSpan(end + 2).Trim();
        if (fields.IsEmpty) throw new InvalidDataException("Missing native process state.");
        var live = fields[0] is not ('Z' or 'X' or 'x');
        for (var field = 3; field < 22; field++)
        {
            var separator = fields.IndexOf(' ');
            if (separator < 0) throw new InvalidDataException("Missing native process birth field.");
            fields = fields[(separator + 1)..].TrimStart();
        }
        var next = fields.IndexOf(' ');
        var birth = next < 0 ? fields : fields[..next];
        if (!ulong.TryParse(birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks) || ticks == 0)
            throw new InvalidDataException("Invalid native process birth field.");
        return (string.Create(System.Globalization.CultureInfo.InvariantCulture, $"linux:{LinuxBootId}:{ticks}"), live);
    }

    private static string BirthId(JsonElement node)
    {
        var value = String(node, "birth", 96);
        var identity = value.AsSpan();
        var separator = identity.IndexOf(':');
        if (separator < 0) throw new InvalidDataException("Invalid native process birth identity.");
        var platform = identity[..separator]; var birth = identity[(separator + 1)..];
        var bootSeparator = birth.IndexOf(':');
        var linux = platform.SequenceEqual("linux") && bootSeparator >= 0 && Guid.TryParseExact(birth[..bootSeparator], "N", out _) &&
            ulong.TryParse(birth[(bootSeparator + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks) && ticks > 0;
        var native = (platform.SequenceEqual("windows") || platform.SequenceEqual("macos")) &&
            long.TryParse(birth, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var utcTicks) && utcTicks > 0;
        if (!linux && !native) throw new InvalidDataException("Invalid native process birth identity.");
        return value;
    }

    internal static BrowserHostRegistration? ReadRegistration(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = ReadFile(path);
        var node = document.RootElement;
        if (node.GetProperty("version").GetInt32() != Version) throw new InvalidDataException("Unsupported browser control protocol; version 4 exact process birth identity is required.");
        Only(node, "version", "pipe", "token", "pid", "birth", "hostPane", "source", "launchNonce");
        var pipe = String(node, "pipe", 96); var token = String(node, "token", 64); Token(token);
        if (!pipe.StartsWith("twa", StringComparison.Ordinal) || pipe.Length != 35 || !pipe.AsSpan(3).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("Unrecognized native browser pipe.");
        var pid = node.GetProperty("pid").GetInt32(); var birthId = BirthId(node);
        if (pid <= 0) throw new InvalidDataException("Invalid native process identity.");
        var nonce = String(node, "launchNonce", 64); Token(nonce);
        return new(pipe, token, pid, birthId, String(node, "hostPane", 256), Source(node.GetProperty("source")), nonce);
    }

    internal static JsonDocument ReadFile(string path)
    {
        BrowserPrivateDirectory.ValidateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 16384) throw new InvalidDataException("Invalid native registration size; preserved, not reclaimed.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return Parse(bytes);
    }

    internal static void Publish(string path, byte[] bytes)
    {
        if (bytes.Length > 16384) throw new InvalidDataException("Oversized native registration.");
        var staging = path + "." + NewToken() + ".tmp";
        try
        {
            using (var stream = BrowserPrivateDirectory.CreateExclusive(staging)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(staging, path, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal static void RemoveOwned(string path, BrowserHostRegistration owned)
    {
        var current = ReadRegistration(path);
        if (current is not null && EqualToken(current.Token, owned.Token) && current.Pid == owned.Pid && current.BirthId == owned.BirthId &&
            current.HostPane == owned.HostPane && current.Source.Matches(owned.Source) && EqualToken(current.LaunchNonce, owned.LaunchNonce)) File.Delete(path);
    }

    internal static byte[] Registration(BrowserHostRegistration record) => Encode(writer =>
    {
        writer.WriteNumber("version", Version); writer.WriteString("pipe", record.Pipe); writer.WriteString("token", record.Token);
        Process(writer, record.Pid, record.BirthId, record.HostPane); Source(writer, record.Source); writer.WriteString("launchNonce", record.LaunchNonce);
    });

    internal static byte[] Request(BrowserControlRequest request) => Encode(writer =>
    {
        writer.WriteString("token", request.Token); writer.WriteString("nonce", request.Nonce);
        Process(writer, request.Pid, request.BirthId, request.HostPane); Source(writer, request.Source);
        writer.WriteString("command", request.Intent.Command);
        if (request.Intent.Value is not null) writer.WriteString("value", request.Intent.Value);
        if (request.Intent.ProposalFile is not null) writer.WriteString("file", request.Intent.ProposalFile);
    });

    internal static BrowserControlRequest Request(byte[] bytes)
    {
        using var doc = Parse(bytes); var node = doc.RootElement;
        Only(node, "token", "nonce", "pid", "birth", "hostPane", "source", "command", "value", "file");
        var token = String(node, "token", 64); Token(token); var nonce = String(node, "nonce", 64); Token(nonce);
        var command = String(node, "command", 32); var value = Optional(node, "value", 32); var file = Optional(node, "file", 4096);
        ValidateIntent(new(command, value, file));
        return new(token, nonce, node.GetProperty("pid").GetInt32(), BirthId(node),
            String(node, "hostPane", 256), Source(node.GetProperty("source")), new(command, value, file));
    }

    internal static void ValidateIntent(BrowserIntent intent)
    {
        if (intent.Command is not ("status" or "view" or "review" or "exit-review" or "reconnect" or "close"))
            throw new InvalidDataException("Unsupported browser control command; view controls cannot publish.");
        if ((intent.Command == "view" && intent.Value is not ("tree" or "table")) || (intent.Command != "view" && intent.Value is not null) ||
            (intent.Command != "review" && intent.ProposalFile is not null)) throw new InvalidDataException("Unexpected browser intent arguments.");
        if (intent.ProposalFile is not null) Text(intent.ProposalFile, 4096);
    }

    internal static byte[] Reply(BrowserControlReply reply) => Encode(writer =>
    {
        writer.WriteString("nonce", reply.Nonce); Process(writer, reply.Pid, reply.BirthId, reply.HostPane); Source(writer, reply.Source);
        writer.WriteBoolean("ok", reply.Result.Ok); writer.WriteString("message", reply.Result.Message.Length <= 8192 ? reply.Result.Message : reply.Result.Message[..8192]);
        writer.WriteString("view", reply.Result.View); writer.WriteBoolean("requiresReconnect", reply.Result.RequiresReconnect); writer.WriteNumber("generation", reply.Result.Generation);
    });

    internal static BrowserControlReply Reply(byte[] bytes)
    {
        using var doc = Parse(bytes); var node = doc.RootElement;
        Only(node, "nonce", "pid", "birth", "hostPane", "source", "ok", "message", "view", "requiresReconnect", "generation");
        var nonce = String(node, "nonce", 64); Token(nonce);
        return new(nonce, node.GetProperty("pid").GetInt32(), BirthId(node), String(node, "hostPane", 256),
            Source(node.GetProperty("source")), new(node.GetProperty("ok").GetBoolean(), node.GetProperty("message").GetString() ?? "",
                String(node, "view", 32), node.GetProperty("requiresReconnect").GetBoolean(), node.GetProperty("generation").GetInt64()));
    }

    internal static byte[] Ticket(BrowserLaunchTicket ticket) => Encode(writer =>
    {
        writer.WriteString("nonce", ticket.Nonce); Process(writer, ticket.Pid, ticket.BirthId, ticket.HostPane);
        Source(writer, ticket.Source); writer.WriteNumber("expires", ticket.Expires);
    });

    internal static BrowserLaunchTicket Ticket(string path)
    {
        using var doc = ReadFile(path); var node = doc.RootElement;
        Only(node, "nonce", "pid", "birth", "hostPane", "source", "expires");
        var nonce = String(node, "nonce", 64); Token(nonce);
        return new(nonce, node.GetProperty("pid").GetInt32(), BirthId(node), node.GetProperty("expires").GetInt64(),
            String(node, "hostPane", 256), Source(node.GetProperty("source")));
    }

    internal static async Task<byte[]> ReadFrame(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > MaximumFrame) throw new InvalidDataException("Control frame exceeds bounded protocol.");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, ct); return body;
    }

    internal static async Task WriteFrame(Stream stream, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length is < 1 or > MaximumFrame) throw new InvalidDataException("Control frame exceeds bounded protocol.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }

    private static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    private static byte[] Encode(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) { writer.WriteStartObject(); body(writer); writer.WriteEndObject(); }
        if (stream.Length > MaximumFrame) throw new InvalidDataException("Oversized control output.");
        return stream.ToArray();
    }
    private static void Process(Utf8JsonWriter writer, int pid, string birthId, string pane)
    { writer.WriteNumber("pid", pid); writer.WriteString("birth", birthId); writer.WriteString("hostPane", pane); }
    private static void Source(Utf8JsonWriter writer, BrowserSource source)
    {
        writer.WriteStartObject("source"); writer.WriteString("socket", source.Socket); writer.WriteString("workspace", source.Workspace);
        writer.WriteString("tab", source.Tab); writer.WriteString("pane", source.Pane); writer.WriteString("cwd", source.Cwd); writer.WriteEndObject();
    }
    private static BrowserSource Source(JsonElement node)
    {
        Only(node, "socket", "workspace", "tab", "pane", "cwd");
        return BrowserSource.Create(String(node, "socket", 4096), String(node, "workspace", 256), String(node, "tab", 256), String(node, "pane", 256), String(node, "cwd", 4096));
    }
    private static string String(JsonElement node, string name, int maximum)
    { var value = node.GetProperty(name).GetString(); Text(value, maximum); return value!; }
    private static string? Optional(JsonElement node, string name, int maximum)
    { if (!node.TryGetProperty(name, out var item)) return null; var value = item.GetString(); Text(value, maximum); return value; }
    private static void Only(JsonElement node, params string[] allowed)
    {
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected control object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException("Unknown or duplicate control property.");
    }
}
