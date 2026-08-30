using System.Diagnostics;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;
using Twig.Infrastructure.Services.ReferenceProfile;
using Twig.Infrastructure.Tests.Services.ReferenceProfile;

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
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), TwigPaths.GetCacheDbPath(twigDir), _workDir);
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
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private ManagedWorktreeInitializer BuildInitializer(IProfileRegistrySource? registrySource = null)
    {
        var store = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System);
        var registry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);
        var fingerprintProvider = new WorktreeFingerprintProvider(_paths, _config);
        return new ManagedWorktreeInitializer(store, registry, fingerprintProvider, _config, _paths,
            registrySource ?? new UnavailableProfileRegistrySource(),
            new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching()));
    }

    [Fact]
    public async Task Init_fails_selected_profile_unavailable_when_no_policy_and_no_registry()
    {
        if (!_gitAvailable) return;

        // No pre-existing Policy in config AND the default (#727-unavailable)
        // registry source — init must fail closed with the named error rather
        // than materialize a synthetic identity/version.
        var initializer = BuildInitializer();
        var result = await initializer.InitializeAsync("Contoso", "proj", null, "Agile", "1");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.SelectedProfileUnavailable);

        // No config.Policy was materialized — the file is untouched.
        _config.Policy.ShouldBeNull();
    }

    [Fact]
    public async Task Init_preserves_existing_configured_policy()
    {
        if (!_gitAvailable) return;

        _config.Policy = new PolicyConfig
        {
            SelectedProfile = new SelectedProfileBinding { Identity = "MyProcess", Version = "3" },
            PrimaryScopeTypes = new List<string> { "Task", "Bug" },
        };
        var initializer = BuildInitializer();

        var result = await initializer.InitializeAsync("Contoso", "proj", null, "Agile", "1");
        result.IsSuccess.ShouldBeTrue(result.Error);

        // Existing configured values are preserved byte-for-byte.
        _config.Policy.SelectedProfile!.Identity.ShouldBe("MyProcess");
        _config.Policy.SelectedProfile.Version.ShouldBe("3");
        _config.Policy.PrimaryScopeTypes.ShouldBe(new[] { "Task", "Bug" });
    }

    [Fact]
    public async Task Init_materializes_registry_supplied_policy_when_no_existing_config()
    {
        if (!_gitAvailable) return;

        var source = new FakeRegistrySource(new SelectedProfileMaterialization("Agile", "1", new[] { "Task", "User Story" }));
        var initializer = BuildInitializer(source);

        var result = await initializer.InitializeAsync("Contoso", "proj", null, "Agile", "1");
        result.IsSuccess.ShouldBeTrue(result.Error);

        _config.Policy!.SelectedProfile!.Identity.ShouldBe("Agile");
        _config.Policy.SelectedProfile.Version.ShouldBe("1");
        _config.Policy.PrimaryScopeTypes.ShouldBe(new[] { "Task", "User Story" });
    }

    private sealed class FakeRegistrySource : IProfileRegistrySource
    {
        private readonly SelectedProfileMaterialization _materialization;
        public FakeRegistrySource(SelectedProfileMaterialization m) { _materialization = m; }
        public Result<SelectedProfileMaterialization> Resolve(string processTemplate) => Result.Ok(_materialization);
    }
}
