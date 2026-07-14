using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.Mutation;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Mutation;

public sealed class StateTransitionWorkflowTests
{
    [Fact]
    public async Task ExecuteAsync_IncompatibleOptionalField_PatchesStateAndClearAtomically()
    {
        var item = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("To Do")
            .WithField("Custom.Substate", "Ready")
            .Build();
        var updated = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("Doing")
            .Build();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChanges = Substitute.For<IPendingChangeStore>();
        var processConfig = Substitute.For<IProcessConfigurationProvider>();
        var ruleProvider = new FakeProcessRuleProvider(
        [
            new ProcessRule(
                Conditions:
                [
                    new RuleCondition("$when", "System.State", "Doing"),
                ],
                Actions:
                [
                    new RuleAction("$disallowValue", "Custom.Substate", "Ready"),
                ],
                IsDisabled: false),
        ]);
        IReadOnlyList<FieldChange>? sentChanges = null;

        processConfig.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        adoService.PatchAsync(42, Arg.Do<IReadOnlyList<FieldChange>>(changes => sentChanges = changes), 10, Arg.Any<CancellationToken>())
            .Returns(11);
        adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(updated);
        pendingChanges.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        var workflow = new StateTransitionWorkflow(
            workItemRepo,
            adoService,
            pendingChanges,
            processConfig,
            parentPropagation: null,
            promptStateWriter: null,
            processRuleProvider: ruleProvider);

        var outcome = await workflow.ExecuteAsync(item, "Doing", 10);

        outcome.ShouldBeOfType<StateTransitionOutcome.Succeeded>();
        sentChanges.ShouldBe(
        [
            new FieldChange("System.State", "To Do", "Doing"),
            new FieldChange("Custom.Substate", "Ready", null),
        ]);
    }

    private sealed class FakeProcessRuleProvider(IReadOnlyList<ProcessRule> rules) : IProcessRuleProvider
    {
        public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(
            string workItemTypeName,
            CancellationToken ct = default) =>
            Task.FromResult(rules);
    }
}
