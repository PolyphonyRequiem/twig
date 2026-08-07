using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IIterationCalendar"/>: the local mapping from iteration path to the
/// span of time it covers.
/// <para>
/// 🔴 In the DISPOSABLE mirror, not the durable store, by 0005's test — ADO can rebuild this,
/// because it is a copy of ADO's own iteration list. Nothing is lost if the mirror is dropped;
/// the next refresh restores it.
/// </para>
/// <para>
/// This is what lets a Bench hold the stable rule ("the iteration covering today") instead of a
/// frozen iteration name or a network call, so the view keeps up when a sprint ends AND still
/// displays with the ADO endpoint unreachable.
/// </para>
/// </summary>
public sealed class SqliteIterationCalendar : IIterationCalendar
{
    private readonly SqliteCacheStore _store;
    private readonly Func<DateTimeOffset> _clock;

    public SqliteIterationCalendar(SqliteCacheStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<IterationPath>> GetCurrentIterationsAsync(CancellationToken ct = default)
    {
        var now = _clock().ToString("O");
        var conn = _store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;

        // "Covers now" is inclusive of both ends. A row with no dates can never be current — it
        // would otherwise match everything and quietly widen the view.
        cmd.CommandText = """
            SELECT path FROM iteration_calendar
            WHERE start_date IS NOT NULL AND end_date IS NOT NULL
              AND start_date <= @now AND end_date >= @now
            ORDER BY start_date, path;
            """;
        cmd.Parameters.AddWithValue("@now", now);

        var paths = new List<IterationPath>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var parsed = IterationPath.Parse(reader.GetString(0));
            if (parsed.IsSuccess)
                paths.Add(parsed.Value);
        }

        return paths;
    }

    public async Task SaveAsync(IReadOnlyList<TeamIteration> iterations, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = _store.ActiveTransaction;
            clear.CommandText = "DELETE FROM iteration_calendar;";
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var iteration in iterations)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = _store.ActiveTransaction;
            cmd.CommandText = """
                INSERT INTO iteration_calendar (path, start_date, end_date)
                VALUES (@path, @start, @end)
                ON CONFLICT(path) DO UPDATE SET
                    start_date = excluded.start_date,
                    end_date = excluded.end_date;
                """;
            cmd.Parameters.AddWithValue("@path", iteration.Path);
            cmd.Parameters.AddWithValue("@start", (object?)iteration.StartDate?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@end", (object?)iteration.EndDate?.ToString("O") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
