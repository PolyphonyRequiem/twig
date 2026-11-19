using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Daemon.Tests;

public sealed class NativeMetadataSyncTests
{
    [Fact]
    public async Task SweepContinuesToOtherAdmittedConnectionsAfterProviderFailure()
    {
        await using var first = await MetadataFixture.Start(DateTimeOffset.UtcNow.AddHours(-1));
        await using var second = await MetadataFixture.Start(DateTimeOffset.UtcNow.AddHours(-1));
        first.Gateway.Other = (second.Root, second.Gateway);
        (await first.Adapter.AdmitAsync(second.Root, default)).IsSuccess.ShouldBeTrue();
        (await first.Definitions.ObserveAsync(second.Root, second.Gateway.Stamp.Connection,
            second.Gateway.Stamp.StoreInstance, [], default)).IsSuccess.ShouldBeTrue();
        first.Gateway.Failure = new(ErrorCode.Unavailable, "Fixture provider temporarily unavailable.");
        second.Gateway.Type = "TwigDelivery.Epic";
        second.Gateway.ObservedAt = DateTimeOffset.UtcNow;
        using var sync = new NativeMetadataSync(first.Panel, NullLogger<NativeMetadataSync>.Instance);
        await sync.RefreshAsync(default);
        first.Gateway.SchemaCalls.ShouldBe(2);
        second.Gateway.SchemaCalls.ShouldBe(3);
        var binding = NativeSourceAdapter.Binding(second.Gateway.Stamp);
        var source = new RegisteredSource(Guid.NewGuid(), "Second connection", second.Root,
            binding, true, second.Gateway.ObservedAt);
        (await first.Adapter.ReadAsync(source, default)).Value.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
        (await first.Adapter.ReadAsync(first.Source, default)).Value.Schema.Types.Single().Reference.ShouldBe("Hyperbright.Epic");
    }

    [Fact]
    public async Task HostRefreshesOverdueRegisteredMetadataWithoutAnOpenViewOrBenchRead()
    {
        await using var fixture = await MetadataFixture.Start(DateTimeOffset.UtcNow.AddHours(-1));
        var before = (await fixture.Definitions.ReadAsync()).Value;
        using var subscription = fixture.Changes.Subscribe(out var notifications);
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = DateTimeOffset.UtcNow;
        await using var app = NativeBenchHost.Build(new(0, fixture.Root), fixture.Panel,
            new ViewSessions(Path.Combine(fixture.Root, "views")), fixture.Changes);
        await app.StartAsync();
        try
        {
            await notifications.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Gateway.SchemaCalls.ShouldBe(2);
            var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
            var observed = (await restarted.ReadAsync(fixture.Source, default)).Value;
            observed.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
            observed.Snapshot.Items.Single().Key.ShouldBe(fixture.Gateway.Item.Key);
            observed.SchemaObservedAt.ShouldBe(fixture.Gateway.ObservedAt);
            var after = (await fixture.Definitions.ReadAsync()).Value;
            after.InstanceId.ShouldBe(before.InstanceId);
            after.Revision.ShouldBe(before.Revision);
            after.Benches.Select(value => (value.Id, value.Name, value.Revision))
                .ShouldBe(before.Benches.Select(value => (value.Id, value.Name, value.Revision)));
            after.Benches.Single().Connections.ShouldBe(before.Benches.Single().Connections);
            BenchCodec.SerializeSelectors(after.Benches.Single().Selectors)
                .ShouldBe(BenchCodec.SerializeSelectors(before.Benches.Single().Selectors));
        }
        finally { await app.StopAsync(); }
    }
}

internal sealed class MetadataFixture : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "twig-metadata-sync-" + Guid.NewGuid().ToString("N"));
    internal string Cache => Path.Combine(Root, "evidence");
    internal MetadataGateway Gateway { get; } = new();
    internal CatalogChanges Changes { get; } = new();
    internal NativeSourceAdapter Adapter { get; private set; } = null!;
    internal UserBenchStore Definitions { get; private set; } = null!;
    internal NativePanel Panel { get; private set; } = null!;
    internal RegisteredSource Source { get; private set; } = null!;
    internal UserBenchWorkspace Workspace => new(Root, Gateway.Stamp.Connection, Gateway.Stamp.StoreInstance);

    internal static async Task<MetadataFixture> Start(DateTimeOffset observedAt)
    {
        var fixture = new MetadataFixture();
        Directory.CreateDirectory(fixture.Root);
        fixture.Gateway.ObservedAt = observedAt;
        fixture.Adapter = new(fixture.Cache, fixture.Gateway, fixture.Changes);
        fixture.Definitions = new(Path.Combine(fixture.Root, "benches.db"));
        fixture.Panel = new(fixture.Definitions, fixture.Gateway, fixture.Adapter);
        var admitted = await fixture.Adapter.AdmitAsync(fixture.Root, default);
        admitted.IsSuccess.ShouldBeTrue();
        fixture.Source = new(Guid.NewGuid(), "Metadata fixture", fixture.Root, admitted.Value.Binding, true, observedAt);
        var bench = new Twig.Core.Bench(Guid.NewGuid(), "Process and policy",
            [new(SelectorKind.Pin, fixture.Gateway.Item.Key)], connections: [fixture.Gateway.Stamp.Connection]);
        (await fixture.Definitions.ObserveAsync(fixture.Root, fixture.Gateway.Stamp.Connection,
            fixture.Gateway.Stamp.StoreInstance, [bench], default)).IsSuccess.ShouldBeTrue();
        return fixture;
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MetadataGateway : INativeSourceGateway
{
    internal NativeSourceStamp Stamp { get; } = new(new(new("ado"), "collection", "project-" + Guid.NewGuid().ToString("N"), "identity"),
        Guid.NewGuid(), "principal", "authority");
    internal (string Root, MetadataGateway Gateway)? Other { get; set; }
    internal int SchemaCalls { get; private set; }
    internal string Type { get; set; } = "Hyperbright.Epic";
    internal DateTimeOffset ObservedAt { get; set; }
    internal Error? Failure { get; set; }
    internal Task? DiscoveryBarrier { get; set; }
    internal TaskCompletionSource DiscoveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal WorkItem Item => new(ItemKey.Remote(Stamp.Connection.Provider, Stamp.Connection.ResourceScope, "1246"),
        "TwigDelivery.Epic", FieldValues.Empty, null, true);

    public async Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected,
        bool observeSchema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Other is { } other && root == other.Root)
            return await other.Gateway.ReadAsync(root, expected, observeSchema, ct);
        if (expected is not null && expected != Stamp)
            return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Fixture source changed.");
        if (observeSchema)
        {
            SchemaCalls++;
            if (DiscoveryBarrier is { } barrier)
            {
                DiscoveryEntered.TrySetResult();
                await barrier.WaitAsync(ct);
            }
            if (Failure is { } error) return Result<NativeSourceObservation>.Fail(error);
        }
        var schema = new ProcessSchema(Stamp.Connection,
            [new(Type, "System.State", [new("System.State", FieldKind.Text)], [], [], [],
                presentation: new(Type, "FF9900", "icon_crown", null))], []);
        return Result<NativeSourceObservation>.Ok(new(Stamp, new(1, [Item]), DateTimeOffset.UtcNow,
            observeSchema ? Result<ProcessSchema>.Ok(schema) : null, observeSchema ? ObservedAt : null));
    }
}
