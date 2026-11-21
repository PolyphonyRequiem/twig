using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bench.Application;
using Bench.Domain;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;

namespace Bench.Infrastructure;

public interface INativeSourceGateway
{
    Task<Result<NativePlanReview>> ReviewPlanAsync(string root, Guid benchId,
        ImmutableArray<NativePlanSource> sources, CancellationToken ct)
        => NativePlanReview.ReviewAsync(root, benchId, sources, ct);
    Task<Result<NativePlanReview>> OpenPlanReviewAsync(string root, string digest, CancellationToken ct)
        => NativePlanReview.OpenAsync(root, digest, ct);
    Task<Result<NativeItemEditor>> OpenEditorAsync(string root, Guid benchId, NativeSourceStamp expected, CancellationToken ct)
        => NativeItemEditor.OpenAsync(root, benchId, expected, ct);
    Task<Result<NativeItemEditor>> OpenExistingEditorAsync(string root, Guid benchId, NativeSourceStamp expected, CancellationToken ct)
        => NativeItemEditor.OpenExistingAsync(root, benchId, expected, ct);
    Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct);
    Task<Result<ImmutableArray<WorkItem>>> ObserveItemsAsync(string root, NativeSourceStamp expected, ImmutableArray<ItemKey> keys, CancellationToken ct)
        => NativeSourceReader.ObserveItemsAsync(root, expected, keys, ct);
    Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Unsupported, "Fresh native online read validation is not implemented by this gateway."));
    Task<Result<NativeSourceObservation>> ReadIdentityProfilesAsync(string root, NativeSourceStamp expected, ImmutableArray<string> identities, CancellationToken ct)
        => Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Unsupported, "Native identity profiles are not implemented by this gateway."));
    Task<Result<NativeSourceObservation>> SelectBenchAsync(string root, NativeSourceStamp expected, Guid? previous, Guid? next, CancellationToken ct)
        => NativeSourceReader.SelectBenchAsync(root, expected, previous, next, ct);
    Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => NativeSourceReader.RefreshPlanningAsync(root, expected, ct);
    Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => NativeSourceReader.DiscoverPlanningTeamsAsync(root, expected, ct);
    Task<Result<NativeSourceObservation>> ReadPlanningAsync(string root, NativeSourceStamp expected, string? selectedTeam, CancellationToken ct)
        => NativeSourceReader.ReadPlanningAsync(root, expected, selectedTeam, ct);
    Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string root, NativeSourceStamp expected, string? selectedTeam, CancellationToken ct)
        => NativeSourceReader.RefreshPlanningAsync(root, expected, selectedTeam, ct);
    Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => NativeSourceReader.DiscoverPlanningSourcesAsync(root, expected, ct);
    Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string root, NativeSourceStamp expected, string? selectedTeam, CancellationToken ct)
        => NativeSourceReader.DiscoverPlanningSourcesAsync(root, expected, selectedTeam, ct);
    Task<Result<NativeSourceObservation>> ReadPlanningSourcesAsync(string root, NativeSourceStamp expected, string? selectedTeam, CancellationToken ct)
        => NativeSourceReader.ReadPlanningSourcesAsync(root, expected, selectedTeam, ct);
    Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
        PlanningSourceKind kind, string id, string? team, CancellationToken ct)
        => NativeSourceReader.BrowsePlanningAsync(root, expected, kind, id, team, ct);
    Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
        string? selectedTeam, PlanningSourceKind kind, string id, CancellationToken ct)
        => NativeSourceReader.BrowsePlanningAsync(root, expected, selectedTeam, kind, id, ct);
    Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string root, NativeSourceStamp expected,
        string? selectedTeam, PlanningSourceKind kind, string id, ProcessSchema mappingSchema, CancellationToken ct)
        => NativeSourceReader.BrowsePlanningAsync(root, expected, selectedTeam, kind, id, mappingSchema, ct);
}
public sealed class NativeSourceGateway : INativeSourceGateway
{
    public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
        => NativeSourceReader.ReadAsync(root, expected, observeSchema, ct);
    public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => NativeSourceReader.ReadOnlineAsync(root, expected, ct);
    public Task<Result<NativeSourceObservation>> ReadIdentityProfilesAsync(string root, NativeSourceStamp expected, ImmutableArray<string> identities, CancellationToken ct)
        => NativeSourceReader.ReadIdentityProfilesAsync(root, expected, identities, ct);
}

public sealed class NativeSourceAdapter(string directory, INativeSourceGateway gateway, CatalogChanges changes) : ISourceReader
{
    private sealed class HistoricalSourceDeniedException : Exception;
    public static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, DateTimeOffset> metadataAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> nativeRoots = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly AsyncLocal<NativeLease?> nativeLease = new();
    private readonly AsyncLocal<EditorLease?> editorLease = new();
    private sealed class EditorLease(SourceBinding binding)
    {
        internal SourceBinding Binding { get; } = binding;
        internal bool Active { get; set; } = true;
    }
    private sealed class NativeLease(string root)
    {
        internal string Root { get; } = root;
        internal bool Active { get; set; } = true;
    }
    internal async Task<T> NativeAsync<T>(string root, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (nativeLease.Value is { Active: true } current && comparer.Equals(current.Root, root))
            return await operation(ct).ConfigureAwait(false);
        var gate = nativeRoots.GetOrAdd(root, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var previous = nativeLease.Value;
        var lease = new NativeLease(root);
        nativeLease.Value = lease;
        try { return await operation(ct).ConfigureAwait(false); }
        finally { lease.Active = false; nativeLease.Value = previous; gate.Release(); }
    }
    internal Task<T> NativeManyAsync<T>(IEnumerable<string> roots, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var ordered = roots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        return Next(0, ct);
        Task<T> Next(int index, CancellationToken token) => index == ordered.Length ? operation(token)
            : NativeAsync(ordered[index], inner => Next(index + 1, inner), token);
    }
    public async Task<Result<T>> EditorOperationAsync<T>(RegisteredSource source, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<T>.Fail(cached.Error);
            RequireHistory(source.Binding);
            var previous = editorLease.Value;
            var lease = new EditorLease(source.Binding);
            editorLease.Value = lease;
            try
            {
                return Result<T>.Ok(await NativeAsync(source.NativeLocator, async token =>
                {
                    using var root = Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(source.NativeLocator, exclusive: true);
                    using var observations = NativeHistoricalEvidence.ObserveFailures(source.Binding.Connection,
                        error => RecordContentFailure(source.Binding, error));
                    return await operation(token).ConfigureAwait(false);
                }, ct).ConfigureAwait(false));
            }
            finally { lease.Active = false; editorLease.Value = previous; }
        }
        catch (HistoricalSourceDeniedException) { return Result<T>.Fail(ErrorCode.PolicyViolation, "Recorded source denial prevents release of retained editor data."); }
        catch (Exception ex) when (StorageFailure(ex)) { return Result<T>.Fail(ErrorCode.StorageUnavailable, "Native editor admission evidence is unavailable."); }
        finally { gate.Release(); }
    }
    public static SourceBinding Binding(NativeSourceStamp stamp)
        => new(stamp.Connection, stamp.StoreInstance, NativeHistoricalEvidence.NativeStamp(stamp));

    public Task<Result<SourceObservation>> RefreshPlanningAsync(RegisteredSource source, NativeSourceStamp expected, CancellationToken ct)
        => ObserveExplicitAsync(source, (_, token) => gateway.RefreshPlanningAsync(source.NativeLocator, expected, token), ct);
    public Task<Result<SourceObservation>> RefreshPlanningAsync(RegisteredSource source, NativeSourceStamp expected, string? team, CancellationToken ct)
        => ObserveExplicitAsync(source, (_, token) => gateway.RefreshPlanningAsync(source.NativeLocator, expected, team, token), ct);

    public Task<Result<SourceObservation>> DiscoverPlanningSourcesAsync(RegisteredSource source, NativeSourceStamp expected, CancellationToken ct)
        => ObserveExplicitAsync(source, (_, token) => gateway.DiscoverPlanningSourcesAsync(source.NativeLocator, expected, token), ct, publish: false);
    public Task<Result<SourceObservation>> DiscoverPlanningSourcesAsync(RegisteredSource source, NativeSourceStamp expected, string? team, CancellationToken ct)
        => ObserveExplicitAsync(source, (_, token) => gateway.DiscoverPlanningSourcesAsync(source.NativeLocator, expected, team, token), ct, publish: false);

    public Task<Result<SourceObservation>> BrowsePlanningAsync(RegisteredSource source, NativeSourceStamp expected,
        PlanningSourceKind kind, string id, string? team, CancellationToken ct)
        => ObserveExplicitAsync(source, (_, token) => gateway.BrowsePlanningAsync(source.NativeLocator, expected, kind, id, team, token), ct);
    public Task<Result<SourceObservation>> BrowsePlanningAsync(RegisteredSource source, NativeSourceStamp expected,
        string? team, PlanningSourceKind kind, string id, CancellationToken ct)
        => ObserveExplicitAsync(source, (schema, token) => gateway.BrowsePlanningAsync(source.NativeLocator, expected, team, kind, id, schema, token), ct);

    public Task<Result<SourceObservation>> ReadPlanningSourcesAsync(RegisteredSource source, string? team, CancellationToken ct)
        => ReadAsync(source, (expected, token) => gateway.ReadPlanningSourcesAsync(source.NativeLocator, expected, team, token), false, ct,
            metadataOnly: true);

    public async Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(RegisteredSource source,
        NativeSourceStamp expected, CancellationToken ct)
    {
        if (Binding(expected) != source.Binding)
            return Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.Conflict, "The team discovery source changed.");
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await NativeAsync(source.NativeLocator,
                token => gateway.DiscoverPlanningTeamsAsync(source.NativeLocator, expected, token), ct).ConfigureAwait(false);
            if (!result.IsSuccess && result.Error.Evidence.Contains("http:401"))
                RecordSourceDenial(source.Binding);
            return result;
        }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.StorageUnavailable, "Native team discovery is unavailable."); }
        finally { gate.Release(); }
    }

    private async Task<Result<SourceObservation>> ObserveExplicitAsync(RegisteredSource source,
        Func<ProcessSchema, CancellationToken, Task<Result<NativeSourceObservation>>> acquire, CancellationToken ct, bool publish = true)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
            var observed = await NativeAsync(source.NativeLocator, async token =>
            {
                using var operation = Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(source.NativeLocator, exclusive: true);
                using var observations = NativeHistoricalEvidence.ObserveFailures(source.Binding.Connection,
                    error => RecordContentFailure(source.Binding, error));
                var result = await acquire(cached.Value.Schema.ToCore(source.Binding.Connection), token).ConfigureAwait(false);
                if (result.IsSuccess && result.Value.Source == cached.Value.Stamp.ToNative())
                    RestoreRetainedBrowseItems(source.Binding, result.Value);
                return result;
            }, ct).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                if (observed.Error.Evidence.Contains("http:401")) RecordSourceDenial(source.Binding);
                return Result<SourceObservation>.Fail(observed.Error);
            }
            if (observed.Value.Source != cached.Value.Stamp.ToNative())
                return Result<SourceObservation>.Fail(ErrorCode.Conflict, "The native source changed during acquisition.");
            Result<SourceObservation> result;
            if (observed.Value.Schema is not null)
                result = await StoreMetadataAsync(observed.Value, source.Binding, ct).ConfigureAwait(false);
            else
            {
                var schema = cached.Value.Schema.ToCore(source.Binding.Connection) with
                { Planning = observed.Value.Planning, PlanningCalendars = observed.Value.PlanningCalendars, Memberships = observed.Value.Memberships };
                result = Result<SourceObservation>.Ok(new(source.Binding, observed.Value.Snapshot, schema,
                    observed.Value.CacheReadAt, cached.Value.OnlineObservedAt, ObservationMode.OnlineMetadata)
                {
                    WorkspaceExclusions = observed.Value.WorkspaceExclusions, SelectedBenchId = observed.Value.SelectedBenchId,
                    PlanningSources = observed.Value.PlanningSources, PlanningBrowse = observed.Value.PlanningBrowse,
                    AcquiredPlanningPage = observed.Value.AcquiredPlanningPage,
                    PlanningSourcesObservedAt = observed.Value.PlanningSourcesCache?.ObservedAt,
                    WithdrawnItems = WithdrawnKeys(source.Binding),
                });
            }
            if (result.IsSuccess && publish) changes.Publish(0);
            return result;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Native planning refresh could not be retained; existing rules were preserved.");
        }
        finally { gate.Release(); }
    }

    public async Task<Result<SourceObservation>> AdmitAsync(string nativeLocator, CancellationToken ct)
    {
        // This local-only read discovers exact recorded identity without token/provider acquisition.
        var local = await NativeAsync(nativeLocator, token => gateway.ReadAsync(nativeLocator, null, false, token), ct).ConfigureAwait(false);
        if (!local.IsSuccess) return Result<SourceObservation>.Fail(local.Error);
        return await AdmitAsync(nativeLocator, local.Value, ct).ConfigureAwait(false);
    }

    internal async Task<Result<SourceObservation>> AdmitAsync(string nativeLocator, NativeSourceObservation local, CancellationToken ct)
    {
        var binding = Binding(local.Source);
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess && cached.Error.Evidence.Contains("legacy-native-cache"))
                return Result<SourceObservation>.Fail(cached.Error);
            if (cached.IsSuccess && cached.Value.Stamp.ToNative() == local.Source)
            {
                RequireHistory(binding);
                var schema = cached.Value.Schema.ToCore(binding.Connection) with
                { Planning = local.Planning, PlanningCalendars = local.PlanningCalendars, Memberships = local.Memberships };
                return Result<SourceObservation>.Ok(new(binding, local.Snapshot, schema,
                    local.CacheReadAt, cached.Value.OnlineObservedAt, ObservationMode.PermittedOfflineCache)
                    { WorkspaceExclusions = local.WorkspaceExclusions, SelectedBenchId = local.SelectedBenchId,
                        WithdrawnItems = WithdrawnKeys(binding) });
            }
            return await ObserveAndRetainAsync(nativeLocator, local.Source, binding, ct).ConfigureAwait(false);
        }
        catch (HistoricalSourceDeniedException)
        { return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "Known source denial blocks historical work-item display."); }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Cannot read or retain native source evidence.");
        }
        finally { gate.Release(); }
    }

    public Task<Result<SourceObservation>> ReadAsync(RegisteredSource source, CancellationToken ct)
        => ReadAsync(source, (expected, token) => source.AllowOffline
            ? gateway.ReadAsync(source.NativeLocator, expected, false, token)
            : gateway.ReadOnlineAsync(source.NativeLocator, expected, token), source.AllowOffline, ct);

    internal async Task<Result<SourceObservation>> ReadCachedAdmissionAsync(string root, NativeSourceObservation local,
        string expectedBinding, CancellationToken ct, bool reuseObservation = false)
    {
        var binding = Binding(local.Source);
        if (CatalogFingerprint.Binding(binding) != expectedBinding)
            return Result<SourceObservation>.Fail(ErrorCode.Conflict, "The retained admission belongs to another exact source.");
        var cached = await Load(binding, ct).ConfigureAwait(false);
        if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
        if (cached.Value.Stamp.ToNative() != local.Source)
            return Result<SourceObservation>.Fail(ErrorCode.Conflict, "The admitted native source changed before cached restoration.");
        var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(root)).AsSpan(0, 16));
        var source = new RegisteredSource(identity, "Cached native source", root, binding, true, cached.Value.OnlineObservedAt);
        return reuseObservation
            ? await ReadAsync(source, (_, _) => Task.FromResult(Result<NativeSourceObservation>.Ok(local)), true, ct).ConfigureAwait(false)
            : await ReadAsync(source, ct).ConfigureAwait(false);
    }

    public Task<Result<SourceObservation>> ReadOnlineAsync(RegisteredSource source, CancellationToken ct)
        => ReadAsync(source, (expected, token) => gateway.ReadOnlineAsync(source.NativeLocator, expected, token), false, ct);

    internal async Task<Result<ImmutableArray<WorkItem>>> ObserveItemsAsync(NativePanelContext context,
        ImmutableArray<ItemKey> keys, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(context.Source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await NativeAsync(context.Root, async token =>
            {
                using var operation = Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(context.Root, exclusive: true);
                var result = await gateway.ObserveItemsAsync(context.Root, context.Stamp, keys, token).ConfigureAwait(false);
                if (result.IsSuccess) return result;
                if (result.Error.Evidence.Contains("http:401"))
                {
                    RecordSourceDenial(context.Source.Binding);
                    return result;
                }
                var unavailable = keys.Where(key => key.BelongsTo(context.Stamp.Connection)
                    && result.Error.Evidence.Contains($"item-unavailable:{key.NativeId}")).Select(key => key.NativeId!).ToArray();
                if (unavailable.Length != 0)
                {
                    try
                    {
                        if (NativeHistoricalEvidence.RecordWithdrawals(PathFor(context.Source.Binding), unavailable))
                            changes.Publish(0);
                    }
                    catch (Exception ex) when (StorageFailure(ex))
                    {
                        deniedSources.TryAdd(context.Source.Binding, 0);
                        changes.Publish(0);
                        throw;
                    }
                }
                return result;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<ImmutableArray<WorkItem>>.Fail(ErrorCode.StorageUnavailable,
                "Native item evidence could not be retained.");
        }
        finally { gate.Release(); }
    }

    public async Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(RegisteredSource source,
        ImmutableArray<string> identities, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<ImmutableArray<IdentityProfile>>.Fail(cached.Error);
            RequireHistory(source.Binding);
            var result = await NativeAsync(source.NativeLocator, token => gateway.ReadIdentityProfilesAsync(
                source.NativeLocator, cached.Value.Stamp.ToNative(), identities, token), ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                if (result.Error.Code is ErrorCode.Conflict or ErrorCode.PolicyViolation
                    || result.Error.Evidence.Contains("http:401"))
                {
                    RecordSourceDenial(source.Binding);
                }
                return Result<ImmutableArray<IdentityProfile>>.Fail(result.Error);
            }
            if (result.Value.Source != cached.Value.Stamp.ToNative())
                return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.Conflict, "The native profile source changed.");
            return Result<ImmutableArray<IdentityProfile>>.Ok(result.Value.IdentityProfiles);
        }
        catch (HistoricalSourceDeniedException)
        { return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.PolicyViolation, "Recorded source denial prevents release of profiles."); }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.StorageUnavailable, "Native profile admission evidence is unavailable."); }
        finally { gate.Release(); }
    }

    internal async Task<Result<SourceObservation>> CheckInitialAsync(NativePanelContext context, SourceObservation observation, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(context.Source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(context.Source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
            RequireHistory(context.Source.Binding);
            if (observation.Binding != context.Source.Binding
                || cached.Value.Stamp.ToNative() != context.Stamp
                || cached.Value.OnlineObservedAt != observation.SchemaObservedAt
                || !WithdrawnKeys(context.Source.Binding).SetEquals(observation.WithdrawnItems))
                return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "The initial source admission changed before projection.");
            return Result<SourceObservation>.Ok(observation);
        }
        catch (HistoricalSourceDeniedException)
        { return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "Recorded source denial prevents release of the historical view."); }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Initial source admission evidence is unavailable."); }
        finally { gate.Release(); }
    }

    internal async Task<Result<bool>> CheckWorkspaceAsync(NativePanelContext context, SourceObservation observation, CancellationToken ct,
        bool recheckLocalSnapshot = true)
    {
        if (recheckLocalSnapshot)
        {
            var local = await NativeAsync(context.Root,
                token => gateway.ReadAsync(context.Root, context.Stamp, false, token), ct).ConfigureAwait(false);
            if (!local.IsSuccess) return Result<bool>.Fail(local.Error);
            if (local.Value.Source != context.Stamp || local.Value.Snapshot.Version != observation.Snapshot.Version
                || local.Value.SelectedBenchId != observation.SelectedBenchId
                || BenchCodec.SerializeSelectors(local.Value.WorkspaceExclusions)
                    != BenchCodec.SerializeSelectors(observation.WorkspaceExclusions))
                return Result<bool>.Fail(ErrorCode.Conflict, "Local source state changed during workspace loading. Read the workspace again.");
        }
        var admission = await CheckInitialAsync(context, observation, ct).ConfigureAwait(false);
        return admission.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(admission.Error);
    }

    public Task<Result<SourceObservation>> ReadPlanningAsync(RegisteredSource source, string? team, CancellationToken ct)
        => ReadAsync(source, (expected, token) => gateway.ReadPlanningAsync(source.NativeLocator, expected, team, token), false, ct,
            metadataOnly: true);

    internal async Task<Result<SourceObservation>> ReadEditorSourceAsync(RegisteredSource source, CancellationToken ct)
    {
        if (editorLease.Value is not { Active: true } lease || lease.Binding != source.Binding)
            return Result<SourceObservation>.Fail(ErrorCode.Conflict, "Editor observation requires its active exact source lease.");
        var read = await ReadAsync(source, (expected, token) => gateway.ReadAsync(source.NativeLocator, expected, false, token),
            true, ct, gateHeld: true).ConfigureAwait(false);
        return read.IsSuccess ? Result<SourceObservation>.Ok(read.Value with
        {
            Snapshot = new NativeHistoricalProjection(read.Value.Snapshot, read.Value.WithdrawnItems).Snapshot()
        }) : read;
    }

    private async Task<Result<SourceObservation>> ReadAsync(RegisteredSource source,
        Func<NativeSourceStamp, CancellationToken, Task<Result<NativeSourceObservation>>> acquire, bool offline, CancellationToken ct,
        bool metadataOnly = false, bool gateHeld = false)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        if (!gateHeld) await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
            if (offline) RequireHistory(source.Binding);
            var expected = cached.Value.Stamp.ToNative();
            var local = await NativeAsync(source.NativeLocator, token => acquire(expected, token), ct).ConfigureAwait(false);
            if (!local.IsSuccess)
            {
                if (IsSourceDenial(local.Error, content: !metadataOnly))
                    RecordSourceDenial(source.Binding);
                return Result<SourceObservation>.Fail(local.Error);
            }
            if (local.Value.Source != expected)
                return Result<SourceObservation>.Fail(ErrorCode.Conflict, "Native source changed.");
            if (!metadataOnly) RequireHistory(source.Binding);
            var schema = cached.Value.Schema.ToCore(source.Binding.Connection) with
            { Planning = local.Value.Planning, PlanningCalendars = local.Value.PlanningCalendars, Memberships = local.Value.Memberships };
            return Result<SourceObservation>.Ok(new(source.Binding, local.Value.Snapshot,
                schema, local.Value.CacheReadAt, cached.Value.OnlineObservedAt,
                offline ? ObservationMode.PermittedOfflineCache
                    : metadataOnly ? ObservationMode.OnlineMetadata : ObservationMode.OnlineValidatedCache)
                {
                    WorkspaceExclusions = local.Value.WorkspaceExclusions, SelectedBenchId = local.Value.SelectedBenchId,
                    PlanningSources = local.Value.PlanningSources,
                    PlanningSourcesObservedAt = local.Value.PlanningSourcesCache?.ObservedAt,
                    WithdrawnItems = WithdrawnKeys(source.Binding),
                });
        }
        catch (HistoricalSourceDeniedException)
        {
            return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "Recorded source denial blocks historical work-item display.");
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Native evidence cache is unavailable; explicitly reauthorize before using content.");
        }
        finally { if (!gateHeld) gate.Release(); }
    }

    public async Task<Result<bool>> RefreshMetadataAsync(UserBenchWorkspace workspace, DateTimeOffset now, CancellationToken ct)
    {
        var local = await NativeAsync(workspace.Root,
            token => gateway.ReadAsync(workspace.Root, null, false, token), ct).ConfigureAwait(false);
        if (!local.IsSuccess) return Result<bool>.Fail(local.Error);
        if (local.Value.Source.Connection != workspace.Connection || local.Value.Source.StoreInstance != workspace.StoreInstance)
            return Result<bool>.Fail(ErrorCode.Conflict, "The registered metadata source changed; explicit admission is required.");
        var binding = Binding(local.Value.Source);
        // A registered workspace is not an admission. Background work must never create one.
        if (!File.Exists(PathFor(binding))) return Result<bool>.Ok(false);
        var key = CatalogFingerprint.Binding(binding);
        var gate = gates.GetOrAdd(key, _ => new(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return Result<bool>.Ok(false);
        try
        {
            var cached = await Load(binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<bool>.Fail(cached.Error);
            RequireHistory(binding);
            if (now - cached.Value.OnlineObservedAt < MetadataRefreshInterval
                || metadataAttempts.TryGetValue(key, out var attempted) && now - attempted < MetadataRefreshInterval)
                return Result<bool>.Ok(false);
            metadataAttempts[key] = now;
            var observed = await NativeAsync(workspace.Root,
                token => gateway.ReadAsync(workspace.Root, cached.Value.Stamp.ToNative(), true, token), ct).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                if (IsSourceDenial(observed.Error)) RecordSourceDenial(binding);
                return Result<bool>.Fail(observed.Error);
            }
            if (observed.Value.Source != cached.Value.Stamp.ToNative())
                return Result<bool>.Fail(ErrorCode.Conflict, "The metadata source changed during discovery.");
            var stored = await StoreMetadataAsync(observed.Value, binding, ct).ConfigureAwait(false);
            if (!stored.IsSuccess) return Result<bool>.Fail(stored.Error);
            changes.Publish(0);
            return Result<bool>.Ok(true);
        }
        catch (HistoricalSourceDeniedException) { return Result<bool>.Ok(false); }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Scheduled metadata refresh could not retain native evidence."); }
        finally { gate.Release(); }
    }

    public async Task<Result<SourceObservation>> RecheckAsync(RegisteredSource source, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess && cached.Error.Evidence.Contains("legacy-native-cache"))
                return Result<SourceObservation>.Fail(cached.Error);
            NativeSourceStamp expected;
            if (cached.IsSuccess)
                expected = cached.Value.Stamp.ToNative();
            else
            {
                // Recover the stamp locally, but do not query a replacement source or principal.
                var local = await NativeAsync(source.NativeLocator,
                    token => gateway.ReadAsync(source.NativeLocator, null, false, token), ct).ConfigureAwait(false);
                if (!local.IsSuccess) return Refuse(local.Error, source.Binding);
                expected = local.Value.Source;
                if (Binding(expected) != source.Binding)
                    return Result<SourceObservation>.Fail(ErrorCode.Conflict, "The connection check found a different source; register it separately.");
            }

            return await ObserveAndRetainAsync(source.NativeLocator, expected, source.Binding, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Cannot retain or revalidate native evidence; this source remains unavailable.");
        }
        finally { gate.Release(); }
    }

    private async Task<Result<SourceObservation>> ObserveAndRetainAsync(string locator, NativeSourceStamp expected,
        SourceBinding binding, CancellationToken ct)
    {
        var observed = await NativeAsync(locator,
            token => gateway.ReadAsync(locator, expected, true, token), ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Refuse(observed.Error, binding);
        if (observed.Value.Source != expected)
            return Result<SourceObservation>.Fail(ErrorCode.Conflict, "The connection check found a different source; register it separately.");
        var result = await StoreMetadataAsync(observed.Value, binding, ct).ConfigureAwait(false);
        if (result.IsSuccess) changes.Publish(0);
        return result;
    }

    private async Task<Result<SourceObservation>> StoreMetadataAsync(
        NativeSourceObservation observed, SourceBinding expected, CancellationToken ct)
    {
        if (Binding(observed.Source) != expected || observed.Schema is not { IsSuccess: true } schema
            || observed.SchemaObservedAt is not { } schemaAt || schemaAt == default || schema.Value.Connection != expected.Connection)
            return Result<SourceObservation>.Fail(ErrorCode.Conflict, "Native schema/admission evidence is absent or mismatched.");
        try
        {
            await Save(expected, new(2, NativeStampDocument.From(observed.Source), SchemaDocument.From(schema.Value), schemaAt), ct);
            return Result<SourceObservation>.Ok(new(expected, observed.Snapshot, schema.Value, observed.CacheReadAt, schemaAt, ObservationMode.OnlineMetadata)
                { WorkspaceExclusions = observed.WorkspaceExclusions, SelectedBenchId = observed.SelectedBenchId,
                    PlanningSources = observed.PlanningSources, PlanningBrowse = observed.PlanningBrowse,
                    AcquiredPlanningPage = observed.AcquiredPlanningPage, WithdrawnItems = WithdrawnKeys(expected) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Cannot retain exact native admission/schema evidence.");
        }
    }
    private Result<SourceObservation> Refuse(Error error, SourceBinding binding)
    {
        if (IsSourceDenial(error)) RecordSourceDenial(binding);
        return Result<SourceObservation>.Fail(error);
    }
    private static bool IsSourceDenial(Error error, bool content = false)
        => error.Code == ErrorCode.PolicyViolation || error.Evidence.Contains("http:401")
            || content && error.Evidence.Contains("http:403");

    private readonly ConcurrentDictionary<SourceBinding, byte> deniedSources = new();

    private void RequireHistory(SourceBinding binding)
    {
        if (deniedSources.ContainsKey(binding)) throw new HistoricalSourceDeniedException();
        try { using var denial = File.OpenRead(PathFor(binding) + ".denied"); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new HistoricalSourceDeniedException();
    }

    private bool HasLegacyCache(SourceBinding binding)
    {
        // Reject the obsolete cache format as a unit; do not interpret its grant state.
        try
        {
            _ = File.GetAttributes(Path.ChangeExtension(PathFor(binding), ".check"));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static Result<NativeCacheDocument> LegacyCacheUnavailable() =>
        Result<NativeCacheDocument>.Fail(new Error(ErrorCode.StorageUnavailable,
            "Legacy native cache cannot be reused. Initialize a fresh Twig workspace before registering this source.")
            { Evidence = ["legacy-native-cache"] });

    private void RecordSourceDenial(SourceBinding binding)
    {
        deniedSources.TryAdd(binding, 0);
        changes.Publish(0);
            NativeHistoricalEvidence.SaveSourceDenial(PathFor(binding));
    }

    private Error RecordContentFailure(SourceBinding binding, Error error)
    {
            try
            {
                if (error.Evidence.Contains("http:401"))
                    RecordSourceDenial(binding);
                else
                {
                    var unavailable = NativeHistoricalEvidence.UnavailableIds(error).ToArray();
                    if (unavailable.Length != 0 && NativeHistoricalEvidence.RecordWithdrawals(PathFor(binding), unavailable))
                        changes.Publish(0);
                }
                return error;
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                deniedSources.TryAdd(binding, 0);
                changes.Publish(0);
                return new(ErrorCode.StorageUnavailable, "Could not retain the provider refusal; no work-item data was released.");
            }
    }
    private string PathFor(SourceBinding binding) => Path.Combine(directory,
        NativeHistoricalEvidence.BindingKey(binding.Connection, binding.StoreInstance, binding.NativeStamp) + ".json");

    private void RestoreRetainedBrowseItems(SourceBinding binding, NativeSourceObservation observation)
    {
        if (observation is not { AcquiredPlanningPage: { Complete: true, Continuation: null } page,
                PlanningBrowse: { } browse })
            return;
        var withdrawn = ReadWithdrawals(binding);
        if (withdrawn.IsEmpty || page.Items.Length != browse.Items.Length
            || !browse.Items.ToHashSet().SetEquals(page.Items.Select(item => item.Key)))
            return;
        var retained = observation.Snapshot.Items.ToDictionary(item => item.Key);
        var restored = page.Items.Where(item => item.Key.BelongsTo(binding.Connection)
            && item.Revision is not null && withdrawn.Contains(item.Key.NativeId!)
            && retained.TryGetValue(item.Key, out var mirror) && mirror.Revision == item.Revision
            && mirror.Type == item.Type && mirror.Fields.Equals(item.Fields))
            .Select(item => item.Key.NativeId!);
        var remaining = withdrawn.Except(restored);
        if (remaining.Count == withdrawn.Count) return;
        SaveWithdrawals(binding, remaining);
        changes.Publish(0);
    }
    private ImmutableHashSet<ItemKey> WithdrawnKeys(SourceBinding binding)
        => ReadWithdrawals(binding).Select(id => ItemKey.Remote(
            binding.Connection.Provider, binding.Connection.ResourceScope, id)).ToImmutableHashSet();
    private ImmutableHashSet<string> ReadWithdrawals(SourceBinding binding)
        => NativeHistoricalEvidence.ReadWithdrawals(PathFor(binding));

    private void SaveWithdrawals(SourceBinding binding, ImmutableHashSet<string> ids)
        => NativeHistoricalEvidence.SaveWithdrawals(PathFor(binding), ids);
    private static bool StorageFailure(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException;
    private async Task<Result<NativeCacheDocument>> Load(SourceBinding binding, CancellationToken ct)
    {
        try
        {
            if (HasLegacyCache(binding)) return LegacyCacheUnavailable();
            await using var file = File.OpenRead(PathFor(binding));
            if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("Native cache bound exceeded.");
            using var document = await JsonDocument.ParseAsync(file, new JsonDocumentOptions { MaxDepth = 64 }, ct).ConfigureAwait(false);
            PersistedJson.Validate(document.RootElement);
            if (document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1)
                return LegacyCacheUnavailable();
            var value = document.Deserialize(NativeCacheJson.Default.NativeCacheDocument);
            if (value is not { Version: 2 } || value.OnlineObservedAt == default || Binding(value.Stamp.ToNative()) != binding)
                throw new JsonException("Invalid native admission cache.");
            _ = value.Schema.ToCore(binding.Connection);
            return Result<NativeCacheDocument>.Ok(value);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return Result<NativeCacheDocument>.Fail(ErrorCode.StorageUnavailable, "Exact native admission/schema cache missing; explicit reauthorization is required.");
        }
    }
    private async Task Save(SourceBinding binding, NativeCacheDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, NativeCacheJson.Default.NativeCacheDocument);
        if (bytes.Length > 4 * 1024 * 1024) throw new IOException("Native cache bound exceeded.");
        var target = PathFor(binding);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            using var retained = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
            retained.Flush(flushToDisk: true);
            ct.ThrowIfCancellationRequested();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class NativeEvidenceJson : System.Text.Json.Serialization.JsonSerializerContext;
