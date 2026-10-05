using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Twig.Domain.Services.Claims;
using System.Diagnostics;

namespace Twig.Infrastructure.Auth;

internal sealed record ConnectionMigrationPreview(int Version, string State, string Digest, bool CanApply,
    string WorktreeRoot, string ConnectionRef, string? IdentityName, string? Method,
    string? SourceMirror, int? MirrorVersion, int? DurableVersion, IReadOnlyList<string> Blockers,
    IReadOnlyList<string> NextSteps);

internal interface IConnectionMigrationService
{
    Task<ConnectionMigrationPreview> PreviewAsync(TwigConfiguration configuration, TwigPaths paths,
        string? identityName, string? method, CancellationToken ct = default);
    Task<ConnectionMigrationPreview> ApplyAsync(TwigConfiguration configuration, TwigPaths paths,
        string? identityName, string? method, string confirmedDigest, CancellationToken ct = default);
}

/// <summary>Versioned native import authority; migration never guesses an actor or publishes unfinished work.</summary>
internal sealed class ConnectionMigrationService : IConnectionMigrationService, IDisposable
{
    private readonly string _home;
    private readonly IConnectionBindingService _bindings;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly Func<Process, string>? _commandLineReader;

    internal ConnectionMigrationService(string userHome, IConnectionBindingService bindings, Func<Process, string>? commandLineReader = null)
    {
        _home = Path.GetFullPath(userHome);
        _bindings = bindings;
        _registry = new SqliteSystemWorktreeRegistry(Path.Combine(_home, "system.db"), TimeProvider.System);
        _commandLineReader = commandLineReader;
    }

    public async Task<ConnectionMigrationPreview> PreviewAsync(TwigConfiguration configuration, TwigPaths paths,
        string? identityName, string? method, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var blockers = new List<string>();
        var connectionRef = ConnectionRefResolver.Compute(configuration);
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir, out var anchor, out var failure))
            return Report("blocked", "", paths, connectionRef, identityName, method, null, null, null,
                [$"migration-worktree-required: {failure}; initialize a real repository worktree before migration."]);
        var fingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
        var remoteWrites = await _registry.ReadConnectionRemoteWritesAsync(fingerprint, unsettledOnly: true, ct).ConfigureAwait(false);
        if (!remoteWrites.IsSuccess) blockers.Add("migration-remote-write-evidence-unavailable: " + remoteWrites.Error);
        else foreach (var write in remoteWrites.Value)
            blockers.Add($"migration-remote-write-outcome-unknown: intent {write.Intent.IntentId} ({write.Intent.RequestDigest}) must be reconciled under its original bound actor; process closure or lease expiry is not settled remote evidence.");
        var existing = await _registry.ReadConnectionMigrationAsync(fingerprint, ct).ConfigureAwait(false);
        if (!existing.IsSuccess) throw new InvalidOperationException(existing.Error);
        var record = existing.Value;
        if (record is not null)
        {
            ValidateRecord(record, paths, fingerprint, connectionRef);
            if (!string.Equals(identityName?.Trim(), record.IdentityName, StringComparison.Ordinal))
                blockers.Add("migration-mapping-conflict: explicitly select the identity recorded by the original native intent; migration cannot become a binding switch.");
            if (record.State == "active")
            {
                var currentPaths = TwigPaths.BuildPaths(paths.TwigDir, configuration, paths.StartDir);
                _ = MirrorAdmission.Acquire(currentPaths, Path.Combine(_home, "system.db"))
                    ?? throw new InvalidOperationException("migration-incomplete: activated native intent has no admitted mirror. Restore its protected artifacts; do not create another intent.");
                return Report("active", Digest(record), paths, connectionRef, record.IdentityName, method,
                    record.SourceMirror, SqliteCacheStore.SchemaVersion, SqliteCacheStore.DurableSchemaVersion, blockers);
            }
            if (!ContextMatches(record, paths))
                blockers.Add("migration-context-changed: portable configuration or attachment changed while activation was interrupted. Restore the original intent context before recovery; no automatic policy/claim rewrite.");
        }
        if (string.IsNullOrWhiteSpace(identityName))
            blockers.Add("migration-binding-setup-required: supply --identity to explicitly map this connection and its preserved work. A global login, token audience or cached display name is not connection intent.");
        if (method is not null && method is not ("aad" or "pat"))
            blockers.Add("migration-method-unsupported: select aad or pat; no alternate method is inferred.");
        if (string.IsNullOrWhiteSpace(configuration.Organization) || string.IsNullOrWhiteSpace(configuration.Project)
            || !File.Exists(paths.RepoConfigPath))
            blockers.Add("migration-portable-endpoint-required: establish checked-in twig.json coordinates and policy explicitly; migration does not infer an endpoint or rewrite portable policy.");

        var sources = LegacyMirrorPaths(paths, configuration).Where(File.Exists).ToArray();
        var source = record is null ? (sources.Length == 1 ? sources[0] : null) :
            File.Exists(record.SourceMirror) ? record.SourceMirror : record.CurrentMirror;
        if (record is null && sources.Length > 1)
            blockers.Add("migration-layout-ambiguous: multiple legacy mirrors exist; inspect and preserve them separately with their compatible Twig version before selecting one layout.");
        if (source is null || !File.Exists(source))
            blockers.Add("migration-source-missing: no supported legacy mirror exists. Use explicit initialization, not a fabricated empty migration.");
        int? mirrorVersion = null;
        int? durableVersion = null;
        if (record is null && source is not null)
        {
            var sourceDurable = SqliteCacheStore.DeriveDurableDataSource(source);
            if (!SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(sourceDurable), Path.GetFullPath(CurrentDurable(paths))) && File.Exists(CurrentDurable(paths)))
                blockers.Add("migration-durable-target-conflict: two durable stores exist. Preserve and resolve both before activation; no pending database is overwritten.");
        }
        if (source is not null && File.Exists(source))
        {
            try
            {
                using var mirror = OpenReadOnly(source);
                mirrorVersion = ReadMirrorVersion(mirror);
                if (mirrorVersion is not (16 or 17))
                    blockers.Add($"migration-mirror-version-unsupported: observed {mirrorVersion}; only split versions 16 and 17 preserve the current staged-identity contract. Retain all files and use the compatible prior Twig version for explicit recovery.");
                if (!HasTable(mirror, "work_items") || !HasTable(mirror, "metadata"))
                    blockers.Add("migration-source-shape-unsupported: mirror is missing required tables; retain it for recovery rather than rebuilding.");
                foreach (var table in new[] { "pending_changes", "publish_id_map", "seed_links" })
                    if (HasTable(mirror, table))
                        blockers.Add("migration-unsplit-durable-state: durable tables remain in the mirror; use the compatible prior version to preserve this layout. Nothing is dropped or flushed.");
                CheckIntegrity(mirror);
                var durable = record is null ? SqliteCacheStore.DeriveDurableDataSource(source) :
                    File.Exists(record.SourceDurable) ? record.SourceDurable : CurrentDurable(paths);
                if (!File.Exists(durable))
                    blockers.Add("migration-durable-source-missing: the split pending.db is absent. Restore it before activation; an empty replacement cannot establish preservation.");
                else
                {
                    using var durableDb = OpenReadOnly(durable);
                    durableVersion = ScalarInt(durableDb, "PRAGMA user_version;");
                    if (durableVersion is < 1 or > SqliteCacheStore.DurableSchemaVersion)
                        blockers.Add($"migration-durable-version-unsupported: observed {durableVersion}; retain the original pending.db and use a compatible Twig version.");
                    CheckIntegrity(durableDb);
                    if (!HasTable(durableDb, "pending_changes") || !HasTable(durableDb, "publish_id_map") || !HasTable(durableDb, "seed_links"))
                        blockers.Add("migration-durable-shape-unsupported: durable tables are missing; restore them before activation.");
                    var hasReceipts = HasTable(durableDb, "proposal_receipts");
                    var openIntentSql = hasReceipts
                        ? "SELECT count(*) FROM publish_intents i WHERE published_id IS NULL AND NOT EXISTS (SELECT 1 FROM proposal_receipts r WHERE r.publish_identity=i.staged_identity AND r.publish_intent_recorded_at=i.recorded_at AND r.kind IN ('Readback','Superseded'));"
                        : "SELECT count(*) FROM publish_intents WHERE published_id IS NULL;";
                    if (HasTable(durableDb, "publish_intents") && ScalarInt(durableDb, openIntentSql) > 0)
                        blockers.Add("migration-inflight-publish-intent: establish the original remote outcome through native reconciliation before migrating; the intent remains preserved.");
                    var inFlightSql = hasReceipts
                        ? "SELECT count(*) FROM proposal_operations o WHERE state IN ('Applying','Applied') AND NOT EXISTS (SELECT 1 FROM proposal_receipts r WHERE r.digest=o.digest AND r.op_id=o.op_id);"
                        : "SELECT count(*) FROM proposal_operations WHERE state IN ('Applying','Applied');";
                    if (HasTable(durableDb, "proposal_operations") && ScalarInt(durableDb, inFlightSql) > 0)
                        blockers.Add("migration-inflight-proposal: settle its original native remote outcome before migration; historical rows are never rewritten.");
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                blockers.Add("migration-source-unreadable: " + ex.Message + " Retain the original files and restore access/integrity before activation.");
            }
        }
        ValidateLocalDocuments(paths, fingerprint, connectionRef, blockers);
        var identities = await _bindings.ListIdentitiesAsync(ct).ConfigureAwait(false);
        var selected = identities.SingleOrDefault(x => x.Name == identityName?.Trim());
        var importMethod = selected?.Method ?? method;
        if (selected is null && importMethod is null)
            blockers.Add("migration-credential-setup-required: register --identity explicitly, or select --method aad/pat to import its legacy material. No ambient account or method fallback exists.");
        if (selected is null && importMethod == "aad")
        {
            var entry = new TwigRefreshTokenStore(Path.Combine(_home, ".refresh-token")).TryRead();
            if (entry is null || string.IsNullOrWhiteSpace(entry.TenantId) || string.IsNullOrWhiteSpace(entry.ObjectId)
                || string.IsNullOrWhiteSpace(entry.ClientId) || string.IsNullOrWhiteSpace(entry.AuthorityHost)
                || string.IsNullOrWhiteSpace(entry.RefreshToken))
                blockers.Add("migration-principal-evidence-incomplete: legacy AAD material needs tenant/object/client/authority and a refresh credential. Enroll the intended identity explicitly; cached audience/display labels are insufficient.");
        }
        if (selected is null && importMethod == "pat" && string.IsNullOrWhiteSpace(ReadLegacyPat(paths)))
            blockers.Add("migration-pat-input-missing: no per-worktree legacy PAT exists; enroll explicitly with 'twig auth pat --identity <name> --stdin'. Secrets are never accepted in migration argv.");
        if (selected is not null && method is not null && selected.Method != method)
            blockers.Add("migration-method-conflict: the selected registered identity has a different immutable method; enroll a separate identity instead.");
        var defaultBinding = await _registry.FindDefaultBindingAsync(connectionRef, ct).ConfigureAwait(false);
        if (!defaultBinding.IsSuccess) throw new InvalidOperationException(defaultBinding.Error);
        if (defaultBinding.Value is { } d)
        {
            var binding = await _registry.FindBindingByIdAsync(d.BindingId, ct).ConfigureAwait(false);
            if (!binding.IsSuccess || binding.Value is null || binding.Value.IdentityId != selected?.IdentityId)
                blockers.Add("migration-default-conflict: a different or unreadable default already owns this endpoint. Migration cannot silently switch it; use the separate guarded binding transition.");
        }
        var worktree = await _registry.FindWorktreeAsync(fingerprint, ct).ConfigureAwait(false);
        if (!worktree.IsSuccess || worktree.Value is { } wt && (wt.RetiredAt is not null || wt.ConnectionRef != connectionRef))
            blockers.Add("migration-worktree-registry-conflict: repair the retired/mismatching/unreadable worktree through its ordinary lifecycle; migration cannot reassign or unretire it.");
        var claims = await _registry.FindClaimsForWorktreeAsync(fingerprint, ct).ConfigureAwait(false);
        if (!claims.IsSuccess) blockers.Add("migration-claims-unreadable: establish native claim evidence before activation.");
        else if (claims.Value.Any(x => x.State is not (ClaimStates.Released or ClaimStates.Superseded)))
            blockers.Add("migration-reserved-claim: settle reserved/active claim ownership through its ordinary authorized lifecycle before migration; no claim is released or reminted.");

        blockers.AddRange(LegacyHostQuiescence.InspectProcesses(_commandLineReader));
        var protectedPaths = ProtectedPaths(paths, configuration, source, record).ToArray();
        try
        {
            var handles = LegacyHostQuiescence.AcquireExclusiveHandles(protectedPaths);
            foreach (var handle in handles) handle.Dispose();
        }
        catch (InvalidOperationException ex) { blockers.Add(ex.Message); }
        var context = record is null
            ? blockers.Count == 0 ? CaptureSourceSnapshot(paths, fingerprint, connectionRef, identityName?.Trim(), importMethod, protectedPaths, null).Digest : string.Empty
            : Digest(record);
        return Report(record is null ? "preview" : "preparing", context,
            paths, connectionRef, identityName?.Trim(), importMethod, source, mirrorVersion, durableVersion, blockers);
    }

    public async Task<ConnectionMigrationPreview> ApplyAsync(TwigConfiguration configuration, TwigPaths paths,
        string? identityName, string? method, string confirmedDigest, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(configuration, paths, identityName, method, ct).ConfigureAwait(false);
        if (!preview.CanApply) return preview;
        if (!string.Equals(confirmedDigest, preview.Digest, StringComparison.Ordinal))
            throw new InvalidOperationException("migration-preview-stale: exact confirmed preview digest no longer matches. Rerun preview and review the changed sources; no activation occurred.");
        if (preview.State == "active") return preview;
        var anchor = WorktreeAnchorDetector.Detect(paths.StartDir)!.Value;
        var fingerprint = WorktreeFingerprintProvider.CanonicalJson(anchor);
        // Serialize migration with all attachment CAS writers. No authority is an owner assertion.
        Directory.CreateDirectory(paths.TwigDir);
        using var attachmentLock = new FileStream(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentLockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var native = await _registry.ReadConnectionMigrationAsync(fingerprint, ct).ConfigureAwait(false);
        if (!native.IsSuccess) throw new InvalidOperationException(native.Error);
        var record = native.Value;
        var handles = LegacyHostQuiescence.AcquireExclusiveHandles(ProtectedPaths(paths, configuration, preview.SourceMirror, record));
        try
        {
            var uncertainWrites = await _registry.ReadConnectionRemoteWritesAsync(fingerprint, unsettledOnly: true, ct).ConfigureAwait(false);
            if (!uncertainWrites.IsSuccess || uncertainWrites.Value.Count != 0)
                throw new InvalidOperationException("migration-remote-write-outcome-unknown: original native mutation evidence is unavailable or unsettled; activation did not occur.");
            var hosts = LegacyHostQuiescence.InspectProcesses(_commandLineReader);
            if (hosts.Count > 0) throw new InvalidOperationException(string.Join('\n', hosts));
            if (record is null)
            {
                var snapshot = CaptureSourceSnapshot(paths, fingerprint, preview.ConnectionRef,
                    identityName?.Trim(), preview.Method,
                    ProtectedPaths(paths, configuration, preview.SourceMirror, null), handles);
                if (preview.Digest != snapshot.Digest)
                    throw new InvalidOperationException("migration-preview-stale: source changed before the exclusive fence. Rerun preview; no activation occurred.");
                var identities = await _bindings.ListIdentitiesAsync(ct).ConfigureAwait(false);
                var identity = identities.SingleOrDefault(x => x.Name == identityName?.Trim());
                if (identity is null)
                {
                    identity = preview.Method == "aad"
                        ? await _bindings.RegisterAadIdentityAsync(identityName!.Trim(),
                            new TwigRefreshTokenStore(Path.Combine(_home, ".refresh-token")).TryRead()
                                ?? throw new InvalidOperationException("Legacy credential changed; rerun preview."), ct).ConfigureAwait(false)
                        : await _bindings.RegisterPatIdentityAsync(identityName!.Trim(), configuration.Organization,
                            ReadLegacyPat(paths) ?? throw new InvalidOperationException("Legacy PAT changed; rerun preview."), ct).ConfigureAwait(false);
                }
                await _bindings.VerifyIdentityCredentialAsync(identity.Name, configuration.Organization, ct).ConfigureAwait(false);
                var binding = await _bindings.CreateBindingAsync(configuration.Organization, configuration.Project, identity.Name,
                    makeDefault: true, ct).ConfigureAwait(false);
                var generation = Guid.NewGuid().ToString("N");
                record = new ConnectionMigrationRecord(1, fingerprint, anchor.WorktreeRoot, ConnectionRefResolver.Compute(configuration),
                    generation, "preparing", identity.Name, binding.BindingId, preview.SourceMirror!,
                    SqliteCacheStore.DeriveDurableDataSource(preview.SourceMirror!),
                    Path.Combine(paths.TwigDir, "cache", "admitted-" + generation + ".db"),
                    snapshot.ConfigurationHash, snapshot.AttachmentHash, snapshot.AttachmentRevision);
                var begun = await _registry.BeginConnectionMigrationAsync(record, ct).ConfigureAwait(false);
                if (!begun.IsSuccess) throw new InvalidOperationException(begun.Error);
            }
            ValidateRecord(record, paths, fingerprint, ConnectionRefResolver.Compute(configuration));
            if (!ContextMatches(record, paths))
                throw new InvalidOperationException("migration-context-changed: attachment or portable policy changed; restore the original context to recover.");
            Directory.CreateDirectory(Path.GetDirectoryName(record.CurrentMirror)!);
            MoveDatabase(record.SourceMirror, record.CurrentMirror, retireSource: true);
            MoveDatabase(record.SourceDurable, CurrentDurable(paths));
            foreach (var legacy in LegacyMirrorPaths(paths, configuration))
            {
                if (File.Exists(legacy)) throw new InvalidOperationException("migration-legacy-reopen-race: a legacy host reopened storage. Explicitly close it and resume the original intent.");
                Directory.CreateDirectory(legacy); // A real filesystem type fence, not a schema marker an old binary ignores.
            }
        }
        finally { foreach (var handle in handles) handle.Dispose(); }

        // From here any interrupted step remains fenced by the native preparing intent.
        using (var store = SqliteCacheStore.OpenMigrationMirror(record!.CurrentMirror))
        {
            var connection = store.GetConnection();
            using var tx = connection.BeginTransaction();
            using var reset = connection.CreateCommand();
            reset.Transaction = tx;
            reset.CommandText = """
                DELETE FROM work_items WHERE is_seed = 0 AND is_dirty = 0;
                DELETE FROM work_item_links;
                DELETE FROM work_item_link_verifications;
                DELETE FROM field_definitions;
                DELETE FROM process_types;
                DELETE FROM iteration_calendar;
                DELETE FROM navigation_history;
                INSERT INTO metadata(key,value) VALUES ('binding_generation',$generation)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            reset.Parameters.AddWithValue("$generation", record.Generation);
            reset.ExecuteNonQuery();
            tx.Commit();
        }
        if (!File.Exists(AttachmentPath(paths)))
        {
            // Only missing documents are provisioned; existing attachment/claims/policy are never replaced.
            using var attachment = new WorktreeLocalAttachmentStore(paths, configuration, TimeProvider.System);
            var initialized = await attachment.InitializeAsync(ct).ConfigureAwait(false);
            if (!initialized.IsSuccess) throw new InvalidOperationException(initialized.Error);
        }
        var upsertConnection = await _registry.UpsertConnectionAsync(record.ConnectionRef, configuration.Organization,
            configuration.Project, configuration.Team, ct).ConfigureAwait(false);
        if (!upsertConnection.IsSuccess) throw new InvalidOperationException(upsertConnection.Error);
        var upsertWorktree = await _registry.UpsertWorktreeAsync(fingerprint, record.ConnectionRef, anchor.WorktreeRoot, ct).ConfigureAwait(false);
        if (!upsertWorktree.IsSuccess) throw new InvalidOperationException(upsertWorktree.Error);
        var marker = new MirrorAdmissionDocument(1, Path.Combine(_home, "system.db"), fingerprint, record.ConnectionRef,
            record.BindingId, record.Generation, Path.GetFileName(record.CurrentMirror));
        var markerPath = Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile);
        var temporary = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(marker, TwigJsonContext.Default.MirrorAdmissionDocument), ct).ConfigureAwait(false);
        File.Move(temporary, markerPath, overwrite: true);
        var remainingHosts = LegacyHostQuiescence.InspectProcesses(_commandLineReader);
        if (remainingHosts.Count > 0) throw new InvalidOperationException(string.Join('\n', remainingHosts));
        // Recheck preserved attachment context; newly provisioned empty attachment is expected only on legacy ingress.
        if (record.AttachmentHash != "absent" && record.AttachmentHash != HashFile(AttachmentPath(paths)))
            throw new InvalidOperationException("migration-attachment-cas-mismatch: attachment drifted; original intent remains fenced.");
        var completed = await _registry.CompleteConnectionMigrationAsync(record, ct).ConfigureAwait(false);
        if (!completed.IsSuccess) throw new InvalidOperationException(completed.Error);
        var activePaths = TwigPaths.BuildPaths(paths.TwigDir, configuration, paths.StartDir);
        _ = MirrorAdmission.Acquire(activePaths, Path.Combine(_home, "system.db")) ?? throw new InvalidOperationException("Migration activation readback failed.");
        return Report("active", Digest(record), activePaths, record.ConnectionRef, record.IdentityName,
            preview.Method, record.SourceMirror, SqliteCacheStore.SchemaVersion, SqliteCacheStore.DurableSchemaVersion, []);
    }

    private static void ValidateRecord(ConnectionMigrationRecord record, TwigPaths paths, string fingerprint, string connectionRef)
    {
        var mirrorFile = Path.GetFileName(record.CurrentMirror);
        var activeMirrorFile = mirrorFile.StartsWith("admitted-", StringComparison.Ordinal)
            && mirrorFile.EndsWith(".db", StringComparison.Ordinal)
            && Guid.TryParseExact(mirrorFile.AsSpan(9, mirrorFile.Length - 12), "N", out _);
        var expectedMirrorFile = record.State == "active" && activeMirrorFile
            ? mirrorFile : "admitted-" + record.Generation + ".db";
        if (record.Version != 1 || record.State is not ("preparing" or "active") || record.Fingerprint != fingerprint
            || record.ConnectionRef != connectionRef || !SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(record.WorktreeRoot), Path.GetFullPath(paths.RepoRoot))
            || !SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(record.CurrentMirror), Path.GetFullPath(Path.Combine(paths.TwigDir, "cache", expectedMirrorFile)))
            || !Guid.TryParseExact(record.Generation, "N", out _))
            throw new InvalidOperationException("migration-intent-unsupported: native version/context is inconsistent. Preserve all artifacts and use the compatible Twig version for recovery.");
    }

    private static ConnectionMigrationPreview Report(string state, string digest, TwigPaths paths, string connection,
        string? identity, string? method, string? source, int? mirror, int? durable, IReadOnlyList<string> blockers)
        => new(1, state, digest, blockers.Count == 0, paths.RepoRoot, connection, identity, method, source, mirror, durable, blockers,
            blockers.Count == 0 ? [state == "active" ? "Migration is active. Reconnect through the admitted binding; legacy paths and authentication are retired." :
                "Apply this exact preview digest after explicitly closing affected legacy hosts/providers/stores. Credentials, unfinished work and portable policy are preserved; no publication or discard occurs."] :
                ["Resolve each blocker separately and rerun preview. Never delete pending.db, rewrite a journal or kill/restart another owner's process; there is no force bypass."]);

    private static IEnumerable<string> LegacyMirrorPaths(TwigPaths paths, TwigConfiguration configuration)
    {
        yield return Path.Combine(paths.TwigDir, "cache", "twig.db");
        yield return Path.Combine(paths.TwigDir, "twig.db");
        yield return Path.Combine(paths.TwigDir, TwigPaths.SanitizePathSegment(configuration.Organization),
            TwigPaths.SanitizePathSegment(configuration.Project), "twig.db");
    }

    private IEnumerable<string> ProtectedPaths(TwigPaths paths, TwigConfiguration configuration, string? source, ConnectionMigrationRecord? record)
    {
        yield return Path.Combine(_home, ".legacy-host-capability");
        foreach (var legacy in LegacyMirrorPaths(paths, configuration))
        {
            yield return Path.Combine(Path.GetDirectoryName(legacy)!, ".legacy-host-capability");
            yield return legacy;
            yield return legacy + "-wal";
            yield return legacy + "-shm";
        }
        if (source is not null)
        {
            yield return source;
            yield return source + "-wal";
            yield return source + "-shm";
            var originalDurable = record?.SourceDurable ?? SqliteCacheStore.DeriveDurableDataSource(source);
            var durable = File.Exists(originalDurable) ? originalDurable : CurrentDurable(paths);
            yield return durable;
            yield return durable + "-wal";
            yield return durable + "-shm";
        }
    }

    internal static void MoveDatabase(string source, string destination, bool retireSource = false)
    {
        if (SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(source), Path.GetFullPath(destination))) return;
        if (File.Exists(source))
        {
            if (File.Exists(destination)) throw new InvalidOperationException("migration-target-conflict: both source and destination exist. Preserve both and resolve the native intent; no file is overwritten.");
            File.Move(source, destination);
        }
        else if (!File.Exists(destination)) throw new InvalidOperationException("migration-source-missing: restore the original database before resuming.");
        // Keep the retired entry point closed even if a later sidecar or durable move fails.
        if (retireSource) Directory.CreateDirectory(source);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(source + suffix))
            {
                if (File.Exists(destination + suffix)) throw new InvalidOperationException("migration-sidecar-conflict: preserve both SQLite sidecars before recovery.");
                File.Move(source + suffix, destination + suffix);
            }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        SQLitePCL.Batteries.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            connection.Open();
            SqlitePlanJournalRepository.RegisterSourcePathCollation(connection, SqlitePlanJournalRepository.CreateDefaultSourcePathComparer());
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static int ReadMirrorVersion(SqliteConnection connection)
    {
        if (!HasTable(connection, "metadata")) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
        return int.TryParse(command.ExecuteScalar()?.ToString(), out var value) ? value : 0;
    }

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void CheckIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidOperationException("SQLite integrity check refused; no data is rebuilt.");
    }

    private static void ValidateLocalDocuments(TwigPaths paths, string fingerprint, string connection, List<string> blockers)
    {
        try
        {
            var layoutPath = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName);
            var worktreePath = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.WorktreeFileName);
            var attachmentPath = AttachmentPath(paths);
            var present = new[] { layoutPath, worktreePath, attachmentPath }.Count(File.Exists);
            if (present == 0) return;
            if (present != 3) { blockers.Add("migration-attachment-partial: restore complete attachment/layout/fingerprint documents before activation."); return; }
            var layout = JsonSerializer.Deserialize(File.ReadAllText(layoutPath), TwigJsonContext.Default.LayoutMarkerDocument);
            var worktree = JsonSerializer.Deserialize(File.ReadAllText(worktreePath), TwigJsonContext.Default.WorktreeFingerprintDocument);
            var attachment = JsonSerializer.Deserialize(File.ReadAllText(attachmentPath), TwigJsonContext.Default.AttachmentDocument);
            if (layout is null || layout.Version != LayoutMarkerDocument.CurrentVersion || layout.Schema != LayoutMarkerDocument.CurrentSchema
                || worktree is null || worktree.Version != WorktreeFingerprintDocument.CurrentVersion || worktree.Schema != WorktreeFingerprintDocument.CurrentSchema
                || attachment is null || attachment.Version != AttachmentDocument.CurrentVersion || attachment.Schema != AttachmentDocument.CurrentSchema)
                blockers.Add("migration-attachment-version-unsupported: retain the original documents; use the compatible Twig version.");
            if (worktree is not null && JsonSerializer.Serialize(worktree.WorktreeFingerprint, TwigJsonContext.Default.WorktreeFingerprintTuple) != fingerprint)
                blockers.Add("migration-fingerprint-mismatch: attachment belongs to another worktree; no automatic identity or claim repair.");
            if (attachment is not null && attachment.ConnectionRef != connection)
                blockers.Add("migration-connection-mismatch: attachment does not match the portable endpoint.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { blockers.Add("migration-attachment-unreadable: restore the original documents before activation."); }
    }

    private static string? ReadLegacyPat(TwigPaths paths)
    {
        if (!File.Exists(paths.ConfigPath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(paths.ConfigPath));
        return document.RootElement.TryGetProperty("auth", out var auth) && auth.TryGetProperty("pat", out var pat)
            && pat.ValueKind == JsonValueKind.String ? pat.GetString() : null;
    }

    private static bool ContextMatches(ConnectionMigrationRecord record, TwigPaths paths)
    {
        if (record.ConfigurationHash != ConfigurationHash(paths)) return false;
        if (record.AttachmentHash == HashFile(AttachmentPath(paths))) return true;
        if (record.AttachmentHash != "absent" || !File.Exists(AttachmentPath(paths))) return false;
        var document = JsonSerializer.Deserialize(File.ReadAllText(AttachmentPath(paths)), TwigJsonContext.Default.AttachmentDocument);
        return document == AttachmentDocument.Empty(record.ConnectionRef);
    }

    private (string Digest, string ConfigurationHash, string AttachmentHash, long AttachmentRevision) CaptureSourceSnapshot(
        TwigPaths paths, string fingerprint, string connectionRef, string? identity,
        string? method, IEnumerable<string> protectedPaths, IReadOnlyList<FileStream>? handles)
    {
        string FileDigest(string path)
        {
            var handle = handles?.FirstOrDefault(x => SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(x.Name), Path.GetFullPath(path)));
            if (handle is null) return HashFile(path);
            handle.Position = 0;
            var hash = Convert.ToHexStringLower(SHA256.HashData(handle));
            handle.Position = 0;
            return hash;
        }
        var configurationHash = ConfigurationHash(paths);
        var attachmentHash = HashFile(AttachmentPath(paths));
        var attachmentRevision = ReadAttachmentRevision(paths);
        var digest = Hash(string.Join('\n', fingerprint, connectionRef, identity, method, configurationHash,
            attachmentHash, HashFile(Path.Combine(_home, ".refresh-token")),
            string.Join('\n', protectedPaths.Where(x => !x.EndsWith("-shm", StringComparison.Ordinal))
                .Select(x => x + "=" + FileDigest(x)))));
        return (digest, configurationHash, attachmentHash, attachmentRevision);
    }

    private static long ReadAttachmentRevision(TwigPaths paths) => File.Exists(AttachmentPath(paths))
        ? JsonSerializer.Deserialize(File.ReadAllText(AttachmentPath(paths)), TwigJsonContext.Default.AttachmentDocument)!.Revision : -1;
    private static string AttachmentPath(TwigPaths paths) => Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
    private static string CurrentDurable(TwigPaths paths) => Path.Combine(paths.TwigDir, "cache", "pending.db");
    private static string ConfigurationHash(TwigPaths paths) => Hash(HashFile(paths.RepoConfigPath) + "\n" + HashFile(paths.ConfigPath));
    private static string Digest(ConnectionMigrationRecord record) => Hash(JsonSerializer.Serialize(record with { State = "preparing" }, TwigJsonContext.Default.ConnectionMigrationRecord));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashFile(string path) => File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    public void Dispose() => _registry.Dispose();
}

