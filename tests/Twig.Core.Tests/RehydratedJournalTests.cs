using Shouldly;
using Twig.Core;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Core.Tests;

public sealed class RehydratedJournalTests
{
    [Theory]
    [InlineData(OpStatus.Applied)]
    [InlineData(OpStatus.Indeterminate)]
    public void IndependentlyRehydratedEvidenceRemainsTheSameHistory(OpStatus status)
    {
        var provider = new ProviderId("test");
        var connection = new Connection(provider, "scope", "destination", "principal");
        var key = ItemKey.Remote(provider, "scope", "item");
        var proposal = new Proposal(connection, "schema", 0,
            [new("edit", OpKind.Batch, key, new Revision(key, "opaque"),
                new([KeyValuePair.Create("title", FieldValue.String("changed"))]))]);
        var attempt = Guid.NewGuid();
        JournalEntry Rehydrate()
        {
            var row = JournalEntry.Planned(proposal.Digest, 0).Begin(attempt).Value;
            return row.Record(new Receipt(proposal.Digest, 0, attempt, status,
                new[] { "attributable evidence" }, error: status == OpStatus.Indeterminate
                    ? new Error(ErrorCode.Indeterminate, "Unknown outcome") { Evidence = ["transport lost"] } : null)).Value;
        }
        var original = Rehydrate();
        var rehydrated = Rehydrate();
        ReferenceEquals(original.Receipts[0], rehydrated.Receipts[0]).ShouldBeFalse();
        original.Receipts[0].Evidence.SequenceEqual(rehydrated.Receipts[0].Evidence).ShouldBeTrue();
        Rules.ValidateCommit(new(1, proposals: [proposal], journal: [original]),
            new(2, proposals: [proposal], journal: [rehydrated])).ShouldBeEmpty();

        var changed = JournalEntry.Planned(proposal.Digest, 0).Begin(attempt).Value.Record(
            new Receipt(proposal.Digest, 0, attempt, status, ["rewritten evidence"],
                error: original.Receipts[0].Error)).Value;
        Rules.ValidateCommit(new(1, proposals: [proposal], journal: [original]),
            new(2, proposals: [proposal], journal: [changed])).ShouldContain(e => e.Code == ErrorCode.Conflict);
        if (status == OpStatus.Indeterminate)
        {
            var changedError = JournalEntry.Planned(proposal.Digest, 0).Begin(attempt).Value.Record(
                new Receipt(proposal.Digest, 0, attempt, status, original.Receipts[0].Evidence,
                    error: new Error(ErrorCode.Indeterminate, "Unknown outcome") { Evidence = ["rewritten error evidence"] })).Value;
            Rules.ValidateCommit(new(1, proposals: [proposal], journal: [original]),
                new(2, proposals: [proposal], journal: [changedError])).ShouldContain(e => e.Code == ErrorCode.Conflict);
        }
    }
}
