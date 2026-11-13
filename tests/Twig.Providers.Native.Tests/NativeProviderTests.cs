using Shouldly;
using Twig.Core;
using Twig.Providers.Native;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Providers.Native.Tests;

public sealed class NativeProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-native-" + Guid.NewGuid().ToString("N"));
    public NativeProviderTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Published_identity_is_authoritative_across_restart_not_a_draft_or_mirror()
    {
        var (authority, mirror, provider) = await Open();
        var (proposal, seed) = await Seed(provider, mirror);
        (await provider.ImportAsync(proposal, proposal.Digest, default)).IsSuccess.ShouldBeTrue();
        var applied = (await provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        applied.Status.ShouldBe(OpStatus.Applied);
        applied.PublishedKey!.IsLocal.ShouldBeFalse();
        applied.PublishedKey.Provider!.Value.ShouldBe("twig");
        applied.PublishedKey.ShouldNotBe(seed);
        var verified = (await provider.VerifyAsync(proposal, applied, default)).Value;
        verified.Status.ShouldBe(OpStatus.Verified);
        var reopened = (await NativeAuthority.OpenAsync(Path.Combine(root, "authority.db"))).Value;
        reopened.Connection.ShouldBe(authority.Connection);
        var emptyMirror = (await SqliteStore.CreateAsync(Path.Combine(root, "other-mirror.db"))).Value;
        var restarted = new NativeProvider(reopened, emptyMirror);
        var read = (await restarted.ReadAsync(reopened.Connection, [applied.PublishedKey], default)).Value;
        read.Items.Single().Fields[NativeProvider.Title].Text.ShouldBe("A durable item");
        (await emptyMirror.ReadAsync(reopened.Connection, default)).Value.Items.ShouldBeEmpty();
        (await restarted.ReadAsync(reopened.Connection, [seed], default)).IsSuccess.ShouldBeFalse();
        (await provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Wrong_digest_binding_and_revision_never_write()
    {
        var (authority, mirror, provider) = await Open();
        var (proposal, _) = await Seed(provider, mirror);
        (await provider.ImportAsync(proposal, "not-the-digest", default)).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        var wrong = authority.Connection with { };
        wrong = new(wrong.Provider, wrong.ResourceScope, "another-destination", wrong.IdentityRef);
        (await provider.ReadAsync(wrong, [], default)).IsSuccess.ShouldBeFalse();
        (await provider.ImportAsync(proposal, proposal.Digest, default)).IsSuccess.ShouldBeTrue();
        var applied = (await provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        (await provider.VerifyAsync(proposal, applied, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var original = (await provider.ReadAsync(authority.Connection, [applied.PublishedKey!], default)).Value.Items.Single();
        var before = (await mirror.ReadAsync(authority.Connection, default)).Value;
        var changes = FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("Changed"));
        var edit = new Proposal(authority.Connection, proposal.SchemaFingerprint, before.Version,
            [new("edit", OpKind.Batch, original.Key, original.Revision, changes)]);
        (await provider.ImportAsync(edit, edit.Digest, default)).IsSuccess.ShouldBeTrue();
        var ack = (await provider.ApplyAsync(edit, 0, Guid.NewGuid(), default)).Value;
        (await provider.VerifyAsync(edit, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        before = (await mirror.ReadAsync(authority.Connection, default)).Value;
        var stale = new Proposal(authority.Connection, proposal.SchemaFingerprint, before.Version,
            [new("stale", OpKind.Batch, original.Key, original.Revision, changes)]);
        (await provider.ImportAsync(stale, stale.Digest, default)).IsSuccess.ShouldBeFalse();
        (await provider.ReadAsync(authority.Connection, [original.Key], default)).Value.Items.Single()
            .Fields[NativeProvider.Title].Text.ShouldBe("Changed");
    }

    [Fact]
    public async Task Interrupted_fence_without_authority_receipt_is_not_replayed()
    {
        var (_, mirror, provider) = await Open();
        var (proposal, _) = await Seed(provider, mirror);
        (await provider.ImportAsync(proposal, proposal.Digest, default)).IsSuccess.ShouldBeTrue();
        var before = (await mirror.ReadAsync(proposal.Connection, default)).Value;
        var attempt = Guid.NewGuid();
        var began = before.Journal.Single().Begin(attempt).Value;
        var next = new StoreSnapshot(before.Version + 1, before.Items, before.Relationships, before.Drafts,
            before.Proposals, [began], before.Mappings, before.Benches);
        (await mirror.CommitAsync(proposal.Connection, before.Version, next, default)).IsSuccess.ShouldBeTrue();
        (await provider.RecoverAsync(proposal, 0, default)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        (await provider.ApplyAsync(proposal, 0, attempt, default)).IsSuccess.ShouldBeFalse();
        before = (await mirror.ReadAsync(proposal.Connection, default)).Value;
        var replacement = new Proposal(proposal.Connection, proposal.SchemaFingerprint, before.Version,
            [new("replacement-digest", OpKind.Create, proposal.Operations[0].Item, fields: proposal.Operations[0].Fields,
                type: NativeProvider.ItemType, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        replacement.Digest.ShouldNotBe(proposal.Digest);
        (await provider.ImportAsync(replacement, replacement.Digest)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await mirror.ReadAsync(proposal.Connection, default)).Value.Journal.Single().Digest.ShouldBe(proposal.Digest);
        var page = await provider.QueryAsync(proposal.Connection, new(QueryKind.All), null, default);
        page.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Replaced_authority_and_wrong_principal_never_adopt_old_bindings()
    {
        var (authority, mirror, provider) = await Open();
        var (proposal, _) = await Seed(provider, mirror);
        (await provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var wrongOwner = new Connection(authority.Connection.Provider, authority.Connection.ResourceScope,
            authority.Connection.Destination, "another-principal");
        (await provider.DiscoverSchemaAsync(wrongOwner, default)).IsSuccess.ShouldBeFalse();
        var replacementPath = Path.Combine(root, "replacement.db");
        var replacement = (await NativeAuthority.CreateAsync(replacementPath)).Value;
        replacement.Connection.ShouldNotBe(authority.Connection);
        File.Move(replacementPath, authority.DatabasePath, overwrite: true);
        (await provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        var observed = new NativeProvider((await NativeAuthority.OpenAsync(authority.DatabasePath)).Value, mirror);
        (await observed.QueryAsync(observed.Connection, new(QueryKind.All), null, default)).Value.Items.ShouldBeEmpty();
        (await mirror.ReadAsync(authority.Connection, default)).Value.Journal.Single().Status.ShouldBe(OpStatus.Planned);
    }

    private async Task<(NativeAuthority, SqliteStore, NativeProvider)> Open()
    {
        var authority = (await NativeAuthority.CreateAsync(Path.Combine(root, "authority.db"))).Value;
        var mirror = (await SqliteStore.CreateAsync(Path.Combine(root, "mirror.db"))).Value;
        return (authority, mirror, new NativeProvider(authority, mirror));
    }

    [Fact]
    public async Task Authority_commit_survives_lost_client_ack_and_recovers_only_original_effect()
    {
        var (authority, mirror, provider) = await Open();
        var (proposal, seed) = await Seed(provider, mirror);
        (await provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var attempt = Guid.NewGuid();
        var interrupted = new NativeProvider(authority, new RejectAppliedStore(mirror));
        (await interrupted.ApplyAsync(proposal, 0, attempt, default)).IsSuccess.ShouldBeFalse();
        (await mirror.ReadAsync(authority.Connection, default)).Value.Journal.Single().Status.ShouldBe(OpStatus.Applying);
        var reopened = (await NativeAuthority.OpenAsync(authority.DatabasePath)).Value;
        var restarted = new NativeProvider(reopened, mirror);
        var receipt = (await restarted.RecoverAsync(proposal, 0)).Value;
        receipt.Status.ShouldBe(OpStatus.Applied);
        receipt.Attempt.ShouldBe(attempt);
        (await restarted.VerifyAsync(proposal, receipt, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var snapshot = (await mirror.ReadAsync(authority.Connection, default)).Value;
        snapshot.Mappings.Single().Local.ShouldBe(seed);
        snapshot.Mappings.Single().Remote.ShouldBe(receipt.PublishedKey);
        (await restarted.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        (await restarted.QueryAsync(authority.Connection, new(QueryKind.All), null, default)).Value.Items.Length.ShouldBe(1);
        (await NativeAuthority.OpenAsync(mirror.DatabasePath)).IsSuccess.ShouldBeFalse();
    }

    private sealed class RejectAppliedStore(IStore inner) : IStore
    {
        public Task<Result<StoreSnapshot>> ReadAsync(Connection connection, CancellationToken ct) => inner.ReadAsync(connection, ct);
        public Task<Result<long>> CommitAsync(Connection connection, long expectedVersion, StoreSnapshot next, CancellationToken ct)
            => next.Journal.Any(entry => entry.Status == OpStatus.Applied)
                ? Task.FromResult(Result<long>.Fail(ErrorCode.StorageUnavailable, "Simulated lost client acknowledgement."))
                : inner.CommitAsync(connection, expectedVersion, next, ct);
    }

    private static async Task<(Proposal, ItemKey)> Seed(NativeProvider provider, SqliteStore mirror)
    {
        var key = ItemKey.Local(Guid.NewGuid());
        var fields = FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("A durable item"))
            .Set(NativeProvider.Description, FieldValue.RichText("Details", FieldKind.Markdown))
            .Set(NativeProvider.StateField, FieldValue.String("Open"));
        var connection = provider.Connection;
        (await mirror.CommitAsync(connection, 0, new(1, drafts: [new(key, NativeProvider.ItemType, null, fields)]), default))
            .IsSuccess.ShouldBeTrue();
        var schema = (await provider.DiscoverSchemaAsync(connection, default)).Value;
        return (new(connection, schema.Fingerprint, 1,
            [new("create", OpKind.Create, key, fields: fields, type: NativeProvider.ItemType,
                concurrency: ConcurrencyMode.ObservedNonAtomic)]), key);
    }
}
