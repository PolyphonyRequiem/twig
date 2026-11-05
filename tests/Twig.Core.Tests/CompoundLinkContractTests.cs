using Shouldly;
using Twig.Core;
using Xunit;
using Model = Twig.Core.Core;
using System.Text.Json;

namespace Twig.Core.Tests;

public sealed class CompoundLinkContractTests
{
    [Theory]
    [InlineData("definition-readonly")]
    [InlineData("rule-readonly")]
    [InlineData("ambiguous-supplier")]
    [InlineData("constrained-empty")]
    [InlineData("null-supplier")]
    public void SupplierProjectionPreservesActualReadOnlyRequiredAmbiguityAndConstraintSemantics(string scenario)
    {
        var rules = new List<FieldRule> { new("title", RuleAction.Required, []),
            new("title", RuleAction.Supplied, [], [scenario == "null-supplier" ? FieldValue.Null : FieldValue.String("default")]) };
        if (scenario == "rule-readonly") rules.Add(new("title", RuleAction.ReadOnly, []));
        if (scenario == "ambiguous-supplier") rules.Add(new("title", RuleAction.Supplied, [], [FieldValue.String("different")]));
        if (scenario == "constrained-empty")
        {
            rules.Add(new("title", RuleAction.Allowed, [], [FieldValue.String("one")]));
            rules.Add(new("title", RuleAction.Allowed, [], [FieldValue.String("two")]));
        }
        var definitions = Schema.Types[0].Fields.Select(field => field.Reference == "title"
            ? field with { ReadOnly = scenario == "definition-readonly" } : field);
        var schema = new ProcessSchema(Connection, [new("type", "state", definitions, Schema.Types[0].States, [], rules)], Schema.Relations);
        var source = new WorkItem(Item, "type", Fields, new(Item, "1"), true);
        var changes = FieldValues.Empty.Set("title", FieldValue.String("one"));
        var op = new Op("edit", OpKind.Batch, Item, source.Revision, changes);
        var proposal = new Proposal(Connection, schema.Fingerprint, 0, [op]);
        var snapshot = new StoreSnapshot(0, [source], proposals: [proposal], journal: [JournalEntry.Planned(proposal.Digest, 0)]);
        var actual = Model.ValidateEffect(proposal, 0, schema, snapshot, new([OpKind.Batch], [], [], true, true));
        var evaluated = Model.EvaluateEditorFields(schema, source, changes, true).Value;
        var title = evaluated.Fields.Single(field => field.Definition.Reference == "title");
        title.ReadOnly.ShouldBe(scenario is "definition-readonly" or "rule-readonly");
        title.Required.ShouldBe(scenario == "null-supplier");
        evaluated.Errors.Select(error => error.Code).ShouldBe(actual.Select(error => error.Code));
        if (scenario == "constrained-empty") { title.HasAllowedConstraint.ShouldBeTrue(); title.Allowed.ShouldBeEmpty(); }
        if (scenario == "ambiguous-supplier") actual.ShouldContain(error => error.Code == ErrorCode.Unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedOnlyAuthoringRemainsEditableUnderTheActualCoreEffectRules(bool seed)
    {
        var key = seed ? ItemKey.Local(Guid.NewGuid()) : Item;
        var changes = FieldValues.Empty.Set("title", FieldValue.String("explicit authored value"));
        if (seed) changes = changes.Set("state", FieldValue.String("Open"));
        var schema = new ProcessSchema(Connection, [new("type", "state", Schema.Types[0].Fields, Schema.Types[0].States, [],
            [new("title", RuleAction.Required, []),
             new("title", RuleAction.Supplied, [], [FieldValue.String("native default")])])], Schema.Relations);
        var source = new WorkItem(key, "type", seed ? FieldValues.Empty.Set("title", FieldValue.Null).Set("state", FieldValue.Null) : Fields,
            seed ? null : new(key, "1"), true);
        var op = new Op("authored", seed ? OpKind.Create : OpKind.Batch, key, source.Revision, changes, seed ? "type" : null,
            concurrency: seed ? ConcurrencyMode.ObservedNonAtomic : ConcurrencyMode.ExactRevision);
        var proposal = new Proposal(Connection, schema.Fingerprint, 0, [op]);
        var snapshot = new StoreSnapshot(0, seed ? [] : [source],
            drafts: seed ? [new(key, "type", null, changes)] : [], proposals: [proposal],
            journal: [JournalEntry.Planned(proposal.Digest, 0)]);
        Model.ValidateEffect(proposal, 0, schema, snapshot, new([OpKind.Create, OpKind.Batch], [], [], true, true)).ShouldBeEmpty();
        var evaluated = Model.EvaluateEditorFields(schema, source, changes, true);
        evaluated.IsSuccess.ShouldBeTrue(evaluated.IsSuccess ? "" : evaluated.Error.Message);
        var field = evaluated.Value.Fields.Single(value => value.Definition.Reference == "title");
        field.Required.ShouldBeFalse("the active supplier still satisfies its required pairing");
        field.ReadOnly.ShouldBeFalse("Supplied is not a native ReadOnly rule");
        evaluated.Value.Errors.ShouldBeEmpty();
        evaluated.Value.States.ShouldContain("Open", "seed guidance must not invent a null-to-state transition requirement");
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("incomplete")]
    [InlineData("inverse-association")]
    [InlineData("missing-remove")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    public void EveryCompoundDeltaRequiresCapabilityDirectionCompletenessAndExactBaseline(string defect)
    {
        var source = new WorkItem(Item, "type", Fields, new(Item, "1"), true);
        var target = new WorkItem(Other, "type", Fields, new(Other, "1"), defect != "incomplete");
        var other = defect == "foreign" ? ItemKey.Remote(Connection.Provider, "foreign-scope", "43") : Other;
        var delta = new CreationLink(other, "related", defect != "inverse-association", defect == "missing-remove");
        var proposal = new Proposal(Connection, Schema.Fingerprint, 0,
            [new("edit", OpKind.Batch, Item, source.Revision, FieldValues.Empty.Set("title", FieldValue.String("new")),
                links: defect == "duplicate" ? [delta, delta] : [delta])]);
        var capabilities = new ProviderCapabilities([OpKind.Batch, OpKind.Relate, OpKind.Unrelate], ["related"], [], true, true)
            { AtomicRelationshipBatches = defect != "capability" };
        Model.Validate(proposal, proposal.Digest, Schema, new(0, [source, target]), capabilities).ShouldNotBeEmpty();
    }

    [Fact]
    public void CreateStillRejectsRemovalAndRemoteDraftStillRejectsRelationshipPayload()
    {
        var seed = ItemKey.Local(Guid.NewGuid());
        var delta = new CreationLink(Other, "related", true, true);
        var draft = new Draft(seed, "type", null, Fields, links: [delta]);
        var proposal = new Proposal(Connection, Schema.Fingerprint, 0,
            [new("create", OpKind.Create, seed, fields: Fields, type: "type",
                concurrency: ConcurrencyMode.ObservedNonAtomic, links: [delta])]);
        Model.Validate(proposal, proposal.Digest, Schema,
            new(0, [new(Other, "type", Fields, new(Other, "1"), true)], drafts: [draft]),
            new([OpKind.Create], ["related"], [], true, true)).ShouldContain(error => error.Code == ErrorCode.InvalidInput);
        Should.Throw<ArgumentException>(() => new Draft(Item, "type", new(Item, "type", Fields, new(Item, "1")), Fields, links: [delta]));
    }

    [Fact]
    public void AddDefaultsPreserveCanonicalTupleAndRemovalChangesOnlyItsExplicitDelta()
    {
        Proposal WithLink(CreationLink link) => new(Connection, Schema.Fingerprint, 0,
            [new("edit", OpKind.Batch, Item, new(Item, "1"), FieldValues.Empty.Set("title", FieldValue.String("new")), links: [link])]);
        var legacy = WithLink(new(Other, "related", true));
        var explicitDefault = WithLink(new(Other, "related", true, false));
        var removal = WithLink(new(Other, "related", true, true));
        Model.ProposalBytes(explicitDefault).ShouldBe(Model.ProposalBytes(legacy));
        using var added = JsonDocument.Parse(Model.ProposalBytes(legacy));
        using var removed = JsonDocument.Parse(Model.ProposalBytes(removal));
        added.RootElement.GetProperty("operations")[0].GetProperty("links")[0].GetArrayLength().ShouldBe(3);
        removed.RootElement.GetProperty("operations")[0].GetProperty("links")[0].GetArrayLength().ShouldBe(4);
        removal.Digest.ShouldNotBe(legacy.Digest);
        Proposal.FormatVersion.ShouldBe(3);
    }

    [Fact]
    public void EvaluatedEditorFieldsReuseConditionalRequiredAllowedAndReadOnlyRules()
    {
        var rules = new FieldRule[] {
            new("title", RuleAction.Required, [new(ConditionKind.Equal, "state", FieldValue.String("Open"))]),
            new("title", RuleAction.Allowed, [], [FieldValue.String("old"), FieldValue.String("new")]),
            new("title", RuleAction.ReadOnly, [new(ConditionKind.Changed, "state")]),
        };
        var schema = new ProcessSchema(Connection, [new("type", "state", Schema.Types[0].Fields,
            [new("Open", StateCategory.Proposed), new("Closed", StateCategory.Completed)],
            [new("Open", "Closed")], rules)], Schema.Relations);
        var source = new WorkItem(Item, "type", Fields, new(Item, "1"), true);
        var result = Model.EvaluateEditorFields(schema, source, FieldValues.Empty, true);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Fields.Single(field => field.Definition.Reference == "title").Required.ShouldBeTrue();
        result.Value.Fields.Single(field => field.Definition.Reference == "title").Allowed.Length.ShouldBe(2);
        var closed = Model.EvaluateEditorFields(schema, source, FieldValues.Empty.Set("state", FieldValue.String("Closed")), true).Value;
        closed.Fields.Single(field => field.Definition.Reference == "title").ReadOnly.ShouldBeTrue();
        closed.States.ShouldBe(["Open", "Closed"]);
    }
    private static readonly Connection Connection = new(new("ado"), "collection", "project", "actor");
    private static readonly ItemKey Item = ItemKey.Remote(Connection.Provider, Connection.ResourceScope, "42");
    private static readonly ItemKey Other = ItemKey.Remote(Connection.Provider, Connection.ResourceScope, "43");
    private static readonly FieldValues Fields = FieldValues.Empty.Set("title", FieldValue.String("old")).Set("state", FieldValue.String("Open"));
    private static readonly ProcessSchema Schema = new(Connection,
        [new("type", "state", [new("title", FieldKind.Text), new("state", FieldKind.Text)],
            [new("Open", StateCategory.Proposed)], [], [])],
        [new("related", RelationShape.Association, false)]);

    [Fact]
    public void CompoundNativeBatchUsesExistingFieldAndRelationshipCapabilities()
    {
        var source = new WorkItem(Item, "type", Fields, new(Item, "1"), true);
        var target = new WorkItem(Other, "type", Fields, new(Other, "1"), true);
        var snapshot = new StoreSnapshot(0, [source, target]);
        var op = new Op("edit", OpKind.Batch, Item, source.Revision,
            FieldValues.Empty.Set("title", FieldValue.String("new")), links: [new(Other, "related", true)]);
        var proposal = new Proposal(Connection, Schema.Fingerprint, snapshot.Version, [op]);
        var capabilities = new ProviderCapabilities([OpKind.Batch, OpKind.Relate, OpKind.Unrelate], ["related"], [], true, true)
            { AtomicRelationshipBatches = true };
        Model.Validate(proposal, proposal.Digest, Schema, snapshot, capabilities).ShouldBeEmpty();
    }

    [Fact]
    public void ApprovedRemovalConstructorIsAdditiveAndLegacyConstructorRemains()
    {
        typeof(CreationLink).GetConstructor([typeof(ItemKey), typeof(string), typeof(bool)])!.ShouldNotBeNull();
        typeof(CreationLink).GetConstructor([typeof(ItemKey), typeof(string), typeof(bool), typeof(bool)]).ShouldNotBeNull();
        Proposal.FormatVersion.ShouldBe(3);
    }
}
