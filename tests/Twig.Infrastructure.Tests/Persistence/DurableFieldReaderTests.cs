using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Epic #1168, split layout: durable rows hold only local-owned or edited values, remote values
/// are read from the mirror at read time, and a sync never moves the local revision. Every test
/// drives the real composition: opted-in mirror, identity projection, durable store and reader.
/// </summary>
public sealed class DurableFieldReaderTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";

    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteLocalWorkItemRepository _local;
    private readonly SqliteWorkItemRepository _mirror;
    private readonly SqliteFieldDefinitionStore _definitions;

    public DurableFieldReaderTests()
    {
        _local = new SqliteLocalWorkItemRepository(_store);
        _mirror = new SqliteWorkItemRepository(
            _store, new WorkItemMapper(), new AdoMirrorIdentityProjection(_local, Scope));
        _definitions = new SqliteFieldDefinitionStore(_store);
    }

    public void Dispose() => _store.Dispose();

    private static Func<string?> Returns(string? scope) => () => scope;

    private DurableFieldReader Reader(string? scope = Scope)
        => DurableFieldReader.Create(neutralStoreOptedIn: true, _local, _mirror, _definitions, Returns(scope))!;

    private static ExternalWorkItemIdentity Ado(string nativeId, string scope = Scope) => new("ado", scope, nativeId);

    private async Task<LocalWorkItemId> SyncAsync(
        int id, string title, int revision, Action<WorkItemBuilder>? configure = null, bool legacyEditAfterSync = false)
    {
        var builder = new WorkItemBuilder(id, title).InState("Active");
        configure?.Invoke(builder);
        var item = builder.Build();
        item.MarkSynced(revision);
        if (legacyEditAfterSync)
            item.SetDirty();
        item.IsDirty.ShouldBe(legacyEditAfterSync);
        await _mirror.SaveAsync(item);
        return (await _local.FindAsync(Ado(id.ToString())))!.Value;
    }

    private string DurableFieldsJson(LocalWorkItemId id)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT fields_json FROM pending.local_work_items WHERE local_id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return (string)cmd.ExecuteScalar()!;
    }

    private void EvictMirrorRow(int id)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "DELETE FROM work_items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery().ShouldBe(1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void WithoutOptInOrScopeSource_NoReaderIsCreated(bool optedIn, bool hasSource)
        => DurableFieldReader.Create(optedIn, _local, _mirror, _definitions, hasSource ? Returns(Scope) : null).ShouldBeNull();

    [Fact]
    public async Task RemoteFields_AreReadFromTheMirror_WithDeclaredTypesAndNoGuessing()
    {
        await _definitions.SaveBatchAsync([new FieldDefinition("Custom.Points", "Points", "double", IsReadOnly: false)]);
        var id = await SyncAsync(42, "Remote title", 3, b => b
            .AssignedTo("Daniel").AssignedToUniqueName("daniel@example.com")
            .WithField("Custom.Points", "3")
            .WithField("Custom.Undeclared", "x"));

        var composed = (await Reader().ReadAsync(id))!;

        composed.Remote.ShouldBe(RemoteFieldSource.Mirrored);
        composed.Fields["System.Title"].Value.ShouldBe(new RawValue("Remote title", DurableFieldReader.UndeclaredDataType));
        composed.Fields["System.State"].Value.ShouldBe(new RawValue("Active", DurableFieldReader.UndeclaredDataType));
        composed.Fields["Custom.Points"].Value.ShouldBe(new RawValue("3", "double"));
        composed.Fields["Custom.Undeclared"].Value.ShouldBe(new RawValue("x", DurableFieldReader.UndeclaredDataType));
        composed.Fields["System.AssignedTo"].Value.ShouldBe(new IdentityValue("daniel@example.com", "Daniel"));
        composed.Local.Fields.ShouldBe(FieldValues.Empty);
    }

    [Fact]
    public async Task LocalValues_WinOverRemote_IncludingAnExplicitClear()
    {
        var id = await SyncAsync(42, "Remote title", 3, b => b.WithField("Custom.Points", "3"));
        var edit = FieldValues.Empty
            .Set("System.Title", new TextValue("Local title"))
            .Set("Custom.Points", NullValue.Instance);
        (await _local.UpdateAsync(id, 1, edit)).Value.ShouldBeOfType<LocalWriteApplied>();

        var composed = (await Reader().ReadAsync(id))!;

        composed.Fields["System.Title"].Value.ShouldBe(new TextValue("Local title"));
        composed.Fields["Custom.Points"].Value.ShouldBe(NullValue.Instance);
        composed.Fields["System.State"].Value.ShouldBe(new RawValue("Active", DurableFieldReader.UndeclaredDataType));
        composed.Local.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Sync_NeverMovesTheLocalRevision_OrTheDurableFields()
    {
        var id = await SyncAsync(42, "Remote title", 3);
        (await _local.UpdateAsync(id, 1, FieldValues.Empty.Set("Custom.Note", new TextValue("mine")))).Value.ShouldBeOfType<LocalWriteApplied>();
        var before = (await _local.GetAsync(id))!;
        var jsonBefore = DurableFieldsJson(id);

        (await SyncAsync(42, "Renamed remotely", 9, b => b.InState("Closed"))).ShouldBe(id);

        var after = (await _local.GetAsync(id))!;
        after.Revision.ShouldBe(before.Revision);
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        DurableFieldsJson(id).ShouldBe(jsonBefore);
        (await _local.GetObservedTokenAsync(Ado("42")))!.Token.ShouldBe(new ProviderRevisionToken("9"));

        var composed = (await Reader().ReadAsync(id))!;
        composed.Fields["System.Title"].Value.ShouldBe(new RawValue("Renamed remotely", DurableFieldReader.UndeclaredDataType));
        composed.Fields["Custom.Note"].Value.ShouldBe(new TextValue("mine"));
    }

    [Fact]
    public async Task AfterSync_TheLocalCompareAndSwapStillUsesTheLocalRevision()
    {
        var id = await SyncAsync(42, "Remote title", 3);
        (await _local.UpdateAsync(id, 1, FieldValues.Empty.Set("Custom.Note", new TextValue("a")))).Value.ShouldBeOfType<LocalWriteApplied>();
        await SyncAsync(42, "Remote title", 4);

        (await _local.UpdateAsync(id, 1, FieldValues.Empty)).Value.ShouldBeOfType<LocalRevisionConflict>();
        (await _local.UpdateAsync(id, 2, FieldValues.Empty.Set("Custom.Note", new TextValue("b")))).Value.ShouldBeOfType<LocalWriteApplied>();
    }

    [Fact]
    public async Task Reading_NeverCopiesRemoteValuesIntoTheDurableFile()
    {
        var id = await SyncAsync(42, "Remote title", 3, b => b.WithField("Custom.Points", "3"));
        var jsonBefore = DurableFieldsJson(id);

        (await Reader().ReadAsync(id))!.Fields.Count.ShouldBeGreaterThan(0);

        DurableFieldsJson(id).ShouldBe(jsonBefore);
        var local = (await _local.GetAsync(id))!;
        local.Fields.ShouldBe(FieldValues.Empty);
        local.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task EvictedMirrorRow_LeavesOnlyLocalValues_AndTheyReturnAfterRebuild()
    {
        var id = await SyncAsync(42, "Remote title", 3);
        (await _local.UpdateAsync(id, 1, FieldValues.Empty.Set("Custom.Note", new TextValue("mine")))).Value.ShouldBeOfType<LocalWriteApplied>();
        EvictMirrorRow(42);

        var evicted = (await Reader().ReadAsync(id))!;
        evicted.Remote.ShouldBe(RemoteFieldSource.NotMirrored);
        evicted.Fields.ShouldBe(FieldValues.Empty.Set("Custom.Note", new TextValue("mine")));

        (await SyncAsync(42, "Rebuilt", 4)).ShouldBe(id);
        var rebuilt = (await Reader().ReadAsync(id))!;
        rebuilt.Remote.ShouldBe(RemoteFieldSource.Mirrored);
        rebuilt.Fields["System.Title"].Value.ShouldBe(new RawValue("Rebuilt", DurableFieldReader.UndeclaredDataType));
        rebuilt.Fields["Custom.Note"].Value.ShouldBe(new TextValue("mine"));
    }

    [Theory]
    [InlineData("99999999-2222-3333-4444-555555555555")]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task MirrorRow_IsNotComposed_UnlessTheIdentityIsInTheMirrorsScope(string? mirrorScope)
    {
        var id = await SyncAsync(42, "Remote title", 3);

        var composed = (await Reader(mirrorScope).ReadAsync(id))!;

        composed.Remote.ShouldBe(RemoteFieldSource.NotRead);
        composed.Fields.ShouldBe(FieldValues.Empty);
    }

    [Fact]
    public async Task TombstonedItem_ReadsNoRemoteValues()
    {
        var id = await SyncAsync(42, "Remote title", 3);
        (await _local.DeleteAsync(id, 1)).Value.ShouldBeOfType<LocalWriteApplied>();

        var composed = (await Reader().ReadAsync(id))!;

        composed.Local.IsDeleted.ShouldBeTrue();
        composed.Remote.ShouldBe(RemoteFieldSource.NotRead);
        composed.Fields.ShouldBe(FieldValues.Empty);
    }

    [Fact]
    public async Task UnknownLocalId_ReadsAsNull()
        => (await Reader().ReadAsync(LocalWorkItemId.New())).ShouldBeNull();

    [Fact]
    public async Task LegacyDirtyMirrorRow_IsSurfaced_NotPresentedAsClean()
    {
        var id = await SyncAsync(42, "Remote title", 3, legacyEditAfterSync: true);

        (await Reader().ReadAsync(id))!.Remote.ShouldBe(RemoteFieldSource.MirroredWithLegacyEdits);
    }

    [Fact]
    public async Task NonCanonicalAdoNativeId_IsRejected_NotReadAsAnotherItem()
    {
        await SyncAsync(42, "Remote title", 3);
        var resolution = await _local.ResolveAsync(Ado("042"), observedToken: null);

        await Should.ThrowAsync<InvalidOperationException>(() => Reader().ReadAsync(resolution.Id));
    }
}
