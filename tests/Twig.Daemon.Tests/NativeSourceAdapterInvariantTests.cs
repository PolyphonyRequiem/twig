using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;

using Xunit;

namespace Twig.Daemon.Tests.Native;

public sealed class NativeSourceAdapterInvariantTests
{
    public static IEnumerable<object[]> Scenarios => cases.Select(test => new object[] { test.Name });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task RetainsAdmissionAndCacheGuards(string scenario)
        => cases.Single(test => test.Name == scenario).Run();

    [Fact]
    public async Task TeamDiscoveryDoesNotClearKnownContentDenial()
    {
        await using var fixture = await Fixture.Start();
        fixture.Gateway.ContentReadError = new(ErrorCode.PolicyViolation, "Synthetic work-item denial");
        Check(!(await fixture.Adapter.ReadOnlineAsync(fixture.Source, default)).IsSuccess, "The live request must report the denial.");
        var stamp = fixture.Gateway.Observation.Source;
        Equal(1, Required(await fixture.Adapter.DiscoverPlanningTeamsAsync(fixture.Source, stamp, default)).Length);
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
    }

    [Fact]
    public async Task CancelledLocalReadDoesNotChangeRetainedCacheState()
    {
        await using var fixture = await Fixture.Start();
        var path = Directory.GetFiles(fixture.CacheDirectory, "*.json").Single();
        var before = await File.ReadAllTextAsync(path);
        using var cancel = new CancellationTokenSource();
        fixture.Gateway.LocalDelay = TimeSpan.FromSeconds(1);
        fixture.Gateway.BeforeLocalDelay = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Adapter.ReadAsync(fixture.Source, cancel.Token));
        Equal(before, await File.ReadAllTextAsync(path));
        fixture.Gateway.LocalDelay = TimeSpan.Zero;
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Required(await restarted.ReadAsync(fixture.Source, default));
    }

    [Theory]
    [InlineData("ado")]
    [InlineData("github")]
    [InlineData("twig")]
    public async Task ExactItemWithdrawalSurvivesRestartAndMetadataRecheck(string provider)
    {
        await using var fixture = await Fixture.Start(provider);
        var definitions = new UserBenchStore(Path.Combine(fixture.Directory, "withdrawal-benches.db"));
        var panel = new NativePanel(definitions, fixture.Gateway, fixture.Adapter);
        var context = Required(await panel.OpenAsync(fixture.Directory, default));
        var first = fixture.Gateway.Observation.Snapshot.Items[0].Key;
        var second = fixture.Gateway.Observation.Snapshot.Items[1].Key;
        fixture.Gateway.ItemFailure = new(ErrorCode.Unavailable, "Synthetic exact item refusal")
            { Evidence = ["http:403", $"item-unavailable:{first.NativeId}"] };
        Check(!(await panel.ObserveItemsAsync(context, [first, second], default)).IsSuccess,
            "The exact read must report the provider refusal.");

        fixture.Gateway.ItemFailure = null;
        Required(await fixture.Adapter.RecheckAsync(fixture.Source, default));
        Required(await panel.ObserveItemsAsync(context, [second], default));
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var observation = Required(await restarted.ReadAsync(fixture.Source, default));
        Check(observation.WithdrawnItems.SetEquals([first]), "Only the exact denied item may be withdrawn.");
        var selection = new SourceSelection(fixture.Source.Id, fixture.Source.Binding,
            [new(SelectorKind.Query, query: new(QueryKind.All))]);
        var view = Required(SourceProjection.Create(new(Guid.NewGuid(), "Fixture", 1, [selection]), selection, observation));
        Equal(second, view.Items.Single().Identity.Key);
        Check(view.Hierarchy.Evidence.IsEmpty, "Withdrawn endpoints must not be emitted in relationships.");

        fixture.Gateway.ItemFailure = new(ErrorCode.Unavailable, "Synthetic exact item refusal")
            { Evidence = ["http:403", $"item-unavailable:{second.NativeId}"] };
        Check(!(await panel.ObserveItemsAsync(context, [second], default)).IsSuccess,
            "A second refusal must be retained independently.");
        fixture.Gateway.BrowseItem = first;
        fixture.Gateway.BrowseRetained = false;
        var unretained = Required(await restarted.BrowsePlanningAsync(fixture.Source, context.Stamp,
            null, PlanningSourceKind.SavedQuery, "fixture-query", default));
        Check(unretained.WithdrawnItems.SetEquals([first, second]),
            "A new response must not restore historical display until the matching item is in the mirror.");
        fixture.Gateway.BrowseRetained = true;
        var browse = Required(await restarted.BrowsePlanningAsync(fixture.Source, context.Stamp,
            null, PlanningSourceKind.SavedQuery, "fixture-query", default));
        Check(browse.WithdrawnItems.SetEquals([second]), "A retained provider browse restores only the item it returned.");
        var reopened = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Check(Required(await reopened.ReadAsync(fixture.Source, default)).WithdrawnItems.SetEquals([second]),
            "Scoped restoration must survive restart.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitOnlineCheckReplacesCorruptCacheForExactSource(bool admit)
    {
        await using var fixture = await Fixture.Start();
        var path = Directory.GetFiles(fixture.CacheDirectory, "*.json").Single();
        var healthy = await File.ReadAllTextAsync(path);
        var fingerprint = fixture.Gateway.Observation.Schema!.Value.Fingerprint;
        var corrupt = healthy.Replace(fingerprint, new string('0', fingerprint.Length), StringComparison.Ordinal);
        Check(corrupt != healthy, "The fixture must corrupt the persisted fingerprint.");
        await File.WriteAllTextAsync(path, corrupt);
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var calls = fixture.Gateway.TotalCalls;
        Equal(ErrorCode.StorageUnavailable, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
        Equal(calls, fixture.Gateway.TotalCalls);

        var schemas = fixture.Gateway.SchemaCalls;
        fixture.Gateway.Version = 42;
        var observed = Required(admit ? await restarted.AdmitAsync(fixture.Source.NativeLocator, default)
            : await restarted.RecheckAsync(fixture.Source, default));
        Equal(ObservationMode.OnlineMetadata, observed.Mode);
        Equal(fixture.Source.Binding, observed.Binding);
        Equal(fixture.Gateway.Observation.Source, fixture.Gateway.Expected);
        Equal(schemas + 1, fixture.Gateway.SchemaCalls);
        Equal(42L, observed.Snapshot.Version);

        var reread = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var restored = Required(await reread.ReadAsync(fixture.Source, default));
        Equal(ObservationMode.PermittedOfflineCache, restored.Mode);
        Equal(fingerprint, restored.Schema.Fingerprint);
        Equal(observed.SchemaObservedAt, restored.SchemaObservedAt);
        Equal(schemas + 1, fixture.Gateway.SchemaCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CorruptCacheRecoveryRefusesCredentialDenialOrChangedAuthority(bool admit, bool changedAuthority)
    {
        await using var fixture = await Fixture.Start();
        var path = Directory.GetFiles(fixture.CacheDirectory, "*.json").Single();
        var healthy = await File.ReadAllBytesAsync(path);
        const string corrupt = "{";
        await File.WriteAllTextAsync(path, corrupt);
        var schemas = fixture.Gateway.SchemaCalls;
        if (changedAuthority)
        {
            var other = fixture.Gateway.Observation.Source with { AuthorityStamp = "replacement-authority" };
            if (admit) fixture.Gateway.BeforeSchemaReturn = () => fixture.Gateway.SourceOverride = other;
            else fixture.Gateway.SourceOverride = other;
        }
        else fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic denied") { Evidence = ["http:401"] };

        var result = admit ? await fixture.Adapter.AdmitAsync(fixture.Source.NativeLocator, default)
            : await fixture.Adapter.RecheckAsync(fixture.Source, default);
        Equal(changedAuthority ? ErrorCode.Conflict : ErrorCode.Unavailable, result.Error.Code);
        if (!changedAuthority) Check(result.Error.Evidence.Contains("http:401"), "Recovery must preserve the credential denial.");
        Equal(schemas + (changedAuthority && !admit ? 0 : 1), fixture.Gateway.SchemaCalls);
        Equal(corrupt, await File.ReadAllTextAsync(path));
        Equal(1, Directory.GetFiles(fixture.CacheDirectory, "*.json").Length);

        // Even if the old schema becomes readable again, it cannot restore the offline grant.
        await File.WriteAllBytesAsync(path, healthy);
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var calls = fixture.Gateway.TotalCalls;
        Equal(changedAuthority ? ErrorCode.Conflict : ErrorCode.PolicyViolation,
            (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
        Equal(calls + (changedAuthority ? 1 : 0), fixture.Gateway.TotalCalls);
    }

private static readonly (string Name, Func<Task> Run)[] cases =
{
    ("reselecting an admitted connection uses historical observations without provider calls", async () =>
    {
        await using var fixture = await Fixture.Start();
        Equal(1, fixture.Gateway.SchemaCalls);
        var online = fixture.Gateway.OnlineCalls;
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        fixture.Gateway.RejectFullMetadata = true;
        var observation = Required(await restarted.AdmitAsync(fixture.Source.NativeLocator, default));
        Equal(1, fixture.Gateway.SchemaCalls);
        Equal(online, fixture.Gateway.OnlineCalls);
        Equal(ObservationMode.PermittedOfflineCache, observation.Mode);
        Equal(fixture.Source.Binding, observation.Binding);
        fixture.Gateway.OnlineError = new(ErrorCode.PolicyViolation, "Synthetic actor denied.");
        var denied = await restarted.ReadOnlineAsync(fixture.Source, default);
        Check(!denied.IsSuccess && denied.Error.Code == ErrorCode.PolicyViolation, "Live validation must surface the denial.");
        fixture.Gateway.OnlineError = null;
        Equal(ErrorCode.PolicyViolation, (await restarted.AdmitAsync(fixture.Source.NativeLocator, default)).Error.Code);
        Equal(1, fixture.Gateway.SchemaCalls);
    }),
    ("admitted source and content reads share the same root coordinator", async () =>
    {
        await using var fixture = await Fixture.Start();
        var definitions = new UserBenchStore(Path.Combine(fixture.Directory, "shared-benches.db"));
        var panel = new NativePanel(definitions, fixture.Gateway, fixture.Adapter);
        var context = Required(await panel.OpenAsync(fixture.Directory, default));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Gateway.LocalDelay = TimeSpan.FromSeconds(1);
        fixture.Gateway.LocalReadBarrier = release.Task;
        fixture.Gateway.BeforeLocalDelay = () => { Interlocked.Increment(ref calls); entered.TrySetResult(); };
        var content = panel.CaptureAsync(context, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selection = panel.ReadAdmittedSourceAsync(context, default);
        try { Equal(1, calls); }
        finally { release.TrySetResult(); await Task.WhenAll(content, selection); }
        Required(await content); Required(await selection); Equal(2, calls);
        fixture.Gateway.LocalDelay = TimeSpan.Zero; fixture.Gateway.LocalReadBarrier = null;
        fixture.Gateway.BeforeLocalDelay = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var nested = Required(await panel.EditorOperationAsync(context,
            token => panel.ReadAdmittedSourceAsync(context, token), deadline.Token));
        Required(nested);
    }),
    ("online transient failures recover through fresh validation without reconnecting", async () =>
    {
        await using var fixture = await Fixture.Start();
        var online = new RegisteredSource(fixture.Source.Id, fixture.Source.Label, fixture.Source.NativeLocator,
            fixture.Source.Binding, false, fixture.Source.AdmittedOnlineAt);
        var schemas = fixture.Gateway.SchemaCalls;
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic read timed out.");
        var timedOut = await fixture.Adapter.ReadAsync(online, default);
        Check(!timedOut.IsSuccess && timedOut.Error.Code == ErrorCode.Unavailable, "A timeout must fail the request, not return cached success.");
        var offline = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Equal(ObservationMode.PermittedOfflineCache, Required(await offline.ReadAsync(fixture.Source, default)).Mode);
        fixture.Gateway.OnlineError = null;
        var recovered = Required(await fixture.Adapter.ReadAsync(online, default));
        Equal(ObservationMode.OnlineValidatedCache, recovered.Mode);
        Equal(schemas, fixture.Gateway.SchemaCalls);
        fixture.Gateway.OnlineError = new Error(ErrorCode.Unavailable, "Synthetic access denied.") { Evidence = ["http:403"] };
        var denied = await fixture.Adapter.ReadAsync(online, default);
        Check(!denied.IsSuccess && denied.Error.Evidence.Contains("http:403"), "Real access denial must remain authoritative.");
        var stillDenied = await fixture.Adapter.ReadAsync(online, default);
        Check(!stillDenied.IsSuccess && stillDenied.Error.Evidence.Contains("http:403"), "Online retry must revalidate and refuse denied access, not release old data.");
        fixture.Gateway.OnlineError = null;
        Equal(ErrorCode.PolicyViolation, (await fixture.Adapter.ReadAsync(online, default)).Error.Code);
        Equal(schemas, fixture.Gateway.SchemaCalls);
    }),
    ("additive Bench edits preserve concurrent additions and duplicate requests", async () =>
    {
        await using var fixture = await Fixture.Start();
        var definitions = new UserBenchStore(Path.Combine(fixture.Directory, "shared-additions.db"));
        var panel = new NativePanel(definitions, fixture.Gateway, fixture.Adapter);
        var bench = Required(await definitions.SaveAsync(new(Guid.NewGuid(), "Concurrent additions", []), 0));
        var connection = fixture.Source.Binding.Connection;
        var first = new Selector(SelectorKind.Pin, ItemKey.Remote(connection.Provider, connection.ResourceScope, "41"), connection: connection);
        var second = new Selector(SelectorKind.Pin, ItemKey.Remote(connection.Provider, connection.ResourceScope, "42"), connection: connection);
        var results = await Task.WhenAll(panel.AddAsync(bench.Id, [first], default), panel.AddAsync(bench.Id, [second], default));
        foreach (var result in results) Required(result);
        var saved = Required(await definitions.ReadAsync()).Benches.Single();
        Equal(2, saved.Selectors.Length);
        var duplicate = Required(await panel.AddAsync(bench.Id, [first, second], default));
        Equal(saved.Revision, duplicate.Revision);
        Equal(BenchCodec.SerializeSelectors(saved.Selectors), BenchCodec.SerializeSelectors(duplicate.Selectors));
        Required(await panel.DeleteAsync(bench.Id, saved.Revision, default));
        var deleted = await panel.AddAsync(bench.Id, [first], default);
        Check(!deleted.IsSuccess && deleted.Error.Code == ErrorCode.NotFound, "Adding work must not recreate a deleted Bench.");
    }),
    ("native schema cache retains full stamp, precision and offline/no-network policy across restart", async () =>
    {
        await using var fixture = await Fixture.Start();
        var first = await fixture.Adapter.ReadAsync(fixture.Source, default);
        Equal(ObservationMode.PermittedOfflineCache, Required(first).Mode);
        Equal(fixture.Gateway.Observation.Source.PrincipalId, fixture.Gateway.Expected!.PrincipalId);
        Equal(1, fixture.Gateway.SchemaCalls);
        fixture.Gateway.Version = 30;
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var second = Required(await restarted.ReadAsync(fixture.Source, default));
        Equal(30L, second.Snapshot.Version);
        Equal(first.Value.Schema.Fingerprint, second.Schema.Fingerprint);
        Equal(1, fixture.Gateway.SchemaCalls);
    }),
    ("native form presentation survives real disk cache without schema rediscovery", async () =>
    {
        await using var fixture = await Fixture.Start();
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var observation = Required(await restarted.ReadAsync(fixture.Source, default));
        var type = observation.Schema.Types.Single();
        Equal("B4009E", type.Presentation!.Color);
        Equal("icon_crown", type.Presentation.IconId);
        Equal(2, type.Presentation.Layout!.Pages[0].Sections.Length);
        Equal("Description", type.Fields.Single(f => f.Reference == "System.Description").Name);
        Equal("https://example.test/project/", observation.Schema.WebUrl);
        Equal(1, fixture.Gateway.SchemaCalls);
    }),
    ("legacy cache state cannot be reopened by schema refresh", async () =>
    {
        await using var fixture = await Fixture.Start();
        var path = Path.Combine(fixture.CacheDirectory, CatalogFingerprint.Binding(fixture.Source.Binding) + ".check");
        await File.WriteAllTextAsync(path, """{"state":"pending"}""");
        File.Delete(Path.ChangeExtension(path, ".json"));
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var schemaCalls = fixture.Gateway.SchemaCalls;
        Check(!(await restarted.RecheckAsync(fixture.Source, default)).IsSuccess,
            "Schema discovery must not readmit a legacy cache.");
        Check(!(await restarted.AdmitAsync(fixture.Source.NativeLocator, default)).IsSuccess,
            "Selection must not readmit a legacy cache.");
        Check(!(await restarted.ReadAsync(fixture.Source, default)).IsSuccess,
            "Historical data must remain withheld until fresh initialization.");
        Equal(schemaCalls, fixture.Gateway.SchemaCalls);
    }),
    ("structured content denial survives restart and schema refresh", async () =>
    {
        await using var fixture = await Fixture.Start();
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic denied") { Evidence = ["http:403"] };
        Equal(ErrorCode.Unavailable, (await fixture.Adapter.ReadOnlineAsync(fixture.Source, default)).Error.Code);
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
        fixture.Gateway.OnlineError = null;
        Required(await restarted.RecheckAsync(fixture.Source, default));
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
    }),
    ("GitHub schema success is independent of historical work-item denial", async () =>
    {
        await using var fixture = await Fixture.Start("github");
        var source = fixture.Source;
        var binding = CatalogFingerprint.Binding(source.Binding);
        var schemaPath = Path.Combine(fixture.CacheDirectory, binding + ".json");
        Check(File.Exists(schemaPath), "Schema is retained separately from work items.");
        var empty = Path.Combine(fixture.Directory, "unadmitted");
        var unadmitted = new NativeSourceAdapter(empty, fixture.Gateway, fixture.Changes);
        fixture.Gateway.ContentReadError = new(ErrorCode.Unavailable, "Synthetic issues read denied") { Evidence = ["http:403"] };
        var schemaCalls = fixture.Gateway.SchemaCalls;

        Required(await unadmitted.AdmitAsync(source.NativeLocator, default));
        Equal(schemaCalls + 1, fixture.Gateway.SchemaCalls);
        Equal(ErrorCode.Unavailable, (await fixture.Adapter.ReadOnlineAsync(source, default)).Error.Code);
        Required(await fixture.Adapter.RecheckAsync(source, default));
        Equal(schemaCalls + 2, fixture.Gateway.SchemaCalls);
        Equal(fixture.Gateway.Observation.Source, fixture.Gateway.Expected!);

        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(source, default)).Error.Code);
        Equal(ErrorCode.PolicyViolation, (await restarted.AdmitAsync(source.NativeLocator, default)).Error.Code);
        var panel = new NativePanel(new UserBenchStore(Path.Combine(fixture.Directory, "benches.db")), fixture.Gateway, restarted);
        Equal(ErrorCode.PolicyViolation, (await panel.OpenAsync(source.NativeLocator, default, cachedAdmissionOnly: true)).Error.Code);

        fixture.Gateway.ContentReadError = null;
        Equal(source.Binding, Required(await restarted.RecheckAsync(source, default)).Binding);
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(source, default)).Error.Code);
        Equal(source.Binding, Required(await unadmitted.RecheckAsync(source, default)).Binding);
        Equal(ObservationMode.PermittedOfflineCache, Required(await unadmitted.ReadAsync(source, default)).Mode);
    }),
    ("ordinary online reads validate access without metadata crawl or fabricated schema freshness", async () =>
    {
        await using var fixture = await Fixture.Start();
        var online = new RegisteredSource(fixture.Source.Id, fixture.Source.Label, fixture.Source.NativeLocator,
            fixture.Source.Binding, false, fixture.Source.AdmittedOnlineAt);
        var cachePath = Path.Combine(fixture.CacheDirectory, CatalogFingerprint.Binding(online.Binding) + ".json");
        var before = await File.ReadAllBytesAsync(cachePath);
        var schemaReads = fixture.Gateway.SchemaCalls;
        var schemaAt = online.AdmittedOnlineAt;
        fixture.Gateway.RejectFullMetadata = true;
        fixture.Gateway.Version = 31;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var observed = Required(await fixture.Adapter.ReadAsync(online, budget.Token));
        Equal(ObservationMode.OnlineValidatedCache, observed.Mode);
        Equal(31L, observed.Snapshot.Version);
        Equal(schemaReads, fixture.Gateway.SchemaCalls);
        Equal(schemaAt, observed.SchemaObservedAt);
        var after = await File.ReadAllBytesAsync(cachePath);
        Check(before.SequenceEqual(after), "Ordinary online read rewrote schema evidence.");
        Equal("icon_crown", observed.Schema.Types.Single().Presentation!.IconId);
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic read access denied") { Evidence = ["http:403"] };
        Equal(ErrorCode.Unavailable, (await fixture.Adapter.ReadAsync(online, default)).Error.Code);
        fixture.Gateway.OnlineError = null;
        var calls = fixture.Gateway.TotalCalls;
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(online, default)).Error.Code);
        Equal(calls + 1, fixture.Gateway.TotalCalls);
        Equal(schemaReads, fixture.Gateway.SchemaCalls);
        Check(!(await restarted.RecheckAsync(online, default)).IsSuccess, "Explicit recheck skipped full metadata discovery.");
        fixture.Gateway.RejectFullMetadata = false;
        Required(await restarted.RecheckAsync(online, default));
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(online, default)).Error.Code);
    }),
    ("401 survives a canceled request and later schema refresh", () => DenialRetentionFault(401)),
    ("403 from a content check survives a canceled request and later schema refresh", () => DenialRetentionFault(403)),
    ("unknown online failure does not become offline success; replacement never adopts another principal/store", async () =>
    {
        await using var fixture = await Fixture.Start();
        var online = new RegisteredSource(fixture.Source.Id, fixture.Source.Label, fixture.Source.NativeLocator,
            fixture.Source.Binding, false, fixture.Source.AdmittedOnlineAt);
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Ambiguous synthetic failure");
        Equal(ErrorCode.Unavailable, (await fixture.Adapter.ReadAsync(online, default)).Error.Code);
        fixture.Gateway.OnlineError = null;
        Required(await fixture.Adapter.RecheckAsync(fixture.Source, default));
        fixture.Gateway.Replaced = true;
        Equal(ErrorCode.Conflict, (await fixture.Adapter.ReadAsync(fixture.Source, default)).Error.Code);
        Equal(ErrorCode.Conflict, (await fixture.Adapter.ReadAsync(fixture.Source, default)).Error.Code);
    }),
    ("missing exact admission record requires explicit recheck, not fabricated schema", async () =>
    {
        await using var fixture = await Fixture.Start();
        var empty = Path.Combine(fixture.Directory, "missing-cache");
        var adapter = new NativeSourceAdapter(empty, fixture.Gateway, fixture.Changes);
        Equal(ErrorCode.StorageUnavailable, (await adapter.ReadAsync(fixture.Source, default)).Error.Code);
        Required(await adapter.RecheckAsync(fixture.Source, default));
        Required(await adapter.ReadAsync(fixture.Source, default));
    }),
    ("unretainable durable guard refuses both acquisition and cached content", async () =>
    {
        await using var fixture = await Fixture.Start();
        var path = Path.Combine(fixture.CacheDirectory, CatalogFingerprint.Binding(fixture.Source.Binding) + ".json.denied");
        var backup = path + ".prior";
        if (File.Exists(path)) File.Move(path, backup);
        Directory.CreateDirectory(path);
        try
        {
            var before = fixture.Gateway.TotalCalls;
            Check(!(await fixture.Adapter.ReadAsync(fixture.Source, default)).IsSuccess, "Unretained guard released content.");
            var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
            Check(!(await restarted.ReadAsync(fixture.Source, default)).IsSuccess, "Restart bypassed an unretainable guard.");
            Equal(before, fixture.Gateway.TotalCalls);
        }
        finally { Directory.Delete(path); if (File.Exists(backup)) File.Move(backup, path); }
    }),
    ("fresh historical reads never create a legacy check cell", async () =>
    {
        await using var fixture = await Fixture.Start();
        var before = fixture.Gateway.TotalCalls;
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Required(await restarted.ReadAsync(fixture.Source, default));
        Equal(before + 1, fixture.Gateway.TotalCalls);
        Check(Directory.GetFiles(fixture.CacheDirectory, "*.check").Length == 0, "Fresh reads never write a grant file.");
    }),
    ("cancelled positive reauthorization cannot clear a prior denial before evidence retention", async () =>
    {
        await using var fixture = await Fixture.Start();
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic denied") { Evidence = ["http:403"] };
        await fixture.Adapter.ReadOnlineAsync(fixture.Source, default);
        fixture.Gateway.OnlineError = null;
        using var cancellation = new CancellationTokenSource();
        fixture.Gateway.BeforeSchemaReturn = cancellation.Cancel;
        try { await fixture.Adapter.RecheckAsync(fixture.Source, cancellation.Token); }
        catch (OperationCanceledException) { }
        fixture.Gateway.BeforeSchemaReturn = null;
        Check(!(await fixture.Adapter.ReadAsync(fixture.Source, default)).IsSuccess, "Cancelled reauthorization unblocked current instance.");
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Check(!(await restarted.ReadAsync(fixture.Source, default)).IsSuccess, "Cancelled reauthorization unblocked after restart.");
        Required(await restarted.RecheckAsync(fixture.Source, default));
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
    }),
    ("uncertain metadata failure cannot clear an existing denial and normal successful reads do not emit loops", async () =>
    {
        await using var fixture = await Fixture.Start();
        using var subscription = fixture.Changes.Subscribe(out var invalidations);
        Required(await fixture.Adapter.ReadAsync(fixture.Source, default));
        Equal(false, invalidations.TryRead(out _));
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic denied") { Evidence = ["http:401"] };
        await fixture.Adapter.RecheckAsync(fixture.Source, default);
        Equal(true, invalidations.TryRead(out _));
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Unknown synthetic metadata failure");
        await fixture.Adapter.RecheckAsync(fixture.Source, default);
        Equal(false, invalidations.TryRead(out _));
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        var calls = fixture.Gateway.TotalCalls;
        Check(!(await restarted.ReadAsync(fixture.Source, default)).IsSuccess, "Unknown failure cleared a known denial.");
        Equal(calls, fixture.Gateway.TotalCalls);
        fixture.Gateway.OnlineError = null;
        Required(await restarted.RecheckAsync(fixture.Source, default));
        Equal(true, invalidations.TryRead(out _));
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
        Equal(false, invalidations.TryRead(out _));
    }),
    ("a fresh workspace for the same account cannot clear the original binding's denial", async () =>
    {
        await using var fixture = await Fixture.Start();
        fixture.Gateway.OnlineError = new(ErrorCode.Unavailable, "Synthetic denied") { Evidence = ["http:401"] };
        Check(!(await fixture.Adapter.RecheckAsync(fixture.Source, default)).IsSuccess,
            "The source must have an observed credential refusal before recovery.");
        fixture.Gateway.OnlineError = null;
        var original = fixture.Gateway.Observation.Source;
        fixture.Gateway.SourceOverride = original with { StoreInstance = Guid.NewGuid() };
        var fresh = Required(await fixture.Adapter.AdmitAsync(fixture.Source.NativeLocator, default));
        Check(fresh.Binding != fixture.Source.Binding, "A fresh store needs independent evidence.");
        var freshSource = new RegisteredSource(Guid.NewGuid(), fixture.Source.Label, fixture.Source.NativeLocator,
            fresh.Binding, true, fresh.SchemaObservedAt);
        var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
        Required(await restarted.ReadAsync(freshSource, default));
        fixture.Gateway.SourceOverride = original;
        Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
    }),
    ("real public native reader refuses uninitialized workspace without writing markers or stores", async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "bench-uninitialized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var result = await new NativeSourceGateway().ReadAsync(path, null, false, default);
            Equal(ErrorCode.StorageUnavailable, result.Error.Code);
            Equal(0, Directory.GetFileSystemEntries(path).Length);
        }
        finally { Directory.Delete(path); }
    }),
};

static async Task DenialRetentionFault(int status)
{
    await using var fixture = await Fixture.Start();
    using var cancellation = new CancellationTokenSource();
    using var subscription = fixture.Changes.Subscribe(out var invalidations);
    fixture.Gateway.ContentReadError = new(ErrorCode.Unavailable, "Synthetic denied") { Evidence = [$"http:{status}"] };
    fixture.Gateway.BeforeOnlineReturn = cancellation.Cancel;
    Check(!(await fixture.Adapter.ReadOnlineAsync(fixture.Source, cancellation.Token)).IsSuccess,
        "Denial was returned as success.");
    fixture.Gateway.BeforeOnlineReturn = null;
    var next = await fixture.Adapter.ReadAsync(fixture.Source, default);
    var restarted = new NativeSourceAdapter(fixture.CacheDirectory, fixture.Gateway, fixture.Changes);
    var afterRestart = await restarted.ReadAsync(fixture.Source, default);
    Check(!next.IsSuccess && !afterRestart.IsSuccess,
        $"Observed http:{status} lost after retention/cancellation: same adapter allowed={next.IsSuccess}, restarted adapter allowed={afterRestart.IsSuccess}.");
    Check(invalidations.TryRead(out _), "Denial did not invalidate open views when cache retention failed.");
    fixture.Gateway.ContentReadError = null;
    Required(await restarted.RecheckAsync(fixture.Source, default));
    Equal(ErrorCode.PolicyViolation, (await restarted.ReadAsync(fixture.Source, default)).Error.Code);
}

static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; received {actual}."); }
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static T Required<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new Exception("Native operation failed: " + result.Error.Code);

sealed class SyntheticGateway : INativeSourceGateway
{
    internal NativeSourceObservation Observation { get; set; }
    internal NativeSourceStamp? Expected { get; private set; }
    internal int OnlineCalls { get; private set; }
    internal int SchemaCalls { get; private set; }
    internal bool RejectFullMetadata { get; set; }
    internal int TotalCalls { get; private set; }
    internal Action? BeforeOnlineReturn { get; set; }
    internal Action? BeforeSchemaReturn { get; set; }
    internal NativeSourceStamp? SourceOverride { get; set; }
    internal TimeSpan LocalDelay { get; set; }
    internal Action? BeforeLocalDelay { get; set; }
    internal Task? LocalReadBarrier { get; set; }
    internal Action? LocalReadCanceled { get; set; }
    internal bool OmitGitHubNumber { get; set; }
    internal bool MalformedGitHubNumber { get; set; }
    internal Error? OnlineError { get; set; }
    internal Error? ContentReadError { get; set; }
    internal Error? ItemFailure { get; set; }
    internal ItemKey? BrowseItem { get; set; }
    internal bool BrowseRetained { get; set; } = true;
    internal bool Replaced { get; set; }
    internal long Version { get; set; } = 1;
    internal SyntheticGateway(string provider = "ado")
    {
        var connection = new Connection(new(provider), "synthetic-project", "synthetic-destination", "synthetic-identity");
        var stamp = new NativeSourceStamp(connection, Guid.NewGuid(), "synthetic-principal", "synthetic-authority");
        var first = ItemKey.Remote(connection.Provider, connection.ResourceScope, provider == "github" ? "I_opaque_node" : "41");
        var second = ItemKey.Remote(connection.Provider, connection.ResourceScope, provider == "github" ? "I_opaque_child" : "42");
        var title = provider == "github" ? Twig.Providers.GitHub.GitHubProvider.Title : "System.Title";
        var fields = FieldValues.Empty.Set(title, FieldValue.String("Synthetic title"))
            .Set("Demo.Workflow", FieldValue.String("Example active")).Set("Demo.Flag", FieldValue.Bool(false))
            .Set("Demo.Decimal", FieldValue.Decimal(1.0m)).Set("Demo.Precision", FieldValue.Decimal(12345678901234567890.123456789m));
        var description = provider == "github" ? "github.body" : "System.Description";
        fields = fields.Set(description, FieldValue.RichText(provider == "github"
            ? "# Synthetic issue\n\n**Native Markdown**\n\n[unsafe](javascript:window.__detailLeak=true)"
            : "<p>Observed <strong>rich</strong> description.</p><script>window.__detailLeak=true</script><img src=\"https://example.invalid/leak\" onerror=\"window.__detailLeak=true\"><a href=\"javascript:window.__detailLeak=true\">unsafe link</a>",
            provider == "github" ? FieldKind.Markdown : FieldKind.Html));
        if (provider == "github") fields = fields.Set(Twig.Providers.GitHub.GitHubProvider.Number, FieldValue.Integer(41));
        var childFields = fields.Set(title, FieldValue.String("Synthetic child")).Set("Demo.Decimal", FieldValue.Decimal(1.00m));
        if (provider == "github") childFields = childFields.Set(Twig.Providers.GitHub.GitHubProvider.Number, FieldValue.Integer(42));
        var items = new[] { new WorkItem(first, "SyntheticWork", fields, new(first, "1"), true),
            new WorkItem(second, "SyntheticWork", childFields, new(second, "1"), true) };
        var nativeBench = new Twig.Core.Bench(Guid.NewGuid(), "Synthetic native Bench", [new(SelectorKind.Query, query: new(QueryKind.All))]);
        var type = new ItemType("SyntheticWork", "Demo.Workflow",
            [new(title, FieldKind.Text), new("Demo.Workflow", FieldKind.Text), new("Demo.Flag", FieldKind.Boolean),
                new("Demo.Decimal", FieldKind.Decimal), new("Demo.Precision", FieldKind.Decimal),
                new(description, provider == "github" ? FieldKind.Markdown : FieldKind.Html, Name: "Description")],
            [new("Example active", StateCategory.InProgress, "B4009E")], [],
            [new("Demo.Decimal", RuleAction.Allowed, [], [FieldValue.Decimal(1.00m)])],
            presentation: new("SyntheticWork", "B4009E", "icon_crown", provider == "github" ? null
                : new("synthetic-process", "SyntheticWork", LayoutAvailability.Served,
                    [new("details", "Details", "custom", true, false,
                        [new("left", [new("description", "Description", true, false,
                            [new(description, "Description", "HtmlFieldControl", false, true, false)])]),
                         new("right", [new("planning", "Planning", true, false,
                            [new("Demo.Decimal", "Effort", "FieldControl", false, true, false),
                             new("extension", "Custom control", "ExtensionControl", true, true, true)])])]),
                     new("history", "History", "history", true, false, [])],
                    [new("Demo.Workflow", "Status", "FieldControl", false, true, false)]),
                provider == "github" ? null : """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path fill="#B4009E" d="M1 1h14v14H1z"/></svg>"""));
        Observation = new(stamp, new(Version, items, [new(first, second, "Demo.Parent", RelationshipOrigin.Provider)], benches: [nativeBench]),
            DateTimeOffset.UtcNow, Result<ProcessSchema>.Ok(new(connection, [type], [new("Demo.Parent", RelationShape.Hierarchy, true)],
                ["SYNTHETIC schema diagnostic: enabled unsupported native rule {\"actionType\":\"syntheticUnsupported\",\"targetField\":\"Demo.ServerField\"}"],
                provider == "github" ? "https://github.com/example/native-repository/" : "https://example.test/project/")), DateTimeOffset.UtcNow);
    }

    public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
        => ObserveAsync(root, expected, observeSchema, observeSchema, ct);
    public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => ObserveAsync(root, expected, false, true, ct);
    public Task<Result<ImmutableArray<WorkItem>>> ObserveItemsAsync(string root, NativeSourceStamp expected,
        ImmutableArray<ItemKey> keys, CancellationToken ct)
        => Task.FromResult(ItemFailure is { } error ? Result<ImmutableArray<WorkItem>>.Fail(error)
            : Result<ImmutableArray<WorkItem>>.Ok([.. Observation.Snapshot.Items.Where(item => keys.Contains(item.Key))]));
    public async Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
        string? selectedTeam, PlanningSourceKind kind, string id, ProcessSchema mappingSchema, CancellationToken ct)
    {
        var result = await ReadAsync(root, expected, false, ct);
        if (!result.IsSuccess) return result;
        var item = result.Value.Snapshot.Items.Single(item => item.Key == BrowseItem);
        if (!BrowseRetained)
            item = new WorkItem(item.Key, item.Type, item.Fields, new Revision(item.Key, "fresh-only"),
                item.RelationshipsComplete, item.Resources, item.ResourcesComplete);
        return Result<NativeSourceObservation>.Ok(result.Value with
        {
            AcquiredPlanningPage = new([item], [], null, true),
            PlanningBrowse = new(new(kind, id, "Fixture query", "Fixture query"), [item.Key], DateTimeOffset.UtcNow, selectedTeam)
        });
    }
    public Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => Task.FromResult(expected == Observation.Source
            ? Result<ImmutableArray<PlanningTeam>>.Ok([new("synthetic-team", "Synthetic team")])
            : Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.Conflict, "Synthetic authority changed"));
    private async Task<Result<NativeSourceObservation>> ObserveAsync(string root, NativeSourceStamp? expected,
        bool observeSchema, bool online, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Expected = expected; TotalCalls++;
        if (!observeSchema && LocalDelay > TimeSpan.Zero)
        {
            BeforeLocalDelay?.Invoke();
            try
            {
                if (LocalReadBarrier is { } barrier) await barrier.WaitAsync(ct);
                else await Task.Delay(LocalDelay, ct);
            }
            catch (OperationCanceledException) { LocalReadCanceled?.Invoke(); throw; }
        }
        if (online) { OnlineCalls++; BeforeOnlineReturn?.Invoke(); }
        if (observeSchema)
        {
            SchemaCalls++;
            BeforeSchemaReturn?.Invoke();
            if (RejectFullMetadata) return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "Synthetic full metadata exceeds the ordinary read budget.");
        }
        var stamp = SourceOverride ?? Observation.Source;
        if (expected is not null && stamp != expected) return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic authority changed");
        if (Replaced) return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic replacement");
        if (online && OnlineError is { } error) return Result<NativeSourceObservation>.Fail(error);
        if (online && !observeSchema && ContentReadError is { } contentError)
            return Result<NativeSourceObservation>.Fail(contentError);
        var items = Observation.Snapshot.Items;
        if (OmitGitHubNumber || MalformedGitHubNumber)
            items = items.Select(item =>
            {
                var fields = new FieldValues(item.Fields.Where(f => f.Key != Twig.Providers.GitHub.GitHubProvider.Number));
                if (MalformedGitHubNumber) fields = fields.Set(Twig.Providers.GitHub.GitHubProvider.Number, FieldValue.String("41"));
                return new WorkItem(item.Key, item.Type, fields, item.Revision, item.RelationshipsComplete);
            }).ToImmutableArray();
        return Result<NativeSourceObservation>.Ok(Observation with
        {
            Source = stamp,
            Snapshot = new(Version, items, Observation.Snapshot.Relationships,
                drafts: Observation.Snapshot.Drafts, benches: Observation.Snapshot.Benches),
            Schema = observeSchema ? Result<ProcessSchema>.Ok(new(stamp.Connection, Observation.Schema!.Value.Types,
                Observation.Schema.Value.Relations, Observation.Schema.Value.Gaps, Observation.Schema.Value.WebUrl)) : null,
            SchemaObservedAt = observeSchema ? DateTimeOffset.UtcNow : null,
        });
    }
}

sealed class Fixture : IAsyncDisposable
{
    public string Directory { get; private init; } = "";
    public string CacheDirectory => Path.Combine(Directory, "native-cache");
    public SyntheticGateway Gateway { get; private init; } = null!;
    public NativeSourceAdapter Adapter { get; private init; } = null!;
    public CatalogChanges Changes { get; private init; } = null!;
    public RegisteredSource Source { get; private init; } = null!;

    public static async Task<Fixture> Start(string provider = "ado")
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-source-adapter-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var gateway = new SyntheticGateway(provider);
            var changes = new CatalogChanges();
            var adapter = new NativeSourceAdapter(Path.Combine(directory, "native-cache"), gateway, changes);
            var observation = await adapter.AdmitAsync(directory, default);
            if (!observation.IsSuccess) throw new InvalidOperationException("Synthetic source admission failed.");
            var source = new RegisteredSource(Guid.NewGuid(), "Synthetic adapter source", directory,
                observation.Value.Binding, true, observation.Value.SchemaObservedAt);
            return new() { Directory = directory, Gateway = gateway, Changes = changes, Adapter = adapter, Source = source };
        }
        catch
        {
            System.IO.Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        System.IO.Directory.Delete(Directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
sealed class TestTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

}
