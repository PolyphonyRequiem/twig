using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Validates <see cref="ProcessConfigExtensions.ComputeChildProgress"/> behavior
/// as exercised by <see cref="Twig.Commands.StatusCommand"/>. Covers NFR-03 matrix:
/// Basic, Agile, custom state, and null-provider fallback.
/// </summary>
public sealed class StatusCommandProgressTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Basic process: To Do / Doing / Done → 1 done of 3
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Basic_ToDo_Doing_Done_OnlyDoneCountedAsComplete()
    {
        var provider = CreateProvider(ProcessConfigBuilder.Basic());
        var children = new[]
        {
            CreateWorkItem(1, "Task", "To Do"),
            CreateWorkItem(2, "Task", "Doing"),
            CreateWorkItem(3, "Task", "Done"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(1);
        result.Value.Total.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agile process: New / Active / Resolved / Closed → 2 done of 4
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Agile_NewActiveResolvedClosed_ResolvedAndClosedCountedAsComplete()
    {
        var provider = CreateProvider(ProcessConfigBuilder.Agile());
        var children = new[]
        {
            CreateWorkItem(1, "User Story", "New"),
            CreateWorkItem(2, "User Story", "Active"),
            CreateWorkItem(3, "User Story", "Resolved"),
            CreateWorkItem(4, "User Story", "Closed"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(2);
        result.Value.Total.ShouldBe(4);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Custom state "UAT" — not in standard entries or fallback
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CustomState_UAT_NotInConfigOrFallback_NotCountedAsDone()
    {
        var provider = CreateProvider(ProcessConfigBuilder.Agile());
        var children = new[]
        {
            CreateWorkItem(1, "Task", "UAT"),
            CreateWorkItem(2, "Task", "Closed"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(1); // Only "Closed" counted
        result.Value.Total.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Null provider → fallback heuristic still counts correctly
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NullProvider_FallsBackToHeuristic_DoneIsCounted()
    {
        IProcessConfigurationProvider? provider = null;
        var children = new[]
        {
            CreateWorkItem(1, "Task", "Done"),
            CreateWorkItem(2, "Task", "Doing"),
            CreateWorkItem(3, "Task", "New"),
        };

        var result = provider.ComputeChildProgress(children);

        result.ShouldNotBeNull();
        result.Value.Done.ShouldBe(1);
        result.Value.Total.ShouldBe(3);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static IProcessConfigurationProvider CreateProvider(ProcessConfiguration config)
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);
        return provider;
    }

    private static WorkItem CreateWorkItem(int id, string typeName, string state) =>
        new WorkItemBuilder(id, $"Test {id}")
            .AsType(WorkItemType.Parse(typeName).Value)
            .InState(state)
            .Build();
}
