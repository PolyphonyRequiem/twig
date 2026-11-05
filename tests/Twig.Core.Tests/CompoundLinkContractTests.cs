using Shouldly;
using Twig.Core;
using Xunit;
using Model = Twig.Core.Core;
using System.Text.Json;

namespace Twig.Core.Tests;

public sealed class CompoundLinkContractTests
{
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
