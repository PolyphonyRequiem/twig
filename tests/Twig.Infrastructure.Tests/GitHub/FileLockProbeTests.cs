using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Tests for <see cref="FileLockProbe"/>. Covers missing-file probes, exclusive-share
/// probes from a held handle, stale .tmp cleanup, and best-effort holder enumeration.
/// </summary>
public sealed class FileLockProbeTests : IDisposable
{
    private readonly string _tempDir;

    public FileLockProbeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-lockprobe-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Probe_MissingFile_ReportsNotExisting()
    {
        var path = Path.Combine(_tempDir, "missing.bin");

        var result = FileLockProbe.Probe(path);

        result.Path.ShouldBe(path);
        result.Exists.ShouldBeFalse();
        result.IsLocked.ShouldBeFalse();
        result.HoldingProcessIds.ShouldBeEmpty();
    }

    [Fact]
    public void Probe_UnlockedFile_ReportsExistsAndUnlocked()
    {
        var path = Path.Combine(_tempDir, "free.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        var result = FileLockProbe.Probe(path);

        result.Exists.ShouldBeTrue();
        result.IsLocked.ShouldBeFalse();
    }

    [Fact]
    public void Probe_HeldExclusively_ReportsLocked()
    {
        // Windows is the platform where this matters most. On Unix, opening a file
        // does not normally produce a sharing violation under FileShare.None, so the
        // probe will report unlocked. Skip the assertion off-Windows to keep the
        // suite green cross-platform without weakening the contract on Windows.
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_tempDir, "locked.bin");
        File.WriteAllBytes(path, [9, 9, 9]);

        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = FileLockProbe.Probe(path);

        result.Exists.ShouldBeTrue();
        result.IsLocked.ShouldBeTrue();
    }

    [Fact]
    public void TryRemoveStaleTemp_RemovesExistingTmpSibling()
    {
        var target = Path.Combine(_tempDir, "twig-mcp.exe");
        var staleTemp = target + ".tmp";
        File.WriteAllBytes(staleTemp, [0]);

        FileLockProbe.TryRemoveStaleTemp(target);

        File.Exists(staleTemp).ShouldBeFalse();
    }

    [Fact]
    public void TryRemoveStaleTemp_NoOpWhenMissing()
    {
        var target = Path.Combine(_tempDir, "twig-mcp.exe");

        // Should not throw even though no .tmp exists.
        FileLockProbe.TryRemoveStaleTemp(target);

        File.Exists(target + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void ProbeAll_ReturnsResultsInInputOrder()
    {
        var a = Path.Combine(_tempDir, "a.bin");
        var b = Path.Combine(_tempDir, "b.bin");
        var c = Path.Combine(_tempDir, "c.bin");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(c, [3]);

        var results = FileLockProbe.ProbeAll([a, b, c]);

        results.Count.ShouldBe(3);
        results[0].Path.ShouldBe(a);
        results[0].Exists.ShouldBeTrue();
        results[1].Path.ShouldBe(b);
        results[1].Exists.ShouldBeFalse();
        results[2].Path.ShouldBe(c);
        results[2].Exists.ShouldBeTrue();
    }
}
