using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Plan;

/// <summary>
/// AB#1169 / D3: a rule set that cannot be loaded must hold the batch, never permit it.
/// </summary>
public sealed class PlanProcessRuleGateLoadFailureTests
{
    [Fact]
    public async Task NullProvider_RequiresRefresh_InsteadOfPermitAll()
    {
        var item = new WorkItemBuilder(5, "x").AsTask().Build();

        var outcome = await new PlanProcessRuleGate(rules: null).EvaluateAsync(
            new BatchOperation
            {
                Id = "b",
                WorkItemId = 5,
                ExpectedRevision = 1,
                Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
            },
            item,
            CancellationToken.None);

        outcome.IsOk.ShouldBeFalse();
        outcome.IsRefreshRequired.ShouldBeTrue();
        outcome.Message.ShouldNotBeNull().ShouldContain("No process-rule provider");
    }

    [Fact]
    public async Task ExplicitEmptyProvider_Permits()
    {
        var item = new WorkItemBuilder(5, "x").AsTask().Build();

        var outcome = await new PlanProcessRuleGate(EmptyProcessRuleProvider.Instance).EvaluateAsync(
            new BatchOperation
            {
                Id = "b",
                WorkItemId = 5,
                ExpectedRevision = 1,
                Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
            },
            item,
            CancellationToken.None);

        outcome.IsOk.ShouldBeTrue();
    }

    [Fact]
    public async Task UnknownWorkItemType_RequiresRefresh_InsteadOfPermitAll()
    {
        var provider = Substitute.For<IProcessRuleProvider>();
        var item = new WorkItemBuilder(5, "x").AsType(default).Build();

        var outcome = await new PlanProcessRuleGate(provider).EvaluateAsync(
            new BatchOperation
            {
                Id = "b",
                WorkItemId = 5,
                ExpectedRevision = 1,
                Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
            },
            item,
            CancellationToken.None);

        outcome.IsOk.ShouldBeFalse();
        outcome.IsRefreshRequired.ShouldBeTrue();
        outcome.Message.ShouldNotBeNull().ShouldContain("type of work item 5 is unknown");
        await provider.DidNotReceive().GetRulesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullRuleSet_RequiresRefresh_InsteadOfPermitAll()
    {
        var provider = Substitute.For<IProcessRuleProvider>();
        provider.GetRulesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProcessRule>>(null!));
        var item = new WorkItemBuilder(5, "x").AsTask().Build();

        var outcome = await new PlanProcessRuleGate(provider).EvaluateAsync(
            new BatchOperation
            {
                Id = "b",
                WorkItemId = 5,
                ExpectedRevision = 1,
                Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
            },
            item,
            CancellationToken.None);

        outcome.IsOk.ShouldBeFalse();
        outcome.IsRefreshRequired.ShouldBeTrue();
        outcome.Message.ShouldNotBeNull().ShouldContain("returned no rule set");
    }

    [Fact]
    public async Task RuleLoadFailure_RequiresRefresh_InsteadOfPermitAll()
    {
        var provider = Substitute.For<IProcessRuleProvider>();
        provider.GetRulesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("offline"));
        var item = new WorkItemBuilder(5, "x").AsTask().Build();

        var outcome = await new PlanProcessRuleGate(provider).EvaluateAsync(
            new BatchOperation
            {
                Id = "b",
                WorkItemId = 5,
                ExpectedRevision = 1,
                Fields = new Dictionary<string, string?> { ["System.State"] = "Done" },
            },
            item,
            CancellationToken.None);

        outcome.IsOk.ShouldBeFalse();
        outcome.IsRefreshRequired.ShouldBeTrue();
        outcome.Message.ShouldNotBeNull().ShouldContain("could not be loaded");
    }
}
