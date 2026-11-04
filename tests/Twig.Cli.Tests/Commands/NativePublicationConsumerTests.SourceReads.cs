using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeSourceReadsTypedEvidenceWithoutChangingNativeState()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var other = ItemKey.Remote(connection.Provider, connection.ResourceScope, "43");
        var item = new WorkItem(key, "Fixture.Task", FieldValues.Empty.Set("System.Title", FieldValue.String("Cached")),
            new(key, "1"), relationshipsComplete: true, resourcesComplete: true);
        var edge = new Relationship(key, other, "Native.ParentForward", RelationshipOrigin.Provider);
        var draft = new Draft(ItemKey.Local(Guid.NewGuid()), item.Type, null, item.Fields);
        var bench = new Bench(Guid.NewGuid(), "Copy this scope", [new(SelectorKind.Pin, key)]);
        var proposal = new Proposal(connection, "retained-schema", 0,
            [new("keep", OpKind.Batch, key, item.Revision, item.Fields)]);
        var journal = JournalEntry.Planned(proposal.Digest, 0);
        (await store.CommitAsync(connection, 0, new(1, [item], [edge], [draft], [proposal], [journal],
            benches: [bench]), default)).IsSuccess.ShouldBeTrue();
        var before = (await store.ReadAsync(connection, default)).Value;
        var marker = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName));
        File.Exists(Path.Combine(paths.TwigDir, "core-context.json")).ShouldBeFalse();
        var requests = _transport.TotalRequests;
        var handles = _transport.SourceHandlesDisposed;
        var local = await ReadSource(paths);
        local.IsSuccess.ShouldBeTrue(local.IsSuccess ? "" : local.Error.Message);
        local.Value.Source.Connection.ShouldBe(connection);
        local.Value.Source.StoreInstance.ShouldBe(store.InstanceId);
        local.Value.Source.PrincipalId.ShouldBe(Actor);
        local.Value.Schema.ShouldBeNull();
        local.Value.SchemaObservedAt.ShouldBeNull();
        _transport.TotalRequests.ShouldBe(requests, "local admission must not acquire/attest a PAT or make provider HTTP");
        _transport.SourceHandlesDisposed.ShouldBe(handles + 2);
        local.Value.Snapshot.Items.ShouldBe(before.Items);
        local.Value.Snapshot.Relationships.ShouldBe(before.Relationships);
        local.Value.Snapshot.Drafts.ShouldBe(before.Drafts);
        var observedBench = local.Value.Snapshot.Benches.ShouldHaveSingleItem();
        observedBench.Id.ShouldBe(bench.Id);
        observedBench.Name.ShouldBe(bench.Name);
        observedBench.Selectors.ShouldBe(bench.Selectors.Select(selector => new Selector(selector.Kind,
            selector.Item, selector.Query, selector.HierarchyRelation, connection)));
        local.Value.Snapshot.Journal.ShouldBe(before.Journal);

        var online = await ReadSource(paths, local.Value.Source, schema: true);
        online.IsSuccess.ShouldBeTrue(online.IsSuccess ? "" : online.Error.Message);
        online.Value.Source.ShouldBe(local.Value.Source);
        online.Value.Schema.ShouldNotBeNull().Value.Connection.ShouldBe(connection);
        online.Value.Schema.Value.Types.ShouldContain(type => type.Reference == "Fixture.Task");
        var layout = online.Value.Schema.Value.Types.Single(type => type.Reference == "Fixture.Task").Presentation
            .ShouldNotBeNull().Layout.ShouldNotBeNull();
        layout.Availability.ShouldBe(LayoutAvailability.Served);
        layout.SystemControls.Single().Id.ShouldBe("System.Title");
        online.Value.Schema.Value.Relations.ShouldContain(kind => kind.Shape == RelationShape.Hierarchy && kind.Directed);
        online.Value.Schema.Value.Fingerprint.ShouldNotBeNullOrWhiteSpace();
        online.Value.SchemaObservedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(online.Value.CacheReadAt);
        _transport.WorkRequests.ShouldBe(0);
        _transport.PatchAttempts.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
        _transport.QueryAfterIds.ShouldBeEmpty();
        File.Exists(Path.Combine(paths.TwigDir, "core-context.json")).ShouldBeFalse();
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName))).ShouldBe(marker);
        var after = (await store.ReadAsync(connection, default)).Value;
        after.Version.ShouldBe(before.Version);
        after.Proposals.Select(value => value.Digest).ShouldBe(before.Proposals.Select(value => value.Digest));
        after.Journal.ShouldBe(before.Journal);
    }

    [Theory]
    [InlineData("same-path-new-store")]
    [InlineData("new-path-same-store")]
    [InlineData("principal")]
    [InlineData("selection-revision")]
    [InlineData("attachment-revision")]
    [InlineData("storage-generation")]
    [InlineData("binding")]
    public async Task NativeSourceRefusesChangedOriginBeforeMetadata(string change)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var captured = await ReadSource(paths);
        captured.IsSuccess.ShouldBeTrue();
        var markerPath = Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName);
        var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(markerPath),
            CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
        if (change == "same-path-new-store")
        {
            File.Move(document.DatabasePath, document.DatabasePath + ".original");
            var replacement = (await SqliteStore.CreateAsync(document.DatabasePath)).Value;
            replacement.InstanceId.ShouldNotBe(store.InstanceId);
            (await replacement.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
            (await replacement.ReadAsync(connection, default)).Value.Version.ShouldBe(captured.Value.Snapshot.Version);
            document = document with { DatabaseInstance = replacement.InstanceId };
        }
        else if (change == "new-path-same-store")
        {
            var copy = document.DatabasePath + ".copy";
            File.Copy(document.DatabasePath, copy);
            (await SqliteStore.OpenAsync(copy)).Value.InstanceId.ShouldBe(store.InstanceId);
            document = document with { DatabasePath = copy };
        }
        else if (change == "principal")
            document = document with { PrincipalId = Sibling };
        else if (change == "selection-revision")
        {
            var config = await TwigConfiguration.LoadSplitAsync(paths);
            var origin = await _bindings.ResolveAsync(config, paths);
            SwapSelection(config, origin.Binding.BindingId);
        }
        else if (change == "binding")
        {
            var config = await TwigConfiguration.LoadSplitAsync(paths);
            var selected = await _bindings.CreateBindingAsync(config.Organization, config.Project, "sibling", false);
            SwapSelection(config, selected.BindingId);
        }
        else if (change == "storage-generation")
        {
            var config = await TwigConfiguration.LoadSplitAsync(paths);
            var origin = await _bindings.ResolveAsync(config, paths);
            origin.StorageGeneration.ShouldBeNull();
            var generation = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.Combine(paths.TwigDir, "cache"));
            await File.WriteAllTextAsync(Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile),
                JsonSerializer.Serialize(new MirrorAdmissionDocument(1, _bindings.RegistryPath,
                    origin.Operation.WorktreeFingerprint, origin.Binding.ConnectionRef, origin.Binding.BindingId,
                    generation, "admitted-" + generation + ".db"), TwigJsonContext.Default.MirrorAdmissionDocument));
        }
        else
        {
            var attachmentPath = Path.Combine(paths.TwigDir, "attachment.json");
            var attachment = JsonNode.Parse(await File.ReadAllTextAsync(attachmentPath))!;
            attachment["revision"] = attachment["revision"]!.GetValue<long>() + 1;
            await File.WriteAllTextAsync(attachmentPath, attachment.ToJsonString());
        }
        await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(document,
            CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        var requests = _transport.TotalRequests;
        var result = await ReadSource(paths, captured.Value.Source, schema: true);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldNotContain(paths.RepoRoot);
        _transport.TotalRequests.ShouldBe(requests);
        _transport.WorkRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSourceRevalidatesReplacementAfterSchemaAwait(bool replaceStore)
    {
        var (paths, connection, _) = await RelationshipWorkspaceAsync();
        var capture = (await ReadSource(paths)).Value;
        _transport.BeforeProjectMetadata = async () =>
        {
            if (replaceStore)
            {
                var markerPath = Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName);
                var marker = JsonSerializer.Deserialize(await File.ReadAllTextAsync(markerPath),
                    CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
                var database = Path.Combine(paths.TwigDir, "replacement.db");
                var replacement = (await SqliteStore.CreateAsync(database)).Value;
                (await replacement.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
                await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker with
                {
                    DatabasePath = database, DatabaseInstance = replacement.InstanceId,
                }, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
            }
            else
            {
                var config = await TwigConfiguration.LoadSplitAsync(paths);
                var origin = await _bindings.ResolveAsync(config, paths);
                SwapSelection(config, origin.Binding.BindingId);
            }
        };
        var result = await ReadSource(paths, capture.Source, schema: true);
        result.IsSuccess.ShouldBeFalse();
        _transport.WorkRequests.ShouldBe(0);
        using var released = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
    }

    [Fact]
    public async Task NativeSourceClosesSnapshotTransactionBeforeHttpAndReleasesGate()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var capture = (await ReadSource(paths)).Value;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _transport.BeforeProjectMetadata = async () =>
        {
            entered.SetResult();
            await resume.Task.WaitAsync(timeout.Token);
        };
        var reading = ReadSource(paths, capture.Source, schema: true, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        try
        {
            (await ReadSource(paths, capture.Source)).IsSuccess.ShouldBeFalse("an independent request must not inherit the reader's ambient gate");
            // An independent writer bypasses the native gate to prove no SQLite read transaction spans HTTP.
            (await store.CommitAsync(connection, 0, new(1),
                timeout.Token)).IsSuccess.ShouldBeTrue();
            (await UserBenchStore.ForRegistry(_bindings.RegistryPath).SaveAsync(new(Guid.NewGuid(), "Later", []),
                0, timeout.Token)).IsSuccess.ShouldBeTrue();
            var marker = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)),
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            foreach (var databasePath in new[] { marker.DatabasePath, Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db") })
            {
                using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1,
                }.ToString());
                await database.OpenAsync(timeout.Token);
                using var checkpoint = database.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var checkpointResult = await checkpoint.ExecuteReaderAsync(timeout.Token);
                (await checkpointResult.ReadAsync(timeout.Token)).ShouldBeTrue();
                checkpointResult.GetInt32(0).ShouldBe(0, "a live pre-HTTP read transaction would prevent the WAL checkpoint");
            }
        }
        finally { resume.TrySetResult(); }
        var observed = await reading;
        observed.IsSuccess.ShouldBeTrue(observed.IsSuccess ? "" : observed.Error.Message);
        observed.Value.Snapshot.Version.ShouldBe(0);
        observed.Value.Snapshot.Benches.ShouldBeEmpty();
        var reread = (await ReadSource(paths, capture.Source)).Value;
        reread.Source.ShouldBe(capture.Source, "ordinary cache versions are not source identities");
        reread.Snapshot.Version.ShouldBe(1);
        reread.Snapshot.Benches.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(400)]
    [InlineData(0)]
    public async Task NativeSourceSchemaFailureNeverFallsBackToCachedContent(int status)
    {
        var (paths, _, _) = await RelationshipWorkspaceAsync();
        _transport.FailSchema = status != 0;
        if (status == 0) _transport.ObservedPrincipalOverride = Sibling;
        _transport.SchemaFailureStatus = (System.Net.HttpStatusCode)status;
        var handles = _transport.SourceHandlesDisposed;
        var failed = await ReadSource(paths, schema: true);
        failed.IsSuccess.ShouldBeFalse();
        if (status is 401 or 403) failed.Error.Evidence.ShouldContain("http:" + status);
        _transport.SourceHandlesDisposed.ShouldBe(handles + 1);
        _transport.WorkRequests.ShouldBe(0);
        using var released = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
    }

    [Fact]
    public async Task NativeSourceCancellationDisposesResourcesAndReleasesGate()
    {
        var (paths, _, _) = await RelationshipWorkspaceAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        _transport.BeforeProjectMetadata = async () =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation.Token);
        };
        var handles = _transport.SourceHandlesDisposed;
        var reading = ReadSource(paths, schema: true, ct: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await reading);
        _transport.SourceHandlesDisposed.ShouldBe(handles + 1);
        using var released = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
    }

    [Fact]
    public async Task NativeSourceMissingLegacyOrNoncanonicalRootsHaveNoInitializationEffects()
    {
        foreach (var legacy in new[] { false, true })
        {
            var root = Path.Combine(_temp, legacy ? "legacy-source" : "missing-source");
            Directory.CreateDirectory(root);
            if (legacy)
            {
                Directory.CreateDirectory(Path.Combine(root, ".twig"));
                await File.WriteAllTextAsync(Path.Combine(root, ".twig", "twig.db"), "preserve legacy bytes");
            }
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            (await NativeSourceReader.ReadAsync(root)).IsSuccess.ShouldBeFalse();
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).ShouldBe(before);
            if (!legacy) Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
            else (await File.ReadAllTextAsync(before.Single())).ShouldBe("preserve legacy bytes");
        }
        (await NativeSourceReader.ReadAsync(".")).Error.Code.ShouldBe(ErrorCode.InvalidInput);
        _transport.TotalRequests.ShouldBe(0);
    }

    private Task<Result<NativeSourceObservation>> ReadSource(TwigPaths paths, NativeSourceStamp? expected = null,
        bool schema = false, CancellationToken ct = default)
        => NativeSourceReader.ReadAsync(paths.RepoRoot, expected, schema, services =>
        {
            services.AddSingleton<IConnectionBindingService>(_bindings);
            services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        }, ct);
}
