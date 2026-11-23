using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Bench.Application;
using Bench.Domain;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using NativeBench = Twig.Core.Bench;

namespace Bench.Infrastructure;

public sealed record NativePanelContext(string Root, NativeSourceStamp Stamp, RegisteredSource Source)
{
    public bool LabelAvailable { get; init; }
}
public sealed record NativePanelCapture(UserBenchSnapshot Definitions, NativePanelContext Context,
    SourceObservation Observation);
public sealed record NativePanelSeeds(NativePanelCapture Capture, SourceView View, ImmutableArray<NativeOwnedSeed> Seeds);
public sealed record NativePanelView(BenchView View, ImmutableDictionary<Guid, string> Labels, NativePlanStatus Plan)
{
    public ImmutableDictionary<Guid, int> NormalDraftCounts { get; init; } = ImmutableDictionary<Guid, int>.Empty;
    public ImmutableDictionary<NativePanelContext, SourceObservation> Observations { get; init; }
        = ImmutableDictionary<NativePanelContext, SourceObservation>.Empty;
}

/// <summary>Native definitions are authoritative. Projection descriptors below are request-local, never persisted owners.</summary>
public sealed class NativePanel(UserBenchStore definitions, INativeSourceGateway gateway, NativeSourceAdapter sources)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> definitionGates = new();
    public Task<Result<NativeItemEditor>> OpenEditorAsync(NativePanelContext context, Guid benchId, CancellationToken ct)
        => sources.NativeAsync(context.Root, token => gateway.OpenEditorAsync(context.Root, benchId, context.Stamp, token), ct);
    public Task<Result<NativeItemEditor>> OpenExistingEditorAsync(NativePanelContext context, Guid benchId, CancellationToken ct)
        => sources.NativeAsync(context.Root, token => gateway.OpenExistingEditorAsync(context.Root, benchId, context.Stamp, token), ct);
    public Task<Result<T>> EditorOperationAsync<T>(NativePanelContext context, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        => sources.EditorOperationAsync(context.Source, operation, ct);
    public Task<Result<SourceObservation>> ReadEditorSourceAsync(NativePanelContext context, CancellationToken ct)
        => sources.ReadEditorSourceAsync(context.Source, ct);
    public async Task<Result<NativePanelSeeds>> ReadSeedsAsync(NativePanelContext context, Guid benchId, CancellationToken ct)
    {
        var captured = await CaptureAsync(context, ct).ConfigureAwait(false);
        if (!captured.IsSuccess) return Result<NativePanelSeeds>.Fail(captured.Error.Code == ErrorCode.Conflict
            ? captured.Error with { Evidence = captured.Error.Evidence.Add("seed-source-binding-changed") } : captured.Error);
        var capture = captured.Value;
        var bench = capture.Definitions.Benches.SingleOrDefault(value => value.Id == benchId);
        if (bench is null) return Result<NativePanelSeeds>.Fail(ErrorCode.NotFound, "The selected Bench is unavailable.");
        var guarded = await EditorOperationAsync(context, async token =>
        {
            var opened = await OpenEditorAsync(context, benchId, token).ConfigureAwait(false);
            if (!opened.IsSuccess) return Result<NativePanelSeeds>.Fail(opened.Error.Code == ErrorCode.Conflict
                ? opened.Error with { Evidence = opened.Error.Evidence.Add("seed-source-binding-changed") } : opened.Error);
            using var editor = opened.Value;
            var owned = await editor.ReadOwnedSeedsAsync(token).ConfigureAwait(false);
            if (!owned.IsSuccess) return Result<NativePanelSeeds>.Fail(owned.Error);
            var current = await definitions.ReadAsync(token).ConfigureAwait(false);
            if (!current.IsSuccess) return Result<NativePanelSeeds>.Fail(current.Error);
            if (current.Value.InstanceId != capture.Definitions.InstanceId
                || current.Value.Benches.SingleOrDefault(value => value.Id == benchId)?.Revision != bench.Revision)
                return Result<NativePanelSeeds>.Fail(ErrorCode.Conflict, "The Bench changed during seed observation.");
            var selection = new SourceSelection(context.Source.Id, context.Source.Binding,
                bench.Selectors.Where(value => value.Connection == context.Stamp.Connection).ToImmutableArray());
            var editable = owned.Value.Where(value => value.Ready && value.Publication is null && value.Draft is not null)
                .Select(value => (RetainedItem)value.Draft!).ToImmutableArray();
            var receipts = owned.Value.Where(value => !value.Ready || value.Publication is not null || value.Draft is null)
                .Select(value => value.Item).ToImmutableArray();
            var view = SourceProjection.CreateOwnedSeeds(new(bench.Id, bench.Name, bench.Revision, [selection]), selection,
                capture.Observation, editable, receipts, token);
            return Result<NativePanelSeeds>.Ok(new(capture,
                view.IsSuccess ? view.Value : SourceView.Failed(context.Source.Id, view.Error), owned.Value));
        }, ct).ConfigureAwait(false);
        return guarded.IsSuccess ? guarded.Value : Result<NativePanelSeeds>.Fail(guarded.Error);
    }
    public Task<Result<UserBenchSnapshot>> ListAsync(CancellationToken ct) => definitions.ReadAsync(ct);
    public async Task<Result<bool>> CheckInitialAsync(IReadOnlyDictionary<NativePanelContext, SourceObservation> observations, CancellationToken ct)
    {
        foreach (var (context, observation) in observations)
        {
            var checkedSource = await sources.CheckInitialAsync(context, observation, ct).ConfigureAwait(false);
            if (!checkedSource.IsSuccess) return Result<bool>.Fail(checkedSource.Error);
        }
        return Result<bool>.Ok(true);
    }
    public Task<Result<ImmutableArray<UserBenchWorkspace>>> WorkspacesAsync(CancellationToken ct) => definitions.WorkspacesAsync(ct);
    public Task<Result<bool>> RefreshMetadataAsync(UserBenchWorkspace workspace, DateTimeOffset now, CancellationToken ct)
        => sources.RefreshMetadataAsync(workspace, now, ct);
    public Task<Result<UserRepositoryBindings>> ReadRepositoryAsync(string root, CancellationToken ct)
        => definitions.ReadRepositoryAsync(root, ct);
    public static IEnumerable<Connection> RepositorySources(UserRepositoryBindings repository)
        => repository.DeclaredConnection is { } declared
            ? repository.Connections.Append(declared).Distinct() : repository.Connections;

    public static IEnumerable<Connection> Dependencies(NativeBench bench, UserRepositoryBindings? repository)
        => repository is not null && repository.AttachedBenches.Contains(bench.Id)
            ? bench.Connections.Union(RepositorySources(repository)) : bench.Connections;
    public const int ViewSourceLimit = 64;
    public Task<Result<UserRepositoryBindings>> SaveRepositoryAsync(string root, long revision,
        IEnumerable<Connection> connections, Guid? defaultBench, CancellationToken ct,
        Guid[]? attachedBenches = null, Connection[]? excludedConnections = null,
        Guid? expectedBenchId = null, long? expectedBenchRevision = null, UserBenchWorkspace? expectedWorkspace = null,
        Guid? operationId = null, string? operationDigest = null)
        => definitions.SaveRepositoryAsync(root, revision, connections, defaultBench, ct, attachedBenches, excludedConnections,
            expectedBenchId, expectedBenchRevision, expectedWorkspace, operationId, operationDigest);
    public async Task<Result<ImmutableArray<NativePanelContext>>> ResolveConnectionsAsync(Guid benchId,
        IEnumerable<NativePanelContext> retained, CancellationToken ct, IReadOnlySet<Connection>? available = null,
        bool cachedAdmissionOnly = false, UserRepositoryBindings? repository = null)
    {
        if (repository is not null) available = RepositorySources(repository).ToHashSet();
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        if (!shared.IsSuccess) return Result<ImmutableArray<NativePanelContext>>.Fail(shared.Error);
        var bench = shared.Value.Benches.SingleOrDefault(value => value.Id == benchId);
        if (bench is null) return Result<ImmutableArray<NativePanelContext>>.Fail(ErrorCode.NotFound, "The Bench was deleted.");
        var registry = await definitions.WorkspacesAsync(ct).ConfigureAwait(false);
        if (!registry.IsSuccess) return Result<ImmutableArray<NativePanelContext>>.Fail(registry.Error);
        var previous = retained.ToArray();
        using var concurrency = new SemaphoreSlim(8);
        var dependencies = Dependencies(bench, repository).Take(ViewSourceLimit);
        var resolved = await Task.WhenAll(dependencies.Select(async connection =>
        {
            if (available is not null && !available.Contains(connection)) return null;
            var admitted = previous.Where(value => value.Stamp.Connection == connection).ToArray();
            if (admitted.Length == 1) return admitted[0];
            var registered = registry.Value.Where(value => value.Connection == connection).ToArray();
            // Several workspace mirrors do not establish which one owns unpublished local drafts.
            if (registered.Length != 1) return null;
            await concurrency.WaitAsync(ct).ConfigureAwait(false);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var opened = await OpenAsync(registered[0].Root, budget.Token, cachedAdmissionOnly: cachedAdmissionOnly).ConfigureAwait(false);
                if (opened.IsSuccess && opened.Value.Stamp.Connection == connection
                    && opened.Value.Stamp.StoreInstance == registered[0].StoreInstance)
                    return opened.Value;
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            finally { concurrency.Release(); }
        })).ConfigureAwait(false);
        var current = await definitions.ReadAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<ImmutableArray<NativePanelContext>>.Fail(current.Error);
        if (current.Value.InstanceId != shared.Value.InstanceId
            || current.Value.Benches.SingleOrDefault(value => value.Id == benchId)?.Revision != bench.Revision)
            return Result<ImmutableArray<NativePanelContext>>.Fail(ErrorCode.Conflict, "The Bench changed during connection resolution. Read the current definition.");
        return Result<ImmutableArray<NativePanelContext>>.Ok(resolved.OfType<NativePanelContext>().ToImmutableArray());
    }
    public Task<Result<NativePlanStatus>> ReadPlanAsync(Guid benchId, CancellationToken ct) => definitions.ReadPlanStatusAsync(benchId, ct);
    public Task<Result<(NativePlanStatus Status, string Version)>> ReadStagedPlanVersionAsync(Guid benchId, CancellationToken ct)
        => definitions.ReadStagedPlanVersionAsync(benchId, ct);
    public Task<Result<ImmutableArray<RetainedItem>>> ReadPlanDraftsAsync(NativePlanStatus plan, NativePanelContext context, Guid scope, CancellationToken ct)
        => sources.NativeAsync(context.Root, token => definitions.ReadPlanDraftsAsync(plan.UserInstance, plan.BenchId,
            plan.Revision, scope, context.Root, context.Stamp, token), ct);
    public Task<Result<NativePlanStatus>> ChangePlanModeAsync(Guid instance, Guid benchId, long benchRevision,
        long planRevision, bool enabled, CancellationToken ct)
        => definitions.ChangePlanModeAsync(instance, benchId, benchRevision, planRevision, enabled, ct);
    public Task<Result<NativePlanReview>> OpenPlanReviewAsync(NativePanelContext anchor, string digest, CancellationToken ct)
        => sources.NativeAsync(anchor.Root, token => gateway.OpenPlanReviewAsync(anchor.Root, digest, token), ct);
    public Task<Result<NativePlanReview>> ReviewPlanAsync(NativePanelContext anchor, Guid benchId,
        ImmutableArray<NativePlanSource> admitted, IEnumerable<NativePanelContext> contexts, CancellationToken ct)
        => sources.NativeManyAsync(contexts.Select(context => context.Root),
            token => gateway.ReviewPlanAsync(anchor.Root, benchId, admitted, token), ct);
    public Task<Result<NativePlanOutcome>> ApplyPlanAsync(NativePlanReview review, IEnumerable<NativePanelContext> contexts,
        string expectedStagedVersion, CancellationToken ct)
        => review.StagedVersion != expectedStagedVersion
            ? Task.FromResult(Result<NativePlanOutcome>.Fail(ErrorCode.Conflict, "Staged Plan intent changed. Reload it before applying."))
            : sources.NativeManyAsync(contexts.Select(context => context.Root), review.ApplyAsync, ct);
    public Task<Result<NativePlanOutcome>> ContinuePlanDeliveryAsync(NativePlanReview attempt,
        IEnumerable<NativePanelContext> contexts, CancellationToken ct)
        => sources.NativeManyAsync(contexts.Select(context => context.Root), attempt.ContinueDeliveryAsync, ct);

    public async Task<Result<NativePanelContext>> OpenAsync(string root, CancellationToken ct, string? expectedBinding = null,
        IDictionary<NativePanelContext, SourceObservation>? initialObservations = null, bool cachedAdmissionOnly = false,
        NativeSourceStamp? expectedSource = null, UserBenchWorkspace? expectedWorkspace = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return Result<NativePanelContext>.Fail(ErrorCode.InvalidInput, "Choose an existing native connection or an initialized workspace in Advanced.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var local = await sources.NativeAsync(root, token => gateway.ReadAsync(root, expectedSource, false, token), ct).ConfigureAwait(false);
        if (!local.IsSuccess) return Result<NativePanelContext>.Fail(local.Error);
        if (expectedSource is not null && local.Value.Source != expectedSource)
            return Result<NativePanelContext>.Fail(ErrorCode.Conflict, "The prepared source or principal changed. Choose it again.");
        if (expectedWorkspace is not null && (local.Value.Source.Connection != expectedWorkspace.Connection
            || local.Value.Source.StoreInstance != expectedWorkspace.StoreInstance))
            return Result<NativePanelContext>.Fail(ErrorCode.Conflict, "The exact workspace registration changed before admission.");
        if (cachedAdmissionOnly)
            expectedBinding ??= CatalogFingerprint.Binding(NativeSourceAdapter.Binding(local.Value.Source));
        var observed = expectedBinding is null
            ? await sources.AdmitAsync(root, local.Value, ct).ConfigureAwait(false)
            : await sources.ReadCachedAdmissionAsync(root, local.Value, expectedBinding, ct,
                reuseObservation: initialObservations is not null).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<NativePanelContext>.Fail(observed.Error);
        var binding = NativeSourceAdapter.Binding(local.Value.Source);
        if (binding != observed.Value.Binding)
            return Result<NativePanelContext>.Fail(ErrorCode.Conflict, "The connection changed during selection; choose it again.");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)));
        var display = await NativeConnectionLabels.ReadAsync(root, local.Value.Source.Connection, ct).ConfigureAwait(false);
        var source = new RegisteredSource(new Guid(bytes.AsSpan(0, 16)), display.Name, root,
            binding, true, observed.Value.SchemaObservedAt);
        var context = new NativePanelContext(root, local.Value.Source, source) { LabelAvailable = display.Available };
        initialObservations?.Add(context, observed.Value);
        return Result<NativePanelContext>.Ok(context);
    }

    public async Task<Result<NativePanelCapture>> CaptureAsync(NativePanelContext context, CancellationToken ct)
    {
        var observed = await sources.ReadAsync(context.Source, ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<NativePanelCapture>.Fail(observed.Error);
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        return shared.IsSuccess ? Result<NativePanelCapture>.Ok(new(shared.Value, context, observed.Value))
            : Result<NativePanelCapture>.Fail(shared.Error);
    }

    public Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(NativePanelContext context, CancellationToken ct)
        => sources.DiscoverPlanningTeamsAsync(context.Source, context.Stamp, ct);

    public Task<Result<SourceObservation>> CheckHistoricalSourceAsync(NativePanelContext context, CancellationToken ct)
        => sources.ReadAsync(context.Source, ct);
    public Task<Result<bool>> CheckCaptureAsync(NativePanelCapture capture, CancellationToken ct)
        => sources.CheckWorkspaceAsync(capture.Context, capture.Observation, ct, recheckLocalSnapshot: false);
    public Task<Result<ImmutableArray<WorkItem>>> ObserveItemsAsync(NativePanelContext context, ImmutableArray<ItemKey> keys, CancellationToken ct)
        => sources.ObserveItemsAsync(context, keys, ct);
    public Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(NativePanelContext context,
        ImmutableArray<string> identities, CancellationToken ct)
        => sources.ReadIdentityProfilesAsync(context.Source, identities, ct);

    public async Task<Result<NativePanelCapture>> CapturePlanningAsync(NativePanelContext context, string? team, CancellationToken ct)
    {
        var observed = await sources.ReadPlanningAsync(context.Source, team, ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<NativePanelCapture>.Fail(observed.Error);
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        return shared.IsSuccess ? Result<NativePanelCapture>.Ok(new(shared.Value, context, observed.Value))
            : Result<NativePanelCapture>.Fail(shared.Error);
    }
    public async Task<Result<NativePanelCapture>> CapturePlanningChoicesAsync(NativePanelContext context, string? team, CancellationToken ct)
    {
        if (team is not null && (!Guid.TryParseExact(team, "D", out var identity) || identity == Guid.Empty
            || identity.ToString("D") != team))
            return Result<NativePanelCapture>.Fail(ErrorCode.InvalidInput, "Choose a canonical team from this connection.");
        var captured = await CaptureAsync(context, ct).ConfigureAwait(false);
        if (!captured.IsSuccess) return captured;
        var metadata = await definitions.ReadPlanningAsync(context.Stamp.Connection, team ?? "", ct).ConfigureAwait(false);
        if (!metadata.IsSuccess) return Result<NativePanelCapture>.Fail(metadata.Error);
        if (metadata.Value.Metadata is { } calendar && calendar.Team != team)
            return Result<NativePanelCapture>.Fail(ErrorCode.Conflict, "The retained calendar belongs to another team.");
        var observation = captured.Value.Observation;
        return Result<NativePanelCapture>.Ok(captured.Value with
        { Observation = observation with { Schema = observation.Schema with { Planning = metadata.Value.Metadata } } });
    }

    public async Task<Result<NativePanelView>> ReadViewAsync(Guid benchId, NativePanelContext? anchor,
        IEnumerable<NativePanelContext> chosenContexts, CancellationToken ct,
        IReadOnlyDictionary<NativePanelContext, SourceObservation>? initialObservations = null,
        IReadOnlySet<Connection>? available = null, UserRepositoryBindings? repository = null,
        bool includeSeedReferences = false)
    {
        if (repository is not null) available = RepositorySources(repository).ToHashSet();
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        if (!shared.IsSuccess) return Result<NativePanelView>.Fail(shared.Error);
        var bench = shared.Value.Benches.SingleOrDefault(value => value.Id == benchId);
        if (bench is null) return Result<NativePanelView>.Fail(ErrorCode.NotFound, "The selected Bench was deleted; its cached items and drafts were not removed.");
        var plan = await definitions.ReadPlanStatusAsync(benchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<NativePanelView>.Fail(plan.Error);
        var references = includeSeedReferences && !plan.Value.Enabled
            ? await definitions.ReadSeedReferencesAsync(benchId, ct).ConfigureAwait(false)
            : Result<ImmutableArray<BenchSeedReference>>.Ok([]);
        if (!references.IsSuccess) return Result<NativePanelView>.Fail(references.Error);
        var byOrigin = references.Value.ToLookup(reference => (reference.Connection, reference.StoreInstance),
            reference => reference.Item);
        var captures = new Lazy<Task<Result<ImmutableArray<NativeEditorCapture>>>>(
            () => definitions.ReadEditorCapturesAsync(ct, inspect: true));
        var contexts = chosenContexts.Where(context => context.Stamp.Connection != anchor?.Stamp.Connection)
            .GroupBy(context => context.Stamp.Connection).ToDictionary(group => group.Key, group => group.Last());
        if (anchor is not null) contexts[anchor.Stamp.Connection] = anchor;
        var selections = ImmutableArray.CreateBuilder<SourceSelection>();
        var views = ImmutableArray.CreateBuilder<SourceView>();
        var labels = ImmutableDictionary.CreateBuilder<Guid, string>();
        var normalDraftCounts = ImmutableDictionary.CreateBuilder<Guid, int>();
        var observations = new ConcurrentDictionary<NativePanelContext, SourceObservation>();
        using var concurrency = new SemaphoreSlim(8);
        var dependencies = Dependencies(bench, repository).Take(ViewSourceLimit);
        var results = await Task.WhenAll(dependencies.Select(ReadConnection)).ConfigureAwait(false);
        foreach (var acquired in results)
        {
            labels[acquired.View.SourceId] = acquired.Label;
            views.Add(acquired.View);
            if (acquired.Selection is { } selection) selections.Add(selection);
            if (acquired.Drafts != 0) normalDraftCounts[acquired.View.SourceId] = acquired.Drafts;
        }
        if (!references.Value.IsEmpty)
        {
            var shown = views.Where(view => view.Error is null && view.Observation is not null)
                .SelectMany(view => view.Items.Where(item => item.BenchMember && item.Identity.Key.IsLocal)
                    .Select(item => new BenchSeedReference(view.Observation!.Binding.Connection,
                        view.Observation.Binding.StoreInstance, item.Identity.Key))).ToHashSet();
            if (references.Value.Any(reference => !shown.Contains(reference)))
            {
                labels[Guid.Empty] = "Unavailable seed membership";
                views.Add(SourceView.Failed(Guid.Empty, new(ErrorCode.Unavailable,
                    "Referenced seed membership is unavailable in this workspace.")));
            }
        }

        async Task<(SourceSelection? Selection, SourceView View, string Label, int Drafts)> ReadConnection(Connection connection)
        {
            contexts.TryGetValue(connection, out var context);
            var sourceId = context?.Source.Id ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("\n", connection.Provider.Value, connection.ResourceScope, connection.Destination, connection.IdentityRef))).AsSpan(0, 16));
            var label = context?.Source.Label ?? NativeConnectionLabels.Unavailable(connection).Name;
            (SourceSelection?, SourceView, string, int) Failed(Error error)
                => (null, SourceView.Failed(sourceId, error) with { RequiredConnection = connection }, label, 0);
            if (available is not null && !available.Contains(connection))
                return Failed(new(ErrorCode.PolicyViolation,
                    "This saved Bench dependency is not available in the selected repository. Its rules and drafts were retained; assign the connection in this user's repository bindings to use it here."));
            if (context is null)
                return Failed(new(ErrorCode.Unavailable,
                    "This Bench's connection could not be resolved to one available registered source. Membership is unknown; operations requiring it are unavailable."));
            await concurrency.WaitAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var observed = initialObservations is not null && initialObservations.TryGetValue(context, out var initial)
                    ? await sources.CheckInitialAsync(context, initial, timeout.Token).ConfigureAwait(false)
                    : await sources.ReadAsync(context.Source, timeout.Token).ConfigureAwait(false);
                if (!observed.IsSuccess) return Failed(observed.Error);
                observations[context] = observed.Value;
                var intent = bench.Selectors.Where(selector => selector.Connection == connection).ToImmutableArray();
                var observation = observed.Value;
                var normalDebt = 0;
                if (includeSeedReferences && !plan.Value.Enabled && observation.Snapshot.RetainedItems.Any(draft => draft is Seed))
                {
                    // A local key (even on the same connection) is not evidence of the origin store.
                    // Never let a tentative mapping turn this read-only membership into a provider pin.
                    var store = observation.Snapshot;
                    var originKeys = byOrigin[(connection, observation.Binding.StoreInstance)].ToHashSet();
                    originKeys.ExceptWith(store.Mappings.Where(mapping => observation.WithdrawnItems.Contains(mapping.Remote))
                        .Select(mapping => mapping.Local));
                    var eligible = store.RetainedItems.OfType<Seed>().Select(seed => seed.Key).Where(originKeys.Contains).ToHashSet();
                    var retained = eligible.ToHashSet();
                    // Keep existing creation-owned rows, independently of explicit memberships.
                    var ownership = await captures.Value.ConfigureAwait(false);
                    if (!ownership.IsSuccess) return Failed(ownership.Error);
                    foreach (var capture in ownership.Value.Where(capture => capture.UserInstance == shared.Value.InstanceId
                        && capture.BenchId == benchId && !capture.PlanMode && !capture.Cancelled && capture.ScopeId == Guid.Empty
                        && capture.Origin == context.Stamp && capture.StoreInstance == context.Stamp.StoreInstance
                        && Twig.Infrastructure.Persistence.NativePathComparer.Instance.Equals(capture.Root, context.Root)))
                        if (UserBenchStore.ParseIntent(capture.IntentJson).Operations is [{ Kind: OpKind.Create } op])
                            retained.Add(op.Item);
                    // Already mapped draft debt belongs to its ordinary provider row, not to a local member.
                    if (references.Value.IsEmpty) retained.UnionWith(store.Mappings.Select(mapping => mapping.Local));
                    bool VisibleEndpoint(ItemKey key) => !key.IsLocal || retained.Contains(key);
                    observation = observation with { Snapshot = new(store.Version, store.Items,
                        store.Relationships.Where(edge => VisibleEndpoint(edge.From) && VisibleEndpoint(edge.To)),
                        store.RetainedItems.Where(draft => draft is not Seed || retained.Contains(draft.Key)),
                        store.Plans, store.Journal, store.Mappings.Where(mapping => !eligible.Contains(mapping.Local)), store.Benches) };
                    intent = intent.AddRange(eligible.Select(key => new Selector(SelectorKind.Pin, key, connection: connection)));
                }
                if (plan.Value.Enabled)
                {
                    normalDebt = observation.Snapshot.RetainedItems.Length;
                    var scope = plan.Value.Sources.SingleOrDefault(value => value.Source.Connection == connection);
                    var drafts = scope is null ? Result<ImmutableArray<RetainedItem>>.Ok([])
                        : await definitions.ReadPlanDraftsAsync(plan.Value.UserInstance, benchId, plan.Value.Revision,
                            scope.Id, context.Root, context.Stamp, timeout.Token).ConfigureAwait(false);
                    if (!drafts.IsSuccess) return Failed(drafts.Error);
                    var baselines = drafts.Value.OfType<LocalEdits>().Select(edits => edits.EditBaseline).ToArray();
                    observation = observation with { Snapshot = new(observation.Snapshot.Version,
                        observation.Snapshot.Items.Concat(baselines.Where(baseline =>
                            !observation.Snapshot.Items.Any(item => item.Key == baseline.Key))),
                        observation.Snapshot.Relationships, drafts.Value, observation.Snapshot.Plans,
                        observation.Snapshot.Journal, observation.Snapshot.Mappings, observation.Snapshot.Benches) };
                    intent = intent.AddRange(drafts.Value.Where(draft => draft.Key.IsLocal)
                        .Select(draft => new Selector(SelectorKind.Pin, draft.Key, connection: connection)));
                }
                var selection = new SourceSelection(context.Source.Id, context.Source.Binding, intent);
                var definition = new BenchDefinition(bench.Id, bench.Name, bench.Revision, [selection]);
                var projected = SourceProjection.Create(definition, selection, observation, timeout.Token);
                if (projected.IsSuccess)
                    return (selection, normalDebt == 0 ? projected.Value : projected.Value with
                    {
                        Limitations = projected.Value.Limitations.Add(
                            $"{normalDebt} normal-mode draft(s) remain separate from this Plan. They are retained in protected draft browse and cannot be published by Plan Apply.")
                    }, label, normalDebt);
                if (!references.Value.IsEmpty) return Failed(projected.Error);
                // Membership failure must not discard this admitted context's protected draft evidence.
                var empty = new SourceSelection(context.Source.Id, context.Source.Binding, []);
                var protectedView = SourceProjection.Create(new(bench.Id, bench.Name, bench.Revision, [empty]), empty,
                    observation with { WorkspaceExclusions = [] }, timeout.Token);
                return (selection, protectedView.IsSuccess ? protectedView.Value with
                {
                    Error = projected.Error,
                    Limitations = protectedView.Value.Limitations.Add("Bench membership is unavailable. These admitted local drafts remain outside the view; exclusion status is not inferred.")
                } : SourceView.Failed(context.Source.Id, projected.Error), label, normalDebt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failed(new(ErrorCode.Unavailable, "This connection exceeded its independent read budget; membership is unknown. Other connections were read separately."));
            }
            finally { concurrency.Release(); }
        }
        var current = await definitions.ReadAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<NativePanelView>.Fail(current.Error);
        if (current.Value.InstanceId != shared.Value.InstanceId
            || current.Value.Benches.SingleOrDefault(value => value.Id == benchId)?.Revision != bench.Revision)
            return Result<NativePanelView>.Fail(new Error(ErrorCode.Conflict, "The shared Bench changed during acquisition.")
                { Evidence = ["bench-definition-changed"] });
        var currentPlan = await definitions.ReadPlanStatusAsync(benchId, ct).ConfigureAwait(false);
        if (!currentPlan.IsSuccess) return Result<NativePanelView>.Fail(currentPlan.Error);
        if (currentPlan.Value.PlanId != plan.Value.PlanId || currentPlan.Value.Revision != plan.Value.Revision)
            return Result<NativePanelView>.Fail(new Error(ErrorCode.Conflict, "The Plan mode or scope changed during acquisition.")
                { Evidence = ["bench-definition-changed"] });
        var projectedDefinition = new BenchDefinition(bench.Id, bench.Name, bench.Revision, selections);
        var result = new BenchView(projectedDefinition, shared.Value.InstanceId, current.Value.Revision,
            views.Select(view => view with { RequiredConnection = view.RequiredConnection
                ?? contexts.Values.SingleOrDefault(context => context.Source.Id == view.SourceId)?.Stamp.Connection }).ToImmutableArray());
        return Result<NativePanelView>.Ok(new(result, labels.ToImmutable(), plan.Value)
        { NormalDraftCounts = normalDraftCounts.ToImmutable(), Observations = observations.ToImmutableDictionary() });
    }

    public async Task<Result<bool>> CheckWorkspaceAsync(NativePanelView view, CancellationToken ct,
        bool recheckLocalSnapshot = true)
    {
        foreach (var (context, observation) in view.Observations)
        {
            var checkedSource = await sources.CheckWorkspaceAsync(context, observation, ct, recheckLocalSnapshot).ConfigureAwait(false);
            if (!checkedSource.IsSuccess) return Result<bool>.Fail(checkedSource.Error);
        }
        var plan = await definitions.ReadPlanStatusAsync(view.View.Definition.Id, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<bool>.Fail(plan.Error);
        return plan.Value.PlanId == view.Plan.PlanId && plan.Value.Revision == view.Plan.Revision
            ? Result<bool>.Ok(true)
            : Result<bool>.Fail(ErrorCode.Conflict, "The Plan changed during workspace loading. Read the workspace again.");
    }

    public async Task<Result<NativeBench>> SaveAsync(NativeBench definition, long expectedRevision, CancellationToken ct)
    {
        var gate = definitionGates.GetOrAdd(definition.Id, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await definitions.SaveAsync(definition, expectedRevision, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeBench>> AddAsync(Guid id, ImmutableArray<Selector> selectors, CancellationToken ct)
    {
        var gate = definitionGates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = await definitions.ReadAsync(ct).ConfigureAwait(false);
            if (!stored.IsSuccess) return Result<NativeBench>.Fail(stored.Error);
            var current = stored.Value.Benches.SingleOrDefault(value => value.Id == id);
            if (current is null) return Result<NativeBench>.Fail(ErrorCode.NotFound, "The Bench was deleted.");
            if (selectors.IsDefaultOrEmpty || selectors.Any(selector => selector.Connection is null))
                return Result<NativeBench>.Fail(ErrorCode.InvalidInput, "Choose source-qualified work to add.");
            var existing = current.Selectors.ToBuilder();
            var known = current.Selectors.Select(selector => BenchCodec.SerializeSelectors([selector])).ToHashSet(StringComparer.Ordinal);
            foreach (var selector in selectors)
                if (known.Add(BenchCodec.SerializeSelectors([selector]))) existing.Add(selector);
            if (existing.Count == current.Selectors.Length) return Result<NativeBench>.Ok(current);
            return await definitions.SaveAsync(new(id, current.Name, existing, connections:
                current.Connections.Union(selectors.Select(selector => selector.Connection!))), current.Revision, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, long revision, CancellationToken ct)
    {
        var gate = definitionGates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await definitions.DeleteAsync(id, revision, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public Task<Result<NativeSourceObservation>> ReadAdmittedSourceAsync(NativePanelContext context, CancellationToken ct)
        => sources.NativeAsync(context.Root, token => gateway.ReadAsync(context.Root, context.Stamp, false, token), ct);
    public Task<Result<SourceObservation>> RecheckAsync(NativePanelContext context, CancellationToken ct)
        => sources.RecheckAsync(context.Source, ct);
    public Task<Result<SourceObservation>> RefreshPlanningAsync(NativePanelContext context, CancellationToken ct)
        => sources.RefreshPlanningAsync(context.Source, context.Stamp, ct);
    public Task<Result<SourceObservation>> RefreshPlanningAsync(NativePanelContext context, string? team, CancellationToken ct)
        => sources.RefreshPlanningAsync(context.Source, context.Stamp, team, ct);
    public Task<Result<SourceObservation>> DiscoverPlanningSourcesAsync(NativePanelContext context, CancellationToken ct)
        => sources.DiscoverPlanningSourcesAsync(context.Source, context.Stamp, ct);
    public Task<Result<SourceObservation>> DiscoverPlanningSourcesAsync(NativePanelContext context, string? team, CancellationToken ct)
        => sources.DiscoverPlanningSourcesAsync(context.Source, context.Stamp, team, ct);
    public Task<Result<SourceObservation>> ReadPlanningSourcesAsync(NativePanelContext context, string? team, CancellationToken ct)
        => sources.ReadPlanningSourcesAsync(context.Source, team, ct);

    public async Task<Result<NativePanelCapture>> BrowsePlanningAsync(NativePanelContext context, PlanningSourceKind kind,
        string id, string? team, CancellationToken ct)
    {
        var observed = await sources.BrowsePlanningAsync(context.Source, context.Stamp, team, kind, id, ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<NativePanelCapture>.Fail(observed.Error);
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        return shared.IsSuccess ? Result<NativePanelCapture>.Ok(new(shared.Value, context, observed.Value))
            : Result<NativePanelCapture>.Fail(shared.Error);
    }

    public static Result<BenchView> ProtectedDrafts(NativePanelCapture capture)
    {
        var source = capture.Context.Source;
        var selection = new SourceSelection(source.Id, source.Binding, []);
        var definition = new BenchDefinition(source.Id, "Protected drafts", 1, [selection]);
        var projected = SourceProjection.Create(definition, selection, capture.Observation);
        return projected.IsSuccess ? Result<BenchView>.Ok(new(definition, capture.Definitions.InstanceId,
            capture.Definitions.Revision, [projected.Value])) : Result<BenchView>.Fail(projected.Error);
    }
}
