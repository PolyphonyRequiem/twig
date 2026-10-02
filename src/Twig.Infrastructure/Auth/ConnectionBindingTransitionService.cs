using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

internal sealed record ConnectionBindingTransitionPreview(string State, string Digest, bool CanApply,
    string WorktreeRoot, string ConnectionRef, string? OriginalBindingId, string? DesiredBindingId,
    long AttachmentRevision, IReadOnlyList<string> Blockers, IReadOnlyList<string> NextSteps);

internal interface IConnectionBindingTransitionService
{
    Task<ConnectionBindingTransitionPreview> PreviewAsync(TwigConfiguration configuration, TwigPaths paths,
        string? bindingId, CancellationToken ct = default);
    Task<ConnectionBindingTransitionPreview> ApplyAsync(TwigConfiguration configuration, TwigPaths paths,
        string? bindingId, string confirmedDigest, CancellationToken ct = default);
}

/// <summary>
/// Native checkout pin authority. Cross-store progress stays fenced until exact local completion;
/// recovery never resolves a new actor, publishes work, or discards a durable record.
/// </summary>
internal sealed class ConnectionBindingTransitionService : IConnectionBindingTransitionService, IDisposable
{
    private readonly IConnectionBindingService _bindings;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly string _registryPath;
    private readonly Action<string>? _checkpoint;

    internal ConnectionBindingTransitionService(string userHome, IConnectionBindingService bindings, Action<string>? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        if (!Path.IsPathFullyQualified(userHome)) throw new ArgumentException("userHome must be absolute.", nameof(userHome));
        _registryPath = Path.Combine(Path.GetFullPath(userHome), "system.db");
        if (!PathsEqual(_registryPath, bindings.RegistryPath))
            throw new ArgumentException("Binding management must use the same native registry as its connection service.", nameof(bindings));
        _bindings = bindings;
        _registry = new SqliteSystemWorktreeRegistry(_registryPath, TimeProvider.System);
        _checkpoint = checkpoint;
    }

    public async Task<ConnectionBindingTransitionPreview> PreviewAsync(TwigConfiguration configuration, TwigPaths paths,
        string? bindingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out var failure))
            return Blocked(paths, configuration, "binding-worktree-required: " + failure);
        IDisposable gate;
        try { gate = ConnectionOperationGate.Acquire(anchor.WorktreeRoot, exclusive: true); }
        catch (InvalidOperationException ex) { return Blocked(paths, configuration, ex.Message); }
        using (gate)
        {
            var (preview, _) = await InspectAsync(configuration, paths, bindingId, ct).ConfigureAwait(false);
            return preview;
        }
    }

    public async Task<ConnectionBindingTransitionPreview> ApplyAsync(TwigConfiguration configuration, TwigPaths paths,
        string? bindingId, string confirmedDigest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedDigest);
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out var failure))
            return Blocked(paths, configuration, "binding-worktree-required: " + failure);
        IDisposable gate;
        try { gate = ConnectionOperationGate.Acquire(anchor.WorktreeRoot, exclusive: true); }
        catch (InvalidOperationException ex) { return Blocked(paths, configuration, ex.Message); }
        using (gate)
        {
            FileStream attachmentLock;
            try
            {
                attachmentLock = new FileStream(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentLockFileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return Blocked(paths, configuration, "binding-transition-attachment-active: another native attachment writer holds its CAS fence; wait for it to settle and rerun.");
            }
            using (attachmentLock)
            {
                var (preview, record) = await InspectAsync(configuration, paths, bindingId, ct, confirmedDigest).ConfigureAwait(false);
                if (!preview.CanApply) return preview;
                if (!string.Equals(preview.Digest, confirmedDigest, StringComparison.Ordinal))
                    throw new InvalidOperationException("binding-transition-preview-stale: exact confirmed digest no longer matches attachment, configuration or native selection revisions. Rerun and review preview; no transition occurred.");
                if (preview.State is "unchanged" or "completed") return preview;
                if (record is null) throw new InvalidOperationException("binding-transition-context-missing: native review context is unavailable.");
                if (preview.State == "preview")
                {
                    var begun = await _registry.BeginConnectionBindingTransitionAsync(record, ct).ConfigureAwait(false);
                    Require(begun);
                    _checkpoint?.Invoke("intent-recorded");
                }
                // From this point the native unfinished row refuses every supported runtime/store/HTTP admission.
                await CompleteAsync(record, configuration, paths, ct).ConfigureAwait(false);
                var current = TwigPaths.BuildPaths(paths.TwigDir, configuration, paths.StartDir);
                _ = MirrorAdmission.Acquire(current, _registryPath)
                    ?? throw new InvalidOperationException("binding-transition-readback-failed: completed native intent has no admitted mirror.");
                return Report("completed", record, []);
            }
        }
    }

    private async Task<(ConnectionBindingTransitionPreview Preview, ConnectionBindingTransitionRecord? Record)> InspectAsync(
        TwigConfiguration configuration, TwigPaths paths, string? bindingId, CancellationToken ct, string? confirmedDigest = null)
    {
        ct.ThrowIfCancellationRequested();
        if (bindingId is not null && string.IsNullOrWhiteSpace(bindingId))
            return (Blocked(paths, configuration, "binding-selector-required: supply an explicit binding id; null alone means deliberate pin removal."), null);
        var anchor = WorktreeAnchorDetector.Detect(paths.StartDir ?? paths.TwigDir)
            ?? throw new InvalidOperationException("binding-worktree-required: restore the attached checkout.");
        var fingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
        var native = await _registry.ReadConnectionBindingTransitionAsync(fingerprint, ct).ConfigureAwait(false);
        Require(native);
        if (native.Value is { State: "completed" } completed && completed.Digest == confirmedDigest
            && completed.DesiredBindingPin == bindingId)
        {
            // A lost acknowledgment reads the original receipt; it is not another transition.
            // Newly fetched values and authored pending work must not be reset or reinspected as switch consent.
            var blockers = new List<string>();
            ValidateRecord(completed, paths, configuration, fingerprint, blockers);
            await InspectRecordedAuthorityAsync(completed, ct, blockers).ConfigureAwait(false);
            if (blockers.Count == 0)
            {
                try
                {
                    var currentPaths = TwigPaths.BuildPaths(paths.TwigDir, configuration, paths.StartDir);
                    var live = await TwigConfiguration.LoadSplitAsync(currentPaths, ct).ConfigureAwait(false);
                    var current = await _bindings.ResolveAsync(live, currentPaths, ct).ConfigureAwait(false);
                    if (current.Binding != completed.DesiredBinding.Binding
                        || current.SelectionSource != completed.DesiredBinding.SelectionSource
                        || current.SelectionRevision != completed.DesiredBinding.SelectionRevision
                        || current.StorageGeneration != completed.Generation)
                        blockers.Add("binding-transition-receipt-stale: the recorded completed selection is no longer the admitted checkout authority.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { blockers.Add("binding-transition-receipt-unavailable: " + ex.Message); }
            }
            return (Report("completed", completed, blockers), completed);
        }
        if (native.Value is { State: not "completed" } interrupted)
        {
            var blockers = new List<string>();
            if (interrupted.DesiredBindingPin != bindingId)
                blockers.Add("binding-transition-selector-conflict: resume the original recorded pin/removal; no second intent can replace it.");
            ValidateRecord(interrupted, paths, configuration, fingerprint, blockers);
            await InspectRecordedAuthorityAsync(interrupted, ct, blockers).ConfigureAwait(false);
            if (blockers.Count == 0)
                await InspectUnfinishedWorkAsync(interrupted, configuration, paths, ct, blockers).ConfigureAwait(false);
            if (blockers.Count == 0)
                await VerifyCredentialAsync(interrupted.DesiredBinding.Identity, configuration.Organization, ct, blockers).ConfigureAwait(false);
            return (Report(interrupted.State, interrupted, blockers), interrupted);
        }

        var errors = new List<string>();
        using var attachmentStore = new WorktreeLocalAttachmentStore(paths, configuration, TimeProvider.System);
        var attachment = await attachmentStore.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachment.IsSuccess) return (Blocked(paths, configuration, "binding-attachment-invalid: " + attachment.Error), null);
        var originalDocument = ReadAttachment(paths);
        if (originalDocument.Revision != attachment.Value.Revision)
            return (Blocked(paths, configuration, "binding-attachment-cas-mismatch: attachment changed during inspection."), null);
        var configurationHash = ConfigurationHash(paths);
        var attachmentHash = HashFile(AttachmentPath(paths));
        var worktreeHash = HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName));
        var layoutHash = HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName));
        var liveConfiguration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
        if (ConnectionRefResolver.Compute(liveConfiguration) != ConnectionRefResolver.Compute(configuration))
            return (Blocked(paths, configuration, "binding-portable-endpoint-changed: reload the checked-in configuration before management."), null);
        ResolvedConnectionBinding original;
        try { original = await _bindings.ResolveAsync(liveConfiguration, paths, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return (Blocked(paths, configuration, ex.Message), null); }
        var defaultResult = await _registry.FindDefaultBindingAsync(original.Binding.ConnectionRef, ct).ConfigureAwait(false);
        Require(defaultResult);
        var defaultRow = defaultResult.Value;
        var selectedId = bindingId ?? defaultRow?.BindingId;
        if (selectedId is null)
            return (Blocked(paths, configuration, "binding-default-required: explicit pin removal requires a valid connection default; set it through native management first."), null);
        var bindingResult = await _registry.FindBindingByIdAsync(selectedId, ct).ConfigureAwait(false);
        Require(bindingResult);
        if (bindingResult.Value is not { } desiredRow)
            return (Blocked(paths, configuration, "binding-unknown: the explicit desired binding does not exist; register/bind it first."), null);
        if (desiredRow.ConnectionRef != original.Binding.ConnectionRef)
            return (Blocked(paths, configuration, "binding-endpoint-mismatch: desired binding belongs to another checked-in endpoint; no account crossover is admitted."), null);
        var identities = await _bindings.ListIdentitiesAsync(ct).ConfigureAwait(false);
        var identity = identities.SingleOrDefault(x => x.IdentityId == desiredRow.IdentityId);
        if (identity is null)
            return (Blocked(paths, configuration, "binding-identity-missing: desired binding principal evidence is unavailable; repair enrollment separately."), null);
        var desired = original with
        {
            Binding = new IdentityBinding(desiredRow.BindingId, desiredRow.ConnectionRef, desiredRow.IdentityId, desiredRow.Revision),
            Identity = identity,
            SelectionSource = bindingId is null ? "connection-default-binding" : "checkout-binding-pin",
            SelectionRevision = bindingId is null ? defaultRow!.Revision : checked(originalDocument.Revision + 1),
            Operation = original.Operation with { AttachmentRevision = checked(originalDocument.Revision + 1) },
            StorageGeneration = null
        };
        var migrationResult = await _registry.ReadConnectionMigrationAsync(fingerprint, ct).ConfigureAwait(false);
        Require(migrationResult);
        if (originalDocument.BindingPin == bindingId)
        {
            // Not an identity transition. Pending work does not make an exact no-op destructive.
            var unchangedDigest = Hash(string.Join('\n', "binding-pin-unchanged-v1", fingerprint,
                configurationHash, attachmentHash, worktreeHash, layoutHash, selectedId,
                desiredRow.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                defaultRow?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return (new("unchanged", unchangedDigest, true, original.WorktreeRoot, original.Binding.ConnectionRef,
                original.Binding.BindingId, desired.Binding.BindingId, originalDocument.Revision, [],
                ["The exact pin/removal is already in place. No identity, cache, attachment, pending work or journal is changed."]), null);
        }
        if (migrationResult.Value is not { State: "active" } migration)
            return (Blocked(paths, configuration, "binding-migration-required: explicitly migrate the current identity before changing a checkout pin. Native legacy-host quiescence and retired entrypoints are required; switching never migrates or kills hosts automatically."), null);
        if (migration.BindingId != original.Binding.BindingId || migration.Generation != original.StorageGeneration)
            return (Blocked(paths, configuration, "binding-generation-mismatch: current runtime does not own native migrated admission; restore/recover its original intent."), null);
        var resetRequired = original.Binding != desired.Binding;
        var record = new ConnectionBindingTransitionRecord(1, "", fingerprint, anchor.WorktreeRoot,
            original.Binding.ConnectionRef, "preparing", originalDocument.BindingPin, bindingId, original, desired,
            defaultRow?.BindingId, defaultRow?.Revision, configurationHash, worktreeHash, layoutHash, attachmentHash,
            originalDocument, originalDocument with { BindingPin = bindingId, Revision = checked(originalDocument.Revision + 1) },
            "", migration.CurrentMirror, migration.CurrentMirror, migration, resetRequired);
        // Generation derives from immutable reviewed context. Recovery never mints a second digest/generation.
        var digest = Digest(record);
        var generation = resetRequired ? digest[..32] : migration.Generation;
        record = record with { Digest = digest, Generation = generation, DesiredBinding = desired with { StorageGeneration = generation } };
        await InspectUnfinishedWorkAsync(record, liveConfiguration, paths, ct, errors).ConfigureAwait(false);
        await VerifyCredentialAsync(identity, liveConfiguration.Organization, ct, errors).ConfigureAwait(false);
        if (ConfigurationHash(paths) != configurationHash || HashFile(AttachmentPath(paths)) != attachmentHash
            || HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName)) != worktreeHash
            || HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)) != layoutHash)
            errors.Add("binding-transition-context-changed: exact portable/worktree/attachment snapshot drifted during inspection; rerun.");
        await InspectRecordedAuthorityAsync(record, ct, errors).ConfigureAwait(false);
        return (Report("preview", record, errors), record);
    }

    private async Task CompleteAsync(ConnectionBindingTransitionRecord record, TwigConfiguration configuration, TwigPaths paths, CancellationToken ct)
    {
        var errors = new List<string>();
        ValidateRecord(record, paths, configuration, record.Fingerprint, errors);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join('\n', errors));
        if (record.State == "preparing")
        {
            Require(await _registry.CommitConnectionBindingTransitionAsync(record, ct).ConfigureAwait(false));
            record = record with { State = "central-committed" };
            _checkpoint?.Invoke("central-committed");
        }
        if (record.State == "central-committed")
        {
            var current = ReadAttachment(paths);
            if (current != record.DesiredAttachment)
            {
                if (current != record.OriginalAttachment || HashFile(AttachmentPath(paths)) != record.AttachmentHash)
                    throw new InvalidOperationException("binding-transition-attachment-cas-mismatch: original pin/scope/claim/revision changed; restore exact native context before resuming.");
                // The caller owns the existing OS attachment CAS lock. Rename changes only pin + revision;
                // taking the ordinary store's lock again here would deadlock the recoverable transaction.
                await WriteAtomicAsync(AttachmentPath(paths), JsonSerializer.Serialize(record.DesiredAttachment,
                    TwigJsonContext.Default.AttachmentDocument), ct).ConfigureAwait(false);
                _checkpoint?.Invoke("attachment-written");
            }
            Require(await _registry.AdvanceConnectionBindingTransitionAsync(record, "attachment-completed", ct).ConfigureAwait(false));
            record = record with { State = "attachment-completed" };
            _checkpoint?.Invoke("attachment-completed");
        }
        if (record.State == "attachment-completed")
        {
            if (record.ResetRequired) ResetMirror(record);
            _checkpoint?.Invoke("mirror-reset");
            var marker = new MirrorAdmissionDocument(1, _registryPath, record.Fingerprint, record.ConnectionRef,
                record.DesiredBinding.Binding.BindingId, record.Generation, Path.GetFileName(record.CurrentMirror));
            await WriteAtomicAsync(Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile),
                JsonSerializer.Serialize(marker, TwigJsonContext.Default.MirrorAdmissionDocument), ct).ConfigureAwait(false);
            Require(await _registry.AdvanceConnectionBindingTransitionAsync(record, "mirror-completed", ct).ConfigureAwait(false));
            record = record with { State = "mirror-completed" };
            _checkpoint?.Invoke("mirror-completed");
        }
        if (record.State == "mirror-completed")
        {
            ValidateRecord(record, paths, configuration, record.Fingerprint, errors);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join('\n', errors));
            if (ReadAttachment(paths) != record.DesiredAttachment)
                throw new InvalidOperationException("binding-transition-completion-cas-mismatch: pin/scope/claim drifted; original intent stays fenced.");
            var marker = MirrorAdmission.ReadDocument(Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile));
            if (marker.RegistryPath != _registryPath || marker.Fingerprint != record.Fingerprint
                || marker.BindingId != record.DesiredBinding.Binding.BindingId || marker.Generation != record.Generation
                || marker.ConnectionRef != record.ConnectionRef || marker.MirrorFile != Path.GetFileName(record.CurrentMirror))
                throw new InvalidOperationException("binding-transition-marker-cas-mismatch: restore exact recorded admission before completion.");
            VerifyMirrorGeneration(record);
            Require(await _registry.AdvanceConnectionBindingTransitionAsync(record, "completed", ct).ConfigureAwait(false));
            _checkpoint?.Invoke("completed");
        }
    }

    private async Task InspectUnfinishedWorkAsync(ConnectionBindingTransitionRecord record, TwigConfiguration configuration,
        TwigPaths paths, CancellationToken ct, List<string> blockers)
    {
        try
        {
            VerifyStorageShape(record.CurrentMirror);
            using var store = SqliteCacheStore.OpenMigrationMirror(record.CurrentMirror);
            using var attachment = new WorktreeLocalAttachmentStore(paths, configuration, TimeProvider.System);
            var inspector = new IdentityChangeEligibilityService(_registry, attachment, new SqlitePendingChangeStore(store),
                new SqliteWorkItemRepository(store, new WorkItemMapper()), new SqlitePublishIntentRepository(store),
                new SqlitePlanJournalRepository(store), _bindings);
            var eligibility = await inspector.InspectAsync(configuration, paths, record.OriginalBinding, ct).ConfigureAwait(false);
            foreach (var row in eligibility.PendingEdits) blockers.Add($"binding-pending-work: unpublished {row.Kind} for work item {row.WorkItemId}; publish/discard separately under original actor.");
            foreach (var row in eligibility.LocalSeeds) blockers.Add($"binding-local-seed: unpublished seed alias {row.SeedAlias}; publish/discard separately.");
            foreach (var row in eligibility.OpenPublishIntents) blockers.Add($"binding-open-publish-intent: {row.Identity}; reconcile its original native remote outcome separately.");
            foreach (var row in eligibility.UnresolvedJournals) blockers.Add($"binding-unresolved-proposal: {row.Digest}/{row.OpId} ({row.State}); {row.Reason}");
            foreach (var row in eligibility.ReservedClaims) blockers.Add($"binding-reserved-claim: {row.ClaimId} ({row.ObservedState}); settle holder/claim lifecycle separately.");
            foreach (var row in eligibility.WorktreeGuards) blockers.Add($"binding-worktree-guard: {row.Guard}; observed {row.Observed}, expected {row.Expected}.");
            foreach (var row in eligibility.UnknownRows) blockers.Add($"binding-unknown-native-evidence: {row.Source}; {row.Detail}");
            using var dirty = store.GetConnection().CreateCommand();
            dirty.CommandText = "SELECT 1 FROM work_items WHERE is_dirty <> 0 OR is_seed <> 0 OR id < 0 LIMIT 1;";
            if (await dirty.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                blockers.Add("binding-dirty-mirror: authored or unknown local mirror state remains; establish it through native publication/discard before changing identity.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { blockers.Add("binding-native-inspection-unavailable: " + ex.Message + " Preserve durable work and recover it separately; no force bypass exists."); }
    }

    private async Task InspectRecordedAuthorityAsync(ConnectionBindingTransitionRecord record, CancellationToken ct, List<string> blockers)
    {
        var defaultResult = await _registry.FindDefaultBindingAsync(record.ConnectionRef, ct).ConfigureAwait(false);
        if (!defaultResult.IsSuccess || defaultResult.Value?.BindingId != record.DefaultBindingId || defaultResult.Value?.Revision != record.DefaultRevision)
            blockers.Add("binding-transition-default-cas-mismatch: exact default revision changed; restore original recorded authority for recovery or review a fresh preview before admission.");
        foreach (var binding in new[] { record.OriginalBinding.Binding, record.DesiredBinding.Binding })
        {
            var result = await _registry.FindBindingByIdAsync(binding.BindingId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is not { } row || row.ConnectionRef != binding.ConnectionRef
                || row.IdentityId != binding.IdentityId || row.Revision != binding.Revision)
                blockers.Add("binding-transition-binding-cas-mismatch: immutable reviewed binding revision changed or disappeared.");
        }
        var migration = await _registry.ReadConnectionMigrationAsync(record.Fingerprint, ct).ConfigureAwait(false);
        var expected = record.State == "preparing" ? record.OriginalMigration : SqliteSystemWorktreeRegistry.TransitionMigration(record);
        if (!migration.IsSuccess || migration.Value != expected)
            blockers.Add("binding-transition-generation-cas-mismatch: recorded native admitted generation changed; recover the original authority, not a new intent.");
    }

    private async Task VerifyCredentialAsync(AuthenticationIdentity identity, string organization, CancellationToken ct, List<string> blockers)
    {
        try { await _bindings.VerifyIdentityCredentialAsync(identity.Name, organization, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { blockers.Add("binding-desired-credential-not-admitted: " + ex.Message + " Repair the selected identity separately; no alternate-account fallback is permitted."); }
    }

    private static void ValidateRecord(ConnectionBindingTransitionRecord record, TwigPaths paths, TwigConfiguration configuration,
        string fingerprint, List<string> blockers)
    {
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out _)
            || WorktreeFingerprintProvider.CanonicalJson(anchor) != record.Fingerprint)
            blockers.Add("binding-transition-fingerprint-changed: the live checkout no longer matches the recorded native worktree; restore it before recovery.");
        if (record.Version != 1 || record.Fingerprint != fingerprint || !PathsEqual(record.WorktreeRoot, paths.RepoRoot)
            || record.ConnectionRef != ConnectionRefResolver.Compute(configuration)
            || record.Digest != Digest(record) || !Guid.TryParseExact(record.Generation, "N", out _)
            || !PathsEqual(record.SourceMirror, record.OriginalMigration.CurrentMirror)
            || !PathsEqual(record.CurrentMirror, record.SourceMirror)
            || !PathsEqual(Path.GetDirectoryName(record.CurrentMirror)!, Path.Combine(paths.TwigDir, "cache")))
            blockers.Add("binding-transition-intent-invalid: native original context/version/digest disagrees; preserve and restore the recorded authority.");
        if (record.ConfigurationHash != ConfigurationHash(paths)
            || record.WorktreeHash != HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName))
            || record.LayoutHash != HashFile(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)))
            blockers.Add("binding-transition-context-changed: portable configuration, layout or worktree fingerprint changed; restore the exact original context before recovery.");
        var attachment = ReadAttachment(paths);
        var isOriginal = attachment == record.OriginalAttachment && HashFile(AttachmentPath(paths)) == record.AttachmentHash;
        var isDesired = attachment == record.DesiredAttachment;
        if (record.State is "preparing" ? !isOriginal : record.State is "completed" ? !isDesired : !isOriginal && !isDesired)
            blockers.Add("binding-transition-attachment-cas-mismatch: original/desired revision, pin, scope or claim is inconsistent; restore exact native context.");
    }

    private static void ResetMirror(ConnectionBindingTransitionRecord record)
    {
        VerifyStorageShape(record.CurrentMirror);
        using var store = SqliteCacheStore.OpenMigrationMirror(record.CurrentMirror);
        using var tx = store.GetConnection().BeginTransaction();
        using var reset = store.GetConnection().CreateCommand();
        reset.Transaction = tx;
        reset.CommandText = """
            DELETE FROM work_items WHERE is_seed=0 AND is_dirty=0 AND id>0;
            DELETE FROM work_item_links;
            DELETE FROM work_item_link_verifications;
            DELETE FROM field_definitions;
            DELETE FROM process_types;
            DELETE FROM iteration_calendar;
            DELETE FROM navigation_history;
            DELETE FROM context;
            DELETE FROM metadata WHERE key <> 'schema_version';
            INSERT INTO metadata(key,value) VALUES('binding_generation',$generation);
            """;
        reset.Parameters.AddWithValue("$generation", record.Generation);
        reset.ExecuteNonQuery();
        tx.Commit();
    }

    private static void VerifyMirrorGeneration(ConnectionBindingTransitionRecord record)
    {
        using var connection = OpenReadOnly(record.CurrentMirror);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key='binding_generation';";
        if (cmd.ExecuteScalar() as string != record.Generation)
            throw new InvalidOperationException("binding-transition-mirror-generation-mismatch: reset/provenance is incomplete; resume the same native intent.");
        if (record.ResetRequired)
        {
            cmd.CommandText = "SELECT 1 FROM work_items LIMIT 1;";
            if (cmd.ExecuteScalar() is not null)
                throw new InvalidOperationException("binding-transition-mirror-not-cold: identity-dependent values remain; native completion stays fenced.");
        }
    }

    private static void VerifyStorageShape(string mirrorPath)
    {
        using var mirror = OpenReadOnly(mirrorPath);
        using var cmd = mirror.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
        if (cmd.ExecuteScalar()?.ToString() != SqliteCacheStore.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("binding-mirror-version-unsupported: use the compatible Twig version; transition cannot rebuild an unknown mirror.");
        var durablePath = SqliteCacheStore.DeriveDurableDataSource(mirrorPath);
        using var durable = OpenReadOnly(durablePath);
        using var version = durable.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var actual = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (actual != SqliteCacheStore.DurableSchemaVersion)
            throw new InvalidOperationException("binding-durable-version-unsupported: use explicit native store migration first; no durable store is created, rebuilt or discarded by switching.");
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("binding-store-missing: restore the original mirror and durable store before transition; no empty replacement is admitted.");
        SQLitePCL.Batteries.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static AttachmentDocument ReadAttachment(TwigPaths paths)
        => JsonSerializer.Deserialize(File.ReadAllText(AttachmentPath(paths)), TwigJsonContext.Default.AttachmentDocument)
            ?? throw new InvalidOperationException("binding-attachment-unreadable: restore the original native attachment.");

    private static async Task WriteAtomicAsync(string path, string contents, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static ConnectionBindingTransitionPreview Report(string state, ConnectionBindingTransitionRecord record, IReadOnlyList<string> blockers)
        => new(state, record.Digest, blockers.Count == 0, record.WorktreeRoot, record.ConnectionRef,
            record.OriginalBinding.Binding.BindingId, record.DesiredBinding.Binding.BindingId, record.OriginalAttachment.Revision,
            blockers, blockers.Count > 0
                ? ["Resolve blockers separately under their original native authority, then resume this exact intent/digest. No publication, discard, claim rewrite, force or new intent is permitted."]
                : [state == "completed" ? "Native transition is complete. Explicitly reconnect affected runtimes; durable work, credentials and portable policy remain preserved."
                    : "Apply/resume this exact digest. Identity-dependent read data becomes cold only when selection changes; credentials, durable history and attachment policy are preserved."]);

    private static ConnectionBindingTransitionPreview Blocked(TwigPaths paths, TwigConfiguration configuration, string reason)
        => new("blocked", "", false, paths.RepoRoot, ConnectionRefResolver.Compute(configuration), null, null, -1,
            [reason], ["Resolve the blocker separately and rerun preview. There is no force, publish/discard or automatic reconnect bypass."]);

    private static string Digest(ConnectionBindingTransitionRecord record)
        => Hash(JsonSerializer.Serialize(record with
        {
            Digest = "", State = "preparing", Generation = "", CurrentMirror = record.SourceMirror,
            DesiredBinding = record.DesiredBinding with { StorageGeneration = null }
        }, TwigJsonContext.Default.ConnectionBindingTransitionRecord));
    private static string ConfigurationHash(TwigPaths paths)
        => Hash(string.Join('\n', paths.RepoConfigPath, HashFile(paths.RepoConfigPath), paths.ConfigPath, HashFile(paths.ConfigPath),
            paths.GlobalDisplayPath, HashFile(paths.GlobalDisplayPath)));
    private static string AttachmentPath(TwigPaths paths) => Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashFile(string path)
    {
        if (!File.Exists(path)) return "absent";
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static bool PathsEqual(string left, string right)
        => SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(left), Path.GetFullPath(right));
    private static void Require(Twig.Domain.Common.Result result)
    { if (!result.IsSuccess) throw new InvalidOperationException(result.Error); }
    private static void Require<T>(Twig.Domain.Common.Result<T> result)
    { if (!result.IsSuccess) throw new InvalidOperationException(result.Error); }
    public void Dispose() => _registry.Dispose();
}
