using Microsoft.Data.Sqlite;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Workspace;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IBenchRepository"/> over the durable store's <c>benches</c> and
/// <c>bench_selectors</c> tables (ADO #144, docs/specs/bench.spec.md §1).
/// <para>
/// The tables live in the attached <c>pending</c> schema; SQLite resolves unqualified table names
/// across attached schemas, so the SQL below carries no prefix (0013). That schema is NEVER
/// dropped — a Bench holds pins the person made by hand, which ADO cannot rebuild and whose loss
/// is silent.
/// </para>
/// </summary>
public sealed class SqliteBenchRepository : IBenchRepository
{
    private readonly SqliteCacheStore _store;

    public SqliteBenchRepository(SqliteCacheStore store) => _store = store;

    public async Task<Bench> GetOrCreateDefaultAsync(
        IReadOnlyCollection<BenchSelector> initialSelectors, CancellationToken ct = default)
    {
        var existing = await LoadAsync("is_default = 1", null, ct);
        if (existing is not null)
        {
            // 🔴 Deliberately NOT reconciled against initialSelectors. Once the default Bench
            // exists it is the person's arrangement, and overwriting it here would silently
            // discard every pin they added by hand on the next command they ran.
            return existing;
        }

        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        var createdAt = DateTimeOffset.UtcNow.ToString("O");

        long benchId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = _store.ActiveTransaction;
            cmd.CommandText = """
                INSERT INTO benches (name, is_default, created_at)
                VALUES (@name, 1, @createdAt)
                ON CONFLICT(name COLLATE NOCASE) DO UPDATE SET is_default = 1
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("@name", Bench.DefaultName);
            cmd.Parameters.AddWithValue("@createdAt", createdAt);
            benchId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        foreach (var selector in initialSelectors)
            await AddSelectorAsync(benchId, selector, ct);

        return await LoadAsync("id = @id", benchId, ct)
            ?? throw new InvalidOperationException("The default Bench could not be read back after creation.");
    }

    public Task<Bench?> GetByNameAsync(string name, CancellationToken ct = default)
        => LoadAsync("name = @name COLLATE NOCASE", null, ct, name);

    public async Task<Bench?> CreateAsync(string name, CancellationToken ct = default)
    {
        // The uniqueness decision is the TABLE's, not a read-then-write here: a check followed by
        // an insert can be raced, and the case-insensitive UNIQUE index is the only place that
        // cannot be. DO NOTHING makes a taken name return no row, which the caller reports.
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();

        long? benchId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = _store.ActiveTransaction;
            cmd.CommandText = """
                INSERT INTO benches (name, is_default, created_at)
                VALUES (@name, 0, @createdAt)
                ON CONFLICT(name COLLATE NOCASE) DO NOTHING
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
            var scalar = await cmd.ExecuteScalarAsync(ct);
            benchId = scalar is null or DBNull ? null : Convert.ToInt64(scalar);
        }

        return benchId is null ? null : await LoadAsync("id = @id", benchId.Value, ct);
    }

    public async Task<IReadOnlyList<Bench>> GetAllAsync(CancellationToken ct = default)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        var benches = new List<Bench>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = _store.ActiveTransaction;
            cmd.CommandText = "SELECT id, name, is_default FROM benches ORDER BY name COLLATE NOCASE;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                benches.Add(new Bench
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    IsDefault = reader.GetInt32(2) == 1,
                });
            }
        }

        var result = new List<Bench>(benches.Count);
        foreach (var bench in benches)
            result.Add(bench with { Selectors = await LoadSelectorsAsync(bench.Id, ct) });

        return result;
    }

    /// <summary>
    /// The Bench the person last switched to, or null when they never have.
    /// <para>
    /// A pointer whose Bench no longer exists also comes back as null, because the read is a JOIN
    /// in effect: the id is looked up and a miss is a miss. So "never switched", "pointer cleared"
    /// and "Bench since deleted" are ONE answer with one meaning — fall back to the default —
    /// rather than three states the caller has to tell apart. That holds whether or not SQLite's
    /// foreign-key enforcement is on, so it does not depend on a connection PRAGMA being set.
    /// </para>
    /// </summary>
    public async Task<Bench?> GetCurrentAsync(CancellationToken ct = default)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT bench_id FROM current_bench WHERE id = 1 AND bench_id IS NOT NULL;";
        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is null or DBNull)
            return null;

        return await LoadAsync("id = @id", Convert.ToInt64(scalar), ct);
    }

    public async Task SetCurrentAsync(long benchId, CancellationToken ct = default)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;

        // Upsert on the pinned single row: switching twice leaves one row saying where the person
        // is, not a history the reader would then need a rule to collapse.
        cmd.CommandText = """
            INSERT INTO current_bench (id, bench_id, switched_at)
            VALUES (1, @benchId, @switchedAt)
            ON CONFLICT(id) DO UPDATE SET bench_id = @benchId, switched_at = @switchedAt;
            """;
        cmd.Parameters.AddWithValue("@benchId", benchId);
        cmd.Parameters.AddWithValue("@switchedAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Bench?> TrySetCurrentAsync(long benchId, CancellationToken ct = default)
    {
        using var operation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        if (_store.ActiveTransaction is not null)
            throw new InvalidOperationException("Bench switching cannot nest a native transaction.");
        using var transaction = conn.BeginTransaction();
        _store.ActiveTransaction = transaction;
        try
        {
            var target = await LoadAsync("id = @id", benchId, ct);
            if (target is null) return null;
            await SetCurrentAsync(target.Id, ct);
            transaction.Commit();
            return target;
        }
        finally { _store.ActiveTransaction = null; }
    }

    public async Task AddSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;

        // Idempotent by the table's UNIQUE index: adding the same selector twice leaves one row,
        // so membership cannot be changed by repetition and overlap cannot duplicate.
        cmd.CommandText = """
            INSERT INTO bench_selectors (bench_id, selector_kind, selector_payload, created_at)
            VALUES (@benchId, @kind, @payload, @createdAt)
            ON CONFLICT(bench_id, selector_kind, selector_payload) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@benchId", benchId);
        cmd.Parameters.AddWithValue("@kind", selector.Kind.ToString());
        cmd.Parameters.AddWithValue("@payload", selector.Payload);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            DELETE FROM bench_selectors
            WHERE bench_id = @benchId AND selector_kind = @kind AND selector_payload = @payload;
            """;
        cmd.Parameters.AddWithValue("@benchId", benchId);
        cmd.Parameters.AddWithValue("@kind", selector.Kind.ToString());
        cmd.Parameters.AddWithValue("@payload", selector.Payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryReplaceQuerySelectorsAsync(long benchId, string expectedSettingsDigest,
        IReadOnlyCollection<BenchSelector> queries, CancellationToken ct = default)
    {
        if (queries.Any(selector => selector.Kind != SelectorKind.Query))
            throw new ArgumentException("Only query selectors can replace automatic settings.", nameof(queries));
        foreach (var selector in queries) _ = BenchQueryRule.Parse(selector);
        using var operation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        if (_store.ActiveTransaction is not null)
            throw new InvalidOperationException("Bench configuration cannot nest a native transaction.");
        using var transaction = conn.BeginTransaction();
        _store.ActiveTransaction = transaction;
        try
        {
            if (!await MatchesCapturedAsync(benchId, expectedSettingsDigest, ct)) return false;
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM bench_selectors WHERE bench_id = @id AND selector_kind = 'Query';";
            command.Parameters.AddWithValue("@id", benchId);
            await command.ExecuteNonQueryAsync(ct);
            foreach (var selector in queries) await AddSelectorAsync(benchId, selector, ct);
            transaction.Commit();
            return true;
        }
        finally { _store.ActiveTransaction = null; }
    }

    public async Task<bool> TryUpdatePinsAsync(long benchId, int workItemId, bool includeSubtree, bool remove,
        string? expectedSettingsDigest = null, CancellationToken ct = default)
    {
        if (workItemId <= 0) throw new ArgumentException("Pins require a positive work item ID.", nameof(workItemId));
        if (remove)
            return await TryRemovePinsAsync(benchId, workItemId, expectedSettingsDigest: expectedSettingsDigest, ct: ct) is not null;
        using var operation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        if (_store.ActiveTransaction is not null)
            throw new InvalidOperationException("Bench pin edits cannot nest a native transaction.");
        using var transaction = conn.BeginTransaction();
        _store.ActiveTransaction = transaction;
        try
        {
            if (!await MatchesCapturedAsync(benchId, expectedSettingsDigest, ct)) return false;
            await AddSelectorAsync(benchId, includeSubtree ? BenchSelector.ForSubtree(workItemId) : BenchSelector.ForItem(workItemId), ct);
            transaction.Commit();
            return true;
        }
        finally { _store.ActiveTransaction = null; }
    }

    public async Task<(Bench Bench, bool WasPinned)?> TryRemovePinsAsync(long benchId, int workItemId,
        TrackingMode? mode = null, string? expectedSettingsDigest = null, CancellationToken ct = default)
    {
        if (workItemId <= 0) throw new ArgumentException("Pins require a positive work item ID.", nameof(workItemId));
        if (mode is not null and not TrackingMode.Single and not TrackingMode.Tree)
            throw new ArgumentException("Pin removal mode must be single or tree.", nameof(mode));
        using var operation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        if (_store.ActiveTransaction is not null)
            throw new InvalidOperationException("Bench pin edits cannot nest a native transaction.");
        using var transaction = conn.BeginTransaction();
        _store.ActiveTransaction = transaction;
        try
        {
            if (!await MatchesCapturedAsync(benchId, expectedSettingsDigest, ct)) return null;
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = mode is null
                ? """
                    DELETE FROM bench_selectors
                    WHERE bench_id = @benchId AND selector_payload = @payload
                        AND selector_kind IN ('Item', 'Subtree');
                    """
                : """
                    DELETE FROM bench_selectors
                    WHERE bench_id = @benchId AND selector_payload = @payload AND selector_kind = @kind;
                    """;
            command.Parameters.AddWithValue("@benchId", benchId);
            command.Parameters.AddWithValue("@payload", workItemId.ToString());
            if (mode is not null)
                command.Parameters.AddWithValue("@kind", mode == TrackingMode.Tree ? nameof(SelectorKind.Subtree) : nameof(SelectorKind.Item));
            var wasPinned = await command.ExecuteNonQueryAsync(ct) > 0;
            var bench = await LoadAsync("id = @id", benchId, ct)
                ?? throw new InvalidOperationException("The captured Bench disappeared during its pin transaction.");
            transaction.Commit();
            return (bench, wasPinned);
        }
        finally { _store.ActiveTransaction = null; }
    }

    private async Task<bool> MatchesCapturedAsync(long benchId, string? settingsDigest, CancellationToken ct)
    {
        var current = await GetCurrentAsync(ct) ?? await GetByNameAsync(Bench.DefaultName, ct);
        return current?.Id == benchId && (settingsDigest is null || string.Equals(
            BenchQueryRule.SettingsDigest(current.Selectors), settingsDigest, StringComparison.Ordinal));
    }

    /// <summary>
    /// Removes a Bench, its selectors, and the current-Bench pointer when it named this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 What is NOT touched: the pending set, seeds, exclusions, tracked items. Deleting a Bench
    /// is a view operation. A cascade that reached staged edits would destroy work twig owes ADO,
    /// which is precisely the loss this ticket exists to prevent — so the three statements below
    /// are the whole of the deletion, deliberately.
    /// </para>
    /// <para>
    /// The selectors and the pointer are ALSO declared <c>ON DELETE CASCADE</c> / <c>SET NULL</c>
    /// in the schema, and Microsoft.Data.Sqlite turns foreign keys on by default, so the first two
    /// statements are redundant against today's provider — measured, not assumed: with them
    /// removed the orphan-row test still passes. They are kept because that redundancy is one
    /// connection PRAGMA away from being load-bearing, and the failure it would produce is silent
    /// (unreachable rows in the store that is never dropped). Belt and braces here costs two
    /// statements; the alternative costs a durable-store leak nobody notices.
    /// </para>
    /// </remarks>
    public async Task DeleteAsync(long benchId, CancellationToken ct = default)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            DELETE FROM bench_selectors WHERE bench_id = @benchId;
            UPDATE current_bench SET bench_id = NULL WHERE bench_id = @benchId;
            DELETE FROM benches WHERE id = @benchId;
            """;
        cmd.Parameters.AddWithValue("@benchId", benchId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Bench?> TryDeleteAsync(long benchId, string expectedContentsDigest, CancellationToken ct = default)
    {
        using var operation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        if (_store.ActiveTransaction is not null)
            throw new InvalidOperationException("Bench deletion cannot nest a native transaction.");
        using var transaction = conn.BeginTransaction();
        _store.ActiveTransaction = transaction;
        try
        {
            var target = await LoadAsync("id = @id", benchId, ct);
            if (target is null || target.IsDefault || !string.Equals(
                BenchQueryRule.ContentsDigest(target.Selectors), expectedContentsDigest, StringComparison.Ordinal))
                return null;
            await DeleteAsync(target.Id, ct);
            transaction.Commit();
            return target;
        }
        finally { _store.ActiveTransaction = null; }
    }

    private async Task<Bench?> LoadAsync(string where, long? id, CancellationToken ct, string? name = null)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        Bench? bench = null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = _store.ActiveTransaction;
            cmd.CommandText = $"SELECT id, name, is_default FROM benches WHERE {where} LIMIT 1;";
            if (id is not null) cmd.Parameters.AddWithValue("@id", id.Value);
            if (name is not null) cmd.Parameters.AddWithValue("@name", name);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                bench = new Bench
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    IsDefault = reader.GetInt32(2) == 1,
                };
            }
        }

        if (bench is null)
            return null;

        return bench with { Selectors = await LoadSelectorsAsync(bench.Id, ct) };
    }

    /// <summary>
    /// Reads a Bench's selectors. Ordered by id purely so the read is deterministic for tests and
    /// diffs — evaluation is order-free, so nothing downstream may depend on this sequence.
    /// </summary>
    private async Task<IReadOnlyCollection<BenchSelector>> LoadSelectorsAsync(long benchId, CancellationToken ct)
    {
        using var bindingOperation = _store.AcquireOperation();
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            SELECT selector_kind, selector_payload
            FROM bench_selectors
            WHERE bench_id = @benchId
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("@benchId", benchId);

        var selectors = new List<BenchSelector>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!Enum.TryParse<SelectorKind>(reader.GetString(0), out var kind))
                throw new InvalidOperationException(
                    $"Bench {benchId} holds a selector of unknown kind '{reader.GetString(0)}'. " +
                    "This build is older than the Bench that wrote it; upgrade rather than " +
                    "silently dropping a rule the person added.");

            selectors.Add(new BenchSelector(kind, reader.GetString(1)));
        }

        return selectors;
    }
}
