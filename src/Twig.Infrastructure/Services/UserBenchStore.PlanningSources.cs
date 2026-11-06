using System.Text.Json;
using Twig.Core;

namespace Twig.Infrastructure.Services;

/// <summary>Recorded catalogue metadata, not query execution, live availability or a read-access grant.</summary>
public sealed record NativePlanningSourcesCache(PlanningSources? Sources, DateTimeOffset? ObservedAt);

public sealed partial class UserBenchStore
{
    internal async Task<Result<NativePlanningSourcesCache>> ReadPlanningSourcesAsync(Connection connection, string? team, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='planning_sources';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                return Result<NativePlanningSourcesCache>.Ok(new(null, null));
            command.CommandText = "SELECT document FROM planning_sources WHERE connection=$connection AND team=$team;";
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
            command.Parameters.AddWithValue("$team", team ?? "");
            var document = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (document is null) return Result<NativePlanningSourcesCache>.Ok(new(null, null));
            if (document.Length > 2 * 1024 * 1024) throw new InvalidDataException("The native source catalogue exceeds its byte bound.");
            var cache = JsonSerializer.Deserialize(document, UserBenchJson.Default.NativePlanningSourcesCache)
                ?? throw new JsonException("Missing native source catalogue.");
            ValidatePlanningSources(team, cache);
            return Result<NativePlanningSourcesCache>.Ok(cache);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativePlanningSourcesCache>(ex); }
    }

    internal async Task<Result<NativePlanningSourcesCache>> SavePlanningSourcesAsync(Connection connection, string? team,
        NativePlanningSourcesCache cache, CancellationToken ct)
    {
        try
        {
            ValidatePlanningSources(team, cache);
            var document = JsonSerializer.Serialize(cache, UserBenchJson.Default.NativePlanningSourcesCache);
            if (document.Length > 2 * 1024 * 1024) throw new InvalidDataException("The native source catalogue exceeds its byte bound.");
            await using var db = await Open(ct).ConfigureAwait(false);
            using var transaction = db.BeginTransaction(deferred: false);
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS planning_sources(
                    connection TEXT NOT NULL,team TEXT NOT NULL,observed_at TEXT NOT NULL,document TEXT NOT NULL,
                    PRIMARY KEY(connection,team));
                INSERT INTO planning_sources(connection,team,observed_at,document) VALUES($connection,$team,$observed,$document)
                ON CONFLICT(connection,team) DO UPDATE SET observed_at=excluded.observed_at,document=excluded.document
                    WHERE planning_sources.observed_at<excluded.observed_at;
                """;
            command.Parameters.AddWithValue("$connection", JsonSerializer.Serialize(connection, UserBenchJson.Default.Connection));
            command.Parameters.AddWithValue("$team", team ?? "");
            command.Parameters.AddWithValue("$observed", cache.ObservedAt!.Value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$document", document);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return Result<NativePlanningSourcesCache>.Fail(ErrorCode.Conflict, "A newer native source catalogue was retained; no older list was substituted.");
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return Result<NativePlanningSourcesCache>.Ok(cache);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativePlanningSourcesCache>(ex); }
    }

    private static void ValidatePlanningSources(string? team, NativePlanningSourcesCache cache)
    {
        var sources = cache.Sources;
        if (sources is null || cache.ObservedAt is null || cache.ObservedAt == default(DateTimeOffset)
            || sources.Team != team || sources.Entries.IsDefault || sources.Gaps.IsDefault || sources.Entries.Length > 2112
            || sources.Entries.Select(source => (source.Kind, source.Id)).Distinct().Count() != sources.Entries.Length
            || sources.Entries.Any(source => !Enum.IsDefined(source.Kind)
                || string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 256 || source.Id.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 1024 || source.Name.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(source.Path) || source.Path.Length > 8192 || source.Path.Any(char.IsControl)
                || source.Kind == PlanningSourceKind.Backlog && (team is null || source.IsFolder || !source.RequiresTeam))
            || sources.Gaps.Length > 64 || sources.Gaps.Any(gap => string.IsNullOrWhiteSpace(gap) || gap.Length > 8192))
            throw new InvalidDataException("The native source catalogue is incomplete or belongs to another selected team.");
    }
}
