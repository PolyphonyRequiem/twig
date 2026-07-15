using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
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

    [Fact]
    public async Task ExecuteAsync_ChainedTransition_ReevaluatesDependentFieldsForEveryHop()
    {
        var item = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("To Do")
            .WithField("Custom.Substate", "Ready")
            .Build();
        var doing = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("Doing")
            .WithField("Custom.Substate", "Generated")
            .Build();
        var done = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("Done")
            .Build();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChanges = Substitute.For<IPendingChangeStore>();
        var processConfig = Substitute.For<IProcessConfigurationProvider>();
        var ruleProvider = new FakeProcessRuleProvider(
        [
            new ProcessRule(
                Conditions: [new RuleCondition("$when", "System.State", "Doing")],
                Actions: [new RuleAction("$disallowValue", "Custom.Substate", "Ready")],
                IsDisabled: false),
            new ProcessRule(
                Conditions: [new RuleCondition("$when", "System.State", "Done")],
                Actions: [new RuleAction("$disallowValue", "Custom.Substate", "Generated")],
                IsDisabled: false),
        ]);

        processConfig.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        adoService.PatchAsync(
                42,
                Arg.Any<IReadOnlyList<FieldChange>>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(11, 12);
        adoService.PatchAsync(
                42,
                Arg.Is<IReadOnlyList<FieldChange>>(changes =>
                    changes.Count == 1 &&
                    changes[0] == new FieldChange("System.State", "To Do", "Done")),
                10,
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException(
                "TF401320: state transition from 'To Do' to 'Done' is not allowed"));
        adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(doing, done);
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

        var outcome = await workflow.ExecuteAsync(item, "Done", 10);

        var success = outcome.ShouldBeOfType<StateTransitionOutcome.Succeeded>();
        success.Path.ShouldBe(["To Do", "Doing", "Done"]);
        await adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(changes =>
                changes.Count == 2 &&
                changes[0] == new FieldChange("System.State", "To Do", "Doing") &&
                changes[1] == new FieldChange("Custom.Substate", "Ready", null)),
            10,
            Arg.Any<CancellationToken>());
        await adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(changes =>
                changes.Count == 2 &&
                changes[0] == new FieldChange("System.State", "Doing", "Done") &&
                changes[1] == new FieldChange("Custom.Substate", "Generated", null)),
            11,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ConflictRetry_ReevaluatesAgainstFreshRemoteFields()
    {
        var item = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("To Do")
            .WithField("Custom.Substate", "Ready")
            .Build();
        var fresh = new WorkItemBuilder(42, "issue")
            .AsIssue()
            .InState("To Do")
            .WithField("Custom.Substate", "Valid")
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
                Conditions: [new RuleCondition("$when", "System.State", "Doing")],
                Actions: [new RuleAction("$disallowValue", "Custom.Substate", "Ready")],
                IsDisabled: false),
        ]);

        processConfig.GetConfiguration().Returns(ProcessConfigBuilder.Basic());
        adoService.PatchAsync(
                42,
                Arg.Is<IReadOnlyList<FieldChange>>(changes =>
                    changes.Count == 2 &&
                    changes[0] == new FieldChange("System.State", "To Do", "Doing") &&
                    changes[1] == new FieldChange("Custom.Substate", "Ready", null)),
                10,
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(0));
        adoService.PatchAsync(
                42,
                Arg.Any<IReadOnlyList<FieldChange>>(),
                0,
                Arg.Any<CancellationToken>())
            .Returns(11);
        adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(fresh, updated);
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
        await adoService.Received(1).PatchAsync(
            42,
            Arg.Is<IReadOnlyList<FieldChange>>(changes =>
                changes.Count == 1 &&
                changes[0] == new FieldChange("System.State", "To Do", "Doing")),
            0,
            Arg.Any<CancellationToken>());
    }

    private sealed class FakeProcessRuleProvider(IReadOnlyList<ProcessRule> rules) : IProcessRuleProvider
    {
        public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(
            string workItemTypeName,
            CancellationToken ct = default) =>
            Task.FromResult(rules);
    }
}
