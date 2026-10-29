using System.Collections.Immutable;
using Shouldly;
using Twig.Core;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class WorkspaceInvariantTests
{
    private static readonly ProviderId Provider = new("test");
    private static readonly Connection Connection = new(Provider, "scope", "target", "principal");
    private static ItemKey Key(string id) => ItemKey.Remote(Provider, "scope", id);
    private static WorkItem Item(string id, string type = "child", bool complete = true, string? value = "yes")
        => new(Key(id), type, value is null ? FieldValues.Empty
            : new([KeyValuePair.Create("match", FieldValue.String(value))]), new Revision(Key(id), "r"), complete);
    private static readonly ProcessSchema Schema = new(Connection,
        [new("child", "state", [new("state", FieldKind.Text)], [new("open", StateCategory.InProgress)], [], []),
            new("allowed", "state", [new("state", FieldKind.Text)], [new("open", StateCategory.InProgress)], [], []),
            new("disallowed", "state", [new("state", FieldKind.Text)], [new("open", StateCategory.InProgress)], [], [])],
        [new("parent-child", RelationShape.Hierarchy, true), new("dependency", RelationShape.Dependency, true),
            new("related", RelationShape.Association, false)]);

    [Fact]
    public void BenchIsASelectorUnionNotAStoredResultOrSyncBoundary()
    {
        var root = Item("root", value: "no");
        var child = Item("child");
        var other = Item("other");
        var edge = new Relationship(root.Key, child.Key, "parent-child", RelationshipOrigin.Provider);
        var bench = new Bench(Guid.NewGuid(), "chosen",
        [
            new(SelectorKind.Pin, other.Key),
            new(SelectorKind.Subtree, root.Key, hierarchyRelation: "parent-child"),
            new(SelectorKind.Query, query: new(QueryKind.Equal, "match", FieldValue.String("yes"))),
            new(SelectorKind.Pin, other.Key),
        ]);
        var snapshot = new StoreSnapshot(0, [root, child, other], [edge]);
        var resolved = Rules.ResolveBench(bench, snapshot, Schema);
        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value.ToHashSet().SetEquals([root.Key, child.Key, other.Key]).ShouldBeTrue();
        bench.Selectors.Length.ShouldBe(4);
        snapshot.Version.ShouldBe(0);
        var reordered = new Bench(bench.Id, bench.Name, bench.Selectors.Reverse());
        Rules.ResolveBench(reordered, snapshot, Schema).Value.ShouldBe(resolved.Value);
    }

    [Fact]
    public void BenchDoesNotTreatMissingFieldsOrIncompleteSubtreesAsEmptyResults()
    {
        var bench = new Bench(Guid.NewGuid(), "query", [new(SelectorKind.Query,
            query: new(QueryKind.Equal, "match", FieldValue.String("yes")))]);
        Rules.ResolveBench(bench, new(0, [Item("unknown", value: null)]), Schema).Error.Code.ShouldBe(ErrorCode.Unavailable);
        bench = new(bench.Id, "subtree", [new(SelectorKind.Subtree, Key("root"), hierarchyRelation: "parent-child")]);
        Rules.ResolveBench(bench, new(0, [Item("root", complete: false)]), Schema).Error.Code.ShouldBe(ErrorCode.Unavailable);
        var cycle = new[]
        {
            new Relationship(Key("root"), Key("child"), "parent-child", RelationshipOrigin.Provider),
            new Relationship(Key("child"), Key("root"), "parent-child", RelationshipOrigin.Local),
        };
        Rules.ResolveBench(bench, new(0, [Item("root"), Item("child")], cycle), Schema).Error.Code.ShouldBe(ErrorCode.Conflict);
        var missingPin = new Bench(Guid.NewGuid(), "missing", [new(SelectorKind.Pin, Key("absent"))]);
        Rules.ResolveBench(missingPin, new(0), Schema).Error.Code.ShouldBe(ErrorCode.Unavailable);
    }

    [Fact]
    public void QueryUsesThreeValuedEvidenceInsteadOfBroadeningUnknownScope()
    {
        var unknown = new Query(QueryKind.Equal, "unobserved", FieldValue.Null);
        var yes = new Query(QueryKind.Equal, "match", FieldValue.String("yes"));
        var no = new Query(QueryKind.Equal, "match", FieldValue.String("no"));
        Rules.Matches(new(QueryKind.And, children: [unknown, no]), Item("1")).Value.ShouldBeFalse();
        Rules.Matches(new(QueryKind.Or, children: [unknown, yes]), Item("1")).Value.ShouldBeTrue();
        Rules.Matches(new(QueryKind.And, children: [unknown, yes]), Item("1")).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Rules.Matches(new(QueryKind.Not, children: [unknown]), Item("1")).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Should.Throw<ArgumentException>(() => new Query(QueryKind.Equal, "match"));
        Should.Throw<ArgumentException>(() => new Query((QueryKind)900));
    }

    [Fact]
    public void NativeAndLocalRelationshipCapabilitiesAreNotInterchangeable()
    {
        var capabilities = new ProviderCapabilities([OpKind.Relate], ["parent-child"], ["related"], true, true);
        var snapshot = new StoreSnapshot(0, [Item("a"), Item("b")]);
        var op = new Op("link", OpKind.Relate, Key("a"), new Revision(Key("a"), "r"), other: Key("b"), relation: "related");
        var proposal = new Proposal(Connection, Schema.Fingerprint, 0, [op]);
        Rules.Validate(proposal, proposal.Digest, Schema, snapshot, capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Unsupported);
        op = new("link", OpKind.Relate, Key("a"), other: Key("b"), relation: "related", origin: RelationshipOrigin.Local);
        proposal = new(Connection, Schema.Fingerprint, 0, [op]);
        Rules.Validate(proposal, proposal.Digest, Schema, snapshot, capabilities).ShouldBeEmpty();
        op = new("link", OpKind.Relate, Key("a"), new Revision(Key("a"), "r"), other: Key("b"), relation: "parent-child");
        proposal = new(Connection, Schema.Fingerprint, 0, [op]);
        Rules.Validate(proposal, proposal.Digest, Schema, snapshot, capabilities).ShouldBeEmpty();
    }

    [Fact]
    public void PolicyBlocksNewViolationsButReportsUnchangedHistoricalOnes()
    {
        var policy = new Policy([new("parent", RuleKind.ParentType, "child", ["allowed"])]);
        var items = new[] { Item("parent", "disallowed"), Item("child") };
        var edge = new Relationship(Key("parent"), Key("child"), "parent-child", RelationshipOrigin.Provider);
        var before = new StoreSnapshot(0, items);
        var after = new StoreSnapshot(1, items, [edge]);
        var added = Rules.Evaluate(policy, before, after, Schema).ShouldHaveSingleItem();
        added.Kind.ShouldBe(FindingKind.Violation);
        added.Blocks.ShouldBeTrue();
        var historical = Rules.Evaluate(policy, after, after, Schema).ShouldHaveSingleItem();
        historical.Kind.ShouldBe(FindingKind.Violation);
        historical.Blocks.ShouldBeFalse();
        historical.Evidence.ShouldBe([edge]);
        var changedParent = new StoreSnapshot(0, [Item("parent", "allowed"), Item("child")], [edge]);
        Rules.Evaluate(policy, changedParent, after, Schema).ShouldHaveSingleItem().Blocks.ShouldBeTrue();
    }

    [Fact]
    public void LocalRelationshipPreconditionsSurviveImportAndDetectMovedTombstones()
    {
        var capabilities = new ProviderCapabilities([], [], ["related"], false, false);
        var tombstone = new Relationship(Key("a"), Key("b"), "related", RelationshipOrigin.Local, 2, Deleted: true);
        var op = new Op("revive", OpKind.Relate, Key("b"), other: Key("a"), relation: "related",
            origin: RelationshipOrigin.Local, expectedLocalVersion: 2);
        var proposal = new Proposal(Connection, Schema.Fingerprint, 0, [op]);
        var planned = JournalEntry.Planned(proposal.Digest, 0);
        var store = new StoreSnapshot(1, [Item("a"), Item("b")], [tombstone], proposals: [proposal], journal: [planned]);
        Rules.ValidateEffect(proposal, 0, Schema, store, capabilities).ShouldBeEmpty();
        var moved = new StoreSnapshot(2, store.Items, [tombstone with { LocalVersion = 3 }],
            proposals: [proposal], journal: [planned]);
        Rules.ValidateEffect(proposal, 0, Schema, moved, capabilities)
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
    }

    [Fact]
    public void UnavailablePolicyEvidenceBlocksEvenWarningRulesWithoutSuppressingDefiniteViolations()
    {
        var policy = new Policy([new("parent", RuleKind.ParentType, "child", ["allowed"], warnOnly: true)]);
        var edge = new Relationship(Key("parent"), Key("child"), "parent-child", RelationshipOrigin.Local);
        var after = new StoreSnapshot(1, [Item("parent", "disallowed", complete: false), Item("child")], [edge]);
        var findings = Rules.Evaluate(policy, new(0), after, Schema);
        findings.ShouldContain(f => f.Kind == FindingKind.EvidenceUnavailable && f.Blocks);
        findings.ShouldContain(f => f.Kind == FindingKind.Violation && !f.Blocks);
        Rules.Evaluate(policy, new(0), new(1), Schema).ShouldContain(f => f.Kind == FindingKind.EvidenceUnavailable);
        Rules.Evaluate(new Policy([]), after, after, Schema)
            .ShouldHaveSingleItem().Kind.ShouldBe(FindingKind.EvidenceUnavailable);
    }

    [Fact]
    public void DependencyPolicyRetainsCycleEvidenceBoundsAndRelationshipProvenance()
    {
        var policy = new Policy([new("dependency", RuleKind.DependencyIntegrity)]);
        var ab = new Relationship(Key("a"), Key("b"), "dependency", RelationshipOrigin.Provider);
        var ba = new Relationship(Key("b"), Key("a"), "dependency", RelationshipOrigin.Local, LocalVersion: 1);
        var items = new[] { Item("a"), Item("b") };
        var after = new StoreSnapshot(1, items, [ab, ba]);
        var findings = Rules.Evaluate(policy, new(0, items, [ab]), after, Schema);
        findings.ShouldContain(f => f.Kind == FindingKind.Violation && f.Blocks
            && f.Evidence.Any(e => e.Origin == RelationshipOrigin.Local)
            && f.Evidence.Any(e => e.Origin == RelationshipOrigin.Provider));
        var bounded = new Policy([new("bounded", RuleKind.DependencyIntegrity, maxNodes: 1)]);
        Rules.Evaluate(bounded, new(0), after, Schema)
            .ShouldContain(f => f.Kind == FindingKind.EvidenceUnavailable && f.Blocks);
        var overlay = new StoreSnapshot(1, items,
            [ab, ab with { Origin = RelationshipOrigin.Local, LocalVersion = 2, Deleted = true }]);
        Rules.Evaluate(policy, new(0), overlay, Schema).ShouldBeEmpty();
        overlay.Relationships.Length.ShouldBe(2);
    }
}
