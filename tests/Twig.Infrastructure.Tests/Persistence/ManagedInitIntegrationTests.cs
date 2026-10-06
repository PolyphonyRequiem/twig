using System.Diagnostics;
using NSubstitute;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.ValueObjects;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;
using Twig.Infrastructure.Services.ReferenceProfile;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class ManagedInitIntegrationTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _systemDbPath;
    private readonly TwigPaths _paths;
    private readonly TwigConfiguration _config;
    private readonly bool _gitAvailable;
    private readonly SqliteSystemWorktreeRegistry _registry;

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
        _registry = new SqliteSystemWorktreeRegistry(_systemDbPath, TimeProvider.System);
    }

    public void Dispose()
    {
        _registry.Dispose();
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

    private ManagedWorktreeInitializer BuildInitializer(IProfileRegistrySource? registrySource = null,
        IReferenceProfileProvider? profileProvider = null)
    {
        var store = new WorktreeLocalAttachmentStore(_paths, _config, TimeProvider.System);
        var registry = _registry;
        var fingerprintProvider = new WorktreeFingerprintProvider(_paths, _config);
        var provider = profileProvider ?? new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config));
        return new ManagedWorktreeInitializer(store, registry, fingerprintProvider, _config, _paths,
            registrySource ?? new ReferenceProfileRegistrySource(provider), provider);
    }

    [Fact]
    public async Task Init_fails_when_explicit_selection_has_no_registry()
    {
        if (!_gitAvailable) return;

        var initializer = BuildInitializer(new UnavailableProfileRegistrySource());
        var result = await initializer.InitializeAsync("Contoso", "proj", null, "selected-profile");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.SelectedProfileUnavailable);

        Directory.Exists(_paths.TwigDir).ShouldBeFalse();
        _config.Profile.ShouldBeNull();
    }

    [Fact]
    public async Task Init_does_not_interpret_existing_policy_as_profile_consent()
    {
        if (!_gitAvailable) return;

        _config.Policy = new PolicyConfig
        {
            SelectedProfile = new SelectedProfileBinding { Identity = "MyProcess", Version = "3" },
            PrimaryScopeTypes = new List<string> { "Task", "Bug" },
        };
        var initializer = BuildInitializer();

        var result = await initializer.InitializeAsync("Contoso", "proj", null);
        result.IsSuccess.ShouldBeTrue(result.Error);

        // Existing configured values are preserved byte-for-byte.
        _config.Policy.SelectedProfile!.Identity.ShouldBe("MyProcess");
        _config.Policy.SelectedProfile.Version.ShouldBe("3");
        _config.Policy.PrimaryScopeTypes.ShouldBe(new[] { "Task", "Bug" });
        _config.Profile.ShouldBeNull();
        new ConfigPrimaryScopeTypeEligibility(new ReferenceProfilePolicySource(
            new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config))))
            .Evaluate(WorkItemType.Parse("Task").Value).Error.ShouldBe(ReferenceProfileErrors.TwigJsonProfileBlockMissing);
    }

    [Fact]
    public async Task Init_explicit_selection_activates_profile_gates_after_reload()
    {
        if (!_gitAvailable) return;
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config));
        var identity = provider.Load().Value.Identity;

        var result = await BuildInitializer().InitializeAsync("Contoso", "proj", null, identity);

        result.IsSuccess.ShouldBeTrue(result.Error);
        var loaded = await TwigConfiguration.LoadSplitAsync(_paths);
        var pinnedProvider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(loaded));
        var policy = new SprintEntryPolicy(pinnedProvider);
        var sprint = IterationPath.Parse("proj\\Sprint 1").Value;
        policy.Evaluate(WorkItemType.Parse("Bug").Value, sprint).Error.ShouldBe(SprintEntryFailure.NotSprintTier);
        policy.Evaluate(WorkItemType.Parse("Task").Value, sprint).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_profile_write_can_be_retried_on_the_same_initializer_and_activates_persisted_gates()
    {
        _gitAvailable.ShouldBeTrue("the persistence retry regression requires Git");
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config));
        var identity = provider.Load().Value.Identity;
        var initializer = BuildInitializer();
        File.Delete(_paths.RepoConfigPath);
        Directory.CreateDirectory(_paths.RepoConfigPath);

        var failed = await initializer.InitializeAsync("Contoso", "proj", null, identity);
        failed.IsSuccess.ShouldBeFalse();
        failed.Error.ShouldStartWith(AttachmentStorageFailure.AtomicWriteFailed);
        Directory.Delete(_paths.RepoConfigPath);

        var retried = await initializer.InitializeAsync("Contoso", "proj", null, identity);
        retried.IsSuccess.ShouldBeTrue(retried.Error);
        var reloadPaths = new TwigPaths(_paths.TwigDir, _paths.ConfigPath, _paths.DbPath,
            _paths.StartDir, Path.Combine(_workDir, "display.json"));
        var reloaded = await TwigConfiguration.LoadSplitAsync(reloadPaths);
        var reloadedProvider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(reloaded));
        new SprintEntryPolicy(reloadedProvider).Evaluate(WorkItemType.Parse("Bug").Value,
            IterationPath.Parse("proj\\Sprint 1").Value).Error.ShouldBe(SprintEntryFailure.NotSprintTier);
    }

    [Fact]
    public async Task Init_without_selection_does_not_consult_profile_sources()
    {
        if (!_gitAvailable) return;
        var registry = Substitute.For<IProfileRegistrySource>();
        var provider = Substitute.For<IReferenceProfileProvider>();
        provider.Load().Returns(Result.Fail<ReferenceProfile>(ReferenceProfileErrors.ProfileBlobNotFound));

        var result = await BuildInitializer(registry, provider).InitializeAsync("Contoso", "proj", null);

        result.IsSuccess.ShouldBeTrue(result.Error);
        registry.DidNotReceiveWithAnyArgs().Resolve(default!);
        provider.DidNotReceive().Load();
        provider.DidNotReceive().ValidatePin();
        _config.Profile.ShouldBeNull();
        _config.Policy.ShouldBeNull();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("", ReferenceProfileErrors.ProfileSchemaInvalid)]
    [InlineData("0.0.0", ReferenceProfileErrors.ProfileVersionMismatch)]
    public async Task Init_refuses_present_broken_pin_before_layout_and_preserves_it(string version, string expectedError)
    {
        if (!_gitAvailable) return;
        var artifact = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config)).Load().Value;
        var original = new ProfilePinConfig
        {
            Identity = artifact.Identity,
            ProfileVersion = version,
            BaseProcessVersion = artifact.BaseProcess.TailoringVersion,
        };
        _config.Profile = original;
        await _config.SaveSplitAsync(_paths);
        var originalBytes = await File.ReadAllBytesAsync(_paths.RepoConfigPath);

        var result = await BuildInitializer().InitializeAsync("Contoso", "proj", null, artifact.Identity);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(expectedError);
        _config.Profile.ShouldBeSameAs(original);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(originalBytes);
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Init_preserves_valid_pin_and_policy_without_selecting_from_registry()
    {
        if (!_gitAvailable) return;
        var artifact = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config)).Load().Value;
        _config.Profile = new ProfilePinConfig
        {
            Identity = artifact.Identity,
            ProfileVersion = artifact.ProfileVersion,
            BaseProcessVersion = artifact.BaseProcess.TailoringVersion,
        };
        _config.Policy = new PolicyConfig { PrimaryScopeTypes = ["User Story"] };
        await _config.SaveSplitAsync(_paths);
        var originalBytes = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var registry = Substitute.For<IProfileRegistrySource>();

        var result = await BuildInitializer(registry).InitializeAsync("Contoso", "proj", null);

        result.IsSuccess.ShouldBeTrue(result.Error);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(originalBytes);
        registry.DidNotReceiveWithAnyArgs().Resolve(default!);
        var eligibility = new ConfigPrimaryScopeTypeEligibility(new ReferenceProfilePolicySource(
            new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config))));
        eligibility.Evaluate(WorkItemType.Parse("User Story").Value).Value.ShouldBeFalse();
        eligibility.Evaluate(WorkItemType.Parse("Task").Value).Value.ShouldBeTrue();
    }

    [Fact]
    public async Task Init_refuses_explicit_switch_of_existing_policy_selection()
    {
        if (!_gitAvailable) return;
        _config.Policy = new PolicyConfig
        {
            SelectedProfile = new SelectedProfileBinding { Identity = "operator-selected-profile", Version = "3" },
        };
        await _config.SaveSplitAsync(_paths);
        var originalBytes = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var artifact = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(_config)).Load().Value;

        var result = await BuildInitializer().InitializeAsync("Contoso", "proj", null, artifact.Identity);

        result.Error.ShouldBe(ReferenceProfileErrors.ProfileIdentityUnknown);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(originalBytes);
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
    }
}
