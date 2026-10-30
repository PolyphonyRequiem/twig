using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Providers.Ado;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed class AdoProviderTests
{
    [Theory]
    [InlineData("Contoso.Initiative", "Waiting", "Delivered")]
    [InlineData("Other.Finding", "Queued", "Settled")]
    public async Task DiscoveryAndAtomicCloseUseNativeVocabularyAndRealStore(string type, string initial, string completed)
    {
        using var f = await Fixture.Create(type, initial, completed);
        var schema = await f.Schema();
        schema.Types.Single().Reference.ShouldBe(type);
        schema.Types.Single().States.Select(s => s.Name).ShouldBe([initial, completed]);
        schema.Gaps.ShouldBeEmpty();
        var read = await f.Read(1);
        var item = read.Items.Single();
        item.Type.ShouldBe(type);
        item.RelationshipsComplete.ShouldBeTrue();
        item.Fields["Custom.Points"].ShouldBe(FieldValue.Decimal(2.5m));
        item.Fields["Custom.Owner"].ShouldBe(FieldValue.Identity(Fixture.Actor));
        item.Fields["Custom.Unset"].ShouldBe(FieldValue.Null);
        item.Fields["Custom.Empty"].ShouldBe(FieldValue.String(""));
        var gateOnly = new Op("close", OpKind.Batch, item.Key, item.Revision,
            Fields(("System.State", FieldValue.String(completed))));
        var invalid = await f.Provider.ImportAsync(new(f.Connection, schema.Fingerprint, 0, [gateOnly]), "", default);
        invalid.IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);

        var fields = Fields(("System.State", FieldValue.String(completed)), ("Custom.Closure", FieldValue.String("Evidence retained")));
        var proposal = await f.Proposal(new("close", OpKind.Batch, item.Key, item.Revision, fields));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var acknowledgement = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        acknowledgement.Status.ShouldBe(OpStatus.Applied);
        f.Access.SawDurableFence.ShouldBeTrue();
        f.Http.Writes.ShouldBe(1);
        using var patch = JsonDocument.Parse(f.Http.LastPayload!);
        patch.RootElement[0].GetProperty("op").GetString().ShouldBe("test");
        patch.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        patch.RootElement[0].GetProperty("value").GetInt32().ShouldBe(4);
        patch.RootElement.GetArrayLength().ShouldBe(3);
        var stored = await f.Stored();
        stored.Journal.Single().Status.ShouldBe(OpStatus.Applied);
        stored.Items.Single().Revision.ShouldBe(acknowledgement.Revision);
        var verified = (await f.Provider.VerifyAsync(proposal, acknowledgement, default)).Value;
        verified.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Journal.Single().Receipts.Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("unknown-rule")]
    [InlineData("missing-required")]
    [InlineData("missing-rules")]
    [InlineData("missing-transitions")]
    [InlineData("unknown-category")]
    [InlineData("unknown-field-kind")]
    [InlineData("default-value")]
    [InlineData("duplicate-json")]
    [InlineData("metadata-page")]
    public async Task IncompleteSchemaNeverBecomesPermission(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = defect;
        var discovered = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        if (discovered.IsSuccess)
        {
            var item = (await f.Read(1)).Items.Single();
            var proposal = new Proposal(f.Connection, discovered.Value.Fingerprint, 0,
                [new("edit", OpKind.Batch, item.Key, item.Revision, Fields(("Custom.Empty", FieldValue.String("edit"))))]);
            (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        }
        else discovered.Error.Code.ShouldBeOneOf(ErrorCode.Unavailable, ErrorCode.Unsupported);
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(400, OpStatus.Failed)]
    [InlineData(401, OpStatus.Failed)]
    [InlineData(403, OpStatus.Failed)]
    [InlineData(409, OpStatus.Failed)]
    [InlineData(412, OpStatus.Failed)]
    [InlineData(429, OpStatus.Failed)]
    [InlineData(500, OpStatus.Indeterminate)]
    [InlineData(503, OpStatus.Indeterminate)]
    [InlineData(202, OpStatus.Indeterminate)]
    public async Task NativeRejectionsAndUnknownDispatchHaveDifferentDurableReceipts(int status, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.WriteStatus = status;
        var result = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(expected);
        result.Value.Evidence.ShouldContain($"http:{status}");
        (await f.Stored()).Journal.Single().Status.ShouldBe(expected);
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task DiscoveryHttpFailureIsExplicitAndNeverCachedAsEmpty(int status)
    {
        using var f = await Fixture.Create();
        (await f.Schema()).Types.ShouldNotBeEmpty();
        f.Http.MetadataStatus = status;
        var result = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Evidence.ShouldContain($"http:{status}");
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("canceled")]
    [InlineData("malformed")]
    [InlineData("lost-create-response")]
    public async Task AmbiguousCreateIsFencedAcrossChangedDigestsAndReopen(string failure)
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        var stored = await f.Stored();
        (await f.Store.CommitAsync(f.Connection, stored.Version,
            new(stored.Version + 1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.WriteFailure = failure;
        var acknowledgement = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        acknowledgement.Status.ShouldBe(OpStatus.Indeterminate);
        f.Http.Writes.ShouldBe(1);
        var reopened = (await SqliteStore.OpenAsync(f.Path)).Value;
        using var provider = new AdoProvider(f.Connection, f.Access, reopened, f.Http.NewHandle());
        (await provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        var snapshot = (await reopened.ReadAsync(f.Connection, default)).Value;
        var replacement = new Proposal(f.Connection, proposal.SchemaFingerprint, snapshot.Version,
            [new("replacement", OpKind.Create, seed, fields: fields, type: f.Http.Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        (await provider.ImportAsync(replacement, replacement.Digest)).IsSuccess.ShouldBeFalse();
        (await provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
        snapshot.Mappings.ShouldBeEmpty();
        snapshot.Drafts.Single().Key.ShouldBe(seed);
    }

    [Fact]
    public async Task CreateAcknowledgementMappingMirrorAndReadbackSurviveReopen()
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        var reopened = (await SqliteStore.OpenAsync(f.Path)).Value;
        var snapshot = (await reopened.ReadAsync(f.Connection, default)).Value;
        snapshot.Mappings.Single().ShouldBe(new PublishMapping(seed, ack.PublishedKey!, proposal.Digest, 0));
        snapshot.Items.ShouldContain(i => i.Key == ack.PublishedKey);
        snapshot.Drafts.Single().Key.ShouldBe(seed);
        using var provider = new AdoProvider(f.Connection, f.Access, reopened, f.Http.NewHandle());
        (await provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DurableApplyingRecoveryNeverInventsNonDispatchOrReplaysComment()
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        var proposal = await f.Proposal(new("comment", OpKind.Comment, item.Key, item.Revision, body: FieldValue.String("note")));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var stored = await f.Stored();
        var applying = stored.Journal.Single().Begin(Guid.NewGuid()).Value;
        (await f.Store.CommitAsync(f.Connection, stored.Version,
            new(stored.Version + 1, stored.Items, stored.Relationships, stored.Drafts, stored.Proposals, [applying]), default)).IsSuccess.ShouldBeTrue();
        (await f.Provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        var replacement = await f.Proposal(new("different-digest", OpKind.Comment, item.Key, item.Revision, body: FieldValue.String("changed")));
        (await f.Provider.ImportAsync(replacement, replacement.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("readback-denied", OpStatus.Indeterminate)]
    [InlineData("readback-mismatch", OpStatus.Partial)]
    [InlineData("readback-canceled", OpStatus.Indeterminate)]
    public async Task VerificationDoesNotTurnAcknowledgementIntoNoEffect(string defect, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        f.Http.Defect = defect;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(expected);
        var journal = (await f.Stored()).Journal.Single();
        journal.Receipts[0].Status.ShouldBe(OpStatus.Applied);
        journal.Receipts.Length.ShouldBe(2);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task RevisionMovedBeforeAcquisitionAndAtTransportCannotWriteStaleFields()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.Items[1]["rev"] = 5;
        var refused = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.Conflict);
        f.Http.Writes.ShouldBe(0);
        f.Http.Items[1]["rev"] = 4;
        f.Http.MoveAtDispatch = true;
        var rejected = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        rejected.Status.ShouldBe(OpStatus.Failed);
        f.Http.Items[1]["fields"]!["Custom.Empty"]!.GetValue<string>().ShouldBe("");
    }

    [Fact]
    public async Task CompetingAdapterAcquisitionHasOneWinnerWithoutNetworkInsideStoreTransaction()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        using var other = new AdoProvider(f.Connection, f.Access, (await SqliteStore.OpenAsync(f.Path)).Value, f.Http.NewHandle());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Access.BeforeAdmission = async () => { entered.SetResult(); await release.Task; };
        var first = f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loser = await other.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        loser.IsSuccess.ShouldBeFalse();
        // A separate real SQLite writer can commit while admission/transport are paused.
        var current = await f.Stored();
        (await f.Store.CommitAsync(f.Connection, current.Version, new(current.Version + 1, current.Items,
            current.Relationships, current.Drafts, current.Proposals, current.Journal, current.Mappings, current.Benches), default)).IsSuccess.ShouldBeTrue();
        release.SetResult();
        (await first).Value.Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task FieldTypingClearsAndHistoryCommentsSendExactIntent()
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        var bad = await f.Proposal(new("bad", OpKind.Batch, item.Key, item.Revision,
            Fields(("Custom.Points", FieldValue.String("3")))));
        (await f.Provider.ImportAsync(bad, bad.Digest)).IsSuccess.ShouldBeFalse();
        var edit = await f.Proposal(new("clear", OpKind.Batch, item.Key, item.Revision,
            Fields(("Custom.Points", FieldValue.Null), ("Custom.Empty", FieldValue.String("")))));
        (await f.Provider.ImportAsync(edit, edit.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(edit, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(edit, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        using (var patch = JsonDocument.Parse(f.Http.LastPayload!))
            patch.RootElement.EnumerateArray().Single(p => p.GetProperty("path").GetString() == "/fields/Custom.Points")
                .GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        item = (await f.Read(1)).Items.Single();
        var comment = await f.Proposal(new("comment", OpKind.Comment, item.Key, item.Revision,
            body: FieldValue.RichText("<p>Reviewed &amp; retained</p>", FieldKind.Html)));
        (await f.Provider.ImportAsync(comment, comment.Digest)).IsSuccess.ShouldBeTrue();
        ack = (await f.Provider.ApplyAsync(comment, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(comment, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        using var commentPatch = JsonDocument.Parse(f.Http.LastPayload!);
        commentPatch.RootElement[1].GetProperty("value").GetString().ShouldBe("<p>Reviewed &amp; retained</p>");
    }

    [Fact]
    public async Task RelationshipsNormalizeProvenanceVerifyBothEndsAndPreserveLocalTombstonesDraftsAndBench()
    {
        using var f = await Fixture.Create();
        var read = await f.Read(1, 2);
        var edge = new Relationship(f.Key(1), f.Key(2), "Native.ParentForward", RelationshipOrigin.Local, 1, true);
        var draft = new Draft(ItemKey.Local(Guid.NewGuid()), f.Http.Type, null, Fields(("System.State", FieldValue.String(f.Http.Initial))));
        var bench = new Bench(Guid.NewGuid(), "Retained", [new(SelectorKind.Pin, f.Key(1))]);
        (await f.Store.CommitAsync(f.Connection, 0, new(1, read.Items, [edge], [draft], benches: [bench]), default)).IsSuccess.ShouldBeTrue();
        var op = new Op("link", OpKind.Relate, f.Key(1), read.Items[0].Revision, other: f.Key(2), relation: "Native.ParentForward");
        var proposal = await f.Proposal(op);
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var mirrored = await f.Stored();
        mirrored.Relationships.ShouldContain(edge);
        mirrored.Relationships.ShouldContain(edge with { Origin = RelationshipOrigin.Provider, LocalVersion = 0, Deleted = false });
        mirrored.Drafts.Single().ShouldBe(draft);
        mirrored.Benches.Single().Id.ShouldBe(bench.Id);
        var item = (await f.Read(1)).Items.Single();
        var unlink = await f.Proposal(new("unlink", OpKind.Unrelate, f.Key(1), item.Revision, other: f.Key(2), relation: op.Relation));
        (await f.Provider.ImportAsync(unlink, unlink.Digest)).IsSuccess.ShouldBeTrue();
        ack = (await f.Provider.ApplyAsync(unlink, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(unlink, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Relationships.ShouldBe([edge]);
    }

    [Fact]
    public async Task ExactQueryUsesOrdinalTypedAstAndExplicitlyRefusesOversizeAndUnknownShapes()
    {
        using var f = await Fixture.Create();
        f.Http.Items[1]["fields"]!["Custom.Empty"] = "One";
        f.Http.Items[2]["fields"]!["Custom.Empty"] = "one";
        var query = new Query(QueryKind.And, children: [new(QueryKind.Not, children:
            [new(QueryKind.Equal, "Custom.Empty", FieldValue.String("One"))]), new(QueryKind.All)]);
        var result = (await f.Provider.QueryAsync(f.Connection, query, null, default)).Value;
        result.Items.Single().Key.ShouldBe(f.Key(2));
        result.Complete.ShouldBeTrue();
        (await f.Provider.QueryAsync(f.Connection, new(QueryKind.Equal, "Not.Native", FieldValue.String("x")), null, default))
            .Error.Code.ShouldBe(ErrorCode.Unsupported);
        (await f.Provider.QueryAsync(f.Connection, query, "guessed", default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        f.Http.Defect = "oversize-query";
        (await f.Provider.QueryAsync(f.Connection, new(QueryKind.All), null, default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
    }

    [Theory]
    [InlineData("wrong-collection")]
    [InlineData("wrong-project")]
    [InlineData("wrong-principal")]
    [InlineData("wrong-item-project")]
    public async Task ChangedNativeBindingCannotAuthorizeReadsOrWrites(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = defect;
        (await f.Provider.ReadAsync(f.Connection, [f.Key(1)], default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task InitialTransitionsSuppliersPicklistsAndDisabledRulesRemainSchemaBound()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "supported-rules";
        var schema = await f.Schema();
        schema.Gaps.ShouldBeEmpty();
        schema.Types.Single().Rules.ShouldContain(r => r.Disabled && r.Action == RuleAction.Unknown);
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Completed)), ("Custom.Closure", FieldValue.String("explicit")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var invalid = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(invalid, invalid.Digest)).IsSuccess.ShouldBeFalse();
        var item = (await f.Read(1)).Items.Single();
        var close = await f.Proposal(new("close-with-provider-supplier", OpKind.Batch, item.Key, item.Revision,
            Fields(("System.State", FieldValue.String(f.Http.Completed)), ("Custom.Points", FieldValue.Null))));
        (await f.Provider.ImportAsync(close, close.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(close, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(close, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Http.Items[1]["fields"]!["Custom.Closure"]!.GetValue<string>().ShouldBe("from native supplier");
        f.Http.Defect = "changed-disabled-rule";
        (await f.Schema()).Fingerprint.ShouldNotBe(schema.Fingerprint);
    }

    [Fact]
    public async Task ReceiptCommitFailureKeepsApplyingAndCreationFenceWithoutInventingMapping()
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.AfterWrite = async () =>
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString());
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON core_snapshots BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;";
            await command.ExecuteNonQueryAsync();
        };
        var result = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        result.Status.ShouldBe(OpStatus.Indeterminate);
        result.Error!.Code.ShouldBe(ErrorCode.StorageUnavailable);
        var snapshot = await f.Stored();
        snapshot.Journal.Single().Status.ShouldBe(OpStatus.Applying);
        snapshot.Mappings.ShouldBeEmpty();
        snapshot.Items.ShouldBeEmpty();
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task ContradictoryOrForeignRelationshipEvidenceIsNotCertifiedComplete()
    {
        using var f = await Fixture.Create();
        f.Http.Items[1]["relations"]!.AsArray().Add(new JsonObject
        {
            ["rel"] = "Native.ParentForward", ["url"] = "https://example.test/organization/_apis/wit/workItems/2",
        });
        (await f.Provider.ReadAsync(f.Connection, [f.Key(1), f.Key(2)], default)).Error.Code.ShouldBe(ErrorCode.Unavailable);
        f.Http.Items[1]["relations"]![0]!["url"] = "https://another.test/organization/_apis/wit/workItems/2";
        (await f.Provider.ReadAsync(f.Connection, [f.Key(1)], default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
    }

    [Fact]
    public async Task ValidButDifferentNativeActorCannotVerifyOrReuseOriginalIdentityBinding()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        var originalActor = $"actor:{Fixture.Actor}";
        ack.Evidence.ShouldContain(originalActor);
        f.Access.Principal = Fixture.Process;
        f.Http.NativeActor = Fixture.Process;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        (await f.Stored()).Journal.Single().Receipts[0].Evidence.ShouldContain(originalActor);
        var replacement = await f.Edit();
        (await f.Provider.ImportAsync(replacement, replacement.Digest)).Error.Code.ShouldBe(ErrorCode.Conflict);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DigestSchemaAdmissionUnsupportedEffectsAndCancellationAreNotBypassed()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, "not-the-digest")).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        f.Http.Initial = "ChangedInitial";
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Initial = "Waiting";
        var item = (await f.Read(1)).Items.Single();
        var delete = await f.Proposal(new("delete", OpKind.Delete, item.Key, item.Revision));
        (await f.Provider.ImportAsync(delete, delete.Digest)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        var markdown = await f.Proposal(new("markdown", OpKind.Comment, item.Key, item.Revision,
            body: FieldValue.RichText("**not converted**", FieldKind.Markdown)));
        (await f.Provider.ImportAsync(markdown, markdown.Digest)).IsSuccess.ShouldBeTrue();
        (await f.Provider.ApplyAsync(markdown, 0, Guid.NewGuid(), default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        var stored = await f.Stored();
        stored.Journal.Single().Status.ShouldBe(OpStatus.Planned);
        proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Access.DenyWrite = true;
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value.Status.ShouldBe(OpStatus.Failed);
        f.Http.Writes.ShouldBe(0);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => f.Provider.DiscoverSchemaAsync(f.Connection, canceled.Token));
    }

    private static FieldValues Fields(params (string Name, FieldValue Value)[] fields)
        => new(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)));

    private sealed class Fixture : IDisposable
    {
        internal const string Collection = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        internal const string Project = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        internal const string Actor = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        internal const string Process = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        internal Connection Connection { get; } = new(new("ado"), Collection, Project, "test-actor-binding");
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"twig-ado-{Guid.NewGuid():N}.db");
        internal SqliteStore Store { get; private set; } = null!;
        internal NativeHttp Http { get; }
        internal TestAccess Access { get; private set; } = null!;
        internal AdoProvider Provider { get; private set; } = null!;
        private Fixture(string type, string initial, string completed) => Http = new(type, initial, completed);
        internal static async Task<Fixture> Create(string type = "Contoso.Initiative", string initial = "Waiting", string completed = "Delivered")
        {
            var f = new Fixture(type, initial, completed);
            f.Store = (await SqliteStore.CreateAsync(f.Path)).Value;
            f.Access = new(f.Connection, f.Store);
            f.Provider = new(f.Connection, f.Access, f.Store, f.Http);
            return f;
        }
        internal ItemKey Key(int id) => ItemKey.Remote(Connection.Provider, Collection, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        internal async Task<ProcessSchema> Schema() => (await Provider.DiscoverSchemaAsync(Connection, default)).Value;
        internal async Task<QueryPage> Read(params int[] ids) => (await Provider.ReadAsync(Connection, ids.Select(Key).ToImmutableArray(), default)).Value;
        internal async Task<StoreSnapshot> Stored() => (await Store.ReadAsync(Connection, default)).Value;
        internal async Task<Proposal> Proposal(Op op) => new(Connection, (await Schema()).Fingerprint, (await Stored()).Version, [op]);
        internal async Task<Proposal> Edit()
        {
            var item = (await Read(1)).Items.Single();
            return await Proposal(new("edit", OpKind.Batch, item.Key, item.Revision, Fields(("Custom.Empty", FieldValue.String("edited")))));
        }
        public void Dispose()
        {
            Provider.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
        }
    }

    private sealed class TestAccess(Connection connection, IStore store) : AdoAccess
    {
        internal bool SawDurableFence;
        internal bool DenyWrite;
        internal string Principal = Fixture.Actor;
        internal Func<Task>? BeforeAdmission;
        public override Task<Result<AdoSession>> AcquireAsync(Connection requested, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            requested.ShouldBe(connection);
            return Task.FromResult(Result<AdoSession>.Ok(new Session(this, connection, store)));
        }
        private sealed class Session(TestAccess owner, Connection connection, IStore store) : AdoSession
        {
            public override Connection Connection => connection;
            public override Uri OrganizationUrl => new("https://example.test/organization");
            public override string PrincipalId => owner.Principal;
            public override AuthenticationHeaderValue Authorization => new("Bearer", "synthetic");
            public override async Task<Result<AdoWriteAdmission>> AdmitAsync(AdoWriteIntent intent, CancellationToken ct)
            {
                var stored = (await store.ReadAsync(connection, ct)).Value;
                var row = stored.Journal.Single(j => j.Digest == intent.Proposal.Digest && j.Operation == intent.Operation);
                row.Status.ShouldBe(OpStatus.Applying);
                row.Attempt.ShouldBe(intent.Attempt);
                owner.SawDurableFence = true;
                if (owner.BeforeAdmission is not null) await owner.BeforeAdmission();
                return owner.DenyWrite
                    ? Result<AdoWriteAdmission>.Fail(ErrorCode.Unavailable, "Native admission denied.")
                    : Result<AdoWriteAdmission>.Ok(new Admission());
            }
            public override void Dispose() { }
        }
        private sealed class Admission : AdoWriteAdmission
        {
            public override Task ObserveAsync(int statusCode, string body, CancellationToken ct) => Task.CompletedTask;
            public override void Dispose() { }
        }
    }

    private sealed class NativeHttp : HttpMessageHandler
    {
        internal string Type;
        internal string Initial;
        internal string Completed;
        internal string NativeActor = Fixture.Actor;
        internal string? Defect;
        internal string? WriteFailure;
        internal int? WriteStatus;
        internal int? MetadataStatus;
        internal bool MoveAtDispatch;
        internal int Writes;
        internal string? LastPayload;
        internal Func<Task>? AfterWrite;
        internal Dictionary<int, JsonObject> Items = [];
        private readonly Dictionary<(int, int), JsonObject> _revisions = [];
        internal NativeHttp(string type, string initial, string completed)
        {
            (Type, Initial, Completed) = (type, initial, completed);
            Items.Add(1, Item(1)); Items.Add(2, Item(2));
        }
        internal HttpMessageHandler NewHandle() => new Forward(this);
        private sealed class Forward(NativeHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.SendAsync(request, ct);
        }
        private JsonObject Item(int id) => new()
        {
            ["id"] = id, ["rev"] = 4,
            ["fields"] = new JsonObject
            {
                ["System.TeamProject"] = "Native Project", ["System.WorkItemType"] = Type,
                ["System.State"] = Initial, ["System.History"] = "",
                ["Custom.Closure"] = "old cached closure", ["Custom.Points"] = 2.5m,
                ["Custom.Owner"] = new JsonObject { ["id"] = Fixture.Actor, ["displayName"] = "Not identity" },
                ["Custom.Empty"] = "",
            },
            ["relations"] = new JsonArray(),
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            request.Headers.Authorization!.Parameter.ShouldBe("synthetic");
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            if (request.Method == HttpMethod.Patch || request.Method == HttpMethod.Post && path.Contains("/workitems/$", StringComparison.OrdinalIgnoreCase))
                return await Write(request, path, ct);
            if (path.EndsWith("/connectionData"))
                return Json($$"""{"instanceId":"{{(Defect == "wrong-collection" ? Fixture.Process : Fixture.Collection)}}","authenticatedUser":{"id":"{{(Defect == "wrong-principal" ? Fixture.Process : NativeActor)}}"} }""");
            if (path.Contains("/_apis/projects/"))
                return Json($$"""{"id":"{{(Defect == "wrong-project" ? Fixture.Process : Fixture.Project)}}","name":"Native Project","capabilities":{"processTemplate":{"templateTypeId":"{{Fixture.Process}}"} } }""");
            if (path.EndsWith("/wiql"))
            {
                var count = Defect == "oversize-query" ? 201 : Items.Count;
                return Json(new JsonObject { ["queryType"] = "flat",
                    ["workItems"] = new JsonArray(Enumerable.Range(1, count).Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()) }.ToJsonString());
            }
            if (path.Contains("/workitems/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = path[(path.IndexOf("/workitems/", StringComparison.OrdinalIgnoreCase) + 11)..].Split('/');
                var id = int.Parse(tail[0], System.Globalization.CultureInfo.InvariantCulture);
                if (tail.Length > 1)
                {
                    if (Defect == "readback-denied") return Status(403);
                    if (Defect == "readback-canceled") throw new OperationCanceledException("Synthetic readback cancellation");
                    var revision = int.Parse(tail[2], System.Globalization.CultureInfo.InvariantCulture);
                    var root = _revisions[(id, revision)].DeepClone();
                    if (Defect == "readback-mismatch") root["fields"]!["Custom.Empty"] = "not edited";
                    return Json(root.ToJsonString());
                }
                if (!Items.TryGetValue(id, out var item)) return Status(404);
                var read = item.DeepClone();
                if (Defect == "wrong-item-project") read["fields"]!["System.TeamProject"] = "Another Project";
                return Json(read.ToJsonString());
            }
            if (MetadataStatus is { } status) return Status(status);
            if (Defect == "duplicate-json") return Json("""{"count":0,"value":[],"value":[]}""");
            HttpResponseMessage response;
            if (path.EndsWith("/workitemrelationtypes"))
                response = Rows("""
                    {"referenceName":"Native.ParentForward","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":true,"topology":"tree","oppositeEndReferenceName":"Native.ParentReverse"}},
                    {"referenceName":"Native.ParentReverse","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":false,"topology":"tree","oppositeEndReferenceName":"Native.ParentForward"}},
                    {"referenceName":"AttachedFile","attributes":{"usage":"resourceLink"}}
                    """);
            else if (path.EndsWith("/workitemtypecategories")) response = Rows("");
            else if (path.EndsWith("/workitemtypes", StringComparison.OrdinalIgnoreCase) && query.Contains("$expand=all"))
                response = Defect == "missing-transitions" ? Rows("") : Rows($$"""
                    {"referenceName":"{{Type}}","transitions":{"":[{"to":"{{Initial}}"}],"{{Initial}}":[{"to":"{{Completed}}"}],"{{Completed}}":[{"to":"{{Initial}}"}]} }
                    """);
            else if (path.EndsWith("/workItemTypes"))
                response = Rows($$"""{"referenceName":"{{Type}}","name":"{{Type}}","isDisabled":false}""");
            else if (path.EndsWith("/states"))
                response = Rows($$"""{"name":"{{Initial}}","stateCategory":"Proposed"},{"name":"{{Completed}}","stateCategory":"{{(Defect == "unknown-category" ? "Alien" : "Completed")}}"}""");
            else if (path.EndsWith("/rules"))
            {
                if (Defect == "missing-rules") return Status(403);
                var required = $$"""
                    {"isDisabled":false,"conditions":[{"conditionType":"when","field":"System.State","value":"{{Completed}}"}],"actions":[{"actionType":"{{(Defect == "unknown-rule" ? "unsupported" : "makeRequired")}}","targetField":"Custom.Closure"}]}
                    """;
                if (Defect is "supported-rules" or "changed-disabled-rule")
                    required += $$"""
                        ,{"isDisabled":false,"conditions":[{"conditionType":"$when","field":"System.State","value":"{{Completed}}"}],"actions":[{"actionType":"$copyValue","targetField":"Custom.Closure","value":"from native supplier"}]},
                        {"isDisabled":true,"conditions":[{"unknown":"retained"}],"actions":[{"unknown":"{{Defect}}"}]}
                        """;
                response = Rows(required);
            }
            else if (path.Contains("/processes/lists/"))
                response = Json($$"""{"id":"{{Fixture.Process}}","isSuggested":false,"items":["1.5","2.5","3.5"]}""");
            else if (path.EndsWith("/fields"))
            {
                var names = new[] { "System.State", "System.History", "Custom.Closure", "Custom.Points", "Custom.Owner", "Custom.Empty", "Custom.Unset" };
                var global = !path.Contains("/work/processes/");
                response = Rows(string.Join(",", names.Select(name =>
                {
                    var field = new JsonObject { ["referenceName"] = name, ["type"] = name == "Custom.Points" ? "double" : "string" };
                    if (Defect == "unknown-field-kind" && name == "Custom.Empty") field["type"] = "unrecognized";
                    if (global)
                    {
                        field["isIdentity"] = name == "Custom.Owner"; field["readOnly"] = false; field["isPicklist"] = false;
                        if (Defect == "supported-rules" && name == "Custom.Points")
                        {
                            field["isPicklist"] = true; field["isPicklistSuggested"] = false; field["picklistId"] = Fixture.Process;
                        }
                    }
                    else
                    {
                        if (Defect != "missing-required") field["required"] = false;
                        if (Defect == "default-value" && name == "Custom.Empty") field["defaultValue"] = "server default";
                    }
                    return field.ToJsonString();
                })));
            }
            else throw new InvalidOperationException($"Unexpected native route {request.Method} {path}{query}");
            if (Defect == "metadata-page") response.Headers.Add("x-ms-continuationtoken", "more");
            return response;
        }
        private async Task<HttpResponseMessage> Write(HttpRequestMessage request, string path, CancellationToken ct)
        {
            Writes++;
            LastPayload = await request.Content!.ReadAsStringAsync(ct);
            if (WriteFailure == "network") throw new HttpRequestException("Synthetic connection loss after possible dispatch");
            if (WriteFailure == "canceled") throw new OperationCanceledException("Synthetic dispatch cancellation");
            if (WriteFailure == "malformed") return Json("{broken");
            if (WriteStatus is { } status) return Status(status);
            var create = request.Method == HttpMethod.Post;
            var id = create ? 3 : int.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
            var item = create ? Item(id) : Items[id];
            if (MoveAtDispatch) item["rev"] = item["rev"]!.GetValue<int>() + 1;
            using var patch = JsonDocument.Parse(LastPayload);
            foreach (var entry in patch.RootElement.EnumerateArray())
            {
                var op = entry.GetProperty("op").GetString();
                var field = entry.GetProperty("path").GetString()!;
                if (op == "test")
                {
                    if (entry.GetProperty("value").GetInt32() != item["rev"]!.GetValue<int>()) return Status(412);
                }
                else if (field.StartsWith("/fields/"))
                    item["fields"]![field[8..]] = JsonNode.Parse(entry.GetProperty("value").GetRawText());
                else if (field == "/relations/-")
                {
                    var relation = JsonNode.Parse(entry.GetProperty("value").GetRawText())!;
                    item["relations"]!.AsArray().Add(relation.DeepClone());
                    var other = Items[2];
                    other["relations"]!.AsArray().Add(new JsonObject { ["rel"] = "Native.ParentReverse",
                        ["url"] = $"https://example.test/organization/_apis/wit/workItems/{id}" });
                }
                else if (op == "remove" && field.StartsWith("/relations/"))
                {
                    item["relations"]!.AsArray().RemoveAt(int.Parse(field[11..], System.Globalization.CultureInfo.InvariantCulture));
                    Items[2]["relations"] = new JsonArray();
                }
                else throw new InvalidOperationException("Unexpected patch operation");
            }
            item["rev"] = item["rev"]!.GetValue<int>() + 1;
            if (Defect == "supported-rules" && item["fields"]!["System.State"]!.GetValue<string>() == Completed)
                item["fields"]!["Custom.Closure"] = "from native supplier";
            Items[id] = item;
            _revisions[(id, item["rev"]!.GetValue<int>())] = item.DeepClone().AsObject();
            if (WriteFailure == "lost-create-response") throw new HttpRequestException("Native create applied but response lost");
            if (AfterWrite is not null) await AfterWrite();
            return Json(item.ToJsonString());
        }
        private static HttpResponseMessage Rows(string rows) => Json($$"""{"value":[{{rows}}]}""");
        private static HttpResponseMessage Status(int status) => new((HttpStatusCode)status)
        { Content = new StringContent("""{"message":"Synthetic native error"}""", Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
