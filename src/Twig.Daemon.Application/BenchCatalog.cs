using System.Collections.Immutable;
using Bench.Domain;
using Twig.Core;

namespace Bench.Application;

public sealed class BenchCatalog(ICatalogStore catalog, ISourceReader sources, CatalogChanges changes,
    TimeSpan? viewBudget = null, TimeSpan? sourceBudget = null)
{
    public Task<Result<CatalogSnapshot>> ListAsync(CancellationToken ct) => catalog.ReadAsync(ct);

    public async Task<Result<long>> RegisterAsync(Guid id, string label, string nativeLocator,
        bool allowOffline, Guid expectedCatalogId, long expectedRevision, CancellationToken ct)
    {
        try { Rules.Id(id); Rules.Id(expectedCatalogId); Rules.Text(label, 120); Rules.Text(nativeLocator, 4096); }
        catch (ArgumentException) { return Result<long>.Fail(ErrorCode.InvalidInput, "Invalid source registration input."); }
        if (expectedRevision < 0) return Result<long>.Fail(ErrorCode.InvalidInput, "A catalog revision is required.");
        var admission = await sources.AdmitAsync(nativeLocator, ct).ConfigureAwait(false);
        if (!admission.IsSuccess) return Result<long>.Fail(admission.Error);
        if (admission.Value.Mode != ObservationMode.OnlineMetadata || !Valid(admission.Value))
            return Result<long>.Fail(ErrorCode.Unavailable, "Registration requires current online native admission.");
        var source = new RegisteredSource(id, label, nativeLocator, admission.Value.Binding,
            allowOffline, admission.Value.SchemaObservedAt);
        var result = await catalog.RegisterAsync(source, expectedCatalogId, expectedRevision, ct).ConfigureAwait(false);
        if (result.IsSuccess) changes.Publish(result.Value);
        return result;
    }

    // Preview freezes exact selector content and binding stamps, not a mutable native-Bench alias.
    public async Task<Result<CreateBenchCommand>> PrepareCreateAsync(Guid id, string name, Guid expectedCatalogId, long expectedRevision,
        ImmutableArray<NativeBenchSeed> seeds, CancellationToken ct)
    {
        try { Rules.Id(id); Rules.Id(expectedCatalogId); Rules.Name(name); }
        catch (ArgumentException) { return Result<CreateBenchCommand>.Fail(ErrorCode.InvalidInput, "Invalid Bench identity or name."); }
        if (expectedRevision < 0 || seeds.IsDefault || seeds.Length > 64
            || seeds.Any(s => s is null || s.SourceId == Guid.Empty || s.NativeBenchId == Guid.Empty)
            || seeds.Select(s => s.SourceId).Distinct().Count() != seeds.Length)
            return Result<CreateBenchCommand>.Fail(ErrorCode.InvalidInput, "Select at most one native Bench per source.");
        var current = await catalog.ReadAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CreateBenchCommand>.Fail(current.Error);
        if (current.Value.Id != expectedCatalogId || current.Value.Revision != expectedRevision)
            return Result<CreateBenchCommand>.Fail(ErrorCode.Conflict, "The catalog changed before the copy preview.");
        var selections = ImmutableArray.CreateBuilder<SourceSelection>();
        foreach (var seed in seeds)
        {
            var source = current.Value.Sources.SingleOrDefault(s => s.Id == seed.SourceId);
            if (source is null) return Result<CreateBenchCommand>.Fail(ErrorCode.NotFound, "A selected source is not registered.");
            var observed = await sources.ReadAsync(source, ct).ConfigureAwait(false);
            if (!observed.IsSuccess) return Result<CreateBenchCommand>.Fail(observed.Error);
            if (!Admitted(source, observed.Value))
                return Result<CreateBenchCommand>.Fail(ErrorCode.Conflict, "Source admission changed; no selectors were copied.");
            var native = observed.Value.Snapshot.Benches.SingleOrDefault(b => b.Id == seed.NativeBenchId);
            if (native is null) return Result<CreateBenchCommand>.Fail(ErrorCode.NotFound, "The selected native Bench is unavailable.");
            selections.Add(new(source.Id, source.Binding, native.Selectors));
        }
        try { return Result<CreateBenchCommand>.Ok(new(id, name, expectedCatalogId, expectedRevision, selections)); }
        catch (ArgumentException) { return Result<CreateBenchCommand>.Fail(ErrorCode.InvalidInput, "Copied selectors exceed the catalog contract."); }
    }

    public async Task<Result<CreationReceipt>> CreateAsync(CreateBenchCommand command, CancellationToken ct)
    {
        var admitted = await ValidateSources(command.ExpectedCatalogId, command.Definition.Sources, ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) return Result<CreationReceipt>.Fail(admitted.Error);
        var result = await catalog.CreateAsync(command, ct).ConfigureAwait(false);
        if (!result.IsSuccess) return Result<CreationReceipt>.Fail(result.Error);
        if (result.Value.Committed) changes.Publish(result.Value.Receipt.CatalogRevision);
        return Result<CreationReceipt>.Ok(result.Value.Receipt);
    }

    public async Task<Result<CreationReceipt>> FindCreationAsync(Guid catalogId, Guid creationId, CancellationToken ct)
    {
        var receipt = await catalog.FindCreationAsync(catalogId, creationId, ct).ConfigureAwait(false);
        if (!receipt.IsSuccess) return receipt;
        var admitted = await ValidateSources(catalogId, receipt.Value.Definition.Sources, ct).ConfigureAwait(false);
        return admitted.IsSuccess ? receipt : Result<CreationReceipt>.Fail(admitted.Error);
    }

    private async Task<Result<bool>> ValidateSources(Guid catalogId, IEnumerable<SourceSelection> selections, CancellationToken ct)
    {
        var current = await catalog.ReadAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        if (current.Value.Id != catalogId)
            return Result<bool>.Fail(ErrorCode.Conflict, "The catalog identity changed; no creation or receipt was disclosed.");
        foreach (var selection in selections)
        {
            var source = current.Value.Sources.SingleOrDefault(s => s.Id == selection.SourceId);
            if (source is null || source.Binding != selection.Binding)
                return Result<bool>.Fail(ErrorCode.Conflict, "A frozen source is no longer registered.");
            var observed = await sources.ReadAsync(source, ct).ConfigureAwait(false);
            if (!observed.IsSuccess) return Result<bool>.Fail(observed.Error);
            if (!Admitted(source, observed.Value))
                return Result<bool>.Fail(ErrorCode.Conflict, "Source admission changed; no creation or receipt was disclosed.");
        }
        var final = await catalog.ReadAsync(ct).ConfigureAwait(false);
        return !final.IsSuccess ? Result<bool>.Fail(final.Error)
            : final.Value.Id == catalogId ? Result<bool>.Ok(true)
            : Result<bool>.Fail(ErrorCode.Conflict, "Catalog replaced during source validation.");
    }

    public async Task<Result<BenchView>> ReadAsync(Guid benchId, CancellationToken ct, string? expectedDefinitionFingerprint = null)
    {
        var read = await catalog.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<BenchView>.Fail(read.Error);
        var bench = read.Value.Benches.SingleOrDefault(b => b.Id == benchId);
        if (bench is null) return Result<BenchView>.Fail(ErrorCode.NotFound, "The catalog Bench does not exist.");
        if (expectedDefinitionFingerprint is not null && CatalogFingerprint.Definition(bench) != expectedDefinitionFingerprint)
            return Result<BenchView>.Fail(ErrorCode.Conflict, "The captured Bench definition changed; no source was acquired.");
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(viewBudget ?? TimeSpan.FromSeconds(20));
        var projections = ImmutableArray.CreateBuilder<SourceView>();
        foreach (var selection in bench.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (overall.IsCancellationRequested)
            {
                projections.Add(SourceView.Failed(selection.SourceId, new(ErrorCode.Unavailable, "The overall view budget expired before this source could be read.")));
                continue;
            }
            var source = read.Value.Sources.SingleOrDefault(s => s.Id == selection.SourceId);
            if (source is null || source.Binding != selection.Binding)
            {
                projections.Add(SourceView.Failed(selection.SourceId, new(ErrorCode.Conflict, "The selected source binding is no longer admitted.")));
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            timeout.CancelAfter(sourceBudget ?? TimeSpan.FromSeconds(8));
            Result<SourceObservation> observed;
            try { observed = await sources.ReadAsync(source, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                projections.Add(SourceView.Failed(source.Id, new(ErrorCode.Unavailable, "Source read budget expired; membership is unknown.")));
                continue;
            }
            if (!observed.IsSuccess) { projections.Add(SourceView.Failed(source.Id, observed.Error)); continue; }
            if (!Admitted(source, observed.Value))
            {
                projections.Add(SourceView.Failed(source.Id, new(ErrorCode.Conflict, "Source identity or offline admission changed.")));
                continue;
            }
            try
            {
                var projected = SourceProjection.Create(bench, selection, observed.Value, timeout.Token);
                projections.Add(projected.IsSuccess ? projected.Value : SourceView.Failed(source.Id, projected.Error));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                projections.Add(SourceView.Failed(source.Id, new(ErrorCode.Unavailable, "Source projection budget expired; membership is unknown.")));
            }
        }
        return Result<BenchView>.Ok(new(bench, read.Value.Id, read.Value.Revision, projections.ToImmutable()));
    }

    private static bool Valid(SourceObservation observation)
        => Enum.IsDefined(observation.Mode) && observation.Schema.Connection == observation.Binding.Connection
            && observation.CacheReadAt != default && observation.SchemaObservedAt != default;
    private static bool Admitted(RegisteredSource source, SourceObservation observation)
        => Valid(observation) && observation.Binding == source.Binding
            && (observation.Mode is ObservationMode.OnlineMetadata or ObservationMode.OnlineValidatedCache || source.AllowOffline);
}
