using System.Diagnostics;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// End-to-end coverage for the AB#738 managed-init contract: the local
/// layout markers and the system-store worktree row MUST both land in the
/// same run so downstream attach/switch/detach find the two prerequisites
/// §9.5 depends on. Skips itself when git is unavailable.
/// </summary>
public sealed class ManagedInitIntegrationTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _systemDbPath;
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly bool _gitAvailable;

    public ManagedInitIntegrationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "twig-managed-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        var twigDir = Path.Combine(_workDir, ".twig");
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"), _workDir);
        _config = new TwigConfiguration { Organization = "contoso", Project = "proj" };
        File.WriteAllText(Path.Combine(_workDir, "twig.json"), "{\n}\n");
        _systemDbPath = Path.Combine(_workDir, "system.db");
        _gitAvailable = TryInitGit(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static bool TryInitGit(string dir)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Init_creates_local_layout_and_registers_worktree_in_the_system_store()
    {
        if (!_gitAvailable) return;

        var store = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System);
        using var registry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);

        // Local layout — §6.3 steps 4-7.
        (await store.InitializeAsync()).IsSuccess.ShouldBeTrue();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeTrue();

        // System store — §9.5 step 5 seed.
        var connectionRef = ConnectionRefResolver.Compute(_config);
        (await registry.UpsertConnectionAsync(connectionRef, _config.Organization, _config.Project, team: null)).IsSuccess.ShouldBeTrue();
        var fingerprint = new WorktreeFingerprintProvider(_paths, _config).CurrentFingerprint;
        (await registry.UpsertWorktreeAsync(fingerprint.CanonicalJson, fingerprint.ConnectionRef, fingerprint.WorktreeRoot)).IsSuccess.ShouldBeTrue();

        // Every §9.5 pre-attach precondition now holds — the row is present,
        // not retired, and its connectionRef matches the current binding.
        var find = await registry.FindWorktreeAsync(fingerprint.CanonicalJson);
        find.Value.ShouldNotBeNull();
        find.Value!.RetiredAt.ShouldBeNull();
        find.Value.ConnectionRef.ShouldBe(fingerprint.ConnectionRef);
    }
}
