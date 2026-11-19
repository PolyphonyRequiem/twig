using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Daemon.Tests.Native;

public sealed class NativeMetadataSyncTests
{
    [Fact]
    public async Task SweepContinuesToOtherAdmittedConnectionsAfterProviderFailure()
    {
        var now = DateTimeOffset.UtcNow;
        await using var first = await MetadataFixture.Start(now.AddHours(-1));
        await using var second = await MetadataFixture.Start(now.AddHours(-1), Path.Combine(first.Root, "second"));
        first.Gateway.Other = (second.Root, second.Gateway);
        (await first.Adapter.AdmitAsync(second.Root, first.Token)).IsSuccess.ShouldBeTrue();
        (await first.Definitions.ObserveAsync(second.Root, second.Gateway.Stamp.Connection,
            second.Gateway.Stamp.StoreInstance, [], first.Token)).IsSuccess.ShouldBeTrue();
        (await first.Definitions.WorkspacesAsync(first.Token)).Value.Select(value => value.Root).ShouldBe([first.Root, second.Root]);
        first.Gateway.Failure = new(ErrorCode.Unavailable, "SYNTHETIC_SECRET_MUST_NOT_LOG");
        second.Gateway.Type = "TwigDelivery.Epic";
        second.Gateway.ObservedAt = now;
        var logger = new MetadataLog();
        using var sync = new NativeMetadataSync(first.Panel, logger, new MetadataClock(now));
        await sync.RefreshAsync(first.Token);
        first.Gateway.SchemaCalls.ShouldBe(2);
        second.Gateway.SchemaCalls.ShouldBe(3);
        var binding = NativeSourceAdapter.Binding(second.Gateway.Stamp);
        var source = new RegisteredSource(Guid.NewGuid(), "Second connection", second.Root,
            binding, true, second.Gateway.ObservedAt);
        var refreshed = (await first.Adapter.ReadAsync(source, first.Token)).Value;
        refreshed.Binding.ShouldBe(binding);
        refreshed.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
        refreshed.SchemaObservedAt.ShouldBe(now);
        var retained = (await first.Adapter.ReadAsync(first.Source, first.Token)).Value;
        retained.Binding.ShouldBe(first.Source.Binding);
        retained.Schema.Types.Single().Reference.ShouldBe("Hyperbright.Epic");
        retained.SchemaObservedAt.ShouldBe(now.AddHours(-1));
        first.Gateway.SchemaExpectations.ShouldAllBe(value => value == first.Gateway.Stamp);
        second.Gateway.SchemaExpectations.ShouldAllBe(value => value == second.Gateway.Stamp);
        logger.Messages.ShouldBe(["Scheduled connection metadata refresh failed: Unavailable."]);
        await sync.RefreshAsync(first.Token);
        first.Gateway.SchemaCalls.ShouldBe(2);
        second.Gateway.SchemaCalls.ShouldBe(3);
        logger.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HostRefreshesOverdueRegisteredMetadataWithoutAnOpenViewOrBenchRead()
    {
        await using var fixture = await MetadataFixture.Start(DateTimeOffset.UtcNow.AddHours(-1));
        var before = (await fixture.Definitions.ReadAsync(fixture.Token)).Value;
        var registeredBefore = (await fixture.Definitions.WorkspacesAsync(fixture.Token)).Value;
        var definitionBytes = await File.ReadAllBytesAsync(fixture.DefinitionsPath, fixture.Token);
        using var subscription = fixture.Changes.Subscribe(out var notifications);
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = DateTimeOffset.UtcNow;
        await using var app = NativeBenchHost.Build(new(0, fixture.Root), fixture.Panel,
            new ViewSessions(Path.Combine(fixture.Root, "views")), fixture.Changes);
        try
        {
            await app.StartAsync(fixture.Token);
            await notifications.ReadAsync(fixture.Token);
            fixture.Gateway.SchemaCalls.ShouldBe(2);
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bench.Server.NativeMetadataSync");
            logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
            logger.IsEnabled(LogLevel.Information).ShouldBeFalse();
            var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
            var observed = (await restarted.ReadAsync(fixture.Source, fixture.Token)).Value;
            observed.Binding.ShouldBe(fixture.Source.Binding);
            observed.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
            observed.Snapshot.Items.Single().Key.ShouldBe(fixture.Gateway.Item.Key);
            observed.SelectedBenchId.ShouldBe(fixture.Gateway.SelectedBenchId);
            observed.SchemaObservedAt.ShouldBe(fixture.Gateway.ObservedAt);
            var after = (await fixture.Definitions.ReadAsync(fixture.Token)).Value;
            after.InstanceId.ShouldBe(before.InstanceId);
            after.Revision.ShouldBe(before.Revision);
            after.Benches.Select(value => (value.Id, value.Name, value.Revision))
                .ShouldBe(before.Benches.Select(value => (value.Id, value.Name, value.Revision)));
            foreach (var bench in before.Benches)
            {
                var retained = after.Benches.Single(value => value.Id == bench.Id);
                retained.Connections.ShouldBe(bench.Connections);
                BenchCodec.SerializeSelectors(retained.Selectors).ShouldBe(BenchCodec.SerializeSelectors(bench.Selectors));
            }
            (await fixture.Definitions.WorkspacesAsync(fixture.Token)).Value.ShouldBe(registeredBefore);
            (await File.ReadAllBytesAsync(fixture.DefinitionsPath, fixture.Token)).ShouldBe(definitionBytes);
            fixture.Gateway.SchemaCalls.ShouldBe(2);
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private sealed class MetadataClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MetadataLog : ILogger<NativeMetadataSync>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            logLevel.ShouldBe(LogLevel.Warning);
            exception.ShouldBeNull();
            var values = state is IEnumerable<KeyValuePair<string, object?>> entries
                ? entries.ToArray() : throw new InvalidOperationException("Expected structured metadata logging.");
            values.Select(value => value.Key).Order(StringComparer.Ordinal).ShouldBe(["ErrorCode", "{OriginalFormat}"]);
            values.Single(value => value.Key == "ErrorCode").Value.ShouldBe(ErrorCode.Unavailable);
            Messages.Add(formatter(state, exception));
        }
    }
}

internal sealed class MetadataFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(55));
    internal string Root { get; private init; } = Path.Combine(Path.GetTempPath(), "twig-metadata-sync-" + Guid.NewGuid().ToString("N"));
    internal string Cache => Path.Combine(Root, "evidence");
    internal string CachePath => Path.Combine(Cache, CatalogFingerprint.Binding(Source.Binding) + ".json");
    internal string CheckPath => Path.ChangeExtension(CachePath, ".check");
    internal string DefinitionsPath => Path.Combine(Root, "benches.db");
    internal CancellationToken Token => deadline.Token;
    internal MetadataGateway Gateway { get; } = new();
    internal CatalogChanges Changes { get; } = new();
    internal NativeSourceAdapter Adapter { get; private set; } = null!;
    internal UserBenchStore Definitions { get; private set; } = null!;
    internal NativePanel Panel { get; private set; } = null!;
    internal RegisteredSource Source { get; private set; } = null!;
    internal UserBenchWorkspace Workspace => new(Root, Gateway.Stamp.Connection, Gateway.Stamp.StoreInstance);

    internal static async Task<MetadataFixture> Start(DateTimeOffset observedAt, string? root = null)
    {
        var fixture = root is null ? new MetadataFixture() : new MetadataFixture { Root = root };
        try
        {
            Directory.CreateDirectory(fixture.Root);
            fixture.Gateway.ObservedAt = observedAt;
            fixture.Adapter = new(fixture.Cache, fixture.Gateway, fixture.Changes);
            fixture.Definitions = new(fixture.DefinitionsPath);
            fixture.Panel = new(fixture.Definitions, fixture.Gateway, fixture.Adapter);
            var admitted = await fixture.Adapter.AdmitAsync(fixture.Root, fixture.Token);
            admitted.IsSuccess.ShouldBeTrue();
            fixture.Source = new(Guid.NewGuid(), "Metadata fixture", fixture.Root, admitted.Value.Binding, true, observedAt);
            (await fixture.Definitions.ObserveAsync(fixture.Root, fixture.Gateway.Stamp.Connection,
                fixture.Gateway.Stamp.StoreInstance, [], fixture.Token)).IsSuccess.ShouldBeTrue();
            var bench = new Twig.Core.Bench(fixture.Gateway.SelectedBenchId, "Process and policy",
                [new(SelectorKind.Pin, fixture.Gateway.Item.Key, connection: fixture.Gateway.Stamp.Connection)],
                connections: [fixture.Gateway.Stamp.Connection]);
            (await fixture.Definitions.SaveAsync(bench, 0, fixture.Token)).IsSuccess.ShouldBeTrue();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public ValueTask DisposeAsync()
    {
        deadline.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class MetadataGateway : INativeSourceGateway
{
    internal NativeSourceStamp Stamp { get; } = new(new(new("ado"), "collection", "project-" + Guid.NewGuid().ToString("N"), "identity"),
        Guid.NewGuid(), "principal", "authority");
    internal (string Root, MetadataGateway Gateway)? Other { get; set; }
    internal int SchemaCalls { get; private set; }
    internal List<NativeSourceStamp?> SchemaExpectations { get; } = [];
    internal string Type { get; set; } = "Hyperbright.Epic";
    internal DateTimeOffset ObservedAt { get; set; }
    internal Guid SelectedBenchId { get; } = Guid.NewGuid();
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
            SchemaExpectations.Add(expected);
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
            observeSchema ? Result<ProcessSchema>.Ok(schema) : null, observeSchema ? ObservedAt : null)
            { SelectedBenchId = SelectedBenchId });
    }
}
