using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Policy;
using Twig.Providers.GitHub;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

internal sealed record CoreDraftPreview(RetainedItem Draft, Plan? Plan);
internal sealed record CoreBenchPull(Bench Bench, int ItemCount, int MemberCount, int ContextCount, int ProtectedCount);
internal sealed record CoreExecution(ImmutableArray<Receipt> Receipts, Error? Error = null,
    bool AlreadyVerified = false, bool DraftRetained = false)
{
    internal bool EffectsVerified => AlreadyVerified || !Receipts.IsEmpty && Receipts.All(r => r.Status == OpStatus.Verified);
}

/// <summary>One draft/plan/journal application path; no retry, note flush or legacy publisher.</summary>
internal sealed partial class CoreWorkspace(Connection connection, IProvider provider, IStore store,
    ConfiguredPolicyProjectionSource policies, string nativeActor, string worktreeRoot, Guid storeInstance = default,
    UserBenchStore? sharedBenches = null, string planningTeam = "", string? executionRegistryPath = null)
{
    internal Connection Connection => connection;
    internal ProviderCapabilities Capabilities => provider.Capabilities;
    internal ConcurrencyMode Concurrency => provider.Capabilities.ExactRevisions
        ? ConcurrencyMode.ExactRevision : ConcurrencyMode.ObservedNonAtomic;
    internal string NativeActor => nativeActor;
    internal string WorktreeRoot => worktreeRoot;
    internal Task<Result<WorkspaceViewRules>> ViewRulesAsync(CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.ReadViewRulesAsync(worktreeRoot, connection, ct)
            : Task.FromResult(Result<WorkspaceViewRules>.Ok(new(0, [])));
    internal Task<Result<WorkspaceViewRules>> SaveViewRulesAsync(long expectedRevision, IEnumerable<Selector> exclusions, CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.SaveViewRulesAsync(worktreeRoot, connection, expectedRevision, exclusions, ct)
            : Task.FromResult(Result<WorkspaceViewRules>.Fail(ErrorCode.Unavailable, "The native user-wide view rule store is not composed."));
    internal Task<Result<StoreSnapshot>> SnapshotAsync(CancellationToken ct) => store.ReadAsync(connection, ct);
    internal Task<Result<StoreSnapshot>> BenchSnapshotAsync(CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.View(store, worktreeRoot, storeInstance).ReadAsync(connection, ct)
            : SnapshotAsync(ct);
    internal Task<Result<bool>> ValidateReadAccessAsync(CancellationToken ct)
        => provider.ValidateReadAccessAsync(connection, ct);
    internal Task<Result<QueryPage>> ObserveItemsAsync(ImmutableArray<ItemKey> keys, CancellationToken ct)
        => ObserveContentAsync(() => provider.ReadAsync(connection, keys, ct, captureOnly: true));

    internal Func<Error, Error>? RecordHistoricalReadFailure { private get; set; }

    internal async Task<Result<T>> ObserveContentAsync<T>(Func<Task<Result<T>>> acquire)
    {
        using var lease = RecordHistoricalReadFailure is null ? null
            : ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var result = await acquire().ConfigureAwait(false);
        return result.IsSuccess ? result : Result<T>.Fail(NativeHistoricalEvidence.ReportReadFailure(
            connection, result.Error, RecordHistoricalReadFailure));
    }
    internal Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(ImmutableArray<string> identities, CancellationToken ct)
        => provider.ReadIdentityProfilesAsync(connection, identities, ct);

    internal Task<Result<ProcessSchema>> SchemaAsync(CancellationToken ct, bool includePresentation = false)
        => includePresentation ? provider.DiscoverSchemaAsync(connection, ct, true) : provider.DiscoverSchemaAsync(connection, ct);
    internal async Task<Result<PlanningCacheEntry>> PlanningAsync(CancellationToken ct)
    {
        var configured = sharedBenches is not null ? await sharedBenches.ReadPlanningAsync(connection, planningTeam, ct).ConfigureAwait(false)
            : Result<PlanningCacheEntry>.Ok(new(null));
        return configured.IsSuccess && configured.Value.Metadata?.Team is { } team
            ? await PlanningAsync(team, ct).ConfigureAwait(false) : configured;
    }
    internal async Task<Result<PlanningCacheEntry>> PlanningAsync(string? selectedTeam, CancellationToken ct)
    {
        if (selectedTeam is null)
            return sharedBenches is not null ? await sharedBenches.ReadPlanningAsync(connection, "", ct).ConfigureAwait(false)
                : Result<PlanningCacheEntry>.Ok(new(null));
        var calendars = await PlanningCalendarsAsync(ct).ConfigureAwait(false);
        return calendars.IsSuccess ? Result<PlanningCacheEntry>.Ok(new(calendars.Value.SingleOrDefault(value => value.Team == selectedTeam)))
            : Result<PlanningCacheEntry>.Fail(calendars.Error);
    }
    internal Task<Result<ImmutableArray<PlanningMetadata>>> PlanningCalendarsAsync(CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.ReadPlanningCalendarsAsync(connection, ct)
            : Task.FromResult(Result<ImmutableArray<PlanningMetadata>>.Ok([]));
    internal Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(CancellationToken ct)
        => provider.DiscoverPlanningTeamsAsync(connection, ct);
    internal Task<Result<PlanningMetadata>> DiscoverPlanningAsync(CancellationToken ct)
        => provider.DiscoverPlanningAsync(connection, planningTeam, ct);
    internal Task<Result<PlanningMetadata>> DiscoverPlanningAsync(string? selectedTeam, CancellationToken ct)
        => provider.DiscoverPlanningAsync(connection, selectedTeam, ct);
    internal Task<Result<PlanningMetadata>> SavePlanningAsync(PlanningMetadata metadata, CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.SavePlanningAsync(connection, planningTeam, metadata, ct)
            : Task.FromResult(Result<PlanningMetadata>.Fail(ErrorCode.Unavailable, "The native planning store is unavailable."));
    internal Task<Result<PlanningMetadata>> SavePlanningAsync(string? selectedTeam, PlanningMetadata metadata, CancellationToken ct)
        => metadata.Team != selectedTeam
            ? Task.FromResult(Result<PlanningMetadata>.Fail(ErrorCode.Conflict, "The planning calendar belongs to another native team."))
            : sharedBenches is not null ? sharedBenches.SavePlanningAsync(connection, selectedTeam ?? "", metadata, ct)
                : Task.FromResult(Result<PlanningMetadata>.Fail(ErrorCode.Unavailable, "The native planning store is unavailable."));
    internal Task<Result<ImmutableArray<PlanningMembership>>> MembershipsAsync(CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.ReadMembershipsAsync(connection, ct)
            : Task.FromResult(Result<ImmutableArray<PlanningMembership>>.Ok([]));

    internal async Task<Result<ItemKey>> ResolveTargetAsync(int locator, bool allowNativeLookup, CancellationToken ct)
    {
        if (locator == 0) return Result<ItemKey>.Fail(ErrorCode.InvalidInput, "A nonzero target is required.");
        var stored = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<ItemKey>.Fail(stored.Error);
        if (locator < 0)
        {
            var matches = CoreHostContext.KnownLocalKeys(stored.Value).Where(key => CoreHostContext.LocalAlias(key) == locator).ToArray();
            return matches.Length == 1 ? Result<ItemKey>.Ok(Resolve(stored.Value, matches[0]))
                : Result<ItemKey>.Fail(ErrorCode.Conflict, "The local UUID alias is absent or ambiguous.");
        }
        if (connection.Provider.Value is "ado" or "twig")
            return Result<ItemKey>.Ok(ItemKey.Remote(connection.Provider, connection.ResourceScope,
                locator.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (provider is not GitHubProvider github)
            return Result<ItemKey>.Fail(ErrorCode.Unsupported, "This provider has no qualified numeric locator.");
        var candidates = stored.Value.Items.Where(item => item.Fields.TryGetValue(GitHubProvider.Number, out var number)
            && number.Kind == FieldKind.Integer && number.Number == locator).ToArray();
        if (candidates.Length > 1 || candidates.Any(item => !item.Key.BelongsTo(connection)))
            return Result<ItemKey>.Fail(ErrorCode.Conflict, "Cached GitHub number identity is ambiguous or mismatched.");
        if (!allowNativeLookup)
            return candidates.Length == 1 ? Result<ItemKey>.Ok(candidates[0].Key)
                : Result<ItemKey>.Fail(ErrorCode.NotFound, $"Issue #{locator} is not cached; use show {locator} --refresh.");
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var acquired = await ObserveContentAsync(() => github.ReadNumberAsync(connection, locator, ct)).ConfigureAwait(false);
        return acquired.IsSuccess ? Result<ItemKey>.Ok(acquired.Value.Items.Single().Key) : Result<ItemKey>.Fail(acquired.Error);
    }

    internal static Result<int> DisplayId(Connection connection, StoreSnapshot snapshot, ItemKey key)
    {
        if (key.IsLocal) return Result<int>.Ok(CoreHostContext.LocalAlias(key));
        if (!key.BelongsTo(connection))
            return Result<int>.Fail(ErrorCode.Conflict, "Native identity belongs to another connection.");
        if (connection.Provider.Value is "ado" or "twig")
            return int.TryParse(key.NativeId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
                ? Result<int>.Ok(id) : Result<int>.Fail(ErrorCode.InvalidInput, "Invalid native ADO ID.");
        var items = snapshot.Items.Where(item => item.Key == key).ToArray();
        if (items.Length == 1 && items[0].Fields.TryGetValue(GitHubProvider.Number, out var number)
            && number.Kind == FieldKind.Integer && number.Number > 0 && number.Number <= int.MaxValue
            && decimal.Truncate(number.Number) == number.Number
            && snapshot.Items.Count(item => item.Fields.TryGetValue(GitHubProvider.Number, out var other)
                && other.Kind == FieldKind.Integer && other.Number == number.Number) == 1)
            return Result<int>.Ok((int)number.Number);
        return Result<int>.Fail(ErrorCode.Conflict, "A unique observed GitHub number is unavailable; native identity was not guessed.");
    }

    internal async Task<Result<bool>> CaptureBenchesAsync(StoreSnapshot capture, IEnumerable<Bench> benches, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        if (current.Value.Version != capture.Version)
            return Result<bool>.Fail(ErrorCode.Conflict, "Bench contents changed during this operation; no newer selector was overwritten.");
        if (sharedBenches is not null)
            return await sharedBenches.CaptureAsync(connection, capture.Benches, benches, ct).ConfigureAwait(false);
        var committed = await store.CommitAsync(connection, capture.Version, Copy(capture, capture.Version + 1, benches: benches), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(committed.Error);
    }

    internal async Task<Result<QueryPage>> QueryAsync(Query query, CancellationToken ct, int maximum = int.MaxValue)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        if (Model.IsEmptyQuery(query)) return Result<QueryPage>.Ok(new([], [], null, true));
        if (query.Kind is QueryKind.SavedQuery or QueryKind.Backlog)
        {
            var kind = query.Kind == QueryKind.SavedQuery ? PlanningSourceKind.SavedQuery : PlanningSourceKind.Backlog;
            var browsed = await BrowsePlanningAsync(query.PlanningTeam, kind, query.Value!.Text!, query.PlanningTeam, ct).ConfigureAwait(false);
            if (!browsed.IsSuccess) return Result<QueryPage>.Fail(browsed.Error);
            var captured = await SnapshotAsync(ct).ConfigureAwait(false);
            if (!captured.IsSuccess) return Result<QueryPage>.Fail(captured.Error);
            var keys = browsed.Value.Items.ToHashSet();
            return Result<QueryPage>.Ok(new(captured.Value.Items.Where(item => keys.Contains(item.Key)).ToImmutableArray(),
                captured.Value.Relationships.Where(edge => keys.Contains(edge.From) || keys.Contains(edge.To)).ToImmutableArray(), null, true));
        }
        if (Model.PlanningReferences(query).Any())
            return Result<QueryPage>.Fail(ErrorCode.Unsupported, "Remote saved-query/backlog refresh requires a standalone reference rule; compound cached intent was preserved without a broader acquisition.");
        if (NeedsPlanning(query))
        {
            var planning = await PlanningCalendarsAsync(ct).ConfigureAwait(false);
            if (!planning.IsSuccess) return Result<QueryPage>.Fail(planning.Error);
            var resolved = Model.ResolvePlanningQuery(query, null, DateTimeOffset.UtcNow, planning.Value);
            if (!resolved.IsSuccess) return Result<QueryPage>.Fail(resolved.Error);
            query = resolved.Value;
            if (Model.IsEmptyQuery(query)) return Result<QueryPage>.Ok(new([], [], null, true));
        }
        static bool NeedsPlanning(Query value) => value.Kind == QueryKind.Iteration || value.Children.Any(NeedsPlanning);
        var items = new List<WorkItem>();
        var relationships = new HashSet<Relationship>();
        var identities = new HashSet<ItemKey>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        while (true)
        {
            var page = await ObserveContentAsync(() => provider.QueryAsync(connection, query, continuation, ct)).ConfigureAwait(false);
            if (!page.IsSuccess) return Result<QueryPage>.Fail(page.Error);
            if (page.Value.Complete == (page.Value.Continuation is not null)
                || page.Value.Items.Any(item => !item.RelationshipsComplete
                    || provider.Capabilities.Operations.Contains(OpKind.LinkResource) && !item.ResourcesComplete)
                || page.Value.Items.Any(item => !identities.Add(item.Key))
                || page.Value.Continuation is { } cursor && !cursors.Add(cursor))
                return Result<QueryPage>.Fail(ErrorCode.Unavailable, "Query exhaustion, incident completeness or keyset progress was not established; this page was not mirrored.");
            var current = await SnapshotAsync(ct).ConfigureAwait(false);
            if (!current.IsSuccess) return Result<QueryPage>.Fail(current.Error);
            var received = page.Value.Items.Select(item => item.Key).ToHashSet();
            var next = Copy(current.Value, current.Value.Version + 1,
                items: current.Value.Items.Where(item => !received.Contains(item.Key)).Concat(page.Value.Items),
                relationships: current.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Local
                    || !received.Contains(edge.From) && !received.Contains(edge.To)).Concat(page.Value.Relationships).Distinct());
            var committed = await store.CommitAsync(connection, current.Value.Version, next, ct).ConfigureAwait(false);
            if (!committed.IsSuccess) return Result<QueryPage>.Fail(committed.Error);
            items.AddRange(page.Value.Items);
            relationships.UnionWith(page.Value.Relationships);
            continuation = page.Value.Continuation;
            if (page.Value.Complete || items.Count >= maximum)
                return Result<QueryPage>.Ok(new(items.ToImmutableArray(), relationships.ToImmutableArray(), continuation, page.Value.Complete));
        }
    }

    internal Task<Result<PlanningSources>> DiscoverPlanningSourcesAsync(CancellationToken ct)
        => provider.DiscoverPlanningSourcesAsync(connection, planningTeam, ct);
    internal Task<Result<PlanningSources>> DiscoverPlanningSourcesAsync(string? selectedTeam, CancellationToken ct)
        => provider.DiscoverPlanningSourcesAsync(connection, selectedTeam, ct);
    internal Task<Result<NativePlanningSourcesCache>> PlanningSourcesAsync(string? selectedTeam, CancellationToken ct)
        => sharedBenches is not null ? sharedBenches.ReadPlanningSourcesAsync(connection, selectedTeam, ct)
            : Task.FromResult(Result<NativePlanningSourcesCache>.Ok(new(null, null)));
    internal async Task<Result<NativePlanningSourcesCache>> PlanningSourcesAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(planningTeam)) return await PlanningSourcesAsync(null, ct).ConfigureAwait(false);
        if (Guid.TryParseExact(planningTeam, "D", out var id) && id.ToString("D") == planningTeam)
            return await PlanningSourcesAsync(planningTeam, ct).ConfigureAwait(false);
        var calendar = await PlanningAsync(ct).ConfigureAwait(false);
        if (!calendar.IsSuccess) return Result<NativePlanningSourcesCache>.Fail(calendar.Error);
        return calendar.Value.Metadata?.Team is { } team ? await PlanningSourcesAsync(team, ct).ConfigureAwait(false)
            : Result<NativePlanningSourcesCache>.Ok(new(null, null));
    }
    internal Task<Result<NativePlanningSourcesCache>> SavePlanningSourcesAsync(NativePlanningSourcesCache cache, CancellationToken ct)
        => sharedBenches is not null && cache.Sources is { } sources
            ? sharedBenches.SavePlanningSourcesAsync(connection, sources.Team, cache, ct)
            : Task.FromResult(Result<NativePlanningSourcesCache>.Fail(ErrorCode.Unavailable, "The native source catalogue store is unavailable."));

    internal Task<Result<PlanningBrowse>> BrowsePlanningAsync(PlanningSourceKind kind, string id, string? expectedTeam, CancellationToken ct)
        => BrowsePlanningAsync(planningTeam, kind, id, expectedTeam, ct);

    private async Task<Result<PlanningBrowse>> BrowsePlanningAsync(string? selectedTeam, PlanningSourceKind kind, string id,
        string? expectedTeam, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var observed = await ObservePlanningBrowseAsync(selectedTeam, kind, id, expectedTeam, ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<PlanningBrowse>.Fail(observed.Error);
        return await SavePlanningBrowseAsync(observed.Value, ct).ConfigureAwait(false);
    }

    internal Task<Result<PlanningBrowsePage>> ObservePlanningBrowseAsync(PlanningSourceKind kind, string id,
        string? expectedTeam, CancellationToken ct)
        => ObservePlanningBrowseAsync(planningTeam, kind, id, expectedTeam, ct);

    internal async Task<Result<PlanningBrowsePage>> ObservePlanningBrowseAsync(string? selectedTeam, PlanningSourceKind kind, string id,
        string? expectedTeam, CancellationToken ct, ProcessSchema? mappingSchema = null)
    {
        var observed = await ObserveContentAsync(() => mappingSchema is null
            ? provider.BrowsePlanningAsync(connection, selectedTeam, kind, id, expectedTeam, ct)
            : provider.BrowsePlanningAsync(connection, selectedTeam, kind, id, expectedTeam, mappingSchema, ct)).ConfigureAwait(false);
        if (!observed.IsSuccess) return observed;
        if (observed.Value.Source.Kind != kind || observed.Value.Source.Id != id || observed.Value.Source.IsFolder
            || observed.Value.ObservedAt == default || observed.Value.Team != expectedTeam
            || (kind == PlanningSourceKind.Backlog || observed.Value.Source.RequiresTeam) && observed.Value.Team is null)
            return Result<PlanningBrowsePage>.Fail(ErrorCode.Conflict, "The native planning response belongs to a different source or has incomplete provenance.");
        var page = observed.Value.Page;
        if (!page.Complete || page.Continuation is not null || page.Items.Length > 10000
            || page.Items.Select(item => item.Key).Distinct().Count() != page.Items.Length
            || page.Items.Any(item => !item.Key.BelongsTo(connection) || !item.RelationshipsComplete
                || provider.Capabilities.Operations.Contains(OpKind.LinkResource) && !item.ResourcesComplete))
            return Result<PlanningBrowsePage>.Fail(ErrorCode.Unavailable, "Native planning membership, identity or incident completeness was not established; no partial result was mirrored.");
        return observed;
    }

    internal async Task<Result<PlanningBrowse>> SavePlanningBrowseAsync(PlanningBrowsePage observed, CancellationToken ct)
    {
        if (sharedBenches is null)
            return Result<PlanningBrowse>.Fail(ErrorCode.Unavailable, "The native membership cache is not composed; no membership was saved.");
        var page = observed.Page;
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<PlanningBrowse>.Fail(current.Error);
        var keys = page.Items.Select(item => item.Key).ToHashSet();
        var next = Copy(current.Value, current.Value.Version + 1,
            items: current.Value.Items.Where(item => !keys.Contains(item.Key)).Concat(page.Items),
            relationships: current.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Local
                || !keys.Contains(edge.From) && !keys.Contains(edge.To)).Concat(page.Relationships).Distinct());
        var committed = await store.CommitAsync(connection, current.Value.Version, next, ct).ConfigureAwait(false);
        if (!committed.IsSuccess) return Result<PlanningBrowse>.Fail(committed.Error);
        var saved = await sharedBenches.SaveMembershipAsync(connection, observed.Team ?? "",
            new(observed.Source.Kind, observed.Source.Id, observed.Team, keys.ToImmutableArray(), observed.ObservedAt, true), ct).ConfigureAwait(false);
        return saved.IsSuccess ? Result<PlanningBrowse>.Ok(new(observed.Source, saved.Value.Items,
            observed.ObservedAt, observed.Team)) : Result<PlanningBrowse>.Fail(saved.Error);
    }

    internal async Task<Result<CoreBenchPull>> PullBenchAsync(Bench expected, int depthUp, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var initial = await BenchSnapshotAsync(ct).ConfigureAwait(false);
        if (!initial.IsSuccess) return Result<CoreBenchPull>.Fail(initial.Error);
        var bench = initial.Value.Benches.SingleOrDefault(value => value.Id == expected.Id);
        if (bench is null)
            return Result<CoreBenchPull>.Fail(ErrorCode.NotFound, "The specified Bench does not exist; no scope was pulled.");
        if (!SameBench(bench, expected))
            return Result<CoreBenchPull>.Fail(ErrorCode.Conflict, "The specified Bench changed before the pull; no scope was pulled.");
        var schema = await SchemaAsync(ct).ConfigureAwait(false);
        if (!schema.IsSuccess) return Result<CoreBenchPull>.Fail(schema.Error);
        var planning = await PlanningCalendarsAsync(ct).ConfigureAwait(false);
        if (!planning.IsSuccess) return Result<CoreBenchPull>.Fail(planning.Error);
        var memberships = await MembershipsAsync(ct).ConfigureAwait(false);
        if (!memberships.IsSuccess) return Result<CoreBenchPull>.Fail(memberships.Error);
        schema = Result<ProcessSchema>.Ok(schema.Value with { PlanningCalendars = planning.Value, Memberships = memberships.Value });
        var fetched = new HashSet<ItemKey>();
        var members = new HashSet<ItemKey>();
        var targets = initial.Value.RetainedItems.Select(draft => Resolve(initial.Value, draft.Key)).ToHashSet();
        foreach (var selector in bench.Selectors)
        {
            if (selector.Connection is { } owner && owner != connection) continue;
            if (selector.Kind != SelectorKind.Query)
            {
                targets.Add(Resolve(initial.Value, selector.Item!));
                members.Add(Resolve(initial.Value, selector.Item!));
                continue;
            }
            foreach (var item in Model.PlanningReferences(selector.Query!).Any() ? [] : initial.Value.Items)
            {
                var match = Model.Matches(selector.Query!, item, schema.Value.Planning, DateTimeOffset.UtcNow, schema.Value.Memberships, planning.Value);
                if (!match.IsSuccess) return Result<CoreBenchPull>.Fail(match.Error);
                if (match.Value) targets.Add(item.Key);
            }
            var queried = await QueryAsync(selector.Query!, ct).ConfigureAwait(false);
            if (!queried.IsSuccess) return Result<CoreBenchPull>.Fail(queried.Error);
            if (!queried.Value.Complete || queried.Value.Continuation is not null)
                return Result<CoreBenchPull>.Fail(ErrorCode.Unavailable, "The selected Bench query was not exhausted; earlier observations remain cached.");
            fetched.UnionWith(queried.Value.Items.Select(item => item.Key));
        }
        foreach (var key in targets)
        {
            var observed = await Fetch(key).ConfigureAwait(false);
            if (!observed.IsSuccess) return Result<CoreBenchPull>.Fail(observed.Error);
        }
        foreach (var selector in bench.Selectors.Where(value => value.Kind == SelectorKind.Subtree
            && (value.Connection is null || value.Connection == connection)))
        {
            if (!schema.Value.Relations.Any(kind => kind.Reference == selector.HierarchyRelation
                && kind.Shape == RelationShape.Hierarchy && kind.Directed))
                return Result<CoreBenchPull>.Fail(ErrorCode.Unsupported, "The subtree selector's declared hierarchy is unavailable.");
            var expanded = await FetchSubtree(selector.Item!, selector.HierarchyRelation!).ConfigureAwait(false);
            if (!expanded.IsSuccess) return Result<CoreBenchPull>.Fail(expanded.Error);
        }
        var hierarchies = schema.Value.Relations.Where(kind => kind.Shape == RelationShape.Hierarchy && kind.Directed).ToArray();
        if (hierarchies.Length > 1 && depthUp > 0)
            return Result<CoreBenchPull>.Fail(ErrorCode.Unsupported, "Automatic context expansion requires one discovered hierarchy; no kind was guessed.");
        if (hierarchies.Length == 1)
        {
            var hierarchy = hierarchies[0].Reference;
            var roots = fetched.Concat(targets).Distinct().ToArray();
            foreach (var root in roots)
            {
                var path = new HashSet<ItemKey>();
                var current = root;
                for (var depth = 0; depth < Math.Max(0, depthUp); depth++)
                {
                    var state = await SnapshotAsync(ct).ConfigureAwait(false);
                    if (!state.IsSuccess) return Result<CoreBenchPull>.Fail(state.Error);
                    current = Resolve(state.Value, current);
                    if (!path.Add(current)) return Result<CoreBenchPull>.Fail(ErrorCode.Conflict, "A hierarchy cycle prevents context expansion.");
                    var parents = ParentKeys(state.Value, current, hierarchy).ToArray();
                    if (parents.Length > 1) return Result<CoreBenchPull>.Fail(ErrorCode.Conflict, "Multiple parents prevent an unambiguous context pull.");
                    if (parents.Length == 0) break;
                    current = parents[0];
                    if (path.Contains(current)) return Result<CoreBenchPull>.Fail(ErrorCode.Conflict, "A hierarchy cycle prevents context expansion.");
                    var observed = await Fetch(current).ConfigureAwait(false);
                    if (!observed.IsSuccess) return Result<CoreBenchPull>.Fail(observed.Error);
                }
            }
        }
        var final = await BenchSnapshotAsync(ct).ConfigureAwait(false);
        if (!final.IsSuccess) return Result<CoreBenchPull>.Fail(final.Error);
        memberships = await MembershipsAsync(ct).ConfigureAwait(false);
        if (!memberships.IsSuccess) return Result<CoreBenchPull>.Fail(memberships.Error);
        schema = Result<ProcessSchema>.Ok(schema.Value with { Memberships = memberships.Value });
        var retained = final.Value.Benches.SingleOrDefault(value => value.Id == bench.Id);
        if (retained is null || !SameBench(bench, retained))
            return Result<CoreBenchPull>.Fail(ErrorCode.Conflict, "The specified Bench changed during pull; newer scope and cached observations were preserved.");
        members.UnionWith(final.Value.RetainedItems.Select(draft => Resolve(final.Value, draft.Key)));
        foreach (var selector in bench.Selectors.Where(value => value.Kind == SelectorKind.Query
            && (value.Connection is null || value.Connection == connection)))
            foreach (var item in final.Value.Items)
            {
                var match = Model.Matches(selector.Query!, item, schema.Value.Planning, DateTimeOffset.UtcNow, schema.Value.Memberships, planning.Value);
                if (!match.IsSuccess) return Result<CoreBenchPull>.Fail(match.Error);
                if (match.Value) members.Add(item.Key);
            }
        var memberCount = fetched.Count(members.Contains);
        return Result<CoreBenchPull>.Ok(new(bench, fetched.Count, memberCount, fetched.Count - memberCount,
            final.Value.RetainedItems.Count(draft => draft.Key.IsLocal || fetched.Contains(Resolve(final.Value, draft.Key)))));

        async Task<Result<bool>> Fetch(ItemKey key)
        {
            var state = await SnapshotAsync(ct).ConfigureAwait(false);
            if (!state.IsSuccess) return Result<bool>.Fail(state.Error);
            key = Resolve(state.Value, key);
            if (key.IsLocal)
                return state.Value.RetainedItems.Any(draft => draft.Key == key) ? Result<bool>.Ok(true)
                    : Result<bool>.Fail(ErrorCode.Unavailable, "An explicitly scoped local item is unavailable.");
            if (fetched.Contains(key)) return Result<bool>.Ok(true);
            var read = await ReadAsync(key, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
            if (!read.Value.RelationshipsComplete
                || provider.Capabilities.Operations.Contains(OpKind.LinkResource) && !read.Value.ResourcesComplete)
                return Result<bool>.Fail(ErrorCode.Unavailable, "Scoped item incident/resource evidence is incomplete; no complete pull is claimed.");
            fetched.Add(key);
            return Result<bool>.Ok(true);
        }

        async Task<Result<bool>> FetchSubtree(ItemKey root, string hierarchy)
        {
            var queue = new Queue<ItemKey>();
            var visited = new HashSet<ItemKey>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var entry))
            {
                var state = await SnapshotAsync(ct).ConfigureAwait(false);
                if (!state.IsSuccess) return Result<bool>.Fail(state.Error);
                var key = Resolve(state.Value, entry);
                if (!visited.Add(key)) return Result<bool>.Fail(ErrorCode.Conflict, "A hierarchy cycle or repeated descendant prevents a complete subtree pull.");
                var observed = await Fetch(key).ConfigureAwait(false);
                if (!observed.IsSuccess) return observed;
                members.Add(key);
                state = await SnapshotAsync(ct).ConfigureAwait(false);
                if (!state.IsSuccess) return Result<bool>.Fail(state.Error);
                foreach (var child in ChildKeys(state.Value, key, hierarchy))
                    queue.Enqueue(child);
            }
            return Result<bool>.Ok(true);
        }
    }

    private static IEnumerable<ItemKey> ChildKeys(StoreSnapshot state, ItemKey parent, string hierarchy)
        => state.Relationships.Where(edge => !edge.Deleted && edge.Kind == hierarchy && Resolve(state, edge.From) == parent)
            .Select(edge => Resolve(state, edge.To)).Concat(state.RetainedItems.OfType<Seed>().Where(draft => true
                && !state.Mappings.Any(mapping => mapping.Local == draft.Key) && draft.Parent is not null
                && draft.ParentRelation == hierarchy && Resolve(state, draft.Parent) == parent).Select(draft => draft.Key)).Distinct();

    private static IEnumerable<ItemKey> ParentKeys(StoreSnapshot state, ItemKey child, string hierarchy)
        => state.Relationships.Where(edge => !edge.Deleted && edge.Kind == hierarchy && Resolve(state, edge.To) == child)
            .Select(edge => Resolve(state, edge.From)).Concat(state.RetainedItems.OfType<Seed>().Where(draft => draft.Key == child
                && draft.Parent is not null && draft.ParentRelation == hierarchy).Select(draft => Resolve(state, draft.Parent!))).Distinct();

    internal static bool SameBench(Bench left, Bench right) => left.Id == right.Id && left.Name == right.Name && left.Revision == right.Revision
        && left.Connections.SequenceEqual(right.Connections)
        && left.Selectors.Length == right.Selectors.Length && left.Selectors.Zip(right.Selectors).All(pair =>
            pair.First.Kind == pair.Second.Kind && pair.First.Item == pair.Second.Item && pair.First.Connection == pair.Second.Connection
            && pair.First.HierarchyRelation == pair.Second.HierarchyRelation
            && (pair.First.Query is null ? pair.Second.Query is null
                : pair.Second.Query is not null && Model.QueryBytes(pair.First.Query).SequenceEqual(Model.QueryBytes(pair.Second.Query))));

    internal async Task<Result<Plan>> PrepareAsync(IEnumerable<Op> operations, CancellationToken ct,
        CorePlanReviewCapture? review = null, bool retainPolicyEvidence = false)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var intent = operations.ToImmutableArray();
        if (review is not null && !OwnsPlanReview(review))
            return Result<Plan>.Fail(ErrorCode.Conflict, "The operation-local review capture belongs to another exact scope.");
        var schema = review is null ? await SchemaAsync(ct).ConfigureAwait(false) : Result<ProcessSchema>.Ok(review.Metadata.Schema);
        if (!schema.IsSuccess) return Result<Plan>.Fail(schema.Error);
        for (var index = 0; index < intent.Length; index++)
        {
            if (intent[index].Kind != OpKind.Create) continue;
            var resolved = await ResolveCreateFieldsAsync(schema.Value, intent[index], ct).ConfigureAwait(false);
            if (!resolved.IsSuccess) return Result<Plan>.Fail(resolved.Error);
            intent = intent.SetItem(index, resolved.Value);
        }
        var policy = review is null ? await policies.CaptureAsync(connection, nativeActor, schema.Value, ct).ConfigureAwait(false)
            : Result<CapturedDeclaredPolicy>.Ok(review.Policy);
        if (!policy.IsSuccess) return Result<Plan>.Fail(policy.Error);
        var snapshot = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<Plan>.Fail(snapshot.Error);
        var plan = new Plan(connection, schema.Value.Fingerprint, snapshot.Value.Version, intent,
            policy.Value.Document.Identity, policy.Value.Document.Revision);
        var checkedPolicy = await CheckPolicyAsync(plan, schema.Value, policy.Value, ct, review,
            retainPolicyEvidence).ConfigureAwait(false);
        if (!checkedPolicy.IsSuccess) return Result<Plan>.Fail(checkedPolicy.Error);
        snapshot = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<Plan>.Fail(snapshot.Error);
        plan = new(connection, schema.Value.Fingerprint, snapshot.Value.Version, intent,
            policy.Value.Document.Identity, policy.Value.Document.Revision);
        var errors = Model.Validate(plan, schema.Value, snapshot.Value, provider.Capabilities);
        return errors.IsEmpty ? Result<Plan>.Ok(plan) : Result<Plan>.Fail(errors[0] with
        {
            Evidence = errors.Select(e => e.Message).ToImmutableArray(),
        });
    }

    internal async Task<Result<ImmutableArray<Error>>> CheckPlanAsync(Plan plan, CancellationToken ct)
    {
        if (plan.Connection != connection)
            return Result<ImmutableArray<Error>>.Fail(ErrorCode.Conflict, "The plan belongs to another immutable connection.");
        var schema = await SchemaAsync(ct).ConfigureAwait(false);
        if (!schema.IsSuccess) return Result<ImmutableArray<Error>>.Ok([schema.Error]);
        var stored = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<ImmutableArray<Error>>.Fail(stored.Error);
        var keys = plan.Operations.SelectMany(op => op.Links.Select(link => link.Other).Prepend(op.Item)
                .Concat(op.Other is null ? [] : new[] { op.Other })).Select(key => Resolve(stored.Value, key))
            .Where(key => !key.IsLocal).Distinct().ToImmutableArray();
        var acquired = await ObserveItemsAsync(keys, ct).ConfigureAwait(false);
        if (!acquired.IsSuccess) return Result<ImmutableArray<Error>>.Ok([acquired.Error]);
        var refreshed = Copy(stored.Value, stored.Value.Version,
            items: stored.Value.Items.Where(item => !keys.Contains(item.Key)).Concat(acquired.Value.Items),
            relationships: stored.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Local
                || !keys.Contains(edge.From) && !keys.Contains(edge.To)).Concat(acquired.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Provider)));
        var errors = Model.Validate(plan, schema.Value, refreshed, provider.Capabilities);
        if (!errors.IsEmpty) return Result<ImmutableArray<Error>>.Ok(errors);
        foreach (var op in plan.Operations.Where(op => op.Kind == OpKind.Create))
        {
            var resolved = await ResolveCreateFieldsAsync(schema.Value, op, ct, reviewed: true).ConfigureAwait(false);
            if (!resolved.IsSuccess) return Result<ImmutableArray<Error>>.Ok([resolved.Error]);
        }
        var policy = await policies.CaptureAsync(connection, nativeActor, schema.Value, ct).ConfigureAwait(false);
        if (!policy.IsSuccess) return Result<ImmutableArray<Error>>.Ok([policy.Error]);
        var checkedPolicy = await CheckPolicyAsync(plan, schema.Value, policy.Value, ct).ConfigureAwait(false);
        return Result<ImmutableArray<Error>>.Ok(checkedPolicy.IsSuccess ? [] : [checkedPolicy.Error]);
    }

    internal async Task<Result<bool>> RetainReviewAsync(Plan plan, CancellationToken ct)
    {
        if (plan.Connection != connection)
            return Result<bool>.Fail(ErrorCode.Conflict, "The review belongs to another immutable connection.");
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        if (current.Value.Plans.Any(value => value.Digest == plan.Digest)) return Result<bool>.Ok(true);
        if (current.Value.Version != plan.ExpectedStoreVersion)
            return Result<bool>.Fail(ErrorCode.Conflict, "Local state changed before review retention.");
        var next = new StoreSnapshot(current.Value.Version + 1, current.Value.Items, current.Value.Relationships,
            current.Value.RetainedItems, current.Value.Plans.Append(plan),
            current.Value.Journal.Concat(plan.Operations.Select((_, ordinal) => JournalEntry.Planned(plan.Digest, ordinal))),
            current.Value.Mappings, current.Value.Benches);
        var committed = await store.CommitAsync(connection, current.Value.Version, next, ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(committed.Error);
    }

    internal async Task<Result<(Plan Plan, long StoreVersion)>> PrepareSeedPublicationAsync(
        IEnumerable<Op> operations, bool retainIfNew, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var fresh = await PrepareAsync(operations, ct, retainPolicyEvidence: true).ConfigureAwait(false);
        if (!fresh.IsSuccess) return Result<(Plan, long)>.Fail(fresh.Error);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<(Plan, long)>.Fail(current.Error);
        var plan = fresh.Value;
        if (current.Value.Version != plan.ExpectedStoreVersion)
            return Result<(Plan, long)>.Fail(ErrorCode.Conflict, "Local state changed during seed publication review.");

        // Only discount the prior review's own one-version retention commit. All
        // other changes, including changed observations, require fresh intent.
        if (current.Value.Version > 0)
        {
            var previous = new Plan(connection, plan.SchemaFingerprint, current.Value.Version - 1,
                plan.Operations, plan.PolicyIdentity, plan.PolicyRevision);
            var digest = previous.Digest;
            var retained = current.Value.Plans.FirstOrDefault(value => value.Digest == digest);
            if (retained is not null && current.Value.Journal.Count(row => row.Digest == digest
                    && row.Status == OpStatus.Planned) == retained.Operations.Length)
                return Result<(Plan, long)>.Ok((retained, current.Value.Version));
        }

        if (!retainIfNew) return Result<(Plan, long)>.Ok((plan, current.Value.Version));
        var saved = await RetainReviewAsync(plan, ct).ConfigureAwait(false);
        return saved.IsSuccess ? Result<(Plan, long)>.Ok((plan, current.Value.Version + 1))
            : Result<(Plan, long)>.Fail(saved.Error);
    }

    internal async Task<Result<WorkItem>> ReadAsync(ItemKey key, CancellationToken ct, NativeEditorMetadata? editor = null,
        bool skipUnchanged = false)
    {
        var stored = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<WorkItem>.Fail(stored.Error);
        key = Resolve(stored.Value, key);
        if (key.IsLocal) return Result<WorkItem>.Fail(ErrorCode.InvalidInput, "A seed is not a provider work item observation.");
        if (editor is not null && editor.Schema.Connection != connection)
            return Result<WorkItem>.Fail(ErrorCode.Conflict, "Editor metadata belongs to another native connection.");
        var read = await ObserveContentAsync(() => editor is null
            ? provider.ReadAsync(connection, [key], ct, captureOnly: true)
            : editor.Read([key], ct)).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<WorkItem>.Fail(read.Error);
        if (read.Value.Items.Length != 1 || read.Value.Items[0].Key != key)
            return Result<WorkItem>.Fail(ErrorCode.Unavailable, "The provider did not observe the exact requested item.");
        var retained = await RetainObservationAsync(read.Value, skipUnchanged, ct).ConfigureAwait(false);
        return retained.IsSuccess ? Result<WorkItem>.Ok(read.Value.Items[0]) : Result<WorkItem>.Fail(retained.Error);
    }

    private async Task<Result<bool>> RetainObservationAsync(QueryPage read, bool skipUnchanged, CancellationToken ct)
    {
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        var item = read.Items.Single();
        var relationships = current.Value.Relationships.Where(r => r.Origin == RelationshipOrigin.Local
                || !item.RelationshipsComplete || r.From != item.Key && r.To != item.Key)
            .Concat(read.Relationships.Where(r => r.Origin == RelationshipOrigin.Provider)).ToHashSet();
        if (skipUnchanged && current.Value.Items.FirstOrDefault(i => i.Key == item.Key) is { } existing
            && existing.Type == item.Type && existing.Revision == item.Revision && existing.Fields.Equals(item.Fields)
            && existing.RelationshipsComplete == item.RelationshipsComplete && existing.ResourcesComplete == item.ResourcesComplete
            && existing.Resources.SequenceEqual(item.Resources) && relationships.SetEquals(current.Value.Relationships))
            return Result<bool>.Ok(true);
        var next = Copy(current.Value, current.Value.Version + 1,
            items: current.Value.Items.Where(i => i.Key != item.Key).Append(item), relationships: relationships);
        var committed = await store.CommitAsync(connection, current.Value.Version, next, ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(committed.Error);
    }

    internal async Task<Result<Seed>> StageSeedAsync(string type, FieldValues fields, CancellationToken ct, Guid? id = null,
        ItemKey? parent = null, string? parentRelation = null, ResourceLink? resource = null,
        IEnumerable<CreationLink>? links = null)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var read = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<Seed>.Fail(read.Error);
        var snapshot = read.Value;
        var key = ItemKey.Local(id ?? Guid.CreateVersion7());
        if (snapshot.Mappings.Any(m => m.Local == key))
            return Result<Seed>.Fail(ErrorCode.Conflict, "This seed is already published; edit its mapped work item.");
        var previous = snapshot.RetainedItems.OfType<Seed>().FirstOrDefault(d => d.Key == key);
        if (previous is not null && previous.Type != type)
            return Result<Seed>.Fail(ErrorCode.Conflict, "The retained seed type differs; no existing intent was replaced.");
        var draft = new Seed(key, type, previous?.Fields.Overlay(fields) ?? fields,
            parent ?? previous?.Parent, parentRelation ?? previous?.ParentRelation, resource ?? previous?.Resource,
            links ?? previous?.Links);
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, retainedItems: snapshot.RetainedItems.Where(d => d.Key != key).Append(draft)), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<Seed>.Ok(draft) : Result<Seed>.Fail(committed.Error);
    }

    internal async Task<Result<bool>> CaptureSeedResourceAsync(IEnumerable<Seed> captures, long expectedVersion,
        ResourceLink resource, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var drafts = captures.ToImmutableArray();
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        if (current.Value.Version != expectedVersion || drafts.Any(capture => !capture.Key.IsLocal
                || !current.Value.RetainedItems.Any(draft => SameDraft(draft, capture))
                || capture.Resource is not null && capture.Resource != resource))
            return Result<bool>.Fail(ErrorCode.Conflict, "Seed or artifact intent changed before branch capture; no replacement was inferred.");
        if (drafts.Any(draft => current.Value.Journal.Any(row => row.Status != OpStatus.Planned
                && current.Value.Plans.Single(plan => plan.Digest == row.Digest).Operations[row.Operation].Item == draft.Key)))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "An attempted UUID owns this seed; branch intent cannot replace its unresolved creation.");
        if (drafts.All(draft => draft.Resource == resource)) return Result<bool>.Ok(true);
        var keys = drafts.Select(draft => draft.Key).ToHashSet();
        var next = current.Value.RetainedItems.Select(item => item is Seed seed && keys.Contains(seed.Key)
            ? new Seed(seed.Key, seed.Type, seed.Fields, seed.Parent, seed.ParentRelation, resource, seed.Links) : item);
        var committed = await store.CommitAsync(connection, expectedVersion,
            Copy(current.Value, expectedVersion + 1, retainedItems: next), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(committed.Error);
    }

    internal async Task<Result<bool>> CaptureSeedLinksAsync(Seed capture, long expectedVersion,
        ItemKey? parent, string? parentRelation, IEnumerable<CreationLink> links, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        if (current.Value.Version != expectedVersion || !capture.Key.IsLocal
            || !current.Value.RetainedItems.Any(draft => SameDraft(draft, capture)))
            return Result<bool>.Fail(ErrorCode.Conflict, "The exact seed changed before link capture.");
        if (current.Value.Mappings.Any(mapping => mapping.Local == capture.Key)
            || current.Value.Journal.Any(row => row.Status != OpStatus.Planned && current.Value.Plans
                .Single(plan => plan.Digest == row.Digest).Operations[row.Operation].Item == capture.Key))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "An attempted creation UUID owns this seed; link intent cannot replace its fenced creation.");
        var changed = new Seed(capture.Key, capture.Type, capture.Fields, parent, parentRelation, capture.Resource, links);
        var drafts = current.Value.RetainedItems.Select(draft => draft.Key == capture.Key ? changed : draft).ToArray();
        var remaining = drafts.OfType<Seed>().Where(draft => !current.Value.Mappings.Any(mapping => mapping.Local == draft.Key)).ToList();
        while (remaining.Count != 0)
        {
            var ready = remaining.FirstOrDefault(draft => !remaining.Any(endpoint => endpoint.Key == draft.Parent
                || draft.Links.Any(link => link.Other == endpoint.Key)));
            if (ready is null) return Result<bool>.Fail(ErrorCode.Conflict, "Creation-bound links contain a cycle; no seed was changed.");
            remaining.Remove(ready);
        }
        var committed = await store.CommitAsync(connection, expectedVersion,
            Copy(current.Value, expectedVersion + 1, retainedItems: drafts), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(committed.Error);
    }

    internal async Task<Result<Seed>> CaptureSeedFieldsAsync(Seed capture, long expectedVersion,
        FieldValues fields, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<Seed>.Fail(current.Error);
        if (current.Value.Version != expectedVersion || !capture.Key.IsLocal
            || !current.Value.RetainedItems.Any(draft => SameDraft(draft, capture)))
            return Result<Seed>.Fail(ErrorCode.Conflict, "The captured seed changed during editing; neither version was overwritten.");
        if (current.Value.Mappings.Any(mapping => mapping.Local == capture.Key)
            || current.Value.Journal.Any(row => row.Status != OpStatus.Planned && current.Value.Plans
                .Single(plan => plan.Digest == row.Digest).Operations[row.Operation].Item == capture.Key))
            return Result<Seed>.Fail(ErrorCode.Indeterminate, "An attempted UUID owns this seed; edits cannot replace its fenced creation.");
        var changed = new Seed(capture.Key, capture.Type, capture.Fields.Overlay(fields),
            capture.Parent, capture.ParentRelation, capture.Resource, capture.Links);
        var committed = await store.CommitAsync(connection, expectedVersion,
            Copy(current.Value, expectedVersion + 1, retainedItems: current.Value.RetainedItems.Where(draft => draft.Key != capture.Key).Append(changed)), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<Seed>.Ok(changed) : Result<Seed>.Fail(committed.Error);
    }

    internal async Task<Result<int>> ReconcileSeedsAsync(CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var captured = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!captured.IsSuccess) return Result<int>.Fail(captured.Error);
        foreach (var row in captured.Value.Journal)
        {
            var plan = captured.Value.Plans.Single(plan => plan.Digest == row.Digest);
            if (plan.Operations[row.Operation].Kind != OpKind.Create || row.Status == OpStatus.Planned) continue;
            if (row.Status is OpStatus.Applied or OpStatus.Applying)
            {
                var recovered = await provider.RecoverAsync(plan, row.Operation, ct).ConfigureAwait(false);
                if (!recovered.IsSuccess) return Result<int>.Fail(recovered.Error);
                if (recovered.Value.Status != OpStatus.Verified)
                    return Result<int>.Fail(recovered.Value.Error ?? new(ErrorCode.Indeterminate, "The original creation receipt remains unresolved; no creation was replayed."));
            }
            else if (row.Status != OpStatus.Verified)
                return Result<int>.Fail(row.Receipts.LastOrDefault()?.Error
                    ?? new(ErrorCode.Indeterminate, "A creation remains fenced; no identity or outcome was inferred."));
        }
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<int>.Fail(current.Error);
        var settled = new HashSet<ItemKey>();
        foreach (var draft in current.Value.RetainedItems.OfType<Seed>())
        {
            var mapping = current.Value.Mappings.SingleOrDefault(mapping => mapping.Local == draft.Key);
            if (mapping is null) continue;
            var row = current.Value.Journal.Single(entry => entry.Digest == mapping.PlanDigest && entry.Operation == mapping.Operation);
            var op = current.Value.Plans.Single(plan => plan.Digest == row.Digest).Operations[row.Operation];
            if (row.Status != OpStatus.Verified || op.Item != draft.Key || op.Type != draft.Type || !op.Fields.Equals(draft.Fields)
                || op.Other != draft.Parent || op.Relation != draft.ParentRelation || op.Resource != draft.Resource
                || !op.Links.SequenceEqual(draft.Links))
                return Result<int>.Fail(ErrorCode.Conflict, "A published seed has later or unverified intent; the original UUID, draft and receipt were preserved.");
            settled.Add(draft.Key);
        }
        if (settled.Count == 0) return Result<int>.Ok(0);
        var committed = await store.CommitAsync(connection, current.Value.Version,
            Copy(current.Value, current.Value.Version + 1, retainedItems: current.Value.RetainedItems.Where(draft => !settled.Contains(draft.Key))), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<int>.Ok(settled.Count) : Result<int>.Fail(committed.Error);
    }

    internal async Task<Result<LocalEdits>> StageEditAsync(ItemKey key, FieldValues fields, CancellationToken ct, string? typeOverride = null,
        WorkItem? capturedBaseline = null, long? expectedVersion = null)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var remote = capturedBaseline is null ? await ReadAsync(key, ct).ConfigureAwait(false) : Result<WorkItem>.Ok(capturedBaseline);
        if (!remote.IsSuccess) return Result<LocalEdits>.Fail(remote.Error);
        if (key.IsLocal || remote.Value.Key != key || remote.Value.Revision is null)
            return Result<LocalEdits>.Fail(ErrorCode.Conflict, "The captured edit baseline does not belong to the exact native target.");
        var read = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<LocalEdits>.Fail(read.Error);
        var snapshot = read.Value;
        if (expectedVersion is not null && snapshot.Version != expectedVersion)
            return Result<LocalEdits>.Fail(ErrorCode.Conflict, "The local workspace changed during editing; the newer local edits were preserved.");
        var previous = snapshot.RetainedItems.OfType<LocalEdits>().FirstOrDefault(d => d.Key == remote.Value.Key);
        var draft = new LocalEdits(remote.Value.Key, typeOverride ?? previous?.Type ?? remote.Value.Type,
            previous?.EditBaseline ?? remote.Value, previous?.Changes.Overlay(fields) ?? fields);
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, retainedItems: snapshot.RetainedItems.Where(d => d.Key != draft.Key).Append(draft)), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<LocalEdits>.Ok(draft) : Result<LocalEdits>.Fail(committed.Error);
    }

    internal async Task<Result<CoreDraftPreview>> PreviewDraftAsync(ItemKey key, CancellationToken ct,
        IEnumerable<Op>? precedingCreates = null, CorePlanReviewCapture? review = null, bool seedPublication = false)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var read = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<CoreDraftPreview>.Fail(read.Error);
        var draft = read.Value.RetainedItems.FirstOrDefault(d => d.Key == Resolve(read.Value, key));
        if (draft is null) return Result<CoreDraftPreview>.Fail(ErrorCode.NotFound, "No local edits or seed exist for the exact target.");
        if (review is not null && !OwnsPlanReview(review))
            return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The operation-local review capture belongs to another exact scope.");
        var discovery = review is null ? await provider.DiscoverSchemaAsync(connection, ct).ConfigureAwait(false)
            : Result<ProcessSchema>.Ok(review.Metadata.Schema);
        if (!discovery.IsSuccess) return Result<CoreDraftPreview>.Fail(discovery.Error);
        var schema = discovery.Value;
        Op op;
        if (draft is Seed seed)
        {
            foreach (var endpoint in seed.Links.Select(link => link.Other))
            {
                var target = Resolve(read.Value, endpoint);
                if (!target.IsLocal)
                {
                    var observed = await ReadAsync(target, ct, review?.Metadata, skipUnchanged: seedPublication).ConfigureAwait(false);
                    if (!observed.IsSuccess) return Result<CoreDraftPreview>.Fail(observed.Error);
                }
                else if (!read.Value.RetainedItems.Any(value => value.Key == target))
                    return Result<CoreDraftPreview>.Fail(ErrorCode.Unavailable, "The exact creation-link seed is missing.");
            }
            if (seed.Parent is { } parent)
            {
                var parentKey = Resolve(read.Value, parent);
                if (!parentKey.IsLocal)
                {
                    var observed = await ReadAsync(parentKey, ct, review?.Metadata, skipUnchanged: seedPublication).ConfigureAwait(false);
                    if (!observed.IsSuccess) return Result<CoreDraftPreview>.Fail(observed.Error);
                }
                else if (!read.Value.RetainedItems.Any(value => value.Key == parentKey))
                    return Result<CoreDraftPreview>.Fail(ErrorCode.Unavailable, "The exact parent seed is missing.");
            }
            op = new("create", OpKind.Create, seed.Key, fields: seed.Fields, type: seed.Type,
                other: seed.Parent, relation: seed.ParentRelation, resource: seed.Resource,
                concurrency: ConcurrencyMode.ObservedNonAtomic, links: seed.Links);
            var resolved = await ResolveCreateFieldsAsync(schema, op, ct).ConfigureAwait(false);
            if (!resolved.IsSuccess) return Result<CoreDraftPreview>.Fail(resolved.Error);
            op = resolved.Value;
        }

        else if (draft is LocalEdits edits)
        {
            var remote = await ReadAsync(edits.Key, ct, review?.Metadata).ConfigureAwait(false);
            if (!remote.IsSuccess) return Result<CoreDraftPreview>.Fail(remote.Error);
            var merge = Model.Merge(edits, remote.Value);
            if (!merge.IsSuccess) return Result<CoreDraftPreview>.Fail(merge.Error);
            if (!merge.Value.Conflicts.IsEmpty)
                return Result<CoreDraftPreview>.Fail(new(ErrorCode.Conflict, "The local edits conflict with newer provider field values.")
                { Evidence = merge.Value.Conflicts });
            var explicitType = edits.Type != edits.EditBaseline.Type;
            if (explicitType && remote.Value.Type != edits.EditBaseline.Type && remote.Value.Type != edits.Type)
                return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The native type changed differently from the authored type intent.");
            var type = explicitType && remote.Value.Type != edits.Type ? edits.Type : null;
            var gates = Model.CloseGates(schema, remote.Value, merge.Value.Fields, type);
            if (!gates.IsSuccess) return Result<CoreDraftPreview>.Fail(gates.Error);
            if (gates.Value.Any(g => edits.Changes.TryGetValue(g, out var authored)
                && merge.Value.Fields.TryGetValue(g, out var merged) && !authored.Equals(merged)))
                return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "An authored close-gate value changed remotely; no replacement judgment was inferred.");
            var fields = new FieldValues(edits.Changes.Where(f => merge.Value.Fields.TryGetValue(f.Key, out var value)
                && (gates.Value.Contains(f.Key) || !remote.Value.Fields.TryGetValue(f.Key, out var current) || !value.Equals(current)))
                .Select(f => KeyValuePair.Create(f.Key, merge.Value.Fields[f.Key])));
            if (fields.Count == 0 && type is null) return Result<CoreDraftPreview>.Ok(new(draft, null));
            op = new("edit", OpKind.Batch, edits.Key, remote.Value.Revision, fields, type,
                concurrency: provider.Capabilities.ExactRevisions ? ConcurrencyMode.ExactRevision : ConcurrencyMode.ObservedNonAtomic);
        }
        else throw new InvalidOperationException("Unknown retained item kind.");
        var policy = review is null ? await policies.CaptureAsync(connection, nativeActor, schema, ct).ConfigureAwait(false)
            : Result<CapturedDeclaredPolicy>.Ok(review.Policy);
        if (!policy.IsSuccess) return Result<CoreDraftPreview>.Fail(policy.Error);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreDraftPreview>.Fail(current.Error);
        if (!current.Value.RetainedItems.Any(d => SameDraft(d, draft)))
            return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "Retained intent changed during preview; no intent was silently rebased.");
        var operations = (precedingCreates ?? []).Append(op).ToImmutableArray();
        var plan = new Plan(connection, schema.Fingerprint, current.Value.Version, operations,
            policy.Value.Document.Identity, policy.Value.Document.Revision);
        var checkedPolicy = await CheckPolicyAsync(plan, schema, policy.Value, ct, review).ConfigureAwait(false);
        if (!checkedPolicy.IsSuccess) return Result<CoreDraftPreview>.Fail(checkedPolicy.Error);
        current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreDraftPreview>.Fail(current.Error);
        if (!current.Value.RetainedItems.Any(d => SameDraft(d, draft)))
            return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "Retained intent changed during policy acquisition.");
        plan = new(connection, schema.Fingerprint, current.Value.Version, operations,
            policy.Value.Document.Identity, policy.Value.Document.Revision);
        var errors = Model.Validate(plan, schema, current.Value, provider.Capabilities);
        return errors.IsEmpty ? Result<CoreDraftPreview>.Ok(new(draft, plan))
            : Result<CoreDraftPreview>.Fail(errors[0] with { Evidence = errors.Select(e => e.Message).ToImmutableArray() });
    }

    private async Task<Result<Op>> ResolveCreateFieldsAsync(ProcessSchema schema, Op op, CancellationToken ct, bool reviewed = false)
    {
        var evidence = await provider.ResolveCreateEvidenceAsync(schema, op, ct).ConfigureAwait(false);
        if (!evidence.IsSuccess) return Result<Op>.Fail(evidence.Error);
        if ((reviewed || op.CreateEvidence.Count != 0) && !op.CreateEvidence.Equals(evidence.Value))
            return Result<Op>.Fail(ErrorCode.Conflict, "Provider create field evidence is missing or changed; preview a new plan.");
        return Result<Op>.Ok(op with { CreateEvidence = evidence.Value });
    }

    internal async Task<Result<CoreExecution>> SaveDraftAsync(CoreDraftPreview preview, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        CoreExecution execution;
        if (preview.Plan is { } plan)
        {
            var applied = await ApplyAsync(plan, ct).ConfigureAwait(false);
            if (!applied.IsSuccess || !applied.Value.EffectsVerified) return applied;
            execution = applied.Value;
        }
        else
        {
            var fresh = await PreviewDraftAsync(preview.Draft.Key, ct).ConfigureAwait(false);
            if (!fresh.IsSuccess) return Result<CoreExecution>.Fail(fresh.Error);
            if (fresh.Value.Plan is not null || !SameDraft(fresh.Value.Draft, preview.Draft))
                return Result<CoreExecution>.Fail(ErrorCode.Conflict, "The no-change preview is stale; review fresh intent instead.");
            execution = new([], AlreadyVerified: true);
        }
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Retained(current.Error);
        var snapshot = current.Value;
        var draft = snapshot.RetainedItems.FirstOrDefault(d => d.Key == preview.Draft.Key);
        if (draft is null) return Result<CoreExecution>.Ok(execution);
        var drafts = snapshot.RetainedItems.Where(d => d.Key != draft.Key);
        var changed = !SameDraft(draft, preview.Draft);
        if (changed)
        {
            if (draft is Seed seed && preview.Draft is Seed capturedSeed && (seed.Parent != capturedSeed.Parent
                || seed.ParentRelation != capturedSeed.ParentRelation || seed.Resource != capturedSeed.Resource
                || !seed.Links.SequenceEqual(capturedSeed.Links)))
                return Retained(new(ErrorCode.Conflict, "Creation verified; later creation-bound link intent remains in the original local draft for explicit reconciliation."));
            var receipt = execution.Receipts.LastOrDefault(r => r.Status == OpStatus.Verified);
            var remoteKey = Resolve(snapshot, draft.Key);
            if (receipt?.Revision is null || remoteKey.IsLocal)
                return Retained(new(ErrorCode.Conflict, "Verified effects did not consume later local draft edits."));
            var baseline = snapshot.Items.FirstOrDefault(i => i.Key == remoteKey && i.Revision == receipt.Revision)
                ?? new WorkItem(remoteKey, preview.Plan!.Operations[0].Type ?? preview.Draft.Type,
                    preview.Plan.Operations[0].Fields, receipt.Revision);
            var type = draft is LocalEdits edits && edits.Type != edits.EditBaseline.Type ? edits.Type : baseline.Type;
            if (snapshot.RetainedItems.Any(d => d.Key == remoteKey && d.Key != draft.Key))
                return Retained(new(ErrorCode.Conflict, "A distinct mapped native draft already exists; neither intent was replaced."));
            drafts = drafts.Append(new LocalEdits(remoteKey, type, baseline, draft.Fields));
        }
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, retainedItems: drafts), ct).ConfigureAwait(false);
        if (!committed.IsSuccess) return Retained(committed.Error);
        return changed ? Retained(new(ErrorCode.Conflict, "Remote effects verified; later local edits remain as a separate draft."))
            : Result<CoreExecution>.Ok(execution);

        Result<CoreExecution> Retained(Error error) => Result<CoreExecution>.Ok(execution with
        {
            Error = error,
            DraftRetained = true,
        });
    }

    internal async Task<Result<CoreExecution>> SaveDraftSetAsync(IEnumerable<CoreDraftPreview> previews,
        Plan? plan, CancellationToken ct, long? expectedStoreVersion = null)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var captures = previews.ToImmutableArray();
        var before = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!before.IsSuccess) return Result<CoreExecution>.Fail(before.Error);
        if (expectedStoreVersion is not null && before.Value.Version != expectedStoreVersion)
            return Result<CoreExecution>.Fail(ErrorCode.Conflict, "Local state changed after seed publication review.");
        if (captures.Any(capture => !before.Value.RetainedItems.Any(draft => SameDraft(draft, capture.Draft))))
            return Result<CoreExecution>.Fail(ErrorCode.Conflict, "A captured draft changed before save; no replacement intent was dispatched.");
        if (plan is null && captures.Any(capture => capture.Plan is not null))
            return Result<CoreExecution>.Fail(ErrorCode.InvalidInput, "Authored effects require their exact reviewed plan before settlement.");
        if (plan is not null)
        {
            var intended = captures.Where(capture => capture.Plan is not null)
                .Select(capture => capture.Plan!.Operations.Single(op => op.Item == capture.Draft.Key)).ToArray();
            if (intended.Length != plan.Operations.Length || new Plan(plan.Connection,
                    plan.SchemaFingerprint, plan.ExpectedStoreVersion,
                    intended.Select((op, index) => op.WithId(plan.Operations[index].Id)),
                    plan.PolicyIdentity, plan.PolicyRevision).Digest != plan.Digest)
                return Result<CoreExecution>.Fail(ErrorCode.InvalidInput, "The reviewed plan does not match the captured draft effects.");
        }
        foreach (var capture in captures.Where(capture => capture.Plan is null))
        {
            var fresh = await PreviewDraftAsync(capture.Draft.Key, ct).ConfigureAwait(false);
            if (!fresh.IsSuccess) return Result<CoreExecution>.Fail(fresh.Error);
            if (fresh.Value.Plan is not null || !SameDraft(fresh.Value.Draft, capture.Draft))
                return Result<CoreExecution>.Fail(ErrorCode.Conflict, "A converged draft changed before settlement.");
        }
        var applied = plan is null
            ? Result<CoreExecution>.Ok(new([], AlreadyVerified: true))
            : await ApplyAsync(plan, ct).ConfigureAwait(false);
        if (!applied.IsSuccess || !applied.Value.EffectsVerified) return applied;
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Retained(current.Error);
        if (captures.Any(capture => current.Value.RetainedItems.Any(draft => draft.Key == capture.Draft.Key
                && !SameDraft(draft, capture.Draft))))
            return Retained(new(ErrorCode.Conflict, "Remote effects verified; later local edits and their captured drafts were retained."));
        var keys = captures.Select(capture => capture.Draft.Key).ToHashSet();
        var committed = await store.CommitAsync(connection, current.Value.Version,
            Copy(current.Value, current.Value.Version + 1, retainedItems: current.Value.RetainedItems.Where(draft => !keys.Contains(draft.Key))),
            ct).ConfigureAwait(false);
        return committed.IsSuccess ? applied : Retained(committed.Error);

        Result<CoreExecution> Retained(Error error) => Result<CoreExecution>.Ok(applied.Value with { Error = error, DraftRetained = true });
    }

    internal async Task<Result<int>> DiscardDraftsAsync(IEnumerable<RetainedItem> captures, long expectedVersion, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var drafts = captures.ToImmutableArray();
        if (drafts.Any(draft => draft.Key.IsLocal))
            return Result<int>.Fail(ErrorCode.InvalidInput, "Seed abandonment is a distinct explicit operation; no seed was discarded.");
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<int>.Fail(current.Error);
        if (current.Value.Version != expectedVersion
            || drafts.Any(capture => !current.Value.RetainedItems.Any(draft => SameDraft(draft, capture))))
            return Result<int>.Fail(ErrorCode.Conflict, "Local intent changed after discard confirmation; no draft was removed.");
        var keys = drafts.Select(draft => draft.Key).ToHashSet();
        var committed = await store.CommitAsync(connection, expectedVersion,
            Copy(current.Value, expectedVersion + 1, retainedItems: current.Value.RetainedItems.Where(draft => !keys.Contains(draft.Key))),
            ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<int>.Ok(keys.Count) : Result<int>.Fail(committed.Error);
    }

    internal async Task<Result<bool>> DiscardSeedAsync(Seed capture, long expectedVersion, CancellationToken ct)
    {
        if (!capture.Key.IsLocal)
            return Result<bool>.Fail(ErrorCode.InvalidInput, "Seed discard requires the exact retained local UUID.");
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        var snapshot = current.Value;
        if (snapshot.Version != expectedVersion || !snapshot.RetainedItems.Any(draft => SameDraft(draft, capture)))
            return Result<bool>.Fail(ErrorCode.Conflict, "Seed intent changed after confirmation; nothing was discarded.");
        if (snapshot.Mappings.Any(mapping => mapping.Local == capture.Key)
            || snapshot.Journal.Any(row => row.Status != OpStatus.Planned
                && snapshot.Plans.Single(plan => plan.Digest == row.Digest).Operations[row.Operation] is { } op
                && (op.Item == capture.Key || op.Other == capture.Key)))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "An attempted operation or permanent mapping owns this UUID; preserve its draft and settle native evidence explicitly.");
        var relationships = snapshot.Relationships.Select(edge => edge.Origin == RelationshipOrigin.Local
                && !edge.Deleted && (edge.From == capture.Key || edge.To == capture.Key)
            ? edge with { Deleted = true, LocalVersion = edge.LocalVersion + 1 } : edge);
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, retainedItems: snapshot.RetainedItems.Where(draft => draft.Key != capture.Key),
                relationships: relationships), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(committed.Error);
    }
    internal async Task<Result<CoreExecution>> ApplyAsync(Plan plan, CancellationToken ct)
    {
        if (plan.Connection != connection)
            return Result<CoreExecution>.Fail(ErrorCode.Conflict, "The plan belongs to another immutable connection.");
        if (executionRegistryPath is not null)
        {
            if (store is not Twig.Store.Sqlite.SqliteStore native || native.InstanceId != storeInstance)
                return Result<CoreExecution>.Fail(ErrorCode.StorageUnavailable, "Native execution requires its exact authoritative Core store.");
            var activation = await NativeCoreExecution.PublishingAsync(executionRegistryPath, ct).ConfigureAwait(false);
            if (!activation.IsSuccess) return Result<CoreExecution>.Fail(activation.Error);
        }
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var stored = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<CoreExecution>.Fail(stored.Error);
        if (!stored.Value.Plans.Any(p => p.Digest == plan.Digest))
        {
            var policy = await CheckCurrentPolicyAsync(plan, ct).ConfigureAwait(false);
            if (!policy.IsSuccess) return Result<CoreExecution>.Fail(policy.Error);
            var imported = await provider.ImportAsync(plan, ct).ConfigureAwait(false);
            if (!imported.IsSuccess) return Result<CoreExecution>.Fail(imported.Error);
        }
        var receipts = ImmutableArray.CreateBuilder<Receipt>();
        var dispatched = false;
        for (var ordinal = 0; ordinal < plan.Operations.Length; ordinal++)
        {
            var current = await SnapshotAsync(ct).ConfigureAwait(false);
            if (!current.IsSuccess) return Finish(current.Error);
            var row = current.Value.Journal.Single(j => j.Digest == plan.Digest && j.Operation == ordinal);
            if (row.Status == OpStatus.Verified) { receipts.Add(row.Receipts[^1]); continue; }
            if (row.Status is OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate)
            {
                receipts.Add(row.Receipts[^1]);
                return Finish(row.Receipts[^1].Error);
            }
            var policy = await CheckCurrentPolicyAsync(plan, ct).ConfigureAwait(false);
            if (!policy.IsSuccess) return Finish(policy.Error);
            Result<Receipt> result;
            if (row.Status == OpStatus.Planned)
            {
                var attempt = Guid.NewGuid();
                using var targets = executionRegistryPath is null ? null : ConnectionOperationGate.AcquireCoreTargets(
                    executionRegistryPath, NativeCoreExecution.OperationKeys(plan.Operations[ordinal], current.Value, connection));
                if (executionRegistryPath is not null && store is Twig.Store.Sqlite.SqliteStore nativeStore)
                {
                    var admitted = await NativeCoreExecution.AdmitAsync(executionRegistryPath,
                        new(storeInstance, nativeStore.DatabasePath, worktreeRoot, connection),
                        plan, ordinal, attempt, ct, planned: true).ConfigureAwait(false);
                    if (!admitted.IsSuccess) return Finish(admitted.Error);
                    admitted.Value.Dispose();
                }
                result = await provider.ApplyAsync(plan, ordinal, attempt, ct).ConfigureAwait(false);
                dispatched = true;
            }
            else result = await provider.RecoverAsync(plan, ordinal, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return Finish(result.Error);
            var receipt = result.Value;
            if (receipt.Status == OpStatus.Applied)
            {
                var verified = await provider.VerifyAsync(plan, receipt, ct).ConfigureAwait(false);
                if (!verified.IsSuccess) { receipts.Add(receipt); return Finish(verified.Error); }
                receipt = verified.Value;
            }
            receipts.Add(receipt);
            if (receipt.Status != OpStatus.Verified) return Finish(receipt.Error);
        }
        return Finish(null);

        Result<CoreExecution> Finish(Error? error) => Result<CoreExecution>.Ok(new(receipts.ToImmutable(), error,
            AlreadyVerified: error is null && !dispatched));
    }

    private async Task<Result<bool>> CheckCurrentPolicyAsync(Plan plan, CancellationToken ct)
    {
        var discovery = await provider.DiscoverSchemaAsync(connection, ct).ConfigureAwait(false);
        if (!discovery.IsSuccess) return Result<bool>.Fail(discovery.Error);
        if (discovery.Value.Fingerprint != plan.SchemaFingerprint)
            return Result<bool>.Fail(ErrorCode.SchemaChanged, "The actual native schema differs from the reviewed plan.");
        var capture = await policies.CaptureAsync(connection, nativeActor, discovery.Value, ct).ConfigureAwait(false);
        return capture.IsSuccess ? await CheckPolicyAsync(plan, discovery.Value, capture.Value, ct).ConfigureAwait(false)
            : Result<bool>.Fail(capture.Error);
    }

    private async Task<Result<bool>> CheckPolicyAsync(Plan plan, ProcessSchema schema, CapturedDeclaredPolicy policy,
        CancellationToken ct, CorePlanReviewCapture? review = null, bool retainEvidence = false)
    {
        if (plan.PolicyIdentity != policy.Document.Identity || plan.PolicyRevision != policy.Document.Revision)
            return Result<bool>.Fail(ErrorCode.SchemaChanged, "The actual declared policy presence or bytes differ from the reviewed intent.");
        if (policy.Policy.Rules.IsEmpty) return Result<bool>.Ok(true);
        var captured = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!captured.IsSuccess) return Result<bool>.Fail(captured.Error);
        var shapes = policy.Policy.Rules.Select(r => r.Kind == RuleKind.ParentType ? RelationShape.Hierarchy : RelationShape.Dependency).ToHashSet();
        var kinds = schema.Relations.Where(r => shapes.Contains(r.Shape)).Select(r => r.Reference).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<ItemKey>(plan.Operations.SelectMany(o => o.Links.Select(link => link.Other)
            .Prepend(o.Item).Concat(o.Other is null ? [] : new[] { o.Other })).Distinct());
        var items = new Dictionary<ItemKey, WorkItem>();
        var edges = new HashSet<Relationship>();
        var bound = policy.Policy.Rules.Min(r => r.MaxNodes);
        while (pending.TryDequeue(out var key))
        {
            key = Resolve(captured.Value, key);
            if (items.ContainsKey(key)) continue;
            if (items.Count >= bound) return Result<bool>.Fail(ErrorCode.Unavailable, "Mechanical policy acquisition exceeded its declared evidence bound.");
            if (key.IsLocal)
            {
                var local = captured.Value.RetainedItems.FirstOrDefault(d => d.Key == key);
                if (local is null) return Result<bool>.Fail(ErrorCode.Unavailable, "A local policy endpoint was not observed.");
                items.Add(key, new(key, local.Type, local.Fields, null, true));
            }
            else
            {
                var read = await ObserveContentAsync(() => review is null
                    ? provider.ReadAsync(connection, [key], ct, captureOnly: true)
                    : review.Metadata.Read([key], ct)).ConfigureAwait(false);
                if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
                var item = read.Value.Items.SingleOrDefault(i => i.Key == key);
                if (item?.RelationshipsComplete != true)
                    return Result<bool>.Fail(ErrorCode.Unavailable, "Complete native policy endpoint observations are unavailable.");
                if (retainEvidence)
                {
                    if (read.Value.Items.Length != 1)
                        return Result<bool>.Fail(ErrorCode.Unavailable, "The provider did not observe the exact policy endpoint.");
                    var retained = await RetainObservationAsync(read.Value, skipUnchanged: true, ct).ConfigureAwait(false);
                    if (!retained.IsSuccess) return retained;
                }
                items.Add(key, item);
                foreach (var edge in read.Value.Relationships.Where(r => kinds.Contains(r.Kind))) edges.Add(edge);
            }
            foreach (var edge in captured.Value.Relationships.Where(r => r.Origin == RelationshipOrigin.Local && !r.Deleted
                && kinds.Contains(r.Kind) && (r.From == key || r.To == key))) edges.Add(edge);
            foreach (var edge in edges.Where(e => e.From == key || e.To == key))
            {
                if (!items.ContainsKey(edge.From)) pending.Enqueue(edge.From);
                if (!items.ContainsKey(edge.To)) pending.Enqueue(edge.To);
            }
        }
        var before = new StoreSnapshot(captured.Value.Version, items.Values, edges);
        var proposed = edges.ToHashSet();
        foreach (var op in plan.Operations.Where(op => op.Kind == OpKind.Create && op.Other is not null))
            proposed.Add(new(Resolve(captured.Value, op.Other!), op.Item, op.Relation!, RelationshipOrigin.Provider));
        foreach (var op in plan.Operations.Where(op => op.Kind is OpKind.Create or OpKind.Batch))
            foreach (var link in op.Links.Where(link => kinds.Contains(link.Relation)))
            {
                var target = Resolve(captured.Value, link.Other);
                var edge = new Relationship(link.Outgoing ? op.Item : target, link.Outgoing ? target : op.Item,
                    link.Relation, RelationshipOrigin.Provider);
                if (!link.Remove) proposed.Add(edge);
                else proposed.RemoveWhere(value => value.From == edge.From && value.To == edge.To
                    && value.Kind == edge.Kind && value.Origin == edge.Origin);
            }
        foreach (var op in plan.Operations.Where(o => o.Kind is OpKind.Relate or OpKind.Unrelate))
        {
            var edge = new Relationship(op.Item, op.Other!, op.Relation!, op.Origin);
            if (op.Kind == OpKind.Relate) proposed.Add(edge);
            else proposed.RemoveWhere(e => e.From == edge.From && e.To == edge.To && e.Kind == edge.Kind && e.Origin == edge.Origin);
        }
        var after = new StoreSnapshot(before.Version, items.Values.Select(item => plan.Operations
            .Where(o => o.Item == item.Key && o.Kind == OpKind.Batch && o.Type is not null)
            .Aggregate(item, (value, op) => new(value.Key, op.Type!, value.Fields, value.Revision, value.RelationshipsComplete))),
            proposed);
        var blocking = Model.Evaluate(policy.Policy, before, after, schema).Where(f => f.Blocks).ToArray();
        return blocking.Length == 0 ? Result<bool>.Ok(true)
            : Result<bool>.Fail(new(ErrorCode.Unavailable, "Declared mechanical policy refused the observed effect.")
            { Evidence = blocking.Select(f => f.RuleId + ": " + f.Message).ToImmutableArray() });
    }

    private static ItemKey Resolve(StoreSnapshot snapshot, ItemKey key)
        => snapshot.Mappings.FirstOrDefault(m => m.Local == key)?.Remote ?? key;
    private static bool SameDraft(RetainedItem left, RetainedItem right) => left.Key == right.Key && left.Type == right.Type
        && left.Fields.Equals(right.Fields) && (left, right) switch
        {
            (LocalEdits a, LocalEdits b) => a.EditBaseline.Revision == b.EditBaseline.Revision,
            (Seed a, Seed b) => a.Parent == b.Parent && a.ParentRelation == b.ParentRelation
                && a.Resource == b.Resource && a.Links.SequenceEqual(b.Links),
            _ => false,
        };

    private static StoreSnapshot Copy(StoreSnapshot snapshot, long version, IEnumerable<WorkItem>? items = null,
        IEnumerable<Relationship>? relationships = null, IEnumerable<RetainedItem>? retainedItems = null, IEnumerable<Bench>? benches = null)
        => new(version, items ?? snapshot.Items, relationships ?? snapshot.Relationships, retainedItems ?? snapshot.RetainedItems,
            snapshot.Plans, snapshot.Journal, snapshot.Mappings, benches ?? snapshot.Benches);
}
