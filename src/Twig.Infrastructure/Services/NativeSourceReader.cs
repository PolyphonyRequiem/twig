using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Services;

/// <summary>Recorded native identity evidence, not a credential or an authorization capability.</summary>
public sealed record NativeSourceStamp(Connection Connection, Guid StoreInstance, string PrincipalId, string AuthorityStamp);

/// <summary>A coherent local snapshot and, when requested, separately observed provider schema.</summary>
public sealed record NativeSourceObservation(NativeSourceStamp Source, StoreSnapshot Snapshot,
    DateTimeOffset CacheReadAt, Result<ProcessSchema>? Schema, DateTimeOffset? SchemaObservedAt)
{
    public ImmutableArray<Selector> WorkspaceExclusions { get; init; } = [];
    public Guid? SelectedBenchId { get; init; }
    public PlanningMetadata? Planning { get; init; }
    public ImmutableArray<PlanningMetadata> PlanningCalendars { get; init; } = [];
    public ImmutableArray<PlanningTeam> PlanningTeams { get; init; } = [];
    public PlanningSources? PlanningSources { get; init; }
    public NativePlanningSourcesCache PlanningSourcesCache { get; init; } = new(null, null);
    public PlanningBrowse? PlanningBrowse { get; init; }
    public ImmutableArray<PlanningMembership> Memberships { get; init; } = [];
    public ImmutableArray<IdentityProfile> IdentityProfiles { get; init; } = [];
}

/// <summary>Request-scoped native reads and explicit local selection/cache updates. No upstream mutations are exposed.</summary>
public static class NativeSourceReader
{
    public static Task<Result<NativeSourceObservation>> ReadIdentityProfilesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, ImmutableArray<string> identities, CancellationToken ct = default)
        => expectedSource is null || identities.IsDefaultOrEmpty || identities.Length > 16
            || identities.Distinct().Count() != identities.Length
            || identities.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured authority and 1-16 distinct identity IDs are required."))
            : ReadCoreAsync(workspaceRoot, expectedSource, false, null, null, ct, validateReadAccess: true, profileIds: identities);
    /// <summary>
    /// Reads recorded native data without token acquisition or HTTP unless schema observation is requested.
    /// An expected source refuses identity/store replacement, not ordinary snapshot version changes.
    /// Calls have a thirty-second ceiling; caller cancellation propagates. Schema failures refuse all content.
    /// Offline permission, previously observed schema and known-denial policy belong to the calling host.
    /// </summary>
    public static Task<Result<NativeSourceObservation>> ReadAsync(string workspaceRoot,
        NativeSourceStamp? expectedSource = null, bool observeSchema = false, CancellationToken ct = default)
        => ReadAsync(workspaceRoot, expectedSource, observeSchema, null, ct);

    internal static async Task<Result<NativeSourceObservation>> ReadAsync(string workspaceRoot,
        NativeSourceStamp? expectedSource, bool observeSchema, Action<IServiceCollection>? configure, CancellationToken ct)
        => await ReadCoreAsync(workspaceRoot, expectedSource, observeSchema, configure, null, ct).ConfigureAwait(false);

    /// <summary>
    /// Validates current remote actor/target read access before releasing the recorded snapshot.
    /// Requires captured authority, does not discover schema/presentation, and never grants offline permission.
    /// </summary>
    public static Task<Result<NativeSourceObservation>> ReadOnlineAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, CancellationToken ct = default)
        => ReadOnlineAsync(workspaceRoot, expectedSource, null, ct);

    internal static Task<Result<NativeSourceObservation>> ReadOnlineAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, Action<IServiceCollection>? configure, CancellationToken ct)
        => expectedSource is null
            ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured native authority is required for online reads."))
            : ReadCoreAsync(workspaceRoot, expectedSource, false, configure, null, ct, validateReadAccess: true);

    /// <summary>Changes only this admitted worktree's Bench selection, preserving active-item history.</summary>
    public static Task<Result<NativeSourceObservation>> SelectBenchAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, Guid? expectedBenchId, Guid? benchId, CancellationToken ct = default)
        => SelectBenchAsync(workspaceRoot, expectedSource, expectedBenchId, benchId, null, ct);

    internal static Task<Result<NativeSourceObservation>> SelectBenchAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, Guid? expectedBenchId, Guid? benchId, Action<IServiceCollection>? configure, CancellationToken ct)
        => expectedSource is null ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured native authority is required."))
            : ReadCoreAsync(workspaceRoot, expectedSource, false, configure, new(expectedBenchId, benchId), ct);

    private sealed record Selection(Guid? Expected, Guid? Next);

    /// <summary>Discovers the bounded actual project team roster; this does not select or configure a team.</summary>
    public static Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, CancellationToken ct = default)
        => DiscoverPlanningTeamsAsync(workspaceRoot, expectedSource, null, ct);

    internal static async Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        if (expectedSource is null)
            return Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.InvalidInput, "Captured native authority is required.");
        var result = await ReadCoreAsync(workspaceRoot, expectedSource, false, configure, null, ct, discoverTeams: true).ConfigureAwait(false);
        return result.IsSuccess ? Result<ImmutableArray<PlanningTeam>>.Ok(result.Value.PlanningTeams)
            : Result<ImmutableArray<PlanningTeam>>.Fail(result.Error);
    }

    /// <summary>Reads a retained exact-team calendar with fresh native access/team validation. Null selects project scope, not the configured team.</summary>
    public static Task<Result<NativeSourceObservation>> ReadPlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct = default)
        => ReadPlanningAsync(workspaceRoot, expectedSource, selectedTeam, null, ct);

    internal static Task<Result<NativeSourceObservation>> ReadPlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, Action<IServiceCollection>? configure, CancellationToken ct)
        => ExplicitPlanningAsync(workspaceRoot, expectedSource, selectedTeam, configure, ct, readPlanning: true);

#pragma warning disable RS0027 // Retain the configured-team optional token; the explicit-team overload requires every argument.
    public static Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, CancellationToken ct = default)
        => expectedSource is null ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured native authority is required."))
            : ReadCoreAsync(workspaceRoot, expectedSource, true, null, null, ct, refreshPlanning: true);
#pragma warning restore RS0027

    /// <summary>Reads an optional recorded catalogue for this exact canonical team or explicit project scope, with fresh native access checks.</summary>
    public static Task<Result<NativeSourceObservation>> ReadPlanningSourcesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct = default)
        => ReadPlanningSourcesAsync(workspaceRoot, expectedSource, selectedTeam, null, ct);

    internal static Task<Result<NativeSourceObservation>> ReadPlanningSourcesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, Action<IServiceCollection>? configure, CancellationToken ct)
        => ExplicitPlanningAsync(workspaceRoot, expectedSource, selectedTeam, configure, ct);

    /// <summary>Refreshes only the selected canonical team (or explicit project scope), preserving every other calendar and workspace selection.</summary>
    public static Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct)
        => RefreshPlanningAsync(workspaceRoot, expectedSource, selectedTeam, null, ct);

    internal static Task<Result<NativeSourceObservation>> RefreshPlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, Action<IServiceCollection>? configure, CancellationToken ct)
        => ExplicitPlanningAsync(workspaceRoot, expectedSource, selectedTeam, configure, ct, refreshPlanning: true);

#pragma warning disable RS0027 // The new overload has no optional arguments and cannot capture existing two/three-argument calls.
    public static Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, CancellationToken ct = default)
        => DiscoverPlanningSourcesAsync(workspaceRoot, expectedSource, (Action<IServiceCollection>?)null, ct);
#pragma warning restore RS0027

    public static Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, CancellationToken ct)
        => DiscoverPlanningSourcesAsync(workspaceRoot, expectedSource, selectedTeam, null, ct);

    internal static Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, Action<IServiceCollection>? configure, CancellationToken ct)
        => ExplicitPlanningAsync(workspaceRoot, expectedSource, selectedTeam, configure, ct, discoverSources: true);

    internal static Task<Result<NativeSourceObservation>> DiscoverPlanningSourcesAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, Action<IServiceCollection>? configure, CancellationToken ct)
        => expectedSource is null ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured native authority is required."))
            : ReadCoreAsync(workspaceRoot, expectedSource, false, configure, null, ct, discoverSources: true, validateReadAccess: true);

#pragma warning disable RS0027 // Preserve expectedTeam semantics; the explicit selectedTeam overload requires its token.
    public static Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, PlanningSourceKind kind, string id, string? expectedTeam, CancellationToken ct = default)
        => BrowsePlanningAsync(workspaceRoot, expectedSource, kind, id, expectedTeam, null, ct);
#pragma warning restore RS0027

    internal static Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, PlanningSourceKind kind, string id, string? expectedTeam,
        Action<IServiceCollection>? configure, CancellationToken ct)
        => expectedSource is null || !Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(id) || id.Length > 256
            ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured native authority and an exact planning source are required."))
            : ReadCoreAsync(workspaceRoot, expectedSource, false, configure, null, ct, browse: new(kind, id, expectedTeam), validateReadAccess: true);

    /// <summary>Browses an exact source using the selected team as both selection and required canonical response identity.</summary>
    public static Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, PlanningSourceKind kind, string id, CancellationToken ct)
        => BrowsePlanningAsync(workspaceRoot, expectedSource, selectedTeam, kind, id, (Action<IServiceCollection>?)null, ct);

    internal static Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, PlanningSourceKind kind, string id,
        Action<IServiceCollection>? configure, CancellationToken ct)
        => !Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(id) || id.Length > 256
            ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "An exact planning source is required."))
            : ExplicitPlanningAsync(workspaceRoot, expectedSource, selectedTeam, configure, ct, browse: new(kind, id, selectedTeam));

    /// <summary>Executes one exact native source using recorded field/type mapping, without acquiring process presentation or claiming fresh schema.</summary>
    public static Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, PlanningSourceKind kind, string id,
        ProcessSchema mappingSchema, CancellationToken ct)
        => BrowsePlanningAsync(workspaceRoot, expectedSource, selectedTeam, kind, id, mappingSchema, null, ct);

    internal static Task<Result<NativeSourceObservation>> BrowsePlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, PlanningSourceKind kind, string id,
        ProcessSchema mappingSchema, Action<IServiceCollection>? configure, CancellationToken ct)
        => mappingSchema is null || expectedSource is null || mappingSchema.Connection != expectedSource.Connection
            ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Recorded native mapping metadata must belong to the captured source connection."))
            : !Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(id) || id.Length > 256
                ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "An exact planning source is required."))
                : ExplicitPlanningAsync(workspaceRoot, expectedSource, selectedTeam, configure, ct, browse: new(kind, id, selectedTeam, mappingSchema));

    private static Task<Result<NativeSourceObservation>> ExplicitPlanningAsync(string workspaceRoot,
        NativeSourceStamp expectedSource, string? selectedTeam, Action<IServiceCollection>? configure, CancellationToken ct,
        bool readPlanning = false, bool refreshPlanning = false, bool discoverSources = false, BrowseRequest? browse = null)
        => expectedSource is null || selectedTeam is not null
            && (!Guid.TryParseExact(selectedTeam, "D", out var id) || id == Guid.Empty || id.ToString("D") != selectedTeam)
            ? Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "Captured native authority and a canonical team ID (or explicit project scope) are required."))
            : ReadCoreAsync(workspaceRoot, expectedSource, refreshPlanning, configure, null, ct, refreshPlanning, discoverSources, browse,
                validateReadAccess: true, explicitTeam: new(selectedTeam), readPlanning: readPlanning);

    private sealed record TeamSelection(string? Team);
    private sealed record BrowseRequest(PlanningSourceKind Kind, string Id, string? Team, ProcessSchema? MappingSchema = null);

    private static async Task<Result<NativeSourceObservation>> ReadCoreAsync(string workspaceRoot,
        NativeSourceStamp? expectedSource, bool observeSchema, Action<IServiceCollection>? configure, Selection? selection, CancellationToken ct,
        bool refreshPlanning = false, bool discoverSources = false, BrowseRequest? browse = null, bool validateReadAccess = false,
        TeamSelection? explicitTeam = null, bool readPlanning = false, bool discoverTeams = false,
        ImmutableArray<string> profileIds = default)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot)
                || !NativePathComparer.Instance.Equals(workspaceRoot, Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot))))
                return Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "An explicit canonical workspace root is required.");
            var twig = Path.Combine(workspaceRoot, ".twig");
            if (!Directory.Exists(workspaceRoot) || !File.Exists(Path.Combine(twig, CoreWorkspaceFactory.FileName)))
                return Result<NativeSourceObservation>.Fail(ErrorCode.StorageUnavailable,
                    "An existing explicitly initialized Core workspace is required; nothing was initialized.");

            // Acquire here, before any await, so nested native operations inherit reentrancy.
            using var operation = ConnectionOperationGate.Acquire(workspaceRoot, exclusive: true);
            using var runtime = await ComposeAsync().ConfigureAwait(false);
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            var opened = await factory.OpenAsync(token).ConfigureAwait(false);
            if (!opened.IsSuccess) return Refuse(opened.Error, "Native workspace admission failed.");
            using var handle = opened.Value;
            var admitted = await factory.AdmitSourceAsync(handle, token).ConfigureAwait(false);
            if (!admitted.IsSuccess) return Refuse(admitted.Error, "Native authority admission failed.");
            if (expectedSource is not null && expectedSource != handle.Source)
                return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict,
                    "The recorded source authority or store changed; explicit source reauthorization is required.");
            if (validateReadAccess)
            {
                var access = await handle.Workspace.ValidateReadAccessAsync(token).ConfigureAwait(false);
                if (!access.IsSuccess) return Refuse(access.Error, "Native online read access could not be validated; no content was released.");
                if (!access.Value)
                    return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "Native online read access was not established; no content was released.");
            }
            ImmutableArray<IdentityProfile> profiles = [];
            if (!profileIds.IsDefaultOrEmpty)
            {
                var result = await handle.Workspace.ReadIdentityProfilesAsync(profileIds, token).ConfigureAwait(false);
                if (!result.IsSuccess) return Refuse(result.Error, "Native identity profiles could not be read.");
                profiles = result.Value;
            }
            var snapshot = await handle.Workspace.BenchSnapshotAsync(token).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Refuse(snapshot.Error, "Native snapshot acquisition failed.");
            ImmutableArray<PlanningTeam> teams = [];
            if (discoverTeams || readPlanning && explicitTeam?.Team is not null)
            {
                var roster = await handle.Workspace.DiscoverPlanningTeamsAsync(token).ConfigureAwait(false);
                if (!roster.IsSuccess) return Refuse(roster.Error, "Native project team discovery failed.");
                teams = roster.Value;
                if (teams.IsDefault || teams.Length > 4096 || teams.Select(team => team.Id).Distinct().Count() != teams.Length
                    || teams.Any(team => !Guid.TryParseExact(team.Id, "D", out var id) || id == Guid.Empty
                        || id.ToString("D") != team.Id || string.IsNullOrWhiteSpace(team.Name) || team.Name.Length > 256 || team.Name.Any(char.IsControl)))
                    return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "The native project team roster has incomplete or invalid identity.");
                if (readPlanning && !teams.Any(team => team.Id == explicitTeam!.Team))
                    return Result<NativeSourceObservation>.Fail(ErrorCode.NotFound, "The explicitly selected team is not available in this project.");
            }
            PlanningSources? sourceChoices = null;
            NativePlanningSourcesCache? discoveredCatalogue = null;
            PlanningBrowse? browsed = null;
            PlanningBrowsePage? browsePage = null;
            if (discoverSources)
            {
                var discovered = explicitTeam is null
                    ? await handle.Workspace.DiscoverPlanningSourcesAsync(token).ConfigureAwait(false)
                    : await handle.Workspace.DiscoverPlanningSourcesAsync(explicitTeam.Team, token).ConfigureAwait(false);
                if (!discovered.IsSuccess) return Refuse(discovered.Error, "Native saved-query/backlog discovery failed.");
                if (explicitTeam is not null && discovered.Value.Team != explicitTeam.Team)
                    return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "The discovered sources belong to another native team.");
                sourceChoices = discovered.Value;
                discoveredCatalogue = new(sourceChoices, DateTimeOffset.UtcNow);
            }
            if (browse is not null)
            {
                var acquired = explicitTeam is null
                    ? await handle.Workspace.ObservePlanningBrowseAsync(browse.Kind, browse.Id, browse.Team, token).ConfigureAwait(false)
                    : await handle.Workspace.ObservePlanningBrowseAsync(explicitTeam.Team, browse.Kind, browse.Id, browse.Team, token, browse.MappingSchema).ConfigureAwait(false);
                if (!acquired.IsSuccess) return Refuse(acquired.Error, "Native saved-query/backlog acquisition failed.");
                browsePage = acquired.Value;
            }
            var context = await handle.Workspace.Context.ReadAsync(token).ConfigureAwait(false);
            if (!context.IsSuccess) return Refuse(context.Error, "Native context acquisition failed.");
            if (selection is not null)
            {
                if (context.Value.BenchId != selection.Expected)
                    return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "The worktree Bench selection changed; the newer selection was preserved.");
                if (selection.Next is { } next && !snapshot.Value.Benches.Any(bench => bench.Id == next))
                    return Result<NativeSourceObservation>.Fail(ErrorCode.NotFound, "The selected Bench does not exist.");
                var saved = await handle.Workspace.Context.WriteAsync(context.Value,
                    context.Value with { BenchId = selection.Next }, token).ConfigureAwait(false);
                if (!saved.IsSuccess) return Refuse(saved.Error, "Native Bench selection was not saved.");
            }
            var viewRules = await handle.Workspace.ViewRulesAsync(token).ConfigureAwait(false);
            if (!viewRules.IsSuccess) return Refuse(viewRules.Error, "Native workspace-view rules could not be read.");
            Result<PlanningCacheEntry> planning;
            if (refreshPlanning)
            {
                var acquired = explicitTeam is null
                    ? await handle.Workspace.DiscoverPlanningAsync(token).ConfigureAwait(false)
                    : await handle.Workspace.DiscoverPlanningAsync(explicitTeam.Team, token).ConfigureAwait(false);
                planning = acquired.IsSuccess ? Result<PlanningCacheEntry>.Ok(new(acquired.Value)) : Result<PlanningCacheEntry>.Fail(acquired.Error);
            }
            else planning = explicitTeam is null
                ? await handle.Workspace.PlanningAsync(token).ConfigureAwait(false)
                : await handle.Workspace.PlanningAsync(explicitTeam.Team, token).ConfigureAwait(false);
            if (!planning.IsSuccess) return Refuse(planning.Error, "Native planning metadata could not be read.");
            if (explicitTeam is not null && planning.Value.Metadata is { } metadata && metadata.Team != explicitTeam.Team)
                return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "The retained or acquired calendar belongs to another native team.");
            if (readPlanning && planning.Value.Metadata is null)
                return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "This explicit planning scope has no retained calendar; refresh it explicitly.");
            var calendars = await handle.Workspace.PlanningCalendarsAsync(token).ConfigureAwait(false);
            if (!calendars.IsSuccess) return Refuse(calendars.Error, "Native team calendars could not be read.");
            var catalogue = explicitTeam is null
                ? await handle.Workspace.PlanningSourcesAsync(token).ConfigureAwait(false)
                : await handle.Workspace.PlanningSourcesAsync(explicitTeam.Team, token).ConfigureAwait(false);
            if (!catalogue.IsSuccess) return Refuse(catalogue.Error, "The retained native source catalogue could not be read.");
            var memberships = await handle.Workspace.MembershipsAsync(token).ConfigureAwait(false);
            if (!memberships.IsSuccess) return Refuse(memberships.Error, "Native query/backlog membership metadata could not be read.");
            var readAt = DateTimeOffset.UtcNow;
            Result<ProcessSchema>? schema = null;
            DateTimeOffset? schemaAt = null;
            if (observeSchema)
            {
                schema = await handle.Workspace.SchemaAsync(token, includePresentation: true).ConfigureAwait(false);
                if (!schema.IsSuccess) return Refuse(schema.Error, "Native schema observation failed; no offline fallback was selected.");
                schema = Result<ProcessSchema>.Ok(schema.Value with { Planning = planning.Value.Metadata,
                    PlanningCalendars = calendars.Value, Memberships = memberships.Value });
                schemaAt = DateTimeOffset.UtcNow;
            }

            using var finalRuntime = await ComposeAsync().ConfigureAwait(false);
            var finalFactory = finalRuntime.GetRequiredService<CoreWorkspaceFactory>();
            var reopened = await finalFactory.OpenAsync(token).ConfigureAwait(false);
            if (!reopened.IsSuccess) return Refuse(reopened.Error, "Native workspace revalidation failed.");
            using var final = reopened.Value;
            var revalidated = await finalFactory.AdmitSourceAsync(final, token).ConfigureAwait(false);
            if (!revalidated.IsSuccess) return Refuse(revalidated.Error, "Native authority revalidation failed.");
            if (handle.Source != final.Source)
                return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict,
                    "Native authority, workspace location or store changed during capture; no content was released.");
            if (discoveredCatalogue is not null)
            {
                catalogue = await handle.Workspace.SavePlanningSourcesAsync(discoveredCatalogue, token).ConfigureAwait(false);
                if (!catalogue.IsSuccess) return Refuse(catalogue.Error, "The native source catalogue was not saved; previous complete metadata was preserved.");
            }
            if (refreshPlanning)
            {
                var saved = explicitTeam is null
                    ? await handle.Workspace.SavePlanningAsync(planning.Value.Metadata!, token).ConfigureAwait(false)
                    : await handle.Workspace.SavePlanningAsync(explicitTeam.Team, planning.Value.Metadata!, token).ConfigureAwait(false);
                if (!saved.IsSuccess) return Refuse(saved.Error, "Native planning metadata was not saved.");
            }
            if (browsePage is not null)
            {
                var saved = await handle.Workspace.SavePlanningBrowseAsync(browsePage, token).ConfigureAwait(false);
                if (!saved.IsSuccess) return Refuse(saved.Error, "Native query/backlog membership was not saved.");
                browsed = saved.Value;
                snapshot = await handle.Workspace.BenchSnapshotAsync(token).ConfigureAwait(false);
                if (!snapshot.IsSuccess) return Refuse(snapshot.Error, "Native snapshot acquisition failed after planning refresh.");
            }
            if (refreshPlanning || browsePage is not null)
            {
                calendars = await handle.Workspace.PlanningCalendarsAsync(token).ConfigureAwait(false);
                memberships = await handle.Workspace.MembershipsAsync(token).ConfigureAwait(false);
                if (!calendars.IsSuccess) return Refuse(calendars.Error, "Native team calendars could not be read after refresh.");
                if (!memberships.IsSuccess) return Refuse(memberships.Error, "Native memberships could not be read after refresh.");
                if (schema is not null)
                    schema = Result<ProcessSchema>.Ok(schema.Value with { PlanningCalendars = calendars.Value, Memberships = memberships.Value });
            }
            token.ThrowIfCancellationRequested();
            return Result<NativeSourceObservation>.Ok(new(handle.Source, snapshot.Value, readAt, schema, schemaAt)
            { WorkspaceExclusions = viewRules.Value.Exclusions, SelectedBenchId = selection is null ? context.Value.BenchId : selection.Next,
                Planning = planning.Value.Metadata, PlanningSources = sourceChoices ?? catalogue.Value.Sources,
                PlanningSourcesCache = catalogue.Value, PlanningBrowse = browsed,
                PlanningCalendars = calendars.Value, PlanningTeams = teams, Memberships = memberships.Value,
                IdentityProfiles = profiles });

            async Task<ServiceProvider> ComposeAsync()
            {
                var paths = TwigPaths.BuildPaths(twig, new TwigConfiguration(), workspaceRoot);
                var configuration = await TwigConfiguration.LoadSplitAsync(paths, token).ConfigureAwait(false);
                var services = new ServiceCollection();
                configure?.Invoke(services);
                services.AddCoreWorkspaceServices(configuration, paths);
                return services.BuildServiceProvider();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "The native source read exceeded its request deadline.");
        }
        catch (ConnectionBindingResolutionException)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "Native binding admission is unavailable or another operation is active.");
        }
        catch (SqliteException)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.StorageUnavailable,
                "Native admission storage is unavailable; no content was released.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or TwigConfigurationException)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable,
                "The native source could not be admitted or revalidated; no content was released.");
        }
    }

    internal static NativeSourceStamp Stamp(CoreWorkspaceDocument document, string root, params string?[] authority)
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue(Path.GetFullPath(root));
        writer.WriteStringValue(Path.GetFullPath(document.DatabasePath));
        foreach (var value in authority) writer.WriteStringValue(value);
        writer.WriteEndArray();
        writer.Flush();
        return new(document.Connection, document.DatabaseInstance, document.PrincipalId,
            Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
    }

    internal static NativeSourceStamp Stamp(CoreWorkspaceDocument document, string root, ResolvedConnectionBinding origin)
        => Stamp(document, root, origin.Binding.BindingId, origin.Binding.ConnectionRef, Number(origin.Binding.Revision),
            origin.Identity.IdentityId, origin.Identity.TenantId, origin.Identity.ObjectId, origin.Identity.Issuer,
            origin.Identity.AuthorityHost, origin.Identity.CredentialRef, origin.Identity.Method,
            origin.Identity.AdoPrincipalId, origin.Identity.AdoAuthority, origin.WorktreeRoot,
            origin.SelectionSource, Number(origin.SelectionRevision), origin.StorageGeneration,
            origin.Operation.Organization, origin.Operation.Project, origin.Operation.Team,
            origin.Operation.WorktreeFingerprint, Number(origin.Operation.AttachmentRevision));

    internal static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Result<NativeSourceObservation> Refuse(Error error, string message)
        => Result<NativeSourceObservation>.Fail(new Error(error.Code, message)
        {
            Evidence = error.Evidence.Where(value => value is "http:401" or "http:403").ToImmutableArray(),
        });
}
