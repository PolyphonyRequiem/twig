using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

public sealed record UserBenchSnapshot(Guid InstanceId, long Revision, ImmutableArray<Bench> Benches);
public sealed record UserBenchWorkspace(string Root, Connection Connection, Guid StoreInstance);
public sealed record WorkspaceViewRules(long Revision, ImmutableArray<Selector> Exclusions);

/// <summary>The native per-user Bench definitions shared by CLI and graphical hosts.</summary>
public sealed class UserBenchStore
{
    private const int ApplicationId = 0x5457424e;
    private readonly string path;
    private readonly object identityGate = new();
    private Guid? instanceId;

    public UserBenchStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute native Bench store path is required.", nameof(path));
        this.path = Path.GetFullPath(path);
    }

    public static UserBenchStore ForCurrentUser()
        => new(Path.Combine(ConnectionBindingService.ResolveUserHome(), "benches.db"));

    internal static UserBenchStore ForRegistry(string registryPath)
        => new(Path.Combine(Path.GetDirectoryName(registryPath)!, "benches.db"));

    public async Task<Result<UserBenchSnapshot>> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: true);
            var value = await Read(db, transaction, ct).ConfigureAwait(false);
            transaction.Commit();
            return Result<UserBenchSnapshot>.Ok(value);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<UserBenchSnapshot>(ex); }
    }

    public async Task<Result<Bench>> SaveAsync(Bench definition, long expectedRevision, CancellationToken ct = default)
    {
        try
        {
            Validate(definition);
            if (expectedRevision < 0 || expectedRevision == long.MaxValue)
                return Result<Bench>.Fail(ErrorCode.InvalidInput, "A valid captured Bench revision is required.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var current = await Find(db, transaction, definition.Id, ct).ConfigureAwait(false);
            if (current is null ? expectedRevision != 0 : current.Value.Deleted || current.Value.Bench.Revision != expectedRevision)
                return Result<Bench>.Fail(ErrorCode.Conflict, "The Bench changed or was deleted. Refresh before changing it.");
            var next = new Bench(definition.Id, definition.Name, definition.Selectors, expectedRevision + 1);
            await Put(db, transaction, next, false, ct).ConfigureAwait(false);
            await Advance(db, transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<Bench>.Ok(next);
        }
        catch (ArgumentException ex) { return Result<Bench>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<Bench>(ex); }
    }

    /// <summary>Atomically preserves explicitly supplied legacy definitions. Identity/name conflicts never overwrite or rename.</summary>
    public async Task<Result<UserBenchSnapshot>> ImportAsync(IEnumerable<Bench> definitions, CancellationToken ct = default)
    {
        try
        {
            var incoming = definitions.ToImmutableArray();
            if (incoming.Length > 1024 || incoming.Select(bench => bench.Id).Distinct().Count() != incoming.Length
                || incoming.Select(bench => CanonicalName(bench.Name)).Distinct(StringComparer.Ordinal).Count() != incoming.Length)
                return Result<UserBenchSnapshot>.Fail(ErrorCode.Conflict, "Legacy Bench identities or names conflict; nothing was imported.");
            foreach (var bench in incoming) Validate(bench);
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var before = await Read(db, transaction, ct).ConfigureAwait(false);
            var changed = false;
            foreach (var bench in incoming)
            {
                var existing = await Find(db, transaction, bench.Id, ct).ConfigureAwait(false);
                if (existing is { } retained)
                {
                    if (retained.Deleted || retained.Bench.Name != bench.Name
                        || BenchCodec.SerializeSelectors(retained.Bench.Selectors) != BenchCodec.SerializeSelectors(bench.Selectors))
                        return Result<UserBenchSnapshot>.Fail(ErrorCode.Conflict, "A legacy Bench identity has different saved intent or was deleted. Reconcile explicitly; nothing was imported.");
                    continue;
                }
                if (before.Benches.Any(value => CanonicalName(value.Name) == CanonicalName(bench.Name)))
                    return Result<UserBenchSnapshot>.Fail(ErrorCode.Conflict, "A legacy Bench name belongs to another identity. Reconcile explicitly; nothing was imported.");
                await Put(db, transaction, new(bench.Id, bench.Name, bench.Selectors, 1), false, ct).ConfigureAwait(false);
                changed = true;
            }
            if (changed) await Advance(db, transaction, ct).ConfigureAwait(false);
            var after = await Read(db, transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<UserBenchSnapshot>.Ok(after);
        }
        catch (ArgumentException ex) { return Result<UserBenchSnapshot>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<UserBenchSnapshot>(ex); }
    }

    public async Task<Result<bool>> DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var current = await Find(db, transaction, id, ct).ConfigureAwait(false);
            if (current is null) return Result<bool>.Fail(ErrorCode.NotFound, "The Bench does not exist.");
            if (current.Value.Deleted || current.Value.Bench.Revision != expectedRevision || expectedRevision == long.MaxValue)
                return Result<bool>.Fail(ErrorCode.Conflict, "The Bench changed or was deleted; nothing else was removed.");
            var previous = current.Value.Bench;
            await Put(db, transaction, new(previous.Id, previous.Name, previous.Selectors, expectedRevision + 1), true, ct).ConfigureAwait(false);
            await Advance(db, transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    public async Task<Result<ImmutableArray<UserBenchWorkspace>>> WorkspacesAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT root,connection,store_instance FROM workspaces ORDER BY context_key;";
            var rows = ImmutableArray.CreateBuilder<UserBenchWorkspace>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var connection = JsonSerializer.Deserialize(reader.GetString(1), UserBenchJson.Default.Connection)
                    ?? throw new JsonException("Missing native connection.");
                rows.Add(new(reader.GetString(0), connection, Guid.ParseExact(reader.GetString(2), "D")));
            }
            return Result<ImmutableArray<UserBenchWorkspace>>.Ok(rows.ToImmutable());
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<ImmutableArray<UserBenchWorkspace>>(ex); }
    }

    public async Task<Result<WorkspaceViewRules>> ReadViewRulesAsync(string workspaceRoot, Connection connection, CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            return Result<WorkspaceViewRules>.Ok(await ReadRules(db, null, WorkspaceKey(workspaceRoot),
                JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection), ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<WorkspaceViewRules>(ex); }
    }

    public async Task<Result<PlanningCacheEntry>> ReadPlanningAsync(Connection connection, string team, CancellationToken ct = default)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='planning';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                return Result<PlanningCacheEntry>.Ok(new(null));
            command.CommandText = "SELECT document FROM planning WHERE connection=$connection AND team=$team;";
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
            command.Parameters.AddWithValue("$team", team);
            var text = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (text is null) return Result<PlanningCacheEntry>.Ok(new(null));
            if (text.Length > 1024 * 1024) throw new InvalidDataException("Native planning metadata exceeds its bound.");
            var value = JsonSerializer.Deserialize(text, UserBenchJson.Default.PlanningMetadata)
                ?? throw new JsonException("Missing planning metadata.");
            ValidatePlanning(value);
            return Result<PlanningCacheEntry>.Ok(new(value));
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<PlanningCacheEntry>(ex); }
    }

    public async Task<Result<PlanningMetadata>> SavePlanningAsync(Connection connection, string team, PlanningMetadata metadata, CancellationToken ct = default)
    {
        try
        {
            ValidatePlanning(metadata);
            var text = JsonSerializer.Serialize(metadata, UserBenchJson.Default.PlanningMetadata);
            if (text.Length > 1024 * 1024) throw new ArgumentException("Native planning metadata exceeds its bound.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                    CREATE TABLE IF NOT EXISTS planning(connection TEXT NOT NULL,team TEXT NOT NULL,document TEXT NOT NULL,PRIMARY KEY(connection,team));
                    INSERT INTO planning(connection,team,document) VALUES($connection,$team,$document)
                    ON CONFLICT(connection,team) DO UPDATE SET document=excluded.document;
                    """;
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
            command.Parameters.AddWithValue("$team", team);
            command.Parameters.AddWithValue("$document", text);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<PlanningMetadata>.Ok(metadata);
        }
        catch (ArgumentException ex) { return Result<PlanningMetadata>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<PlanningMetadata>(ex); }
    }

    private static void ValidatePlanning(PlanningMetadata value)
    {
        if (value.ObservedAt == default || value.Areas.IsDefault || value.Iterations.IsDefault || value.Gaps.IsDefault
            || value.Areas.Length + value.Iterations.Length > 8192
            || value.Areas.Any(string.IsNullOrWhiteSpace) || value.Iterations.Any(iteration => iteration is null
                || string.IsNullOrWhiteSpace(iteration.Path) || iteration.Start > iteration.Finish)
            || value.Iterations.Select(iteration => iteration.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Iterations.Length)
            throw new InvalidDataException("Native planning metadata is invalid or incomplete.");
    }
    internal async Task<Result<ImmutableArray<PlanningMembership>>> ReadMembershipsAsync(Connection connection, string selectedTeam, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='planning_memberships';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                return Result<ImmutableArray<PlanningMembership>>.Ok([]);
            command.CommandText = "SELECT kind,source_id,item_count,document FROM planning_memberships WHERE connection=$connection AND selected_team=$team ORDER BY kind,source_id;";
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
            command.Parameters.AddWithValue("$team", selectedTeam);
            var result = ImmutableArray.CreateBuilder<PlanningMembership>();
            var totalItems = 0;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (result.Count >= 1024) throw new InvalidDataException("Native planning membership cache exceeds its source bound.");
                var text = reader.GetString(3);
                if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Native planning membership exceeds its byte bound.");
                var value = JsonSerializer.Deserialize(text, UserBenchJson.Default.UserMembershipDocument)
                    ?? throw new JsonException("Missing native planning membership.");
                if ((int)value.Kind != reader.GetInt32(0) || value.Id != reader.GetString(1) || value.NativeIds?.Length != reader.GetInt32(2))
                    throw new InvalidDataException("The native membership document belongs to another source.");
                var membership = value.ToCore(connection);
                ValidateMembership(connection, membership);
                totalItems += membership.Items.Length;
                if (totalItems > 100000) throw new InvalidDataException("Native planning memberships exceed their total identity bound.");
                result.Add(membership);
            }
            return Result<ImmutableArray<PlanningMembership>>.Ok(result.ToImmutable());
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<ImmutableArray<PlanningMembership>>(ex); }
    }

    internal async Task<Result<PlanningMembership>> SaveMembershipAsync(Connection connection, string selectedTeam,
        PlanningMembership membership, CancellationToken ct)
    {
        try
        {
            ValidateMembership(connection, membership);
            var text = JsonSerializer.Serialize(UserMembershipDocument.From(membership), UserBenchJson.Default.UserMembershipDocument);
            if (text.Length > 2 * 1024 * 1024) throw new ArgumentException("Native planning membership exceeds its byte bound.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS planning_memberships(
                    connection TEXT NOT NULL,selected_team TEXT NOT NULL,kind INTEGER NOT NULL,source_id TEXT NOT NULL,item_count INTEGER NOT NULL,document TEXT NOT NULL,
                    PRIMARY KEY(connection,selected_team,kind,source_id));
                INSERT INTO planning_memberships(connection,selected_team,kind,source_id,item_count,document) VALUES($connection,$team,$kind,$id,$count,$document)
                ON CONFLICT(connection,selected_team,kind,source_id) DO UPDATE SET item_count=excluded.item_count,document=excluded.document;
                """;
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
            command.Parameters.AddWithValue("$team", selectedTeam);
            command.Parameters.AddWithValue("$kind", (int)membership.Kind);
            command.Parameters.AddWithValue("$id", membership.Id);
            command.Parameters.AddWithValue("$count", membership.Items.Length);
            command.Parameters.AddWithValue("$document", text);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            command.CommandText = "SELECT COUNT(*)>1024 OR COALESCE(SUM(item_count),0)>100000 FROM planning_memberships WHERE connection=$connection AND selected_team=$team;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                return Result<PlanningMembership>.Fail(ErrorCode.Partial, "The native membership cache source bound was reached; existing memberships were preserved.");
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<PlanningMembership>.Ok(membership);
        }
        catch (ArgumentException ex) { return Result<PlanningMembership>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<PlanningMembership>(ex); }
    }

    private static void ValidateMembership(Connection connection, PlanningMembership value)
    {
        if (!Enum.IsDefined(value.Kind) || string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 256 || !value.Complete
            || value.ObservedAt == default || value.Items.IsDefault || value.Items.Length > 10000
            || value.Items.Any(key => key.IsLocal || !key.BelongsTo(connection))
            || value.Items.Distinct().Count() != value.Items.Length || value.Kind == PlanningSourceKind.Backlog && value.Team is null
            || value.Team is not null && (string.IsNullOrWhiteSpace(value.Team) || value.Team.Any(char.IsControl)))
            throw new InvalidDataException("Native planning membership is incomplete or belongs to a different connection.");
    }

    public async Task<Result<WorkspaceViewRules>> SaveViewRulesAsync(string workspaceRoot, Connection connection,
        long expectedRevision, IEnumerable<Selector> exclusions, CancellationToken ct = default)
    {
        try
        {
            if (expectedRevision < 0 || expectedRevision == long.MaxValue)
                return Result<WorkspaceViewRules>.Fail(ErrorCode.InvalidInput, "A captured workspace-rule revision is required.");
            var scoped = exclusions.Select(rule => Scope(rule, connection)).ToImmutableArray();
            ValidateSelectors(scoped);
            var key = WorkspaceKey(workspaceRoot);
            var binding = JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection);
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var before = await ReadRules(db, transaction, key, binding, ct).ConfigureAwait(false);
            if (before.Revision != expectedRevision)
                return Result<WorkspaceViewRules>.Fail(ErrorCode.Conflict, "Workspace exclusions changed; newer rules were preserved.");
            var next = new WorkspaceViewRules(expectedRevision + 1, scoped);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO view_rules(context_key,connection,revision,exclusions) VALUES($key,$connection,$revision,$rules)
                ON CONFLICT(context_key,connection) DO UPDATE SET revision=excluded.revision,exclusions=excluded.exclusions;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$connection", binding);
            command.Parameters.AddWithValue("$revision", next.Revision);
            command.Parameters.AddWithValue("$rules", BenchCodec.SerializeSelectors(scoped));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Advance(db, transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<WorkspaceViewRules>.Ok(next);
        }
        catch (ArgumentException ex) { return Result<WorkspaceViewRules>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<WorkspaceViewRules>(ex); }
    }

    private static async Task<WorkspaceViewRules> ReadRules(SqliteConnection db, SqliteTransaction? transaction,
        string key, string binding, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,exclusions FROM view_rules WHERE context_key=$key AND connection=$connection;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$connection", binding);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new(0, []);
        var text = reader.GetString(1);
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Workspace exclusions exceed their bound.");
        var rules = BenchCodec.DeserializeSelectors(text).ToImmutableArray();
        ValidateSelectors(rules);
        if (rules.Any(rule => JsonSerializer.Serialize(rule.Connection ?? throw new JsonException("Missing exclusion connection."),
            UserBenchJson.Default.Connection) != binding))
            throw new InvalidDataException("Workspace exclusions belong to another connection.");
        return new(reader.GetInt64(0), rules);
    }

    private static string WorkspaceKey(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute workspace root is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    // Import existing user-created definitions once. Never derive a Bench from a repository or worktree.
    internal async Task<Result<bool>> ObserveAsync(string root, Connection connection, Guid storeInstance,
        ImmutableArray<Bench> legacy, CancellationToken ct)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || storeInstance == Guid.Empty)
                return Result<bool>.Fail(ErrorCode.InvalidInput, "An admitted native workspace/store instance is required.");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var key = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
            var binding = JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection);
            await using var db = await Open(ct).ConfigureAwait(false);
            await using (var probe = db.CreateCommand())
            {
                probe.CommandText = """
                    SELECT i.connection,w.connection,w.store_instance,w.root
                    FROM imports i LEFT JOIN workspaces w ON w.context_key=$key
                    WHERE i.store_instance=$instance;
                    """;
                probe.Parameters.AddWithValue("$key", key);
                probe.Parameters.AddWithValue("$instance", storeInstance.ToString("D"));
                await using var observed = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await observed.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (observed.GetString(0) != binding)
                        return Result<bool>.Fail(ErrorCode.Conflict, "The native store instance belongs to a different connection.");
                    if (!observed.IsDBNull(1) && observed.GetString(1) == binding
                        && observed.GetString(2) == storeInstance.ToString("D") && observed.GetString(3) == full)
                        return Result<bool>.Ok(true);
                }
            }
            using var transaction = db.BeginTransaction(deferred: false);
            await using var imported = db.CreateCommand();
            imported.Transaction = transaction;
            imported.CommandText = "SELECT connection FROM imports WHERE store_instance=$id;";
            imported.Parameters.AddWithValue("$id", storeInstance.ToString("D"));
            var previous = await imported.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (previous is not null && previous != binding)
                return Result<bool>.Fail(ErrorCode.Conflict, "The native store instance belongs to a different connection.");
            if (previous is null)
            {
                foreach (var old in legacy)
                {
                    var existing = await Find(db, transaction, old.Id, ct).ConfigureAwait(false);
                    if (existing is { Deleted: true }) continue;
                    var scoped = old.Selectors.Select(selector => Scope(selector, connection)).ToImmutableArray();
                    if (existing is { } retained)
                    {
                        if (retained.Bench.Name != old.Name)
                            return Result<bool>.Fail(ErrorCode.Conflict, "Existing Bench identity has different saved intent; nothing was adopted.");
                        var same = retained.Bench.Selectors.Where(s => s.Connection == connection).ToArray();
                        if (same.Length != 0 && BenchCodec.SerializeSelectors(same) != BenchCodec.SerializeSelectors(scoped))
                            return Result<bool>.Fail(ErrorCode.Conflict, "Existing Bench selectors differ; reconcile them explicitly.");
                        if (same.Length != 0) continue;
                        scoped = retained.Bench.Selectors.AddRange(scoped);
                    }
                    var next = new Bench(old.Id, old.Name, scoped, (existing?.Bench.Revision ?? 0) + 1);
                    Validate(next);
                    await Put(db, transaction, next, false, ct).ConfigureAwait(false);
                }
                imported.CommandText = "INSERT INTO imports(store_instance,connection) VALUES($id,$connection);";
                imported.Parameters.AddWithValue("$connection", binding);
                await imported.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var workspace = db.CreateCommand();
            workspace.Transaction = transaction;
            workspace.CommandText = """
                INSERT INTO workspaces(context_key,root,connection,store_instance) VALUES($key,$root,$connection,$instance)
                ON CONFLICT(context_key) DO UPDATE SET root=excluded.root,connection=excluded.connection,store_instance=excluded.store_instance;
                """;
            workspace.Parameters.AddWithValue("$key", key);
            workspace.Parameters.AddWithValue("$root", full);
            workspace.Parameters.AddWithValue("$connection", binding);
            workspace.Parameters.AddWithValue("$instance", storeInstance.ToString("D"));
            await workspace.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (previous is null) await Advance(db, transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<bool>.Ok(true);
        }
        catch (ArgumentException ex) { return Result<bool>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    internal async Task<Result<bool>> CaptureAsync(Connection connection, IEnumerable<Bench> captured,
        IEnumerable<Bench> proposed, CancellationToken ct)
    {
        try
        {
            var before = captured.ToDictionary(bench => bench.Id);
            var after = proposed.ToDictionary(bench => bench.Id);
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            var changed = false;
            foreach (var id in before.Keys.Union(after.Keys))
            {
                before.TryGetValue(id, out var expected);
                after.TryGetValue(id, out var requested);
                if (requested is null)
                    return Result<bool>.Fail(ErrorCode.InvalidInput, "Use explicit Bench deletion; selector capture cannot delete shared definitions.");
                var normalized = new Bench(requested.Id, requested.Name,
                    requested.Selectors.Select(selector => Scope(selector, connection)), expected?.Revision ?? 0);
                if (expected is not null && BenchCodec.Serialize(normalized) == BenchCodec.Serialize(expected)) continue;
                var current = await Find(db, transaction, id, ct).ConfigureAwait(false);
                if (current is null ? expected is not null : current.Value.Deleted || expected is null
                    || current.Value.Bench.Revision != expected.Revision)
                    return Result<bool>.Fail(ErrorCode.Conflict, "The shared Bench changed during this operation; newer selectors were preserved.");
                var selectors = (current?.Bench.Selectors ?? []).Where(s => s.Connection != connection)
                    .Concat(normalized.Selectors);
                var next = new Bench(id, normalized.Name, selectors, (expected?.Revision ?? 0) + 1);
                Validate(next);
                await Put(db, transaction, next, false, ct).ConfigureAwait(false);
                changed = true;
            }
            if (changed) await Advance(db, transaction, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<bool>.Ok(true);
        }
        catch (ArgumentException ex) { return Result<bool>.Fail(ErrorCode.InvalidInput, ex.Message); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    internal IStore View(IStore native, string root, Guid instance)
        => new NativeView(this, native, root, instance);

    private sealed class NativeView(UserBenchStore owner, IStore native, string root, Guid instance) : IStore
    {
        public async Task<Result<StoreSnapshot>> ReadAsync(Connection connection, CancellationToken ct)
        {
            var read = await native.ReadAsync(connection, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return read;
            var observed = await owner.ObserveAsync(root, connection, instance, read.Value.Benches, ct).ConfigureAwait(false);
            if (!observed.IsSuccess) return Result<StoreSnapshot>.Fail(observed.Error);
            var shared = await owner.ReadAsync(ct).ConfigureAwait(false);
            if (!shared.IsSuccess) return Result<StoreSnapshot>.Fail(shared.Error);
            var snapshot = read.Value;
            return Result<StoreSnapshot>.Ok(new(snapshot.Version, snapshot.Items, snapshot.Relationships, snapshot.Drafts,
                snapshot.Proposals, snapshot.Journal, snapshot.Mappings, shared.Value.Benches.Select(bench =>
                    new Bench(bench.Id, bench.Name, bench.Selectors.Where(s => s.Connection == connection), bench.Revision))));
        }

        public async Task<Result<long>> CommitAsync(Connection connection, long expectedVersion, StoreSnapshot next, CancellationToken ct)
        {
            var current = await native.ReadAsync(connection, ct).ConfigureAwait(false);
            if (!current.IsSuccess) return Result<long>.Fail(current.Error);
            if (current.Value.Version != expectedVersion)
                return Result<long>.Fail(ErrorCode.Conflict, "Native state changed; no local changes were overwritten.");
            return await native.CommitAsync(connection, expectedVersion, new(next.Version, next.Items, next.Relationships,
                next.Drafts, next.Proposals, next.Journal, next.Mappings, current.Value.Benches), ct).ConfigureAwait(false);
        }
    }

    private static Selector Scope(Selector selector, Connection connection)
    {
        if (selector.Connection is { } recorded && recorded != connection)
            throw new ArgumentException("A selector update cannot modify another connection.");
        return new(selector.Kind, selector.Item, selector.Query, selector.HierarchyRelation, connection);
    }

    private static void Validate(Bench bench)
    {
        if (bench.Name.Length > 120 || bench.Name != bench.Name.Trim() || bench.Name.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(bench.Name))
            throw new ArgumentException("Use a trimmed Bench name (1-120 characters).");
        ValidateSelectors(bench.Selectors);
    }

    private static void ValidateSelectors(ImmutableArray<Selector> selectors)
    {
        if (selectors.Length > 1024 || selectors.Any(selector => selector.Connection is null))
            throw new ArgumentException("Use at most 1024 explicitly connection-qualified selectors.");
        var budget = 8192;
        var pending = new Stack<(Query Query, int Depth)>(selectors.Where(s => s.Query is not null)
            .Select(s => (s.Query!, 1)));
        while (pending.TryPop(out var entry))
        {
            if (--budget < 0 || entry.Depth > 16)
                throw new ArgumentException("The saved Bench exceeds its query complexity bound.");
            foreach (var child in entry.Query.Children) pending.Push((child, entry.Depth + 1));
        }
        if (BenchCodec.SerializeSelectors(selectors).Length > 1024 * 1024)
            throw new ArgumentException("The saved selector set exceeds its definition bound.");
    }

    private static string CanonicalName(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        if (!File.Exists(path)) await Create(ct).ConfigureAwait(false);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            await db.OpenAsync(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "PRAGMA application_id;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != ApplicationId)
                throw new InvalidDataException("This is not the native user Bench store.");
            command.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 1)
                throw new InvalidDataException("Unsupported native Bench store version; the existing file was preserved.");
            command.CommandText = "SELECT instance FROM meta WHERE id=1;";
            if (!Guid.TryParseExact(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string, "D", out var current)
                || current == Guid.Empty)
                throw new InvalidDataException("Missing native Bench store identity.");
            lock (identityGate)
            {
                if (instanceId is { } captured && captured != current)
                    throw new InvalidDataException("The native Bench store was replaced; no captured operation was adopted.");
                instanceId = current;
            }
            command.CommandText = "PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return db;
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task Create(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Pooling = false }.ToString()))
            {
                await db.OpenAsync(ct).ConfigureAwait(false);
                await using var command = db.CreateCommand();
                command.CommandText = $"""
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    PRAGMA application_id={ApplicationId};
                    PRAGMA user_version=1;
                    CREATE TABLE meta(id INTEGER PRIMARY KEY CHECK(id=1),instance TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>=0)) STRICT;
                    INSERT INTO meta VALUES(1,'{Guid.NewGuid():D}',0);
                    CREATE TABLE benches(id TEXT PRIMARY KEY,canonical_name TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>0),deleted INTEGER NOT NULL CHECK(deleted IN(0,1)),document TEXT NOT NULL) STRICT;
                    CREATE UNIQUE INDEX bench_names ON benches(canonical_name) WHERE deleted=0;
                    CREATE TABLE imports(store_instance TEXT PRIMARY KEY,connection TEXT NOT NULL) STRICT;
                    CREATE TABLE workspaces(context_key TEXT PRIMARY KEY,root TEXT NOT NULL,connection TEXT NOT NULL,store_instance TEXT NOT NULL) STRICT;
                    CREATE TABLE view_rules(context_key TEXT NOT NULL,connection TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>0),exclusions TEXT NOT NULL,PRIMARY KEY(context_key,connection)) STRICT;
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { } // Another creator installed its complete store first.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<UserBenchSnapshot> Read(SqliteConnection db, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT instance,revision FROM meta WHERE id=1;";
        Guid instance; long revision;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("Missing native Bench store identity.");
            instance = Guid.ParseExact(reader.GetString(0), "D"); revision = reader.GetInt64(1);
            if (instance == Guid.Empty || revision < 0) throw new InvalidDataException("Invalid native Bench store identity.");
        }
        command.CommandText = "SELECT id,canonical_name,revision,document FROM benches WHERE deleted=0 ORDER BY canonical_name;";
        var benches = ImmutableArray.CreateBuilder<Bench>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var bench = Decode(reader.GetString(3));
                if (bench.Id.ToString("D") != reader.GetString(0) || CanonicalName(bench.Name) != reader.GetString(1)
                    || bench.Revision != reader.GetInt64(2))
                    throw new InvalidDataException("Native Bench row disagrees with its saved definition.");
                benches.Add(bench);
                if (benches.Count > 10000) throw new InvalidDataException("Native Bench store exceeds its metadata bound.");
            }
        return new(instance, revision, benches.ToImmutable());
    }

    private static async Task<(Bench Bench, bool Deleted)?> Find(SqliteConnection db, SqliteTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT document,deleted FROM benches WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? (Decode(reader.GetString(0)), reader.GetInt64(1) != 0) : null;
    }

    private static Bench Decode(string json)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException("Native Bench definition exceeds its bound.");
        var bench = BenchCodec.Deserialize(json);
        Validate(bench);
        return bench;
    }

    private static async Task Put(SqliteConnection db, SqliteTransaction transaction, Bench bench, bool deleted, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM benches WHERE deleted=0 AND id<>$id;";
        command.Parameters.AddWithValue("$id", bench.Id.ToString("D"));
        if (!deleted && Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) >= 10000)
            throw new ArgumentException("The native Bench store supports at most 10000 active definitions.");
        command.CommandText = """
            INSERT INTO benches(id,canonical_name,revision,deleted,document) VALUES($id,$name,$revision,$deleted,$document)
            ON CONFLICT(id) DO UPDATE SET canonical_name=excluded.canonical_name,revision=excluded.revision,deleted=excluded.deleted,document=excluded.document;
            """;
        command.Parameters.AddWithValue("$name", CanonicalName(bench.Name));
        command.Parameters.AddWithValue("$revision", bench.Revision);
        command.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        command.Parameters.AddWithValue("$document", BenchCodec.Serialize(bench));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task Advance(SqliteConnection db, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE meta SET revision=revision+1 WHERE id=1 AND revision<9223372036854775807;";
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Native Bench revision is exhausted.");
    }

    private static bool StorageFailure(Exception error) => error is SqliteException or IOException or InvalidDataException
        or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException;
    private static Result<T> Failure<T>(Exception error)
        => Result<T>.Fail(error is SqliteException { SqliteErrorCode: 5 or 6 or 19 } ? ErrorCode.Conflict : ErrorCode.StorageUnavailable,
            "The native user Bench store could not complete this operation; existing data was preserved. " + error.Message);
}

internal sealed record UserMembershipDocument(PlanningSourceKind Kind, string Id, string? Team, string[] NativeIds,
    DateTimeOffset ObservedAt, bool Complete)
{
    internal static UserMembershipDocument From(PlanningMembership value)
        => new(value.Kind, value.Id, value.Team, value.Items.Select(key => key.NativeId!).ToArray(), value.ObservedAt, value.Complete);
    internal PlanningMembership ToCore(Connection connection)
        => new(Kind, Id, Team, (NativeIds ?? throw new JsonException("Missing native membership identities."))
            .Select(id => ItemKey.Remote(connection.Provider, connection.ResourceScope, id)).ToImmutableArray(), ObservedAt, Complete);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Connection))]
[JsonSerializable(typeof(PlanningMetadata))]
[JsonSerializable(typeof(UserMembershipDocument))]
internal sealed partial class UserBenchJson : JsonSerializerContext;
