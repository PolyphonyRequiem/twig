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
                .Select(value => value.Draft!).ToImmutableArray();
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
    public Task<Result<NativePlanStatus>> ReadPlanAsync(Guid benchId, CancellationToken ct) => definitions.ReadPlanStatusAsync(benchId, ct);
    public Task<Result<ImmutableArray<Draft>>> ReadPlanDraftsAsync(NativePlanStatus plan, NativePanelContext context, Guid scope, CancellationToken ct)
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
        string digest, bool deliveryOnly, CancellationToken ct)
        => sources.NativeManyAsync(contexts.Select(context => context.Root), token => deliveryOnly
            ? review.ContinueDeliveryAsync(digest, token) : review.ApplyAsync(digest, token), ct);

    public async Task<Result<NativePanelContext>> OpenAsync(string root, CancellationToken ct, string? expectedBinding = null,
        IDictionary<NativePanelContext, SourceObservation>? initialObservations = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return Result<NativePanelContext>.Fail(ErrorCode.InvalidInput, "Choose an existing native connection or an initialized workspace in Advanced.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var local = await sources.NativeAsync(root, token => gateway.ReadAsync(root, null, false, token), ct).ConfigureAwait(false);
        if (!local.IsSuccess) return Result<NativePanelContext>.Fail(local.Error);
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

    public async Task<Result<NativePanelCapture>> CaptureAsync(NativePanelContext context, CancellationToken ct, bool cachedGuidance = false)
    {
        var observed = cachedGuidance
            ? await sources.ReadAsync(context.Source, ct).ConfigureAwait(false)
            : await sources.ReadOnlineAsync(context.Source, ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<NativePanelCapture>.Fail(observed.Error);
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        return shared.IsSuccess ? Result<NativePanelCapture>.Ok(new(shared.Value, context, observed.Value))
            : Result<NativePanelCapture>.Fail(shared.Error);
    }

    public Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(NativePanelContext context, CancellationToken ct)
        => sources.DiscoverPlanningTeamsAsync(context.Source, context.Stamp, ct);

    public Task<Result<SourceObservation>> ValidateDisplayAccessAsync(NativePanelContext context, CancellationToken ct)
        => sources.ReadOnlineAsync(context.Source, ct);
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

    public async Task<Result<NativePanelView>> ReadViewAsync(Guid benchId, NativePanelContext anchor,
        IEnumerable<NativePanelContext> chosenContexts, CancellationToken ct,
        IReadOnlyDictionary<NativePanelContext, SourceObservation>? initialObservations = null)
    {
        var shared = await definitions.ReadAsync(ct).ConfigureAwait(false);
        if (!shared.IsSuccess) return Result<NativePanelView>.Fail(shared.Error);
        var bench = shared.Value.Benches.SingleOrDefault(value => value.Id == benchId);
        if (bench is null) return Result<NativePanelView>.Fail(ErrorCode.NotFound, "The selected Bench was deleted; its cached items and drafts were not removed.");
        var plan = await definitions.ReadPlanStatusAsync(benchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<NativePanelView>.Fail(plan.Error);
        var contexts = chosenContexts.Where(context => context.Stamp.Connection != anchor.Stamp.Connection)
            .GroupBy(context => context.Stamp.Connection).ToDictionary(group => group.Key, group => group.Last());
        contexts[anchor.Stamp.Connection] = anchor;
        var connections = bench.Selectors.Select(selector => selector.Connection!).Prepend(anchor.Stamp.Connection).Distinct().ToArray();
        var selections = ImmutableArray.CreateBuilder<SourceSelection>();
        var views = ImmutableArray.CreateBuilder<SourceView>();
        var labels = ImmutableDictionary.CreateBuilder<Guid, string>();
        var normalDraftCounts = ImmutableDictionary.CreateBuilder<Guid, int>();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(30));
        foreach (var connection in connections)
        {
            ct.ThrowIfCancellationRequested();
            if (!contexts.TryGetValue(connection, out var context))
            {
                var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join("\n", connection.Provider.Value, connection.ResourceScope, connection.Destination, connection.IdentityRef))).AsSpan(0, 16));
                labels[identity] = NativeConnectionLabels.Unavailable(connection).Name;
                views.Add(SourceView.Failed(identity, new(ErrorCode.Unavailable,
                    "Choose this connection for Add items in Connections. No workspace association was selected automatically; membership is unknown.")));
                continue;
            }
            labels[context.Source.Id] = context.Source.Label;
            if (overall.IsCancellationRequested)
            {
                views.Add(SourceView.Failed(context.Source.Id, new(ErrorCode.Unavailable, "The view budget expired before this context could be read.")));
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            Result<SourceObservation> observed;
            try { observed = initialObservations is not null && initialObservations.TryGetValue(context, out var initial)
                    ? await sources.CheckInitialAsync(context, initial, timeout.Token).ConfigureAwait(false)
                    : await sources.ReadAsync(context.Source, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                views.Add(SourceView.Failed(context.Source.Id, new(ErrorCode.Unavailable, "Native context read exceeded the view budget; membership is unknown.")));
                continue;
            }
            if (!observed.IsSuccess)
            {
                if (context == anchor) return Result<NativePanelView>.Fail(observed.Error);
                views.Add(SourceView.Failed(context.Source.Id, observed.Error));
                continue;
            }
            var intent = bench.Selectors.Where(selector => selector.Connection == connection).ToImmutableArray();
            var observation = observed.Value;
            var normalDebt = 0;
            if (plan.Value.Enabled)
            {
                normalDebt = observation.Snapshot.Drafts.Length;
                normalDraftCounts[context.Source.Id] = normalDebt;
                var scope = plan.Value.Sources.SingleOrDefault(value => value.Source.Connection == connection);
                var drafts = scope is null ? Result<ImmutableArray<Draft>>.Ok([])
                    : await definitions.ReadPlanDraftsAsync(plan.Value.UserInstance, benchId, plan.Value.Revision,
                        scope.Id, context.Root, context.Stamp, timeout.Token).ConfigureAwait(false);
                if (!drafts.IsSuccess)
                {
                    if (context == anchor) return Result<NativePanelView>.Fail(drafts.Error);
                    views.Add(SourceView.Failed(context.Source.Id, drafts.Error)); continue;
                }
                var baselines = drafts.Value.Where(draft => draft.Baseline is not null).Select(draft => draft.Baseline!).ToArray();
                observation = observation with { Snapshot = new(observation.Snapshot.Version,
                    observation.Snapshot.Items.Concat(baselines.Where(baseline =>
                        !observation.Snapshot.Items.Any(item => item.Key == baseline.Key))),
                    observation.Snapshot.Relationships, drafts.Value, observation.Snapshot.Proposals,
                    observation.Snapshot.Journal, observation.Snapshot.Mappings, observation.Snapshot.Benches) };
                intent = intent.AddRange(drafts.Value.Where(draft => draft.Key.IsLocal)
                    .Select(draft => new Selector(SelectorKind.Pin, draft.Key, connection: connection)));
            }
            var selection = new SourceSelection(context.Source.Id, context.Source.Binding, intent);
            selections.Add(selection);
            if (selections.Count > 64)
                return Result<NativePanelView>.Fail(ErrorCode.Partial, "The view exceeds its context projection budget. All native Bench rules were retained.");
            var definition = new BenchDefinition(bench.Id, bench.Name, bench.Revision, [selection]);
            Result<SourceView> projected;
            try { projected = SourceProjection.Create(definition, selection, observation, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                views.Add(SourceView.Failed(context.Source.Id, new(ErrorCode.Unavailable, "Context projection exceeded its budget; no empty membership was inferred.")));
                continue;
            }
            if (projected.IsSuccess)
            {
                views.Add(normalDebt == 0 ? projected.Value : projected.Value with { Limitations = projected.Value.Limitations.Add(
                    $"{normalDebt} normal-mode draft(s) remain separate from this Plan. They are retained in protected draft browse and cannot be published by Plan Apply.") });
                continue;
            }
            // Membership failure must not discard this admitted context's protected draft evidence.
            var empty = new SourceSelection(context.Source.Id, context.Source.Binding, []);
            var protectedView = SourceProjection.Create(new(bench.Id, bench.Name, bench.Revision, [empty]), empty,
                observation with { WorkspaceExclusions = [] }, ct);
            views.Add(protectedView.IsSuccess ? protectedView.Value with
            {
                Error = projected.Error,
                Limitations = protectedView.Value.Limitations.Add("Bench membership is unavailable. These admitted local drafts remain outside the view; exclusion status is not inferred.")
            } : SourceView.Failed(context.Source.Id, projected.Error));
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
        var result = new BenchView(projectedDefinition, shared.Value.InstanceId, current.Value.Revision, views.ToImmutable());
        return Result<NativePanelView>.Ok(new(result, labels.ToImmutable(), plan.Value) { NormalDraftCounts = normalDraftCounts.ToImmutable() });
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
            return await definitions.SaveAsync(new(id, current.Name, existing), current.Revision, ct).ConfigureAwait(false);
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

    public Task<Result<NativeSourceObservation>> SelectAsync(NativePanelContext context, Guid? expected, Guid? next, CancellationToken ct)
        => sources.NativeAsync(context.Root, token => gateway.SelectBenchAsync(context.Root, context.Stamp, expected, next, token), ct);

    public Task<Result<NativeSourceObservation>> ReadSelectionAsync(NativePanelContext context, CancellationToken ct)
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
