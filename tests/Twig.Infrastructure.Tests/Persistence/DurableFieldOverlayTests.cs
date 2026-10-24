using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Epic #1168: the display overlay composes a mirrored item from the durable store through the
/// registered composition (mirror factory, projection, shared scope, reader). It is a pass-through
/// when not opted in, shares the projection's single scope resolution, surfaces failures, and never
/// writes either store.
/// </summary>
public sealed class DurableFieldOverlayTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";

    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteLocalWorkItemRepository _local;
    private readonly SqliteFieldDefinitionStore _definitions;

    public DurableFieldOverlayTests()
    {
        _local = new SqliteLocalWorkItemRepository(_store);
        _definitions = new SqliteFieldDefinitionStore(_store);
    }

    public void Dispose() => _store.Dispose();

    private sealed class CountingProvider(Func<string?> scope) : IWorkItemProvider
    {
        public int Calls { get; private set; }
        public ProviderCapabilityDescriptor Capabilities => throw new NotSupportedException();

        public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(scope());
        }
    }

    private (IWorkItemRepository Mirror, DurableFieldOverlay Overlay) Compose(IWorkItemProvider provider, bool optedIn = true)
    {
        var services = new ServiceCollection().AddSingleton(_store).AddSingleton(provider).BuildServiceProvider();
        var mirror = TwigServiceRegistration.CreateMirrorRepository(services, optedIn);
        return (mirror, DurableFieldOverlay.Create(mirror, _store, _definitions));
    }

    private static WorkItem Synced(int id, string title, int revision = 3)
    {
        var item = new WorkItemBuilder(id, title).InState("Active")
            .AssignedTo("Remote Person").AssignedToUniqueName("remote@example.com")
            .WithField("Custom.Points", "3")
            .Build();
        item.MarkSynced(revision);
        return item;
    }

    private async Task<LocalWorkItemId> EditLocallyAsync(int id, FieldValues edit)
    {
        var localId = (await _local.FindAsync(new ExternalWorkItemIdentity("ado", Scope, id.ToString())))!.Value;
        var current = (await _local.GetAsync(localId))!;
        (await _local.UpdateAsync(localId, current.Revision, edit)).Value.ShouldBeOfType<LocalWriteApplied>();
        return localId;
    }

    private (long Revision, string UpdatedAt, string FieldsJson) DurableRow(LocalWorkItemId id)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT revision, updated_at, fields_json FROM pending.local_work_items WHERE local_id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    [Fact]
    public async Task OptedIn_ShowsDurableValues_OverTheMirror_WithoutDirtyingOrWriting()
    {
        var (mirror, overlay) = Compose(new CountingProvider(() => Scope));
        await mirror.SaveAsync(Synced(42, "Remote title"));
        var localId = await EditLocallyAsync(42, FieldValues.Empty
            .Set("System.Title", new TextValue("Local title"))
            .Set("System.State", new TextValue("Doing"))
            .Set("System.AssignedTo", new IdentityValue("local@example.com", "Local Person"))
            .Set("Custom.Points", new IntegerValue(8))
            .Set("Custom.Note", new TextValue("mine")));
        var durableBefore = DurableRow(localId);

        var remote = (await mirror.GetByIdAsync(42))!;
        var shown = await overlay.ApplyAsync(remote);

        overlay.IsEnabled.ShouldBeTrue();
        shown.Title.ShouldBe("Local title");
        shown.State.ShouldBe("Doing");
        shown.AssignedTo.ShouldBe("Local Person");
        shown.AssignedToUniqueName.ShouldBe("local@example.com");
        shown.Fields["Custom.Points"].ShouldBe("8");
        shown.Fields["Custom.Note"].ShouldBe("mine");
        shown.Revision.ShouldBe(3);
        shown.IsDirty.ShouldBeFalse();

        remote.Title.ShouldBe("Remote title");
        (await mirror.GetByIdAsync(42))!.Title.ShouldBe("Remote title");
        DurableRow(localId).ShouldBe(durableBefore);
    }

    [Fact]
    public async Task NotOptedIn_IsAPassThrough_ThatNeverResolvesTheProvider()
    {
        var provider = new CountingProvider(() => Scope);
        var (mirror, overlay) = Compose(provider, optedIn: false);
        await mirror.SaveAsync(Synced(42, "Remote title"));
        var remote = (await mirror.GetByIdAsync(42))!;

        (await overlay.ApplyAsync(remote)).ShouldBeSameAs(remote);

        overlay.IsEnabled.ShouldBeFalse();
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ScopeIsResolvedOnce_SharedBetweenProjectionOverlayAndReader()
    {
        var provider = new CountingProvider(() => Scope);
        var (mirror, overlay) = Compose(provider);
        await mirror.SaveAsync(Synced(42, "Remote title"));
        await EditLocallyAsync(42, FieldValues.Empty.Set("System.Title", new TextValue("Local title")));

        for (var i = 0; i < 3; i++)
            (await overlay.ApplyAsync((await mirror.GetByIdAsync(42))!)).Title.ShouldBe("Local title");

        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ScopeIsResolvedOnce_WhenTheOverlayReadsFirst()
    {
        var provider = new CountingProvider(() => Scope);
        var (mirror, overlay) = Compose(provider);
        var unsaved = Synced(42, "Remote title");

        (await overlay.ApplyAsync(unsaved)).ShouldBeSameAs(unsaved);
        await mirror.SaveAsync(unsaved);

        provider.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task NullScope_ShowsTheMirrorUnchanged()
    {
        var (mirror, overlay) = Compose(new CountingProvider(() => null));
        await mirror.SaveAsync(Synced(42, "Remote title"));
        var remote = (await mirror.GetByIdAsync(42))!;

        (await overlay.ApplyAsync(remote)).ShouldBeSameAs(remote);
    }

    [Fact]
    public async Task ScopeTransportFailure_Surfaces_AndTheNextReadRetries()
    {
        var calls = 0;
        var provider = Substitute.For<IWorkItemProvider>();
        provider.ResolveResourceScopeAsync(Arg.Any<CancellationToken>()).Returns(_ => ++calls == 1
            ? Task.FromException<string?>(new HttpRequestException("transport"))
            : Task.FromResult<string?>(Scope));
        var (_, overlay) = Compose(provider);
        var item = Synced(42, "Remote title");

        await Should.ThrowAsync<HttpRequestException>(() => overlay.ApplyAsync(item));
        (await overlay.ApplyAsync(item)).ShouldBeSameAs(item);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task ProviderGateRefusal_Surfaces_FromTheRead()
    {
        var services = new ServiceCollection().AddSingleton(_store)
            .AddSingleton<IWorkItemProvider>(_ => throw new TwigConfigurationException(
                "provider-unavailable: github", new NotSupportedException()))
            .BuildServiceProvider();
        var mirror = TwigServiceRegistration.CreateMirrorRepository(services, neutralStoreOptedIn: true);
        var overlay = DurableFieldOverlay.Create(mirror, _store, _definitions);

        var thrown = await Should.ThrowAsync<TwigConfigurationException>(() => overlay.ApplyAsync(Synced(42, "Remote title")));
        thrown.Message.ShouldStartWith("provider-unavailable");
    }

    [Fact]
    public async Task Tombstoned_OrUnedited_ShowsTheMirrorUnchanged()
    {
        var (mirror, overlay) = Compose(new CountingProvider(() => Scope));
        await mirror.SaveAsync(Synced(42, "Remote title"));
        await mirror.SaveAsync(Synced(43, "Other"));
        var localId = await EditLocallyAsync(42, FieldValues.Empty.Set("System.Title", new TextValue("Local title")));
        (await _local.DeleteAsync(localId, 2)).Value.ShouldBeOfType<LocalWriteApplied>();

        var tombstoned = (await mirror.GetByIdAsync(42))!;
        var unedited = (await mirror.GetByIdAsync(43))!;

        (await overlay.ApplyAsync(tombstoned)).ShouldBeSameAs(tombstoned);
        (await overlay.ApplyAsync(unedited)).ShouldBeSameAs(unedited);
    }

    [Fact]
    public async Task Sync_LeavesTheDurableRowUnchanged_AndTheOverlayStillWins()
    {
        var (mirror, overlay) = Compose(new CountingProvider(() => Scope));
        await mirror.SaveAsync(Synced(42, "Remote title"));
        var localId = await EditLocallyAsync(42, FieldValues.Empty.Set("System.Title", new TextValue("Local title")));
        var before = DurableRow(localId);

        await mirror.SaveAsync(Synced(42, "Renamed remotely", revision: 9));

        DurableRow(localId).ShouldBe(before);
        var shown = await overlay.ApplyAsync((await mirror.GetByIdAsync(42))!);
        shown.Title.ShouldBe("Local title");
        shown.Revision.ShouldBe(9);
    }

    [Fact]
    public async Task DirtyFlagAndPendingNotes_SurviveTheCopy()
    {
        var (mirror, overlay) = Compose(new CountingProvider(() => Scope));
        await mirror.SaveAsync(Synced(42, "Remote title"));
        await EditLocallyAsync(42, FieldValues.Empty.Set("System.Title", new TextValue("Local title")));
        var remote = (await mirror.GetByIdAsync(42))!;
        remote.AddNote(new PendingNote("hello", DateTimeOffset.UnixEpoch, false));

        var shown = await overlay.ApplyAsync(remote);

        shown.Title.ShouldBe("Local title");
        shown.IsDirty.ShouldBeTrue();
        shown.PendingNotes.ShouldHaveSingleItem().Text.ShouldBe("hello");
    }

    [Theory]
    [InlineData("System.IterationPath", @"Proj\Sprint 9")]
    [InlineData("System.AreaPath", @"Proj\Team")]
    public async Task PathValues_ReplaceTheCoreProperty(string field, string value)
    {
        var (mirror, overlay) = Compose(new CountingProvider(() => Scope));
        await mirror.SaveAsync(Synced(42, "Remote title"));
        await EditLocallyAsync(42, FieldValues.Empty.Set(field, new TextValue(value)));

        var shown = await overlay.ApplyAsync((await mirror.GetByIdAsync(42))!);

        (field == "System.AreaPath" ? shown.AreaPath.Value : shown.IterationPath.Value).ShouldBe(value);
    }

    [Fact]
    public void Render_MatchesTheMirrorsStringForm()
    {
        DurableFieldOverlay.Render(new TextValue("a")).ShouldBe("a");
        DurableFieldOverlay.Render(new IntegerValue(7)).ShouldBe("7");
        DurableFieldOverlay.Render(new DecimalValue(1.5m)).ShouldBe("1.5");
        DurableFieldOverlay.Render(new BooleanValue(true)).ShouldBe("True");
        DurableFieldOverlay.Render(new IdentityValue("x@y", null)).ShouldBe("x@y");
        DurableFieldOverlay.Render(new StringListValue(["a", "b"])).ShouldBe("a; b");
        DurableFieldOverlay.Render(NullValue.Instance).ShouldBeNull();
        DurableFieldOverlay.Render(new RawValue("r", "string")).ShouldBe("r");
    }
}
