using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bench.Application;
using Bench.Domain;
using Twig.Core;
using Twig.Infrastructure.Services;

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
            if (cached.Value.Blocked) return Result<T>.Fail(ErrorCode.PolicyViolation, "This source requires explicit reauthorization.");
            NativeCacheCheck.RequireAllowed(CheckPath(source.Binding), ct);
            var previous = editorLease.Value;
            var lease = new EditorLease(source.Binding);
            editorLease.Value = lease;
            try { return Result<T>.Ok(await NativeAsync(source.NativeLocator, operation, ct).ConfigureAwait(false)); }
            finally { lease.Active = false; editorLease.Value = previous; }
        }
        catch (NativeCheckBlockedException) { return Result<T>.Fail(ErrorCode.PolicyViolation, "A pending or denied native source cannot release editor data."); }
        catch (Exception ex) when (StorageFailure(ex)) { return Result<T>.Fail(ErrorCode.StorageUnavailable, "Native editor admission evidence is unavailable."); }
        finally { gate.Release(); }
    }
    public static SourceBinding Binding(NativeSourceStamp stamp)
    {
        // Full native evidence is retained separately; this opaque digest is not decoded for a principal.
        var text = JsonSerializer.Serialize(new[] { stamp.PrincipalId, stamp.AuthorityStamp },
            NativeEvidenceJson.Default.StringArray);
        return new(stamp.Connection, stamp.StoreInstance,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }

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
        => ReadAsync(source, (expected, token) => gateway.ReadPlanningSourcesAsync(source.NativeLocator, expected, team, token), false, ct);

    public async Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(RegisteredSource source,
        NativeSourceStamp expected, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        NativeCacheCheck? check = null;
        try
        {
            check = NativeCacheCheck.Begin(CheckPath(source.Binding), explicitReauthorization: true, ct);
            var result = await NativeAsync(source.NativeLocator,
                token => gateway.DiscoverPlanningTeamsAsync(source.NativeLocator, expected, token), ct).ConfigureAwait(false);
            if (result.IsSuccess) check.Complete(ct);
            return result;
        }
        catch (NativeCheckBlockedException)
        { return Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.PolicyViolation, "Recheck this connection before loading its teams."); }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.StorageUnavailable, "Native team discovery admission evidence is unavailable."); }
        finally { check?.InvalidateIfUnfinished(changes); check?.Dispose(); gate.Release(); }
    }

    private async Task<Result<SourceObservation>> ObserveExplicitAsync(RegisteredSource source,
        Func<ProcessSchema, CancellationToken, Task<Result<NativeSourceObservation>>> acquire, CancellationToken ct, bool publish = true)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        NativeCacheCheck? check = null;
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
            check = NativeCacheCheck.Begin(CheckPath(source.Binding), explicitReauthorization: true, ct);
            var observed = await NativeAsync(source.NativeLocator,
                token => acquire(cached.Value.Schema.ToCore(source.Binding.Connection), token), ct).ConfigureAwait(false);
            if (!observed.IsSuccess) return Refuse(observed.Error, check);
            Result<SourceObservation> result;
            if (observed.Value.Schema is not null)
                result = await StoreOnline(observed.Value, source.Binding, ct).ConfigureAwait(false);
            else if (observed.Value.Source != cached.Value.Stamp.ToNative())
                return Refuse(new(ErrorCode.Conflict, "The native source changed during acquisition."), check);
            else
            {
                var schema = cached.Value.Schema.ToCore(source.Binding.Connection) with
                { Planning = observed.Value.Planning, PlanningCalendars = observed.Value.PlanningCalendars, Memberships = observed.Value.Memberships };
                result = Result<SourceObservation>.Ok(new(source.Binding, observed.Value.Snapshot, schema,
                    observed.Value.CacheReadAt, cached.Value.OnlineObservedAt, ObservationMode.OnlineValidatedCache)
                {
                    WorkspaceExclusions = observed.Value.WorkspaceExclusions, SelectedBenchId = observed.Value.SelectedBenchId,
                    PlanningSources = observed.Value.PlanningSources, PlanningBrowse = observed.Value.PlanningBrowse,
                    PlanningSourcesObservedAt = observed.Value.PlanningSourcesCache?.ObservedAt,
                });
            }
            if (result.IsSuccess) { check.Complete(ct); if (publish) changes.Publish(0); }
            return result;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Native planning refresh could not be retained; existing rules were preserved.");
        }
        finally { check?.InvalidateIfUnfinished(changes); check?.Dispose(); gate.Release(); }
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
        NativeCacheCheck? check = null;
        try
        {
            check = NativeCacheCheck.Begin(CheckPath(binding), explicitReauthorization: true, ct);
            if (File.Exists(PathFor(binding)))
            {
                var cached = await Load(binding, ct).ConfigureAwait(false);
                if (!cached.IsSuccess) return Refuse(cached.Error, check);
                if (cached.Value.Stamp.ToNative() == local.Source)
                {
                    var online = await NativeAsync(nativeLocator,
                        token => gateway.ReadOnlineAsync(nativeLocator, local.Source, token), ct).ConfigureAwait(false);
                    if (!online.IsSuccess) return Refuse(online.Error, check);
                    if (Binding(online.Value.Source) != binding)
                        return Refuse(new(ErrorCode.Conflict, "Native source changed during connection selection."), check);
                    var schema = cached.Value.Schema.ToCore(binding.Connection) with
                    { Planning = online.Value.Planning, PlanningCalendars = online.Value.PlanningCalendars, Memberships = online.Value.Memberships };
                    check.Complete(ct);
                    changes.Publish(0);
                    return Result<SourceObservation>.Ok(new(binding, online.Value.Snapshot, schema,
                        online.Value.CacheReadAt, cached.Value.OnlineObservedAt, ObservationMode.OnlineValidatedCache)
                        { WorkspaceExclusions = online.Value.WorkspaceExclusions, SelectedBenchId = online.Value.SelectedBenchId });
                }
            }
            var observed = await NativeAsync(nativeLocator,
                token => gateway.ReadAsync(nativeLocator, local.Source, true, token), ct).ConfigureAwait(false);
            if (!observed.IsSuccess) return Refuse(observed.Error, check);
            var result = await StoreOnline(observed.Value, binding, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            check.Complete(ct);
            changes.Publish(0);
            return result;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Cannot retain native revalidation state; no source grant was released.");
        }
        finally { check?.InvalidateIfUnfinished(changes); check?.Dispose(); gate.Release(); }
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

    public async Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(RegisteredSource source,
        ImmutableArray<string> identities, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        NativeCacheCheck? check = null;
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<ImmutableArray<IdentityProfile>>.Fail(cached.Error);
            if (cached.Value.Blocked)
                return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.PolicyViolation, "This source requires explicit reauthorization.");
            check = NativeCacheCheck.Begin(CheckPath(source.Binding), explicitReauthorization: false, ct);
            var result = await NativeAsync(source.NativeLocator, token => gateway.ReadIdentityProfilesAsync(
                source.NativeLocator, cached.Value.Stamp.ToNative(), identities, token), ct).ConfigureAwait(false);
            if (!result.IsSuccess) return Result<ImmutableArray<IdentityProfile>>.Fail(result.Error);
            if (Binding(result.Value.Source) != source.Binding)
                return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.Conflict, "The native profile source changed.");
            check.Complete(ct);
            return Result<ImmutableArray<IdentityProfile>>.Ok(result.Value.IdentityProfiles);
        }
        catch (NativeCheckBlockedException)
        { return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.PolicyViolation, "A pending or denied source cannot release profiles."); }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.StorageUnavailable, "Native profile admission evidence is unavailable."); }
        finally { check?.InvalidateIfUnfinished(changes); check?.Dispose(); gate.Release(); }
    }

    internal async Task<Result<SourceObservation>> CheckInitialAsync(NativePanelContext context, SourceObservation observation, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(context.Source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await Load(context.Source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
            NativeCacheCheck.RequireAllowed(CheckPath(context.Source.Binding), ct);
            if (cached.Value.Blocked || observation.Binding != context.Source.Binding
                || cached.Value.Stamp.ToNative() != context.Stamp
                || cached.Value.OnlineObservedAt != observation.SchemaObservedAt)
                return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "The initial source admission changed before projection.");
            return Result<SourceObservation>.Ok(observation);
        }
        catch (NativeCheckBlockedException)
        { return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "A pending or denied source cannot release an initial view."); }
        catch (Exception ex) when (StorageFailure(ex))
        { return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Initial source admission evidence is unavailable."); }
        finally { gate.Release(); }
    }

    internal Task<Result<SourceObservation>> ReadEditorSourceAsync(RegisteredSource source, CancellationToken ct)
        => editorLease.Value is { Active: true } lease && lease.Binding == source.Binding
            ? ReadAsync(source, (expected, token) => gateway.ReadOnlineAsync(source.NativeLocator, expected, token),
                false, ct, gateHeld: true)
            : Task.FromResult(Result<SourceObservation>.Fail(ErrorCode.Conflict, "Editor observation requires its active exact source admission lease."));

    public Task<Result<SourceObservation>> ReadPlanningAsync(RegisteredSource source, string? team, CancellationToken ct)
        => ReadAsync(source, (expected, token) => gateway.ReadPlanningAsync(source.NativeLocator, expected, team, token), false, ct);

    private async Task<Result<SourceObservation>> ReadAsync(RegisteredSource source,
        Func<NativeSourceStamp, CancellationToken, Task<Result<NativeSourceObservation>>> acquire, bool offline, CancellationToken ct,
        bool gateHeld = false)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        if (!gateHeld) await gate.WaitAsync(ct).ConfigureAwait(false);
        NativeCacheCheck? check = null;
        try
        {
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            if (!cached.IsSuccess) return Result<SourceObservation>.Fail(cached.Error);
            if (offline && cached.Value.Blocked)
                return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "This source requires explicit authorization/schema recheck.");
            // Online reads must establish current access, not inherit an older read's pending outcome.
            check = NativeCacheCheck.Begin(CheckPath(source.Binding), explicitReauthorization: !offline, ct);
            var expected = cached.Value.Stamp.ToNative();
            var local = await NativeAsync(source.NativeLocator, token => acquire(expected, token), ct).ConfigureAwait(false);
            if (!local.IsSuccess) return Refuse(local.Error, check);
            if (Binding(local.Value.Source) != source.Binding)
                return Refuse(new(ErrorCode.Conflict, "Native source changed."), check);
            var schema = cached.Value.Schema.ToCore(source.Binding.Connection) with
            { Planning = local.Value.Planning, PlanningCalendars = local.Value.PlanningCalendars, Memberships = local.Value.Memberships };
            check.Complete(ct);
            return Result<SourceObservation>.Ok(new(source.Binding, local.Value.Snapshot,
                schema, local.Value.CacheReadAt, cached.Value.OnlineObservedAt,
                offline ? ObservationMode.PermittedOfflineCache : ObservationMode.OnlineValidatedCache)
                {
                    WorkspaceExclusions = local.Value.WorkspaceExclusions, SelectedBenchId = local.Value.SelectedBenchId,
                    PlanningSources = local.Value.PlanningSources,
                    PlanningSourcesObservedAt = local.Value.PlanningSourcesCache?.ObservedAt,
                });
        }
        catch (NativeCheckBlockedException)
        {
            return Result<SourceObservation>.Fail(ErrorCode.PolicyViolation, "A denied or interrupted native check requires explicit successful reauthorization.");
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Native evidence cache is unavailable; explicitly reauthorize before using content.");
        }
        finally { check?.InvalidateIfUnfinished(changes); check?.Dispose(); if (!gateHeld) gate.Release(); }
    }

    public async Task<Result<SourceObservation>> RecheckAsync(RegisteredSource source, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(CatalogFingerprint.Binding(source.Binding), _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        NativeCacheCheck? check = null;
        try
        {
            check = NativeCacheCheck.Begin(CheckPath(source.Binding), explicitReauthorization: true, ct);
            var cached = await Load(source.Binding, ct).ConfigureAwait(false);
            var expected = cached.IsSuccess ? cached.Value.Stamp.ToNative() : null;
            var observed = await NativeAsync(source.NativeLocator,
                token => gateway.ReadAsync(source.NativeLocator, expected, true, token), ct).ConfigureAwait(false);
            if (!observed.IsSuccess)
            {
                return Refuse(observed.Error, check);
            }
            if (Binding(observed.Value.Source) != source.Binding)
                return Refuse(new(ErrorCode.Conflict, "Explicit reauthorization found a different source; register it separately."), check);
            var result = await StoreOnline(observed.Value, source.Binding, ct).ConfigureAwait(false);
            if (result.IsSuccess) { check.Complete(ct); changes.Publish(0); }
            return result;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Cannot retain or revalidate native evidence; this source remains unavailable.");
        }
        finally { check?.InvalidateIfUnfinished(changes); check?.Dispose(); gate.Release(); }
    }

    private async Task<Result<SourceObservation>> StoreOnline(NativeSourceObservation observed, SourceBinding expected, CancellationToken ct)
    {
        if (Binding(observed.Source) != expected || observed.Schema is not { IsSuccess: true } schema
            || observed.SchemaObservedAt is not { } schemaAt || schemaAt == default || schema.Value.Connection != expected.Connection)
            return Result<SourceObservation>.Fail(ErrorCode.Conflict, "Native schema/admission evidence is absent or mismatched.");
        try
        {
            await Save(expected, new(1, NativeStampDocument.From(observed.Source), SchemaDocument.From(schema.Value), schemaAt, false), ct);
            return Result<SourceObservation>.Ok(new(expected, observed.Snapshot, schema.Value, observed.CacheReadAt, schemaAt, ObservationMode.OnlineMetadata)
                { WorkspaceExclusions = observed.WorkspaceExclusions, SelectedBenchId = observed.SelectedBenchId,
                    PlanningSources = observed.PlanningSources, PlanningBrowse = observed.PlanningBrowse });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<SourceObservation>.Fail(ErrorCode.StorageUnavailable, "Cannot retain exact native admission/schema evidence.");
        }
    }
    private Result<SourceObservation> Refuse(Error error, NativeCacheCheck check)
    {
        // The pre-retained pending state is already the durable denial. No request-token-dependent IO here.
        check.InvalidateIfUnfinished(changes);
        return Result<SourceObservation>.Fail(error);
    }
    private string PathFor(SourceBinding binding) => Path.Combine(directory, CatalogFingerprint.Binding(binding) + ".json");
    private string CheckPath(SourceBinding binding) => Path.Combine(directory, CatalogFingerprint.Binding(binding) + ".check");
    private static bool StorageFailure(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException;
    private async Task<Result<NativeCacheDocument>> Load(SourceBinding binding, CancellationToken ct)
    {
        try
        {
            await using var file = File.OpenRead(PathFor(binding));
            if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("Native cache bound exceeded.");
            using var document = await JsonDocument.ParseAsync(file, new JsonDocumentOptions { MaxDepth = 64 }, ct).ConfigureAwait(false);
            PersistedJson.Validate(document.RootElement);
            var value = document.Deserialize(NativeCacheJson.Default.NativeCacheDocument);
            if (value is not { Version: 1 } || value.OnlineObservedAt == default || Binding(value.Stamp.ToNative()) != binding)
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
