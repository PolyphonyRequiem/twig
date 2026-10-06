using System.Diagnostics;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Collection("NonParallel")]
public sealed class InitProfileSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-profile-selection-{Guid.NewGuid():N}");
    private readonly string _originalCwd = Directory.GetCurrentDirectory();
    private readonly TwigPaths _paths;
    private readonly SqliteSystemWorktreeRegistry _registry;

    public InitProfileSelectionTests()
    {
        Directory.CreateDirectory(_root);
        InitCommandTestFixture.InitTempWorktree(_root).ShouldBeTrue("this consumer regression requires Git");
        Directory.SetCurrentDirectory(_root);
        var twigDir = Path.Combine(_root, ".twig");
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"),
            Path.Combine(twigDir, "cache", "twig.db"), _root, Path.Combine(_root, "display.json"));
        _registry = new SqliteSystemWorktreeRegistry(Path.Combine(_root, "system.db"), TimeProvider.System);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        _registry.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("Agile", "User Story")]
    [InlineData("Scrum", "Product Backlog Item")]
    public async Task Fresh_init_without_selection_does_not_activate_reference_sprint_gate(
        string processTemplate, string requirementType)
    {
        var command = CreateCommand(processTemplate);
        var exitCode = await command.ExecuteAsync("ProbeOrg", "ProbeProject");
        exitCode.ShouldBe(0);

        var config = await TwigConfiguration.LoadSplitAsync(_paths);
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(config));
        var candidate = WorkItemType.Parse(requirementType).Value;
        var sprint = IterationPath.Parse("ProbeProject\\Sprint 1").Value;

        var sprintEntry = new SprintEntryPolicy(provider).Evaluate(candidate, sprint);
        sprintEntry.IsSuccess.ShouldBeTrue("init without profile consent must not activate the reference sprint gate");
        var scope = new ConfigPrimaryScopeTypeEligibility(new ReferenceProfilePolicySource(provider)).Evaluate(candidate);
        scope.IsSuccess.ShouldBeFalse("unprofiled init does not authorize primary-scope attachment");
        scope.Error.ShouldBe(ReferenceProfileErrors.TwigJsonProfileBlockMissing);
    }

    [Fact]
    public async Task Unprofiled_init_remains_usable_when_reference_registry_is_unavailable()
    {
        var command = CreateCommand("Agile", new UnavailableProfileRegistrySource());
        var exitCode = await command.ExecuteAsync("ProbeOrg", "ProbeProject");
        exitCode.ShouldBe(0);
        var config = await TwigConfiguration.LoadSplitAsync(_paths);
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(config));
        new SprintEntryPolicy(provider).Evaluate(WorkItemType.Parse("User Story").Value,
            IterationPath.Parse("ProbeProject\\Sprint 1").Value).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Explicit_reference_selection_enforces_sprint_tier_and_enables_profile_scope()
    {
        var command = CreateCommand("Basic");
        var exitCode = await command.ExecuteAsync("ProbeOrg", "ProbeProject", profile: "twig.reference-profile.hyperbright");
        exitCode.ShouldBe(0);
        var config = await TwigConfiguration.LoadSplitAsync(_paths);
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(config));
        var sprint = IterationPath.Parse("ProbeProject\\Sprint 1").Value;
        var policy = new SprintEntryPolicy(provider);
        policy.Evaluate(WorkItemType.Parse("Feature").Value, sprint).Error.ShouldBe(SprintEntryFailure.NotSprintTier);
        policy.Evaluate(WorkItemType.Parse("Task").Value, sprint).IsSuccess.ShouldBeTrue();
        var scope = new ConfigPrimaryScopeTypeEligibility(new ReferenceProfilePolicySource(provider))
            .Evaluate(WorkItemType.Parse("Feature").Value);
        scope.IsSuccess.ShouldBeTrue(scope.Error);
        scope.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_selection_refuses_without_declaring_or_initializing_a_profile()
    {
        var command = CreateCommand("Agile");
        var exitCode = await command.ExecuteAsync("ProbeOrg", "ProbeProject", profile: "unknown-profile");
        exitCode.ShouldBe(1);
        File.Exists(_paths.RepoConfigPath).ShouldBeFalse();
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.0.0")]
    public async Task Existing_broken_pin_is_refused_and_preserved_even_with_explicit_selection(string profileVersion)
    {
        var original = $$$"""
            {"organization":"ProbeOrg","project":"ProbeProject","profile":{"identity":"twig.reference-profile.hyperbright","profileVersion":"{{{profileVersion}}}","baseProcessVersion":"basic:2026-08-24:1"}}
            """;
        await File.WriteAllTextAsync(_paths.RepoConfigPath, original);
        using (var git = Process.Start(new ProcessStartInfo("git", "add -- twig.json")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            git.ShouldNotBeNull();
            await git.WaitForExitAsync();
            git.ExitCode.ShouldBe(0);
        }
        var command = CreateCommand("Basic");
        var exitCode = await command.ExecuteAsync("ProbeOrg", "ProbeProject", profile: "twig.reference-profile.hyperbright");
        exitCode.ShouldBe(1);
        (await File.ReadAllTextAsync(_paths.RepoConfigPath)).ShouldBe(original);
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeFalse();
    }

    private Twig.Commands.InitCommand CreateCommand(string processTemplate,
        Twig.Domain.Services.Attachment.IProfileRegistrySource? profileRegistry = null)
    {
        var metadata = Substitute.For<IIterationService>();
        metadata.DetectTemplateNameAsync(Arg.Any<CancellationToken>()).Returns(processTemplate);
        metadata.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("ProbeProject\\Sprint 1").Value);
        metadata.GetProcessConfigurationAsync(Arg.Any<CancellationToken>()).Returns(new ProcessConfigurationData());
        metadata.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItemTypeAppearance>());
        var provider = new EmbeddedReferenceProfileProvider(new TwigJsonReferenceProfilePinSource(new TwigConfiguration()));
        return InitCommandTestFixture.CreateInitCommand(_registry, profileRegistry ?? new ReferenceProfileRegistrySource(provider), metadata, _paths,
            new OutputFormatterFactory(new HumanOutputFormatter()), new HintEngine(new DisplayConfig { Hints = false }));
    }
}
