using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Domain.Services;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed record PreservationMigrationResult(
    string State, string Digest, string BackupDirectory, string? IntentId = null);

internal sealed record PreservationMigrationMovement(string Source, string Destination, long Rows, string Effect);

internal sealed record PreservationMigrationReport(
    PreservationMigrationResult Migration,
    bool CanApply,
    PreservationMigrationMovement[] Movements,
    string[] UnsupportedWork,
    string[] RollbackPreconditions,
    string[] NextSteps);

internal sealed record PreservationBackupManifest(
    int Version, string Generation, string SourceDigest, int MirrorVersion, int DurableVersion,
    string SnapshotDirectory, StoreParityClassReport[] Classes);

/// <summary>
/// Explicitly retains the local-owned portion of one admitted generation. Provider observations
/// stay in the mirror; this is not legacy identity import or provider rebinding.
/// </summary>
internal sealed class PreservationMigrationService(
    SqliteCacheStore store, TwigPaths paths, string resourceScope,
    Action<string>? checkpoint = null)
{
    private static readonly HashSet<string> OwnedClasses = new(StringComparer.Ordinal)
    {
        "pending.seed_work_items", "pending.workspace_context",
        "pending.workspace_navigation_history", "pending.preservation_migrations",
        "pending.workspace-navigation-sequence",
        "pending.seed_local_identities",
    };

    internal PreservationMigrationResult Preview()
    {
        using var operation = store.AcquireOperation();
        var generation = RequireGeneration();
        var existing = ReadIntent();
        if (existing is not null)
        {
            if (existing.Value.Generation != generation)
                throw new InvalidOperationException("preservation-generation-mismatch: resume the intent under its original admitted generation.");
            return existing.Value.Result;
        }
        RequirePristineTarget();
        var report = InspectSource();
        var digest = Digest(generation, report.Classes);
        return new("preview", digest, BackupDirectory(digest));
    }

    internal PreservationMigrationReport Describe(PreservationMigrationResult? result = null)
    {
        using var operation = store.AcquireOperation();
        var prior = store.ActiveTransaction;
        using var transaction = prior is null ? store.GetConnection().BeginTransaction(deferred: true) : null;
        if (transaction is not null) store.ActiveTransaction = transaction;
        try
        {
            var preview = result ?? Preview();
            var source = InspectSource();
            var sourceChanged = preview.State == "preparing"
                && Digest(RequireGeneration(), source.Classes) != preview.Digest;
            string[] unsupported = sourceChanged
                ? ["preservation-source-changed: new local work changed the retained preparation; cancel that exact intent and review again."]
                : [];
            using var seeds = Command();
            seeds.CommandText = "SELECT COUNT(*) FROM main.work_items WHERE is_seed=1;";
            var seedCount = Convert.ToInt64(seeds.ExecuteScalar(), CultureInfo.InvariantCulture);
            long Rows(string name) => source.Classes.Single(entry => entry.ClassName == name).RowCount
                ?? throw new InvalidOperationException("preservation-source-unverified: a source row count is unavailable.");
            var movements = new List<PreservationMigrationMovement>
            {
                new("main.work_items (seeds)", "pending.seed_work_items", seedCount, "copy full payload; retain original"),
                new("seed staged identities", "pending.seed_local_identities", seedCount, "mint independent local identities"),
                new("main.context", "pending.workspace_context", Rows("main.context"), "copy all opaque context; retain original"),
                new("main.navigation_history", "pending.workspace_navigation_history", Rows("main.navigation_history"), "copy exact IDs, timestamps, order and cursor"),
                new("main navigation sequence", "pending navigation sequence", Rows("mirror.navigation-sequence"), "preserve allocator position"),
            };
            movements.AddRange(source.Classes
                .Where(entry => entry.ClassName.StartsWith("pending.", StringComparison.Ordinal)
                    && !OwnedClasses.Contains(entry.ClassName))
                .Select(entry => new PreservationMigrationMovement(entry.ClassName, entry.ClassName,
                    entry.RowCount ?? throw new InvalidOperationException("preservation-source-unverified: durable inventory is unavailable."),
                    "retain existing durable evidence unchanged")));
            return new(preview, !sourceChanged && (preview.State is "preview" or "preparing"), movements.ToArray(), unsupported,
                [
                    "The original admitted ADO generation must remain selected.",
                    $"The reviewed write-coverage matrix must remain version {PreservationWriteCoverage.Version}.",
                    "Original and retained seed, context, navigation and sequence values must still match.",
                    "Protected workspace-local backups and originals are retained; rollback never deletes them.",
                ],
                sourceChanged
                    ? [$"Cancel the stale preparation without deleting work: twig workspace migrate --cancel-intent {preview.IntentId} --confirm {preview.Digest}",
                        "Run twig workspace migrate again to review the changed source."]
                    : preview.State == "active"
                    ? [$"Return to original readers: twig workspace migrate --rollback --confirm {preview.Digest}"]
                    : preview.State is "preview" or "preparing"
                        ? [
                            "Review every movement and rollback precondition. No provider publication or pending flush is performed.",
                            $"Explicitly authorize this cutover: twig workspace migrate --confirm {preview.Digest}",
                            "An interrupted preparation resumes only with its original digest; changed work requires explicit cancellation and a fresh review.",
                        ]
                        : ["This intent is terminal; inspect the retained originals and history before further work."]);
        }
        finally { store.ActiveTransaction = prior; }
    }

    internal async Task<PreservationMigrationResult> ApplyAsync(string confirmedDigest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PreservationWriteCoverage.RequireComplete();
        using var exclusive = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
        using var operation = store.AcquireOperation();
        var generation = RequireGeneration();
        if (store.ActiveTransaction is not null)
            throw new InvalidOperationException("preservation-transaction-active: finish the current local operation before migration.");
        var existing = ReadIntent();
        if (existing is not null && (existing.Value.Generation != generation || existing.Value.Result.Digest != confirmedDigest))
            throw new InvalidOperationException("preservation-intent-conflict: only the exact original generation and source digest may resume this intent.");
        if (existing?.Result.State == "active")
        {
            VerifyOwnedParity();
            return existing.Value.Result;
        }
        if (existing?.Result.State == "rolled-back")
            throw new InvalidOperationException("preservation-intent-rolled-back: this retained intent is terminal; it cannot silently start a second migration.");

        RequirePristineTarget();
        var source = InspectSource();
        if (Digest(generation, source.Classes) != confirmedDigest)
            throw new InvalidOperationException("preservation-source-changed: source contents changed after review; no activation occurred.");
        var backup = BackupDirectory(confirmedDigest);
        var intentId = existing?.IntentId ?? Guid.NewGuid().ToString("N");
        if (existing is null)
        {
            using var intent = Command();
            intent.CommandText = """
                INSERT INTO pending.preservation_migrations(intent_id,slot,source_generation,source_digest,state,coverage_version,backup_directory)
                VALUES($intent,1,$generation,$digest,'preparing',$coverage,$backup);
                """;
            intent.Parameters.AddWithValue("$generation", generation);
            intent.Parameters.AddWithValue("$digest", confirmedDigest);
            intent.Parameters.AddWithValue("$backup", backup);
            intent.Parameters.AddWithValue("$coverage", PreservationWriteCoverage.Version);
            intent.Parameters.AddWithValue("$intent", intentId);
            intent.ExecuteNonQuery();
        }
        checkpoint?.Invoke("intent-recorded");
        QualifyBackup(backup, generation, confirmedDigest, source);
        checkpoint?.Invoke("backup-qualified");
        checkpoint?.Invoke("before-activation");

        using var transaction = store.GetConnection().BeginTransaction();
        store.ActiveTransaction = transaction;
        try
        {
            // BEGIN IMMEDIATE fences raw SQLite writers too. A fresh admission and fingerprint
            // inside this transaction close the gap between backup and the activation CAS.
            if (RequireGeneration() != generation || Digest(generation, InspectSource().Classes) != confirmedDigest)
                throw new InvalidOperationException("preservation-source-changed: source writer or generation changed before activation; the original admitted view remains selected.");
            RequirePristineTarget();
            QualifyBackup(backup, generation, confirmedDigest, source);

            using (var copy = Command())
            {
                copy.CommandText = $"""
                    INSERT INTO pending.workspace_context(key,value) SELECT key,value FROM main.context;
                    INSERT INTO pending.workspace_navigation_history(id,work_item_id,visited_at)
                        SELECT id,work_item_id,visited_at FROM main.navigation_history ORDER BY id;
                    DELETE FROM pending.sqlite_sequence WHERE name='workspace_navigation_history';
                    INSERT INTO pending.sqlite_sequence(name,seq)
                        SELECT 'workspace_navigation_history',seq FROM main.sqlite_sequence WHERE name='navigation_history';
                    """;
                copy.ExecuteNonQuery();
            }
            var seeds = new List<(int Alias, string Staged)>();
            using (var read = Command())
            {
                read.CommandText = "SELECT id,staged_identity FROM main.work_items WHERE is_seed=1 ORDER BY id;";
                using var reader = read.ExecuteReader();
                while (reader.Read()) seeds.Add((reader.GetInt32(0), reader.GetString(1)));
            }
            foreach (var seed in seeds)
            {
                ct.ThrowIfCancellationRequested();
                var local = LocalWorkItemId.New();
                while (local.Value == Guid.Parse(seed.Staged)) local = LocalWorkItemId.New();
                await new SqliteLocalWorkItemRepository(store)
                    .CreateAsync(local, FieldValues.Empty, ct).ConfigureAwait(false);
                using var copy = Command();
                copy.CommandText = $"""
                    INSERT INTO pending.seed_local_identities(staged_identity,local_id)
                        VALUES($staged,$local);
                    INSERT INTO pending.seed_work_items(local_id,{PreservedWorkspaceState.ItemColumns})
                    SELECT $local,{PreservedWorkspaceState.ItemColumns} FROM main.work_items WHERE id=$alias;
                    """;
                copy.Parameters.AddWithValue("$local", local.ToString());
                copy.Parameters.AddWithValue("$alias", seed.Alias);
                copy.Parameters.AddWithValue("$staged", seed.Staged);
                if (copy.ExecuteNonQuery() != 2)
                    throw new InvalidOperationException("preservation-seed-copy-failed: source seed changed inside activation.");
            }
            VerifyOwnedParity();
            using (var activate = Command())
            {
                activate.CommandText = """
                    UPDATE pending.preservation_migrations SET state='active'
                    WHERE slot=1 AND source_generation=$generation AND source_digest=$digest AND state='preparing'
                        AND intent_id=$intent AND coverage_version=$coverage AND backup_directory=$backup;
                    """;
                activate.Parameters.AddWithValue("$generation", generation);
                activate.Parameters.AddWithValue("$digest", confirmedDigest);
                activate.Parameters.AddWithValue("$coverage", PreservationWriteCoverage.Version);
                activate.Parameters.AddWithValue("$backup", backup);
                activate.Parameters.AddWithValue("$intent", intentId);
                if (activate.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("preservation-activation-cas-failed: original intent no longer owns activation.");
            }
            transaction.Commit();
        }
        finally
        {
            store.ActiveTransaction = null;
        }
        checkpoint?.Invoke("activated");
        return new("active", confirmedDigest, backup, intentId);
    }

    internal PreservationMigrationResult CancelPreparation(string confirmedDigest, string expectedIntentId)
    {
        using var exclusive = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
        using var operation = store.AcquireOperation();
        var generation = RequireGeneration();
        if (store.ActiveTransaction is not null)
            throw new InvalidOperationException("preservation-transaction-active: finish the current operation before cancelling preparation.");
        using var transaction = store.GetConnection().BeginTransaction();
        store.ActiveTransaction = transaction;
        try
        {
            var existing = ReadIntent();
            if (existing is null || existing.Value.IntentId != expectedIntentId
                || existing.Value.Generation != generation || existing.Value.Result.Digest != confirmedDigest
                || existing.Value.Result.State != "preparing")
                throw new InvalidOperationException("preservation-cancel-cas-failed: only the exact reviewed preparing intent can be cancelled.");
            using var cancel = Command();
            cancel.CommandText = """
                UPDATE pending.preservation_migrations SET state='cancelled',slot=NULL
                WHERE slot=1 AND intent_id=$intent AND source_generation=$generation
                    AND source_digest=$digest AND state='preparing' AND coverage_version=$coverage;
                """;
            cancel.Parameters.AddWithValue("$intent", expectedIntentId);
            cancel.Parameters.AddWithValue("$generation", generation);
            cancel.Parameters.AddWithValue("$digest", confirmedDigest);
            cancel.Parameters.AddWithValue("$coverage", PreservationWriteCoverage.Version);
            if (cancel.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("preservation-cancel-cas-failed: preparation changed; no intent or backup was removed.");
            transaction.Commit();
            return existing.Value.Result with { State = "cancelled" };
        }
        finally { store.ActiveTransaction = null; }
    }

    internal PreservationMigrationResult Rollback(string confirmedDigest)
    {
        PreservationWriteCoverage.RequireComplete();
        using var exclusive = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
        using var operation = store.AcquireOperation();
        var generation = RequireGeneration();
        if (store.ActiveTransaction is not null)
            throw new InvalidOperationException("preservation-transaction-active: finish the current local operation before rollback.");
        using var transaction = store.GetConnection().BeginTransaction();
        store.ActiveTransaction = transaction;
        try
        {
            var existing = ReadIntent();
            if (existing is null || existing.Value.Generation != generation
                || existing.Value.Result.Digest != confirmedDigest || existing.Value.Result.State != "active")
                throw new InvalidOperationException("preservation-rollback-cas-failed: only the exact active intent can be rolled back.");
            _ = InspectSource();
            VerifyOwnedParity();
            using var rollback = Command();
            rollback.CommandText = """
                UPDATE pending.preservation_migrations SET state='rolled-back'
                WHERE slot=1 AND source_generation=$generation AND source_digest=$digest AND state='active'
                    AND coverage_version=$coverage;
                """;
            rollback.Parameters.AddWithValue("$generation", generation);
            rollback.Parameters.AddWithValue("$digest", confirmedDigest);
            rollback.Parameters.AddWithValue("$coverage", PreservationWriteCoverage.Version);
            if (rollback.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("preservation-rollback-cas-failed: original write coverage or intent changed; no read route was switched.");
            transaction.Commit();
            return existing.Value.Result with { State = "rolled-back" };
        }
        finally { store.ActiveTransaction = null; }
    }

    internal LocalWorkItemId? FindSeedLocalId(StagedIdentity identity)
    {
        using var operation = store.AcquireOperation();
        using var command = Command();
        command.CommandText = "SELECT local_id FROM pending.seed_local_identities WHERE staged_identity=$identity;";
        command.Parameters.AddWithValue("$identity", identity.ToString());
        if (command.ExecuteScalar() is not string text) return null;
        return LocalWorkItemId.TryParse(text, out var id)
            ? id
            : throw new InvalidOperationException("preservation-seed-mapping-invalid: retained local identity cannot be read.");
    }

    internal void VerifyOwnedParity() => new PreservedWorkspaceState(store).VerifyParity();

    private StoreParityReport InspectSource()
    {
        var report = new StoreParityHarness(store, resourceScope, preserveOpaqueContext: true).Inspect();
        if (!report.IsConsistent)
            throw new InvalidOperationException("preservation-source-unverified: inventory, identity or pending evidence is unknown or inconsistent; preserve the originals.");
        using var references = Command();
        references.CommandText = """
            SELECT
                EXISTS(SELECT 1 FROM main.work_items
                    WHERE (is_seed=1 AND (id>=0 OR staged_identity IS NULL))
                       OR (is_seed=0 AND id<0))
              OR EXISTS(SELECT 1 FROM main.context
                    WHERE key='active_work_item_id' AND NOT EXISTS(
                        SELECT 1 FROM main.work_items WHERE CAST(id AS TEXT)=value))
              OR EXISTS(SELECT 1 FROM main.navigation_history n
                    WHERE n.id>2147483647 OR NOT EXISTS(SELECT 1 FROM main.work_items w WHERE w.id=n.work_item_id))
              OR EXISTS(SELECT 1 FROM main.context c
                    WHERE c.key='nav_history_cursor' AND NOT EXISTS(
                        SELECT 1 FROM main.navigation_history n WHERE CAST(n.id AS TEXT)=c.value))
              OR (EXISTS(SELECT 1 FROM main.navigation_history)
                    AND NOT EXISTS(SELECT 1 FROM main.context WHERE key='nav_history_cursor'));
            """;
        if (Convert.ToInt64(references.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("preservation-reference-unmapped: seed, active item or navigation cursor cannot be preserved unambiguously.");
        ValidateReaderPayloads();
        using var seeds = Command();
        seeds.CommandText = "SELECT staged_identity FROM main.work_items WHERE is_seed=1;";
        using var reader = seeds.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.GetString(0);
            if (!StagedIdentity.TryParse(text, out var identity) || text != identity.ToString())
                throw new InvalidOperationException("preservation-seed-identity-invalid: unknown staged identity spelling is not coerced.");
        }
        return report;
    }

    private void ValidateReaderPayloads()
    {
        try
        {
            using (var items = Command())
            {
                items.CommandText = "SELECT * FROM main.work_items WHERE is_seed=1;";
                using var reader = items.ExecuteReader();
                var mapper = new WorkItemMapper();
                while (reader.Read())
                    _ = mapper.Map(SqliteWorkItemRepository.MapRowToSnapshot(reader));
            }
            using var navigation = Command();
            navigation.CommandText = "SELECT visited_at FROM main.navigation_history;";
            using var entries = navigation.ExecuteReader();
            while (entries.Read())
                _ = SqliteNavigationHistoryStore.ParseVisitedAt(entries.GetString(0));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                "preservation-source-payload-invalid: an existing seed or navigation reader cannot interpret the source. Originals were not changed.", error);
        }
    }

    private void RequirePristineTarget()
        => RequirePristineTarget(store);

    private static void RequirePristineTarget(SqliteCacheStore target)
    {
        using var command = target.GetConnection().CreateCommand();
        command.Transaction = target.ActiveTransaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM pending.seed_local_identities)
                OR EXISTS(SELECT 1 FROM pending.seed_work_items)
                OR EXISTS(SELECT 1 FROM pending.workspace_context)
                OR EXISTS(SELECT 1 FROM pending.workspace_navigation_history)
                OR EXISTS(SELECT 1 FROM pending.sqlite_sequence WHERE name='workspace_navigation_history');
            """;
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("preservation-target-unclassified: target data exists without a completed activation. Preserve it and the admitted originals; no target row is cleared or adopted.");
    }

    private void QualifyBackup(string directory, string generation, string digest, StoreParityReport source)
    {
        GuardBackupPath(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var retained = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), TwigJsonContext.Default.PreservationBackupManifest)
                ?? throw new InvalidOperationException("preservation-backup-unreadable: retain the original backup and repair access before activation.");
            if (retained.Version != 1 || retained.Generation != generation || retained.SourceDigest != digest
                || retained.MirrorVersion != SqliteCacheStore.SchemaVersion
                || retained.DurableVersion != SqliteCacheStore.DurableSchemaVersion)
                throw new InvalidOperationException("preservation-backup-conflict: the existing backup belongs to another source; it is never overwritten.");
            if (!SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(
                    Path.GetFullPath(Path.GetDirectoryName(retained.SnapshotDirectory)!),
                    Path.GetFullPath(directory)))
                throw new InvalidOperationException("preservation-backup-path-invalid: qualified snapshot must remain inside its workspace backup.");
            GuardBackupPath(retained.SnapshotDirectory);
            VerifyBackup(retained.SnapshotDirectory, retained);
            return;
        }
        Directory.CreateDirectory(directory);
        var attempt = Path.Combine(directory, "snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        CheckpointWal("main");
        CheckpointWal("pending");
        using (var read = store.GetConnection().BeginTransaction(deferred: true))
        {
            store.ActiveTransaction = read;
            try
            {
                if (Digest(generation, InspectSource().Classes) != digest)
                    throw new InvalidOperationException("preservation-source-changed: source changed before its backup snapshot.");
                BackupFile(Path.Combine(attempt, "mirror.db"), "main");
                checkpoint?.Invoke("backup-mirror-copied");
                BackupFile(Path.Combine(attempt, "pending.db"), "pending");
                checkpoint?.Invoke("backup-durable-copied");
            }
            finally { store.ActiveTransaction = null; }
        }
        var manifest = new PreservationBackupManifest(1, generation, digest,
            SqliteCacheStore.SchemaVersion, SqliteCacheStore.DurableSchemaVersion,
            attempt,
            source.Classes.Where(entry => !OwnedClasses.Contains(entry.ClassName)).ToArray());
        VerifyBackup(attempt, manifest);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, TwigJsonContext.Default.PreservationBackupManifest);
        var temporaryManifest = Path.Combine(attempt, "qualified-manifest.json");
        using (var file = new FileStream(temporaryManifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporaryManifest, manifestPath);
    }

    private void VerifyBackup(string directory, PreservationBackupManifest manifest)
    {
        // Restore qualification uses another SQLite backup, not a raw copy of WAL files.
        var restore = Path.Combine(directory, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restore);
        foreach (var name in new[] { "mirror.db", "pending.db" })
        {
            GuardBackupPath(Path.Combine(directory, name));
            using var source = Open(Path.Combine(directory, name), SqliteOpenMode.ReadOnly);
            using (var integrity = source.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                if (integrity.ExecuteScalar() as string != "ok")
                    throw new InvalidOperationException("preservation-backup-integrity-failed: backup is not restorable; activation is blocked.");
            }
            using var destination = Open(Path.Combine(restore, name), SqliteOpenMode.ReadWriteCreate);
            source.BackupDatabase(destination);
        }
        using var restored = new SqliteCacheStore(
            BackupConnectionString(Path.Combine(restore, "mirror.db"), SqliteOpenMode.ReadWrite));
        RequirePristineTarget(restored);
        var report = new StoreParityHarness(restored, resourceScope, preserveOpaqueContext: true).Inspect();
        if (!report.IsConsistent || Digest(manifest.Generation, report.Classes) != manifest.SourceDigest)
            throw new InvalidOperationException("preservation-backup-parity-failed: restore-qualified content does not match the reviewed source.");
    }

    private void BackupFile(string destination, string sourceSchema)
    {
        using var target = Open(destination, SqliteOpenMode.ReadWriteCreate);
        store.GetConnection().BackupDatabase(target, destinationName: "main", sourceName: sourceSchema);
    }

    private void CheckpointWal(string schema)
    {
        using var command = Command();
        command.CommandText = $"PRAGMA {schema}.wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0)
            throw new InvalidOperationException("preservation-backup-busy: the WAL checkpoint could not finish; activation is blocked.");
    }

    private string RequireGeneration()
    {
        var generation = store.ValidateAdmittedGeneration()
            ?? throw new InvalidOperationException(
                "preservation-admission-required: this migration only accepts an already-admitted generation, not legacy stores or caller assertions.");
        var comparer = SqlitePlanJournalRepository.CreateDefaultSourcePathComparer();
        if (!comparer.Equals(Path.GetFullPath(paths.RepoRoot), Path.GetFullPath(store.AdmittedWorktreeRoot!))
            || !comparer.Equals(Path.GetFullPath(Path.GetDirectoryName(store.GetConnection().DataSource)!),
                Path.GetFullPath(Path.Combine(paths.TwigDir, "cache"))))
            throw new InvalidOperationException("preservation-workspace-mismatch: the backup destination must belong to the admitted source workspace.");
        return generation;
    }

    private string BackupDirectory(string digest) => Path.Combine(paths.TwigDir, "backups", "preservation-" + digest);

    private void GuardBackupPath(string path)
    {
        var root = Path.GetFullPath(paths.TwigDir);
        var comparer = SqlitePlanJournalRepository.CreateDefaultSourcePathComparer();
        var current = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, current);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("preservation-backup-path-invalid: backup path is outside its workspace.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("preservation-backup-path-invalid: workspace backups cannot traverse a symbolic link or junction.");
            if (comparer.Equals(current, root)) return;
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException("preservation-backup-path-invalid: backup path is outside its workspace.");
        }
    }

    private (string IntentId, string Generation, PreservationMigrationResult Result)? ReadIntent()
    {
        using var command = Command();
        command.CommandText = "SELECT source_generation,source_digest,state,backup_directory,coverage_version,intent_id FROM pending.preservation_migrations WHERE slot=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt32(4) != PreservationWriteCoverage.Version)
            throw new InvalidOperationException("preservation-write-coverage-unverified: retained intent has an incompatible write-path matrix.");
        var digest = reader.GetString(1);
        var state = reader.GetString(2);
        if (!Guid.TryParseExact(reader.GetString(0), "N", out _)
            || !Guid.TryParseExact(reader.GetString(5), "N", out _)
            || digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character))
            || digest != digest.ToLowerInvariant()
            || state is not ("preparing" or "active" or "rolled-back")
            || !SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(
                reader.GetString(3), BackupDirectory(digest)))
            throw new InvalidOperationException("preservation-intent-invalid: retained intent identity, state or workspace backup binding is unknown; nothing was rewritten.");
        return (reader.GetString(5), reader.GetString(0),
            new(reader.GetString(2), reader.GetString(1), reader.GetString(3), reader.GetString(5)));
    }

    private static string Digest(string generation, IEnumerable<StoreParityClassReport> classes)
    {
        var content = generation + "\n" + string.Join("\n", classes
            .Where(entry => !OwnedClasses.Contains(entry.ClassName))
            .OrderBy(entry => entry.ClassName, StringComparer.Ordinal)
            .Select(entry => $"{entry.ClassName}:{entry.RowCount?.ToString(CultureInfo.InvariantCulture)}:{entry.CanonicalDigest}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(BackupConnectionString(path, mode));
        try
        {
            connection.Open();
            SqliteCacheStore.RegisterCollations(connection);
            return connection;
        }
        catch (SqliteException error)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                "preservation-backup-unavailable: the required native backup or restore connection could not be opened. Activation is blocked; retain the workspace and existing backups.",
                error);
        }
        catch { connection.Dispose(); throw; }
    }

    private string BackupConnectionString(string path, SqliteOpenMode mode)
    {
        var logical = Path.GetFullPath(path);
        GuardBackupPath(logical);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = logical, Mode = mode, Pooling = false,
        };
        if (OperatingSystem.IsWindows())
        {
            builder.Vfs = "win32-longpath";
            builder.DataSource = logical.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? logical
                : logical.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + logical[2..]
                    : @"\\?\" + logical;
        }
        return builder.ToString();
    }

    private SqliteCommand Command()
    {
        var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        return command;
    }
}
