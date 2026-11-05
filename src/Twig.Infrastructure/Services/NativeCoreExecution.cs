using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Store.Sqlite;
using Twig.Infrastructure.GitHub;

namespace Twig.Infrastructure.Services;

internal sealed record NativeCoreStoreReference(Guid Instance, string DatabasePath, string Root, Connection Connection);
internal sealed record NativeCoreGate(Guid Instance, long Revision, bool Active, ImmutableArray<NativeCoreStoreReference> Sources);
internal sealed record NativeCoreInventory(Guid Instance, long Revision, string Digest,
    ImmutableArray<NativeCoreStoreReference> Sources, ImmutableArray<long> Versions,
    ImmutableArray<string> ExplicitRoots, ImmutableArray<NativeCoreStoreReference> ExplicitStores)
{
    public string CaptureDigest { get; init; } = "";
    internal InitialAuthoringObservation? InitialAuthoring { get; init; }
}

internal static class NativeCoreExecution
{
    internal static async Task<Result<bool>> RegisterAsync(string registryPath, NativeCoreStoreReference source, CancellationToken ct)
    {
        var read = await InspectAsync(source, ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
        using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
        var registered = await registry.RegisterCoreStoreAsync(source, ct).ConfigureAwait(false);
        return registered.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(registered.Error);
    }

    internal static async Task<Result<bool>> PublishingAsync(string registryPath, CancellationToken ct)
    {
        using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
        var gate = await registry.ReadCoreGateAsync(ct).ConfigureAwait(false);
        if (!gate.IsSuccess) return Result<bool>.Fail(gate.Error);
        var captures = await registry.CapturePublishingAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<bool>.Fail(captures.Error);
        return gate.Value.Active ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.PolicyViolation,
            "The one-time native publishing safety check is pending. Reads and protected draft browse remain available.");
    }

    internal static async Task<Result<NativeCoreInventory>> InventoryAsync(string registryPath,
        IEnumerable<string>? explicitRoots = null, IEnumerable<NativeCoreStoreReference>? explicitStores = null,
        CancellationToken ct = default, InitialAuthoringObservation? initialAuthoring = null)
    {
        try
        {
            var suppliedRoots = (explicitRoots ?? []).ToImmutableArray();
            var suppliedStores = (explicitStores ?? []).ToImmutableArray();
            using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
            var roots = await registry.CoreInventoryRootsAsync(ct).ConfigureAwait(false);
            if (!roots.IsSuccess) return Result<NativeCoreInventory>.Fail(roots.Error);
            var benches = UserBenchStore.ForRegistry(registryPath);
            var workspaces = await benches.WorkspacesAsync(ct).ConfigureAwait(false);
            if (!workspaces.IsSuccess) return Result<NativeCoreInventory>.Fail(workspaces.Error);
            var scopes = await benches.CorePlanReferencesAsync(ct).ConfigureAwait(false);
            if (!scopes.IsSuccess) return Result<NativeCoreInventory>.Fail(scopes.Error);
            var legacy = await registry.CoreLegacyIntentsAsync(ct).ConfigureAwait(false);
            if (!legacy.IsSuccess) return Result<NativeCoreInventory>.Fail(legacy.Error);
            var known = await registry.ReadCoreGateAsync(ct).ConfigureAwait(false);
            if (!known.IsSuccess) return Result<NativeCoreInventory>.Fail(known.Error);
            var references = suppliedStores.Concat(scopes.Value).Concat(known.Value.Sources).ToList();
            foreach (var root in roots.Value.Concat(workspaces.Value.Select(value => value.Root))
                .Concat(legacy.Value.Select(value => value.Origin.WorktreeRoot)).Concat(suppliedRoots)
                .Distinct(NativePathComparer.Instance))
            {
                CanonicalPath(root);
                var marker = Path.Combine(root, ".twig", CoreWorkspaceFactory.FileName);
                if (!File.Exists(marker))
                {
                    if (references.Any(source => NativePathComparer.Instance.Equals(source.Root, root))) continue;
                    return Result<NativeCoreInventory>.Fail(ErrorCode.StorageUnavailable,
                        "A retained native root has no initialized store locator. Supply its exact historical store; no filesystem search was performed.");
                }
                var text = await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false);
                if (text.Length > 65536) throw new InvalidDataException("Native marker exceeds its bound.");
                var document = JsonSerializer.Deserialize(text, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)
                    ?? throw new JsonException("Missing native marker.");
                if (document.Version != 1) throw new InvalidDataException("Unsupported native marker.");
                references.Add(new(document.DatabaseInstance, document.DatabasePath, root, document.Connection));
            }
            foreach (var source in references.Distinct())
            {
                var registered = await RegisterAsync(registryPath, source, ct).ConfigureAwait(false);
                if (!registered.IsSuccess) return Result<NativeCoreInventory>.Fail(registered.Error);
            }
            var captured = await registry.ReadCoreGateAsync(ct).ConfigureAwait(false);
            if (!captured.IsSuccess) return Result<NativeCoreInventory>.Fail(captured.Error);
            if (workspaces.Value.Any(workspace => !captured.Value.Sources.Any(source =>
                source.Instance == workspace.StoreInstance && source.Connection == workspace.Connection)))
                return Result<NativeCoreInventory>.Fail(ErrorCode.StorageUnavailable, "A retained workspace store locator is missing from the actual inventory.");
            var versions = ImmutableArray.CreateBuilder<long>();
            var snapshots = new Dictionary<Guid, StoreSnapshot>();
            var evidence = new StringBuilder();
            foreach (var source in captured.Value.Sources)
            {
                var read = await InspectAsync(source, ct).ConfigureAwait(false);
                if (!read.IsSuccess) return Result<NativeCoreInventory>.Fail(read.Error);
                snapshots.Add(source.Instance, read.Value);
                versions.Add(read.Value.Version);
                var claims = await registry.CoreAttemptReferencesAsync(source.Instance, ct).ConfigureAwait(false);
                if (!claims.IsSuccess) return Result<NativeCoreInventory>.Fail(claims.Error);
                if (!ReferencesMatch(read.Value, claims.Value))
                    return Result<NativeCoreInventory>.Fail(ErrorCode.StorageUnavailable, "Retained Core claim evidence is missing or mismatched; activation remains pending.");
                // Only the explicitly inventoried native artifact directory is inspected.
                var directory = Path.Combine(source.Root, ".twig", "core-github-intents");
                if (Directory.Exists(directory))
                    foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                    {
                        var info = new FileInfo(file);
                        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("Native transport evidence exceeds its bound.");
                        evidence.Append(Path.GetFileName(file)).Append(':')
                            .Append(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false))));
                    }
            }
            foreach (var intent in legacy.Value)
            {
                var core = intent.Request.CoreCorrelation;
                if (core is null || !snapshots.TryGetValue(core.StoreInstance, out var stored)
                    || !captured.Value.Sources.Any(source => source.Instance == core.StoreInstance
                        && NativePathComparer.Instance.Equals(source.Root, intent.Origin.WorktreeRoot))
                    || !LegacyCorrelationMatches(core, stored)
                    || !stored.Journal.Any(row => row.Digest == core.ProposalDigest && row.Operation == core.Operation
                        && row.Attempt == core.Attempt))
                    return Result<NativeCoreInventory>.Fail(ErrorCode.StorageUnavailable,
                        "Original native transport lacks its exact authoritative Core store/journal reference. ACK is not settlement.");
                evidence.Append(intent.IntentId).Append(':').Append(intent.RequestDigest);
            }
            foreach (var source in captured.Value.Sources)
                foreach (var row in snapshots[source.Instance].Journal.Where(row => row.Status != OpStatus.Planned))
                {
                    var proposal = snapshots[source.Instance].Proposals.Single(value => value.Digest == row.Digest);
                    var indexed = await registry.IndexRetainedCoreAttemptAsync(source, proposal, row,
                        Targets(proposal.Operations[row.Operation], snapshots[source.Instance]), snapshots[source.Instance].Version, ct).ConfigureAwait(false);
                    if (!indexed.IsSuccess) return Result<NativeCoreInventory>.Fail(indexed.Error);
                }
            foreach (var root in captured.Value.Sources.Select(value => value.Root).Distinct(NativePathComparer.Instance))
            {
                var directory = Path.Combine(root, ".twig", "core-github-intents");
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var intent = JsonSerializer.Deserialize(text, CoreGitHubNativeJsonContext.Default.CoreGitHubNativeIntent)
                        ?? throw new JsonException("Missing original GitHub intent.");
                    if (!snapshots.TryGetValue(intent.StoreInstance, out var stored)
                        || !captured.Value.Sources.Any(source => source.Instance == intent.StoreInstance
                            && NativePathComparer.Instance.Equals(source.Root, root))
                        || !stored.Proposals.Any(value => value.Digest == intent.Digest && value.Connection == intent.Connection)
                        || intent.RequestHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(intent.Payload)))
                        || !stored.Journal.Any(row => row.Digest == intent.Digest && row.Operation == intent.Operation
                            && row.Attempt == intent.Attempt))
                        return Result<NativeCoreInventory>.Fail(ErrorCode.StorageUnavailable, "Original GitHub intent lacks its exact retained Core store/journal; ACK cannot settle it.");
                }
            }
            var captureInventory = await benches.CaptureInventoryEvidenceAsync(captured.Value.Sources, ct, initialAuthoring).ConfigureAwait(false);
            if (!captureInventory.IsSuccess) return Result<NativeCoreInventory>.Fail(captureInventory.Error);
            var payload = JsonSerializer.Serialize(new NativeCoreInventory(captured.Value.Instance, captured.Value.Revision, "",
                captured.Value.Sources, versions.ToImmutable(), suppliedRoots, suppliedStores) { CaptureDigest = captureInventory.Value }, NativeCoreExecutionJson.Default.NativeCoreInventory);
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload + evidence)));
            return Result<NativeCoreInventory>.Ok(new(captured.Value.Instance, captured.Value.Revision, digest,
                captured.Value.Sources, versions.ToImmutable(), suppliedRoots, suppliedStores) { CaptureDigest = captureInventory.Value, InitialAuthoring = initialAuthoring });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return Result<NativeCoreInventory>.Fail(ErrorCode.StorageUnavailable, "Native source inventory could not be established; original evidence was preserved."); }
    }

    internal static async Task<Result<bool>> ActivateAsync(string registryPath, NativeCoreInventory inventory,
        string exactDigest, bool completeInventoryConfirmed, bool oldProcessesQuiesced, CancellationToken ct = default,
        string? initialAuthoringConfirmation = null)
    {
        if (!completeInventoryConfirmed || !oldProcessesQuiesced)
            return Result<bool>.Fail(ErrorCode.PolicyViolation, "Actual source-inventory completeness and old-process quiescence must be explicitly confirmed.");
        if (exactDigest != inventory.Digest) return Result<bool>.Fail(ErrorCode.DigestMismatch, "Confirm the exact native inventory digest.");
        if (inventory.InitialAuthoring is not null && initialAuthoringConfirmation != NativeInitialAuthoring.Statement(exactDigest))
            return Result<bool>.Fail(ErrorCode.PolicyViolation, "Explicit initial-authoring namespace confirmation is required for this exact inventory.");
        try
        {
            ct.ThrowIfCancellationRequested();
            using var lease = ConnectionOperationGate.AcquireCoreTargets(registryPath, inventory.Sources.Select(source => Destination(source.Connection)));
            var fresh = await InventoryAsync(registryPath, inventory.ExplicitRoots, inventory.ExplicitStores, ct, inventory.InitialAuthoring).ConfigureAwait(false);
            if (!fresh.IsSuccess) return Result<bool>.Fail(fresh.Error);
            if (fresh.Value.Digest != inventory.Digest)
                return Result<bool>.Fail(ErrorCode.Conflict, "Native inventory/evidence changed; no activation was inferred.");
            var proof = inventory.InitialAuthoring is null ? "" : JsonSerializer.Serialize(inventory, NativeCoreExecutionJson.Default.NativeCoreInventory)
                + "\ncompleteInventory=true;oldProcessesQuiesced=true\n" + initialAuthoringConfirmation;
            var migrated = await UserBenchStore.ForRegistry(registryPath).MigrateCaptureProvenanceAsync(fresh.Value.Sources, ct,
                inventory.InitialAuthoring, proof, fresh.Value.CaptureDigest).ConfigureAwait(false);
            if (!migrated.IsSuccess) return migrated;
            if (fresh.Value.CaptureDigest.StartsWith("legacy:", StringComparison.Ordinal) || inventory.InitialAuthoring is not null)
            {
                fresh = await InventoryAsync(registryPath, inventory.ExplicitRoots, inventory.ExplicitStores, ct).ConfigureAwait(false);
                if (!fresh.IsSuccess) return Result<bool>.Fail(fresh.Error);
                if (!fresh.Value.Sources.SequenceEqual(inventory.Sources) || !fresh.Value.Versions.SequenceEqual(inventory.Versions))
                    return Result<bool>.Fail(ErrorCode.Conflict, "Core inventory changed during the one-time check; review its preserved current state before activation.");
            }
            using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
            return await registry.ConfirmCoreInventoryAsync(fresh.Value, ct).ConfigureAwait(false);
        }
        catch (ConnectionBindingResolutionException)
        { return Result<bool>.Fail(ErrorCode.Conflict, "Native publishing is still active; quiescence was not established."); }
    }

    internal static async Task<Result<IDisposable>> AdmitAsync(string registryPath, NativeCoreStoreReference source,
        Proposal proposal, int operation, Guid attempt, CancellationToken ct, bool planned = false)
    {
        IDisposable? lease = null;
        try
        {
            var provenance = await PublishingAsync(registryPath, ct).ConfigureAwait(false);
            if (!provenance.IsSuccess) return Result<IDisposable>.Fail(provenance.Error);
            if (operation < 0 || operation >= proposal.Operations.Length || attempt == Guid.Empty || proposal.Connection != source.Connection)
                return Result<IDisposable>.Fail(ErrorCode.InvalidInput, "An exact native Core source and attempt are required.");
            var own = await Snapshot(source, ct).ConfigureAwait(false);
            if (!own.IsSuccess) return Result<IDisposable>.Fail(own.Error);
            var row = own.Value.Journal.SingleOrDefault(value => value.Digest == proposal.Digest && value.Operation == operation);
            if (row is null || (planned ? row.Status != OpStatus.Planned || row.Attempt != Guid.Empty
                : row.Status != OpStatus.Applying || row.Attempt != attempt))
                return Result<IDisposable>.Fail(ErrorCode.Conflict, "Native target admission requires the exact durable Applying attempt.");
            var targets = Targets(proposal.Operations[operation], own.Value);
            lease = ConnectionOperationGate.AcquireCoreTargets(registryPath, OperationKeys(proposal.Operations[operation], own.Value, source.Connection));
            ct.ThrowIfCancellationRequested();
            var freshOwn = await Snapshot(source, ct).ConfigureAwait(false);
            if (!freshOwn.IsSuccess) return Result<IDisposable>.Fail(freshOwn.Error);
            if (freshOwn.Value.Version != own.Value.Version)
                return Result<IDisposable>.Fail(ErrorCode.Conflict, "The authoritative Core source changed before exclusive target admission.");
            using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
            var captured = await registry.ReadCoreGateAsync(ct).ConfigureAwait(false);
            if (!captured.IsSuccess) return Result<IDisposable>.Fail(captured.Error);
            if (!captured.Value.Active || !captured.Value.Sources.Contains(source))
                return Result<IDisposable>.Fail(ErrorCode.PolicyViolation, "Native publishing activation or exact source inventory is pending.");
            var relevant = await registry.CoreTargetSourcesAsync(targets, source.Connection, ct).ConfigureAwait(false);
            if (!relevant.IsSuccess) return Result<IDisposable>.Fail(relevant.Error);
            foreach (var priorSource in captured.Value.Sources.Where(value => relevant.Value.Contains(value.Instance)
                || value.Connection.Provider == source.Connection.Provider && value.Connection.ResourceScope == source.Connection.ResourceScope
                    && value.Connection.Destination == source.Connection.Destination))
            {
                var prior = await InspectAsync(priorSource, ct).ConfigureAwait(false);
                if (!prior.IsSuccess) return Result<IDisposable>.Fail(prior.Error);
                var references = await registry.CoreAttemptReferencesAsync(priorSource.Instance, ct).ConfigureAwait(false);
                if (!references.IsSuccess) return Result<IDisposable>.Fail(references.Error);
                if (!ReferencesMatch(prior.Value, references.Value))
                    return Result<IDisposable>.Fail(ErrorCode.StorageUnavailable, "A retained target claim has missing or mismatched authoritative Core evidence; no absence proof was inferred.");
                foreach (var reference in references.Value.Where(reference => targets.Contains(reference.Target)
                    && reference.Attempt != attempt && prior.Value.Journal.Any(value => value.Digest == reference.Digest
                        && value.Operation == reference.Operation && value.Status == OpStatus.Planned && value.Attempt == Guid.Empty)))
                {
                    var transport = await registry.CoreLegacyIntentsAsync(ct).ConfigureAwait(false);
                    if (!Directory.Exists(priorSource.Root) || !transport.IsSuccess || transport.Value.Any(value => value.Request.CoreCorrelation?.Attempt == reference.Attempt)
                        || File.Exists(Path.Combine(priorSource.Root, ".twig", "core-github-intents", reference.Attempt.ToString("D") + ".json")))
                        return Result<IDisposable>.Fail(ErrorCode.Indeterminate, "An original native dispatch cannot be excluded; unattempted-reference reclaim was refused.");
                    var reclaimed = await registry.ReclaimUnattemptedCoreReferenceAsync(reference, ct).ConfigureAwait(false);
                    if (!reclaimed.IsSuccess) return Result<IDisposable>.Fail(reclaimed.Error);
                }
                foreach (var previous in prior.Value.Journal)
                {
                    if (priorSource == source && previous.Digest == proposal.Digest
                        && previous.Operation == operation && (planned || previous.Attempt == attempt)) continue;
                    var previousProposal = prior.Value.Proposals.Single(value => value.Digest == previous.Digest);
                    var op = previousProposal.Operations[previous.Operation];
                    if (previous.Status == OpStatus.Planned) continue;
                    var overlaps = Targets(op, prior.Value).Intersect(targets).Any();
                    if (op.Kind == OpKind.Create && proposal.Operations[operation].Kind == OpKind.Create
                        && op.Item == proposal.Operations[operation].Item)
                        return Result<IDisposable>.Fail(ErrorCode.Indeterminate, "This permanent creation UUID was already attempted; no replacement request is allowed.");
                    if (previous.Status == OpStatus.Verified || NoEffect(previous)) continue;
                    var unmappedCreate = op.Kind == OpKind.Create && !prior.Value.Mappings.Any(value => value.Local == op.Item);
                    if (overlaps || unmappedCreate && previousProposal.Connection.Destination == source.Connection.Destination)
                        return Result<IDisposable>.Fail(ErrorCode.Indeterminate,
                            "An original unresolved native effect fences this target or unmapped-creation destination. Read/status remain available; no replay was performed.");
                }
            }
            var recorded = await registry.RecordCoreAttemptAsync(source, proposal, operation, attempt, targets,
                own.Value.Version, !planned, captured.Value.Instance, captured.Value.Revision, ct).ConfigureAwait(false);
            if (!recorded.IsSuccess) return Result<IDisposable>.Fail(recorded.Error);
            var retained = lease ?? throw new InvalidOperationException("Missing native target lease.");
            lease = null;
            return Result<IDisposable>.Ok(retained);
        }
        catch (ConnectionBindingResolutionException)
        { return Result<IDisposable>.Fail(ErrorCode.Conflict, "Another native target operation owns the exclusive lease; retry only after its outcome is established."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return Result<IDisposable>.Fail(ErrorCode.StorageUnavailable, "Native target evidence is unavailable; publication was refused."); }
        finally { lease?.Dispose(); }
    }

    private static bool NoEffect(JournalEntry row) => row.Status == OpStatus.Failed && row.Receipts.Any(receipt =>
        receipt.Status == OpStatus.Failed && receipt.PublishedKey is null && receipt.Revision is null && receipt.Error is not null
        && receipt.Digest == row.Digest && receipt.Operation == row.Operation && receipt.Attempt == row.Attempt);

    private static bool ReferencesMatch(StoreSnapshot snapshot, IEnumerable<CoreAttemptReference> references)
        => references.All(reference => snapshot.Proposals.FirstOrDefault(value => value.Digest == reference.Digest) is { } proposal
            && reference.MinimumVersion <= snapshot.Version
            && reference.Operation >= 0 && reference.Operation < proposal.Operations.Length
            && Targets(proposal.Operations[reference.Operation], snapshot).Contains(reference.Target, StringComparer.Ordinal)
            && snapshot.Journal.Any(value => value.Digest == reference.Digest && value.Operation == reference.Operation
                && (value.Status == OpStatus.Planned && value.Attempt == Guid.Empty && !reference.EverApplying || value.Attempt == reference.Attempt)));

    private static bool LegacyCorrelationMatches(CoreWriteCorrelation correlation, StoreSnapshot snapshot)
    {
        var proposal = snapshot.Proposals.FirstOrDefault(value => value.Digest == correlation.ProposalDigest);
        if (proposal is null || correlation.Operation < 0 || correlation.Operation >= proposal.Operations.Length
            || correlation.ConnectionIdentity != CleanCoreAdoAccess.ConnectionText(proposal.Connection)) return false;
        var op = proposal.Operations[correlation.Operation];
        return correlation.TargetIdentity == CleanCoreAdoAccess.ItemText(op.Item)
            && correlation.CreationIdentity == (op.Kind == OpKind.Create ? op.Item.LocalId : (Guid?)null)
            && correlation.ExpectedRevision == op.ExpectedRevision?.Token;
    }

    private static IEnumerable<string> Targets(Op op, StoreSnapshot snapshot)
        => op.Links.Select(value => value.Other).Prepend(op.Item).Concat(op.Other is null ? [] : new[] { op.Other })
            .SelectMany(key => snapshot.Mappings.FirstOrDefault(value => value.Local == key) is { } map
                ? new[] { Key(key), Key(map.Remote) } : new[] { Key(key) }).Distinct(StringComparer.Ordinal);

    internal static IEnumerable<string> OperationKeys(Op op, StoreSnapshot snapshot, Connection connection)
        => Targets(op, snapshot).Append(Destination(connection));

    private static string Key(ItemKey key) => key.IsLocal ? "uuid:" + key.LocalId.ToString("D")
        : JsonSerializer.Serialize(new[] { key.Provider!.Value, key.ResourceScope!, key.NativeId! }, NativeCoreExecutionJson.Default.StringArray);
    private static string Destination(Connection connection) => "destination:" + JsonSerializer.Serialize(
        new[] { connection.Provider.Value, connection.ResourceScope, connection.Destination }, NativeCoreExecutionJson.Default.StringArray);

    internal static async Task<Result<StoreSnapshot>> Snapshot(NativeCoreStoreReference source, CancellationToken ct)
        => !Directory.Exists(source.Root)
            ? Result<StoreSnapshot>.Fail(ErrorCode.StorageUnavailable, "The exact authoritative native root is unavailable; its store reference was preserved.")
            : await InspectAsync(source, ct).ConfigureAwait(false);

    internal static async Task<Result<StoreSnapshot>> InspectAsync(NativeCoreStoreReference source, CancellationToken ct)
    {
        try
        {
            CanonicalPath(source.DatabasePath); CanonicalPath(source.Root);
            var store = await SqliteStore.OpenAsync(source.DatabasePath, ct, source.Instance).ConfigureAwait(false);
            if (!store.IsSuccess) return Result<StoreSnapshot>.Fail(store.Error);
            var pinned = await store.Value.CheckWorkspaceAsync(source.Connection, ct).ConfigureAwait(false);
            return pinned.IsSuccess ? await store.Value.ReadAsync(source.Connection, ct).ConfigureAwait(false)
                : Result<StoreSnapshot>.Fail(pinned.Error);
        }
        catch (ArgumentException)
        { return Result<StoreSnapshot>.Fail(ErrorCode.StorageUnavailable, "Native store reference is not canonical."); }
    }

    private static void CanonicalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !NativePathComparer.Instance.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
            throw new ArgumentException("An exact canonical native locator is required.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NativeCoreStoreReference))]
[JsonSerializable(typeof(NativeCoreInventory))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class NativeCoreExecutionJson : JsonSerializerContext;
