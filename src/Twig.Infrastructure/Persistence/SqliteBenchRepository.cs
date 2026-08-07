using Microsoft.Data.Sqlite;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

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

    public async Task AddSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default)
    {
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

    private async Task<Bench?> LoadAsync(string where, long? id, CancellationToken ct, string? name = null)
    {
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
