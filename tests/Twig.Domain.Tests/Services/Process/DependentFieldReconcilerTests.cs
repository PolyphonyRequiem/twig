using Shouldly;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services.Process;

public sealed class DependentFieldReconcilerTests
{
    [Fact]
    public void GetSafeClears_DisallowedOptionalValue_ReturnsFieldClear()
    {
        var rules = new[]
        {
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
        };
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Substate"] = "Ready",
        };

        var result = DependentFieldReconciler.GetSafeClears(rules, "To Do", "Doing", fields);

        result.ShouldBe(
        [
            new FieldChange("Custom.Substate", "Ready", null),
        ]);
    }

    [Fact]
    public void GetSafeClears_DisallowedRequiredValue_DoesNotClearField()
    {
        var rules = new[]
        {
            new ProcessRule(
                Conditions:
                [
                    new RuleCondition("$when", "System.State", "Doing"),
                ],
                Actions:
                [
                    new RuleAction("$disallowValue", "Custom.Substate", "Ready"),
                    new RuleAction("$makeRequired", "Custom.Substate", null),
                ],
                IsDisabled: false),
        };
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Substate"] = "Ready",
        };

        var result = DependentFieldReconciler.GetSafeClears(rules, "To Do", "Doing", fields);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetSafeClears_RuleMatchesSourceAndTargetState_ReturnsFieldClear()
    {
        var rules = new[]
        {
            new ProcessRule(
                Conditions:
                [
                    new RuleCondition("$whenWas", "System.State", "To Do"),
                    new RuleCondition("$when", "System.State", "Doing"),
                ],
                Actions:
                [
                    new RuleAction("$disallowValue", "Custom.Substate", "Ready"),
                ],
                IsDisabled: false),
        };
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Substate"] = "Ready",
        };

        var result = DependentFieldReconciler.GetSafeClears(rules, "To Do", "Doing", fields);

        result.ShouldBe(
        [
            new FieldChange("Custom.Substate", "Ready", null),
        ]);
    }

    [Fact]
    public void GetSafeClears_ServerWritesDependentField_DoesNotPreemptServerRule()
    {
        var rules = new[]
        {
            new ProcessRule(
                Conditions:
                [
                    new RuleCondition("$when", "System.State", "Doing"),
                ],
                Actions:
                [
                    new RuleAction("$disallowValue", "Custom.Substate", "Ready"),
                    new RuleAction("$copyValue", "Custom.Substate", "Started"),
                ],
                IsDisabled: false),
        };
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Substate"] = "Ready",
        };

        var result = DependentFieldReconciler.GetSafeClears(rules, "To Do", "Doing", fields);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetSafeClears_ValueAllowedInTargetState_PreservesField()
    {
        var rules = new[]
        {
            new ProcessRule(
                Conditions:
                [
                    new RuleCondition("$when", "System.State", "Doing"),
                ],
                Actions:
                [
                    new RuleAction("$disallowValue", "Custom.Substate", "Blocked"),
                ],
                IsDisabled: false),
        };
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.Substate"] = "Ready",
        };

        var result = DependentFieldReconciler.GetSafeClears(rules, "To Do", "Doing", fields);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetSafeClears_NoDependentRules_ReturnsNoChanges()
    {
        var result = DependentFieldReconciler.GetSafeClears(
            Array.Empty<ProcessRule>(),
            "To Do",
            "Doing",
            new Dictionary<string, string?>());

        result.ShouldBeEmpty();
    }
}
