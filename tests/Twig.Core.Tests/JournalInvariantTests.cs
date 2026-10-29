using Shouldly;
using Twig.Core;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class JournalInvariantTests
{
    private static readonly ProviderId Provider = new("test");
    private static readonly Connection Connection = new(Provider, "scope", "destination", "principal");
    private static readonly ItemKey Local = ItemKey.Local(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly ItemKey Remote = ItemKey.Remote(Provider, "scope", "native-id");
    private static readonly Guid Attempt = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Proposal Creation = new(Connection, "schema", 0,
        [new("create", OpKind.Create, Local, fields: new([KeyValuePair.Create("title", FieldValue.String("new"))]),
            type: "record", concurrency: ConcurrencyMode.ObservedNonAtomic)]);

    private static Receipt Receipt(JournalEntry row, OpStatus status, bool published = false)
        => new(row.Digest, row.Operation, row.Attempt, status, ["attributable provider evidence"],
            published ? new Revision(Remote, "opaque") : null, published ? Remote : null,
            status is OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate
                ? new Error(status == OpStatus.Partial ? ErrorCode.Partial : ErrorCode.Indeterminate, "Known outcome limits.") : null);

    [Theory]
    [InlineData(OpStatus.Failed)]
    [InlineData(OpStatus.Partial)]
    [InlineData(OpStatus.Indeterminate)]
    public void UnsuccessfulAttemptCannotRestartOrBecomeVerifiedWithoutSettlement(OpStatus status)
    {
        var applying = JournalEntry.Planned("digest", 0).Begin(Attempt).Value;
        var terminal = applying.Record(Receipt(applying, status)).Value;
        terminal.Status.ShouldBe(status);
        terminal.Begin(Guid.NewGuid()).IsSuccess.ShouldBeFalse();
        terminal.Record(Receipt(terminal, OpStatus.Verified)).IsSuccess.ShouldBeFalse();
        terminal.Receipts.Length.ShouldBe(1);
    }

    [Fact]
    public void JournalSeparatesAcknowledgementAndVerificationAndBindsExactAttempt()
    {
        var planned = JournalEntry.Planned("digest", 0);
        planned.Begin(Guid.Empty).IsSuccess.ShouldBeFalse();
        var applying = planned.Begin(Attempt).Value;
        applying.Record(Receipt(applying, OpStatus.Verified)).IsSuccess.ShouldBeFalse();
        applying.Record(new("different", 0, Attempt, OpStatus.Applied, ["ack"])).IsSuccess.ShouldBeFalse();
        applying.Record(new("digest", 1, Attempt, OpStatus.Applied, ["ack"])).IsSuccess.ShouldBeFalse();
        applying.Record(new("digest", 0, Guid.NewGuid(), OpStatus.Applied, ["ack"])).IsSuccess.ShouldBeFalse();
        var applied = applying.Record(Receipt(applying, OpStatus.Applied)).Value;
        applied.Status.ShouldBe(OpStatus.Applied);
        applied.Record(Receipt(applied, OpStatus.Failed)).IsSuccess.ShouldBeFalse();
        var verified = applied.Record(Receipt(applied, OpStatus.Verified)).Value;
        verified.Status.ShouldBe(OpStatus.Verified);
        verified.Receipts.Length.ShouldBe(2);
        verified.Begin(Guid.NewGuid()).IsSuccess.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new Receipt("digest", 0, Attempt, OpStatus.Applied, []));
        Should.Throw<ArgumentException>(() => new Receipt("digest", 0, Attempt, OpStatus.Indeterminate, ["timeout"]));
    }

    [Fact]
    public void CreationAcknowledgementAndPermanentMappingHaveOneCommitBoundary()
    {
        var planned = JournalEntry.Planned(Creation.Digest, 0);
        var imported = new StoreSnapshot(1, proposals: [Creation], journal: [planned]);
        Rules.ValidateCommit(new(0), imported).ShouldBeEmpty();
        var applying = planned.Begin(Attempt).Value;
        var dispatched = new StoreSnapshot(2, proposals: [Creation], journal: [applying]);
        Rules.ValidateCommit(imported, dispatched).ShouldBeEmpty();
        var applied = applying.Record(Receipt(applying, OpStatus.Applied, published: true)).Value;
        var missingMap = new StoreSnapshot(3, proposals: [Creation], journal: [applied]);
        Rules.ValidateCommit(dispatched, missingMap).ShouldContain(e => e.Message.Contains("commit together", StringComparison.Ordinal));
        var mapping = new PublishMapping(Local, Remote, Creation.Digest, 0);
        var acknowledged = new StoreSnapshot(3, proposals: [Creation], journal: [applied], mappings: [mapping]);
        Rules.ValidateCommit(dispatched, acknowledged).ShouldBeEmpty();
        var uncertain = applied.Record(Receipt(applied, OpStatus.Indeterminate)).Value;
        var readbackUnavailable = new StoreSnapshot(4, proposals: [Creation], journal: [uncertain], mappings: [mapping]);
        Rules.ValidateCommit(acknowledged, readbackUnavailable).ShouldBeEmpty();
        Rules.ValidateCommit(readbackUnavailable, new(5, proposals: [Creation], journal: [uncertain]))
            .ShouldContain(e => e.Message.Contains("permanent", StringComparison.Ordinal));
        var rewritten = new PublishMapping(Local, ItemKey.Remote(Provider, "scope", "different"), Creation.Digest, 0);
        Rules.ValidateCommit(acknowledged, new(4, proposals: [Creation], journal: [applied], mappings: [rewritten]))
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
    }

    [Fact]
    public void StoreCannotEraseEvidenceSkipWriteAheadOrRunPastAnUnknownOutcome()
    {
        var op2 = new Op("second", OpKind.Create, ItemKey.Local(Guid.NewGuid()), type: "record",
            fields: FieldValues.Empty, concurrency: ConcurrencyMode.ObservedNonAtomic);
        var proposal = new Proposal(Connection, "schema", 0, [Creation.Operations[0], op2]);
        var first = JournalEntry.Planned(proposal.Digest, 0);
        var second = JournalEntry.Planned(proposal.Digest, 1);
        var before = new StoreSnapshot(1, proposals: [proposal], journal: [first, second]);
        var applying = first.Begin(Attempt).Value;
        Rules.ValidateCommit(new(0), new(1, proposals: [proposal], journal: [applying, second]))
            .ShouldContain(e => e.Message.Contains("Planned", StringComparison.Ordinal));
        Rules.ValidateCommit(before, new(2, proposals: [proposal], journal: [first, second.Begin(Guid.NewGuid()).Value]))
            .ShouldContain(e => e.Message.Contains("earlier operations verify", StringComparison.Ordinal));
        Rules.ValidateCommit(before, new(2, proposals: [proposal], journal: [first]))
            .ShouldContain(e => e.Code == ErrorCode.Conflict);
        Rules.ValidateCommit(before, new(2)).ShouldContain(e => e.Code == ErrorCode.Conflict);
        var attempted = new StoreSnapshot(2, proposals: [proposal], journal: [applying, second]);
        Rules.ValidateCommit(attempted, new(3, proposals: [proposal], journal: [first, second]))
            .ShouldContain(e => e.Message.Contains("replayed", StringComparison.Ordinal));
        Rules.ValidateCommit(before, new(1, proposals: [proposal], journal: [first, second]))
            .ShouldContain(e => e.Message.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplacingTheDigestCannotEvadeTheCreationFence()
    {
        var original = JournalEntry.Planned(Creation.Digest, 0).Begin(Attempt).Value;
        var unknown = original.Record(Receipt(original, OpStatus.Indeterminate)).Value;
        var replacement = new Proposal(Connection, "different-schema", 0, Creation.Operations);
        replacement.Digest.ShouldNotBe(Creation.Digest);
        var replacementRow = JournalEntry.Planned(replacement.Digest, 0);
        var before = new StoreSnapshot(1, proposals: [Creation, replacement], journal: [unknown, replacementRow]);
        var after = new StoreSnapshot(2, proposals: [Creation, replacement],
            journal: [unknown, replacementRow.Begin(Guid.NewGuid()).Value]);
        Rules.ValidateCommit(before, after)
            .ShouldContain(e => e.Message.Contains("replacement digest", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalEdgesSurviveRefreshAsVersionedTombstones()
    {
        var edge = new Relationship(Remote, ItemKey.Remote(Provider, "scope", "other"), "related",
            RelationshipOrigin.Local, LocalVersion: 1);
        var before = new StoreSnapshot(1, relationships: [edge]);
        Rules.ValidateCommit(new(0), before).ShouldBeEmpty();
        Rules.ValidateCommit(before, new(2)).ShouldContain(e => e.Message.Contains("mirror refresh", StringComparison.Ordinal));
        Rules.ValidateCommit(before, new(2, relationships: [edge with { Deleted = true }]))
            .ShouldContain(e => e.Message.Contains("CAS", StringComparison.Ordinal));
        var tombstone = edge with { Deleted = true, LocalVersion = 2 };
        var after = new StoreSnapshot(2, relationships: [tombstone]);
        Rules.ValidateCommit(before, after).ShouldBeEmpty();
        Rules.ValidateCommit(after, new(3, relationships: [edge]))
            .ShouldContain(e => e.Message.Contains("CAS", StringComparison.Ordinal));
    }
}
