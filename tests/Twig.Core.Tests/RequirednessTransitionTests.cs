using Shouldly;
using Twig.Core;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class RequirednessTransitionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnknownPreRequirednessMattersOnlyForAnUnsuppliedCompletionTransition(bool close, bool supplied)
    {
        var connection = new Connection(new("github"), "https://api.github.com", "immutable", "actor");
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "opaque-node");
        var before = FieldValues.Empty.Set("state", FieldValue.String("OPEN"))
            .Set("value", supplied ? FieldValue.Null : FieldValue.String("known effective value"));
        before.ContainsKey("trigger").ShouldBeFalse("the pre-condition operand must really be absent, not null or observed");
        var source = new WorkItem(key, "native", before, new(key, "observed"));
        var when = new[] { new RuleCondition(ConditionKind.Equal, "trigger", FieldValue.String("on")) };
        var rules = new List<FieldRule> { new("value", RuleAction.Required, when) };
        if (supplied) rules.Add(new("value", RuleAction.Supplied, when));
        var schema = new ProcessSchema(connection, [new("native", "state",
            [new("state", FieldKind.Text), new("trigger", FieldKind.Text), new("value", FieldKind.Text)],
            [new("OPEN", StateCategory.Proposed), new("CLOSED", StateCategory.Completed)], [new("OPEN", "CLOSED")], rules)], []);
        var fields = FieldValues.Empty.Set("trigger", FieldValue.String("on"));
        if (close) fields = fields.Set("state", FieldValue.String("CLOSED"));
        var proposal = new Proposal(connection, schema.Fingerprint, 0,
            [new("edit", OpKind.Batch, key, source.Revision, fields, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        var errors = Model.Validate(proposal, proposal.Digest, schema, new(0, [source]),
            new([OpKind.Batch], [], [], false, false));
        if (close && !supplied)
        {
            errors.ShouldContain(error => error.Code == ErrorCode.Unavailable);
            Model.CloseGates(schema, source, fields).IsSuccess.ShouldBeFalse();
        }
        else errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void PreexistingRequirednessIsNotANewCompletionGate(bool populated, bool completionGate, bool includeGate)
    {
        var connection = new Connection(new("github"), "https://api.github.com", "immutable", "actor");
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "opaque-node");
        var before = FieldValues.Empty.Set("state", FieldValue.String("OPEN"))
            .Set("title", populated ? FieldValue.String("present") : FieldValue.Null)
            .Set("gate", FieldValue.String("already present"));
        var source = new WorkItem(key, "native", before, new(key, "opaque-observation"));
        var rules = new List<FieldRule> { new("title", RuleAction.Required, []) };
        if (completionGate) rules.Add(new("gate", RuleAction.Required, [new(ConditionKind.Equal, "state", FieldValue.String("CLOSED"))]));
        var schema = new ProcessSchema(connection, [new("native", "state",
            [new("title", FieldKind.Text), new("state", FieldKind.Text), new("gate", FieldKind.Text)],
            [new("OPEN", StateCategory.Proposed), new("CLOSED", StateCategory.Completed)], [new("OPEN", "CLOSED")], rules)], []);
        var changes = FieldValues.Empty.Set("state", FieldValue.String("CLOSED"));
        if (includeGate) changes = changes.Set("gate", FieldValue.String("authored"));
        var proposal = new Proposal(connection, schema.Fingerprint, 0,
            [new("close", OpKind.Batch, key, source.Revision, changes, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        var gates = Model.CloseGates(schema, source, changes);
        gates.IsSuccess.ShouldBeTrue();
        gates.Value.ShouldBe(completionGate ? ["gate"] : []);
        var errors = Model.Validate(proposal, proposal.Digest, schema, new(0, [source]),
            new([OpKind.Batch], [], [], false, false));
        if (populated && !completionGate) errors.ShouldBeEmpty();
        if (!populated) errors.ShouldContain(error => error.Message.Contains("no effective value", StringComparison.Ordinal));
        if (completionGate && !includeGate) errors.ShouldContain(error => error.Message.Contains("same batch", StringComparison.Ordinal));
        if (completionGate) errors.ShouldContain(error => error.Code == ErrorCode.Unsupported);
    }
}
