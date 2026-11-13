using System.Text;
using Bench.Application;

namespace Bench.Infrastructure;

internal sealed class NativeCheckBlockedException : Exception;

// One durable state cell, not a provider-operation journal. The exclusive handle owns the transition.
internal sealed class NativeCacheCheck : IDisposable
{
    private readonly FileStream file;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly bool previouslyBlocked;
    private bool completed;
    private bool notified;

    internal bool HadAllowedEvidence { get; }
    private NativeCacheCheck(FileStream file, bool previouslyBlocked, bool hadAllowedEvidence)
        => (this.file, this.previouslyBlocked, HadAllowedEvidence) = (file, previouslyBlocked, hadAllowedEvidence);

    internal static NativeCacheCheck Begin(string path, bool explicitReauthorization, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path);
        if (!existing && !explicitReauthorization) throw new NativeCheckBlockedException();
        var stream = new FileStream(path, existing ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        try
        {
            var allowed = !existing || Read(stream) is { State: "allowed" };
            if (!allowed && !explicitReauthorization) throw new NativeCheckBlockedException();
            var check = new NativeCacheCheck(stream, !allowed, existing && allowed);
            check.Write("pending"); // Must be durably retained before any denial-capable native call.
            return check;
        }

        catch { stream.Dispose(); throw; }
    }

    internal static void RequireAllowed(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Read(file) is not { State: "allowed" }) throw new NativeCheckBlockedException();
    }

    internal void Complete(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Read(file) is not { State: "pending" } state || state.Owner != owner)
            throw new InvalidDataException("This operation no longer owns its native check.");
        Write("allowed");
        completed = true;
    }

    internal void InvalidateIfUnfinished(CatalogChanges changes)
    {
        if (!completed && !previouslyBlocked && !notified)
        {
            notified = true;
            changes.Publish(0);
        }
    }

    private void Write(string state)
    {
        var bytes = Encoding.ASCII.GetBytes($"twig-native-check-v1\n{state}\n{owner}");
        file.Position = 0;
        file.SetLength(0);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
    private static (string State, string Owner)? Read(FileStream file)
    {
        if (file.Length is < 1 or > 128) return null;
        file.Position = 0;
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        var fields = Encoding.ASCII.GetString(bytes).Split('\n');
        return fields.Length == 3 && fields[0] == "twig-native-check-v1"
            && fields[1] is "pending" or "allowed" && Guid.TryParseExact(fields[2], "N", out var id) && id != Guid.Empty
                ? (fields[1], fields[2]) : null;
    }
    public void Dispose() => file.Dispose(); // Never delete/clear pending state on failure or cancellation.
}
