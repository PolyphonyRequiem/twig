using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class CurrentCoreRemoteWriteAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-core-write-admission-" + Guid.NewGuid().ToString("N"));
    private string Registry => Path.Combine(root, "system.db");

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task CurrentCoreSourceRequiresItsRecordedApplyingAttempt(bool recordAttempt, bool foreignStore, bool scopedStore)
    {
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        using var registry = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        Required(await registry.UpsertConnectionAsync("connection", "org", "project", null));
        Required(await registry.UpsertWorktreeAsync("worktree", "connection", root));
        Required(await registry.InsertIdentityAsync(new("identity", "actor", "tenant", "object", "issuer", "authority",
            "credential", null, now, now)));
        Required(await registry.InsertOrGetBindingAsync(new("binding", "connection", "identity", 1, now, now)));
        var origin = new ResolvedConnectionBinding(new("binding", "connection", "identity", 1),
            new("identity", "actor", "tenant", "object", "issuer", "authority", "credential", null), root,
            "checkout-binding-pin", 0, new("org", "project", "", "worktree", 0, "", "", "", ""));
        var connection = new Connection(new("ado"), "collection", "destination", CleanCoreAdoAccess.Identity(origin, "principal"));
        var store = Required(await SqliteStore.CreateAsync(Path.Combine(root, "core.db")));
        Required(await store.PinWorkspaceAsync(connection));
        var originSource = new NativeCoreStoreReference(store.InstanceId, store.DatabasePath, root, connection);
        Required(await NativeCoreExecution.RegisterAsync(Registry, originSource, default));
        if (scopedStore)
        {
            store = Required(await SqliteStore.CreateAsync(Path.Combine(root, "scope.db")));
            Required(await store.PinWorkspaceAsync(connection));
        }
        var source = new NativeCoreStoreReference(store.InstanceId, store.DatabasePath, root, connection);
        Required(await NativeCoreExecution.RegisterAsync(Registry, source, default));
        var inventory = Required(await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [originSource, source]));
        Required(await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true));

        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var operation = new Op("edit", OpKind.Batch, key, new(key, "ado-rev:1"),
            FieldValues.Empty.Set("System.Title", FieldValue.String("changed")));
        var proposal = new Proposal(connection, "schema", 0, [operation]);
        var planned = JournalEntry.Planned(proposal.Digest, 0);
        Required(await store.CommitAsync(connection, 0, new(1, proposals: [proposal], journal: [planned]), default));
        var applying = Required(planned.Begin(Guid.NewGuid()));
        Required(await store.CommitAsync(connection, 1, new(2, proposals: [proposal], journal: [applying]), default));
        IDisposable? targets = null;
        try
        {
            if (recordAttempt)
                targets = Required(await NativeCoreExecution.AdmitAsync(Registry, source, proposal, 0, applying.Attempt, default));
            origin = origin with { StorageGeneration = "core:" + originSource.Instance.ToString("N") };
            var correlation = new CoreWriteCorrelation(CleanCoreAdoAccess.ConnectionText(connection), proposal.Digest,
                0, applying.Attempt, CleanCoreAdoAccess.ItemText(key), null, "ado-rev:1",
                foreignStore ? Guid.NewGuid() : store.InstanceId);
            var request = new ConnectionRemoteWriteRequest("workitem-update", "PATCH",
                "https://dev.azure.com/org/project/_apis/wit/workitems/42?api-version=7.1", "[]", null, CoreCorrelation: correlation);
            var intent = new ConnectionRemoteWriteIntent(1, Guid.NewGuid().ToString("N"), "digest", "worktree", origin, request, now);
            var admitted = await registry.BeginConnectionRemoteWriteAsync(intent);
            if (recordAttempt && !foreignStore)
            {
                admitted.IsSuccess.ShouldBeTrue(admitted.IsSuccess ? "" : admitted.Error.Message);
                var replay = await registry.BeginConnectionRemoteWriteAsync(intent with { IntentId = Guid.NewGuid().ToString("N") });
                replay.IsSuccess.ShouldBeFalse();
                replay.Error.Message.ShouldContain("remote-write-core-attempt-fenced");
            }
            else
            {
                admitted.IsSuccess.ShouldBeFalse();
                admitted.Error.Message.ShouldContain(foreignStore ? "remote-write-core-source-mismatch" : "remote-write-core-attempt-unavailable");
                Required(await registry.ReadConnectionRemoteWritesAsync("worktree")).ShouldBeEmpty();
            }
        }
        finally { targets?.Dispose(); }
    }

    private static T Required<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        return result.Value;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
