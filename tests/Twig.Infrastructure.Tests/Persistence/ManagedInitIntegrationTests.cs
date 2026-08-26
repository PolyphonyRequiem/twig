using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

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
        _config = new TwigConfiguration { Organization = "Contoso", Project = "proj" };
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
    public async Task Init_creates_local_layout_registers_worktree_and_materializes_policy()
    {
        if (!_gitAvailable) return;

        var store = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System);
        using var registry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);
        var fingerprintProvider = new WorktreeFingerprintProvider(_paths, _config);
        var initializer = new ManagedWorktreeInitializer(store, registry, fingerprintProvider, _config, _paths);

        var initResult = await initializer.InitializeAsync(
            _config.Organization, _config.Project, team: null,
            profileIdentity: "twig/default", profileVersion: "1");
        initResult.IsSuccess.ShouldBeTrue(initResult.Error);

        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeTrue();

        _config.Policy.ShouldNotBeNull();
        _config.Policy!.SelectedProfile.ShouldNotBeNull();
        _config.Policy.SelectedProfile!.Identity.ShouldBe("twig/default");
        _config.Policy.SelectedProfile.Version.ShouldBe("1");
        _config.Policy.PrimaryScopeTypes.ShouldNotBeNull();

        var fingerprint = fingerprintProvider.CurrentFingerprint;
        var find = await registry.FindWorktreeAsync(fingerprint.CanonicalJson);
        find.Value.ShouldNotBeNull();
    }
}
