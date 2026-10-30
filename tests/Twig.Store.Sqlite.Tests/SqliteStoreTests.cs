using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Store.Sqlite.Tests;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "twig-clean-store-" + Guid.NewGuid().ToString("N"));
    private static readonly ProviderId Provider = new("test");
    private static readonly Connection Connection = new(Provider, "Case/Scope", "immutable-destination", "identity-reference");
    private static readonly ItemKey Remote = ItemKey.Remote(Provider, Connection.ResourceScope, "opaque/Id:007");
    private static readonly ItemKey Other = ItemKey.Remote(Provider, Connection.ResourceScope, "other");
    private static readonly ItemKey Local = ItemKey.Local(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private string Database => Path.Combine(_directory, "clean-core.db");

    public SqliteStoreTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<SqliteStore> Create()
    {
        var result = await SqliteStore.CreateAsync(Database);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }

    private async Task<SqliteStore> Open()
    {
        var result = await SqliteStore.OpenAsync(Database);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }

    private static async Task<StoreSnapshot> Read(IStore store, Connection? connection = null)
    {
        var result = await store.ReadAsync(connection ?? Connection, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }

    private static async Task Commit(IStore store, StoreSnapshot before, StoreSnapshot next, Connection? connection = null)
    {
        var result = await store.CommitAsync(connection ?? Connection, before.Version, next, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message + ": " + string.Join("; ", result.Error.Evidence));
        result.Value.ShouldBe(next.Version);
    }

    private static StoreSnapshot Next(StoreSnapshot s, IEnumerable<WorkItem>? items = null,
        IEnumerable<Relationship>? relationships = null, IEnumerable<Draft>? drafts = null,
        IEnumerable<Proposal>? proposals = null, IEnumerable<JournalEntry>? journal = null,
        IEnumerable<PublishMapping>? mappings = null, IEnumerable<Bench>? benches = null)
        => new(s.Version + 1, items ?? s.Items, relationships ?? s.Relationships, drafts ?? s.Drafts,
            proposals ?? s.Proposals, journal ?? s.Journal, mappings ?? s.Mappings, benches ?? s.Benches);

    private static FieldValues Fields(params (string Name, FieldValue Value)[] fields)
        => new(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)));
    private static WorkItem Item(string token = "opaque\"rev/000:xyz", FieldValues? fields = null)
        => new(Remote, "type-ref", fields ?? Fields(("title", FieldValue.String("original"))), new Revision(Remote, token), true);
    private static Draft Seed() => new(Local, "type-ref", null, Fields(("title", FieldValue.String("seed"))));
    private static Proposal Creation(long version = 0, string schema = "schema") => new(Connection, schema, version,
        [new("create", OpKind.Create, Local, fields: Seed().Changes, type: Seed().Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
    private static JournalEntry Planned(Proposal p, int ordinal = 0) => JournalEntry.Planned(p.Digest, ordinal);
    private static Receipt Receipt(JournalEntry row, OpStatus status, ItemKey? published = null)
        => new(row.Digest, row.Operation, row.Attempt, status, [$"attributable {status} evidence", "preserve order"],
            published is null ? null : new Revision(published, "rev:ack"), published,
            status is OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate
                ? new Error(ErrorCode.Indeterminate, "Outcome limits") { Evidence = ["timeout is not non-dispatch"] } : null);

    private async Task Execute(string sql)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task LosslessTypedSnapshotsDraftBasesBenchesAndExactConnectionPartitionsSurviveReopen()
    {
        JsonSerializer.IsReflectionEnabledByDefault.ShouldBeFalse();
        var store = await Create();
        var fields = Fields(
            ("null", FieldValue.Null), ("empty", FieldValue.String("")), ("text", FieldValue.String("line\r\n\u00e9")),
            ("html", FieldValue.RichText("<p>a &amp; b</p>", FieldKind.Html)),
            ("markdown", FieldValue.RichText("**a**\n", FieldKind.Markdown)),
            ("integer", FieldValue.Integer(long.MinValue)), ("decimal", FieldValue.Decimal(7922816251426433759354395.0335m)),
            ("boolean", FieldValue.Bool(true)),
            ("instant", FieldValue.At(DateTimeOffset.Parse("2026-10-30T13:02:03.1234567-07:00", CultureInfo.InvariantCulture))),
            ("identity", FieldValue.Identity("native:Case")), ("list", FieldValue.Strings(["z", "", "a", "z"])),
            ("raw", FieldValue.Raw("{ \"keep\": [null, 1.00] }", "provider/unknown")));
        var baseline = Item(fields: fields);
        var remoteDraft = new Draft(Remote, "type-ref", baseline, Fields(("null", FieldValue.String("")), ("clear", FieldValue.Null)));
        var bench = new Bench(Guid.NewGuid(), "deliberate view",
            [new(SelectorKind.Pin, Local), new(SelectorKind.Subtree, Remote, hierarchyRelation: "parent"),
                new(SelectorKind.Query, query: new(QueryKind.And, children: [
                    new(QueryKind.Equal, "null", FieldValue.Null),
                    new(QueryKind.Or, children: [new(QueryKind.All), new(QueryKind.Not, children: [new(QueryKind.Equal, "empty", FieldValue.String(""))])])]))]);
        var initial = await Read(store);
        initial.Version.ShouldBe(0);
        var identities = new[]
        {
            baseline,
            new WorkItem(ItemKey.Remote(Provider, "case/scope", Remote.NativeId!), "type-ref", FieldValues.Empty, null),
            new WorkItem(ItemKey.Remote(new ProviderId("another"), Connection.ResourceScope, Remote.NativeId!), "type-ref", FieldValues.Empty, null),
        };
        await Commit(store, initial, Next(initial, items: identities, drafts: [Seed(), remoteDraft], benches: [bench]));
        var reopened = await Read(await Open());
        reopened.Items.Select(i => i.Key).ShouldBe(identities.Select(i => i.Key));
        var item = reopened.Items[0];
        item.Revision.ShouldBe(baseline.Revision);
        item.Fields.ShouldBe(fields);
        item.Fields.ContainsKey("missing").ShouldBeFalse();
        item.Fields["null"].Kind.ShouldBe(FieldKind.Null);
        item.Fields["empty"].Text.ShouldBe("");
        item.RelationshipsComplete.ShouldBeTrue();
        reopened.Items[1].Revision.ShouldBeNull();
        reopened.Drafts[0].Baseline.ShouldBeNull();
        reopened.Drafts[1].Baseline!.Fields.ShouldBe(fields);
        reopened.Drafts[1].Baseline!.Revision.ShouldBe(baseline.Revision);
        reopened.Drafts[1].Changes.ShouldBe(remoteDraft.Changes);
        reopened.Benches[0].Id.ShouldBe(bench.Id);
        reopened.Benches[0].Name.ShouldBe(bench.Name);
        var query = reopened.Benches[0].Selectors[2].Query!;
        query.Children[1].Children[1].Children[0].Value!.Text.ShouldBe("");
        reopened.Benches[0].Selectors[0].Item.ShouldBe(Local);
        reopened.Benches[0].Selectors[1].HierarchyRelation.ShouldBe("parent");

        foreach (var connection in new[]
        {
            new Connection(Provider, Connection.ResourceScope, Connection.Destination, "different-principal"),
            new Connection(Provider, Connection.ResourceScope, "different-destination", Connection.IdentityRef),
            new Connection(Provider, "different-scope", Connection.Destination, Connection.IdentityRef),
            new Connection(new ProviderId("another"), Connection.ResourceScope, Connection.Destination, Connection.IdentityRef),
        })
        {
            var empty = await Read(store, connection);
            empty.Version.ShouldBe(0);
            await Commit(store, empty, Next(empty, benches: [new Bench(Guid.NewGuid(), "isolated", [])]), connection);
        }
        (await Read(await Open())).Items[0].Fields.ShouldBe(fields);
    }

    [Fact]
    public async Task ExplicitInitializationNeverAdoptsOverwritesOrMigratesAnExistingFile()
    {
        (await SqliteStore.OpenAsync(Database)).IsSuccess.ShouldBeFalse();
        File.Exists(Database).ShouldBeFalse();
        (await SqliteStore.CreateAsync("relative.db")).Error.Code.ShouldBe(ErrorCode.InvalidInput);
        await File.WriteAllTextAsync(Database, "unrelated user content");
        var bytes = await File.ReadAllBytesAsync(Database);
        (await SqliteStore.CreateAsync(Database)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await SqliteStore.OpenAsync(Database)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await File.ReadAllBytesAsync(Database)).ShouldBe(bytes);
        File.Delete(Database);
        var store = await Create();
        await Execute("PRAGMA user_version=999;");
        (await SqliteStore.OpenAsync(Database)).Error.Message.ShouldContain("No migration or reinitialization");
        (await store.ReadAsync(Connection, default)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await store.CommitAsync(Connection, 0, new(1), default)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        await Execute("PRAGMA user_version=1;");
        (await Read(await Open())).Version.ShouldBe(0);
    }

    [Fact]
    public async Task WalReaderSeesCommittedSnapshotWhileAnotherHandleHasUncommittedChanges()
    {
        var store = await Create();
        await Commit(store, new(0), new(1, items: [Item()]));
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Database, Pooling = false }.ToString());
        await writer.OpenAsync();
        using (var transaction = writer.BeginTransaction(deferred: false))
        {
            await using var command = writer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE core_snapshots SET document='uncommitted invalid document';";
            await command.ExecuteNonQueryAsync();
            var observed = await Read(await Open());
            observed.Version.ShouldBe(1);
            observed.Items[0].Key.ShouldBe(Remote);
            transaction.Rollback();
        }
        (await Read(await Open())).Items[0].Key.ShouldBe(Remote);
        await using var pragma = writer.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode;";
        (await pragma.ExecuteScalarAsync()).ShouldBe("wal");
    }

    [Fact]
    public async Task ProposalImportIsAtomicOrderedAndDigestBound()
    {
        var store = await Create();
        var op = new Op("edit", OpKind.Batch, Remote, Item().Revision, Fields(("title", FieldValue.String("changed"))));
        var proposal = new Proposal(Connection, "schema", 0,
            [op, new("note", OpKind.Comment, Remote, Item().Revision, body: FieldValue.RichText("**keep**", FieldKind.Markdown))]);
        var reverse = new Proposal(Connection, "schema", 0, proposal.Operations.Reverse());
        reverse.Digest.ShouldNotBe(proposal.Digest);
        var empty = await Read(store);
        var missingRow = Next(empty, items: [Item()], proposals: [proposal], journal: [Planned(proposal)]);
        (await store.CommitAsync(Connection, 0, missingRow, default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await Read(await Open())).Version.ShouldBe(0);
        var imported = Next(empty, items: [Item()], proposals: [proposal], journal: [Planned(proposal), Planned(proposal, 1)]);
        await Commit(store, empty, imported);
        var retained = await Read(await Open());
        retained.Proposals[0].Digest.ShouldBe(proposal.Digest);
        Rules.ProposalBytes(retained.Proposals[0]).ShouldBe(Rules.ProposalBytes(proposal));
        retained.Proposals[0].Operations.Select(o => o.Id).ShouldBe(["edit", "note"]);
        retained.Journal.Select(j => j.Status).ShouldAllBe(s => s == OpStatus.Planned);
        var skip = Next(retained, journal: [retained.Journal[0], retained.Journal[1].Begin(Guid.NewGuid()).Value]);
        (await store.CommitAsync(Connection, retained.Version, skip, default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        var wrongBinding = new Connection(Provider, Connection.ResourceScope, Connection.Destination, "other");
        (await store.CommitAsync(wrongBinding, 0, imported, default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await Read(store, wrongBinding)).Version.ShouldBe(0);
    }

    [Fact]
    public async Task EveryOperationPayloadRetainsCanonicalIdentityAndLocalEdgePreconditions()
    {
        var store = await Create();
        var proposal = new Proposal(Connection, "schema", 0,
        [
            Creation().Operations[0],
            new("batch", OpKind.Batch, Remote, Item().Revision,
                Fields(("clear", FieldValue.Null), ("empty", FieldValue.String(""))), type: "replacement-type"),
            new("local-link", OpKind.Relate, Local, other: Remote, relation: "parent",
                origin: RelationshipOrigin.Local, expectedLocalVersion: 17),
            new("native-unlink", OpKind.Unrelate, Remote, Item().Revision, other: Other, relation: "related",
                concurrency: ConcurrencyMode.ObservedNonAtomic),
            new("html-comment", OpKind.Comment, Remote, Item().Revision, body: FieldValue.RichText("<p>exact</p>", FieldKind.Html)),
            new("delete", OpKind.Delete, Other, new Revision(Other, "separate-opaque")),
        ]);
        await Commit(store, new(0), new(1, drafts: [Seed()], proposals: [proposal],
            journal: proposal.Operations.Select((_, ordinal) => Planned(proposal, ordinal))));
        var retained = (await Read(await Open())).Proposals.Single();
        retained.Digest.ShouldBe(proposal.Digest);
        Rules.ProposalBytes(retained).ShouldBe(Rules.ProposalBytes(proposal));
        retained.Operations[1].Fields["clear"].Kind.ShouldBe(FieldKind.Null);
        retained.Operations[1].Fields["empty"].Text.ShouldBe("");
        retained.Operations[2].Origin.ShouldBe(RelationshipOrigin.Local);
        retained.Operations[2].ExpectedLocalVersion.ShouldBe(17);
        retained.Operations[3].Concurrency.ShouldBe(ConcurrencyMode.ObservedNonAtomic);
        retained.Operations[5].ExpectedRevision!.Item.ShouldBe(Other);
    }

    [Fact]
    public async Task CompetingIndependentHandlesOnlyAcquireOneDurableAttempt()
    {
        var first = await Create();
        var proposal = Creation();
        await Commit(first, new(0), new(1, drafts: [Seed()], proposals: [proposal], journal: [Planned(proposal)]));
        var second = await Open();
        var observed1 = await Read(first);
        var observed2 = await Read(second);
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<long>> Race(IStore store, StoreSnapshot observed, Guid attempt) => Task.Run(async () =>
        {
            await gate.Task;
            return await store.CommitAsync(Connection, observed.Version,
                Next(observed, journal: [observed.Journal[0].Begin(attempt).Value]), default);
        });
        var a = Race(first, observed1, attempt1);
        var b = Race(second, observed2, attempt2);
        gate.SetResult();
        var results = await Task.WhenAll(a, b);
        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => !r.IsSuccess).Error.Code.ShouldBe(ErrorCode.Conflict);
        var winner = await Read(await Open());
        winner.Version.ShouldBe(2);
        winner.Journal[0].Status.ShouldBe(OpStatus.Applying);
        winner.Journal[0].Attempt.ShouldBe(results[0].IsSuccess ? attempt1 : attempt2);
        winner.Journal[0].Receipts.ShouldBeEmpty();
        (await first.CommitAsync(Connection, 1, Next(observed1), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task AcknowledgementMappingMirrorAndVerificationAreOneRollbackSafeDurableUnit()
    {
        var store = await Create();
        var proposal = Creation();
        var seed = Seed();
        var bench = new Bench(Guid.NewGuid(), "pin local alias", [new(SelectorKind.Pin, Local)]);
        await Commit(store, new(0), new(1, drafts: [seed], proposals: [proposal], journal: [Planned(proposal)], benches: [bench]));
        var imported = await Read(await Open());
        var applying = imported.Journal[0].Begin(Guid.NewGuid()).Value;
        await Commit(store, imported, Next(imported, journal: [applying]));
        var dispatched = await Read(await Open());
        dispatched.Journal[0].Attempt.ShouldBe(applying.Attempt);
        var applied = dispatched.Journal[0].Record(Receipt(applying, OpStatus.Applied, Remote)).Value;
        var mapping = new PublishMapping(Local, Remote, proposal.Digest, 0);
        var noMapping = Next(dispatched, journal: [applied], items: [Item()]);
        (await store.CommitAsync(Connection, dispatched.Version, noMapping, default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await Read(await Open())).Journal[0].Status.ShouldBe(OpStatus.Applying);

        var acknowledged = Next(dispatched, journal: [applied], mappings: [mapping], items: [Item()]);
        await Execute("""
            CREATE TRIGGER reject_commit AFTER UPDATE ON core_snapshots BEGIN
                SELECT RAISE(ABORT, 'injected disk-write failure after update');
            END;
            """);
        var failed = await store.CommitAsync(Connection, dispatched.Version, acknowledged, default);
        failed.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        failed.Error.Message.ShouldContain("rolled back");
        var rolledBack = await Read(await Open());
        rolledBack.Version.ShouldBe(dispatched.Version);
        rolledBack.Mappings.ShouldBeEmpty();
        rolledBack.Items.ShouldBeEmpty();
        rolledBack.Journal[0].Status.ShouldBe(OpStatus.Applying);
        rolledBack.Journal[0].Receipts.ShouldBeEmpty();
        await Execute("DROP TRIGGER reject_commit;");
        await Commit(store, rolledBack, acknowledged);
        var ack = await Read(await Open());
        ack.Journal[0].Status.ShouldBe(OpStatus.Applied);
        ack.Mappings[0].ShouldBe(mapping);
        ack.Items[0].Revision.ShouldBe(Item().Revision);
        var verified = ack.Journal[0].Record(Receipt(ack.Journal[0], OpStatus.Verified, Remote)).Value;
        await Commit(store, ack, Next(ack, journal: [verified], items: [Item("verified:opaque")]));
        var readback = await Read(await Open());
        readback.Journal[0].Status.ShouldBe(OpStatus.Verified);
        readback.Journal[0].Receipts.Select(r => r.Status).ShouldBe([OpStatus.Applied, OpStatus.Verified]);
        readback.Journal[0].Receipts[0].Evidence.ShouldBe(applied.Receipts[0].Evidence);
        readback.Items[0].Revision!.Token.ShouldBe("verified:opaque");
        readback.Drafts[0].Key.ShouldBe(Local);
        readback.Benches[0].Selectors[0].Item.ShouldBe(Local);
        (await store.CommitAsync(Connection, readback.Version, Next(readback, mappings: []), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        var rewritten = new PublishMapping(Local, Other, proposal.Digest, 0);
        (await store.CommitAsync(Connection, readback.Version, Next(readback, mappings: [rewritten]), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        await Commit(store, readback, Next(readback, items: []));
        var refreshed = await Read(await Open());
        refreshed.Mappings[0].ShouldBe(mapping);
        refreshed.Journal[0].Status.ShouldBe(OpStatus.Verified);
        refreshed.Drafts[0].Key.ShouldBe(Local);
        refreshed.Benches[0].Id.ShouldBe(bench.Id);
    }

    [Theory]
    [InlineData(OpStatus.Applying)]
    [InlineData(OpStatus.Indeterminate)]
    [InlineData(OpStatus.Partial)]
    [InlineData(OpStatus.Failed)]
    public async Task AttemptFenceSurvivesReopenAndReplacementProposalWithoutBlindReplay(OpStatus outcome)
    {
        var store = await Create();
        var proposal = Creation();
        await Commit(store, new(0), new(1, drafts: [Seed()], proposals: [proposal], journal: [Planned(proposal)]));
        var imported = await Read(store);
        var applying = imported.Journal[0].Begin(Guid.NewGuid()).Value;
        await Commit(store, imported, Next(imported, journal: [applying]));
        var attempted = await Read(await Open());
        if (outcome != OpStatus.Applying)
        {
            var terminal = attempted.Journal[0].Record(Receipt(attempted.Journal[0], outcome)).Value;
            await Commit(store, attempted, Next(attempted, journal: [terminal]));
            attempted = await Read(await Open());
        }
        attempted.Journal[0].Status.ShouldBe(outcome);
        attempted.Journal[0].Begin(Guid.NewGuid()).IsSuccess.ShouldBeFalse();
        var replacement = Creation(attempted.Version, "new schema");
        replacement.Digest.ShouldNotBe(proposal.Digest);
        await Commit(store, attempted, Next(attempted, proposals: [proposal, replacement],
            journal: [attempted.Journal[0], Planned(replacement)]));
        var retained = await Read(await Open());
        var replay = Next(retained, journal: [retained.Journal[0], retained.Journal[1].Begin(Guid.NewGuid()).Value]);
        var result = await store.CommitAsync(Connection, retained.Version, replay, default);
        result.Error.Code.ShouldBe(ErrorCode.Conflict);
        result.Error.Evidence.ShouldContain(e => e.Contains("replacement digest", StringComparison.Ordinal));
        var unchanged = await Read(await Open());
        unchanged.Version.ShouldBe(retained.Version);
        unchanged.Journal[0].Attempt.ShouldBe(applying.Attempt);
        unchanged.Journal[0].Status.ShouldBe(outcome);
        unchanged.Journal[1].Status.ShouldBe(OpStatus.Planned);
        (await store.CommitAsync(Connection, retained.Version, Next(retained, proposals: [replacement],
            journal: [retained.Journal[1]]), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task UnavailableReadbackRetainsAcknowledgementMappingAndStructuredErrorEvidence()
    {
        var store = await Create();
        var proposal = Creation();
        await Commit(store, new(0), new(1, drafts: [Seed()], proposals: [proposal], journal: [Planned(proposal)]));
        var imported = await Read(store);
        await Commit(store, imported, Next(imported, journal: [imported.Journal[0].Begin(Guid.NewGuid()).Value]));
        var applying = await Read(store);
        var applied = applying.Journal[0].Record(Receipt(applying.Journal[0], OpStatus.Applied, Remote)).Value;
        await Commit(store, applying, Next(applying, journal: [applied], mappings: [new(Local, Remote, proposal.Digest, 0)]));
        var ack = await Read(await Open());
        var unknown = ack.Journal[0].Record(Receipt(ack.Journal[0], OpStatus.Indeterminate)).Value;
        await Commit(store, ack, Next(ack, journal: [unknown]));
        var reopened = await Read(await Open());
        reopened.Journal[0].Receipts.Length.ShouldBe(2);
        reopened.Journal[0].Receipts[1].Error!.Evidence.ShouldBe(["timeout is not non-dispatch"]);
        reopened.Journal[0].Record(Receipt(reopened.Journal[0], OpStatus.Failed)).IsSuccess.ShouldBeFalse();
        await Commit(store, reopened, Next(reopened));
        var retained = await Read(await Open());
        retained.Mappings[0].Remote.ShouldBe(Remote);
        retained.Journal[0].Status.ShouldBe(OpStatus.Indeterminate);
    }

    [Fact]
    public async Task LocalRelationshipTombstoneAndCasSurviveProviderMirrorReplacement()
    {
        var store = await Create();
        var local = new Relationship(Remote, Other, "related", RelationshipOrigin.Local, 1);
        var provider = new Relationship(Remote, Other, "related", RelationshipOrigin.Provider);
        await Commit(store, new(0), new(1, items: [Item()], relationships: [local, provider], drafts: [Seed()]));
        var before = await Read(await Open());
        before.Relationships.Select(r => r.Origin).ShouldBe([RelationshipOrigin.Local, RelationshipOrigin.Provider]);
        var tombstone = local with { Deleted = true, LocalVersion = 2 };
        await Commit(store, before, Next(before, relationships: [tombstone, provider]));
        var deleted = await Read(await Open());
        (await store.CommitAsync(Connection, deleted.Version,
            Next(deleted, relationships: [tombstone with { Deleted = false }, provider]), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await store.CommitAsync(Connection, deleted.Version, Next(deleted, relationships: [provider]), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await store.CommitAsync(Connection, deleted.Version, Next(deleted, relationships: [local, provider]), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        await Commit(store, deleted, Next(deleted, items: [Item("new mirror")], relationships: [tombstone]));
        var refreshed = await Read(await Open());
        refreshed.Relationships.Single().ShouldBe(tombstone);
        refreshed.Drafts.Single().Key.ShouldBe(Local);
        await Commit(store, refreshed, Next(refreshed, relationships: [tombstone with { Deleted = false, LocalVersion = 3 }]));
        (await Read(await Open())).Relationships.Single().LocalVersion.ShouldBe(3);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("journal")]
    public async Task MalformedPersistedDocumentsAreExplicitFailuresNotEmptyFallbacks(string corruption)
    {
        var store = await Create();
        var proposal = Creation();
        await Commit(store, new(0), new(1, drafts: [Seed()], proposals: [proposal], journal: [Planned(proposal)]));
        var expression = corruption switch
        {
            "unknown" => "json_set(document, '$.Unexpected', 1)",
            "duplicate" => "'{\"Version\":1,' || substr(document, 2)",
            "missing" => "json_remove(document, '$.Drafts')",
            "version" => "json_set(document, '$.Version', 999)",
            "digest" => "json_set(document, '$.Proposals[0].Digest', 'changed')",
            "journal" => "json_set(document, '$.Journal[0].Status', 3)",
            _ => throw new InvalidOperationException(),
        };
        await Execute($"UPDATE core_snapshots SET document={expression};");
        (await store.ReadAsync(Connection, default)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await store.CommitAsync(Connection, 1, new(2), default)).Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
    }

    [Fact]
    public async Task CancellationAndInvalidVersionCommitNothing()
    {
        var store = await Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => store.CommitAsync(Connection, 0, new(1), cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => store.ReadAsync(Connection, cts.Token));
        (await store.CommitAsync(Connection, 0, new(2), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await Read(await Open())).Version.ShouldBe(0);
    }
}
