using Twig.Core;

namespace Twig.Infrastructure.Services;

internal sealed record PrivateInput(string Item, string Field, long Version, Guid? Write, string? Text, string? Baseline,
    long? Acknowledged);
internal sealed record InputReview(Guid Write, Guid Review, UpdateIdentity? Original);

public sealed partial class UserBenchStore
{
    internal Task<Result<PrivateInput>> AccessInputAsync(string scope, string item, string field, Guid? write, long? expected,
        string? text, string? baseline, CancellationToken ct, bool rereview = false, UpdateOrigin? origin = null) => UpdateTransaction(async (command, token) =>
    {
        command.CommandText = InputSchema + """
            SELECT version,write,text,baseline FROM private_inputs WHERE scope=$s AND item=$i AND field=$f;
            """;
        command.Parameters.AddWithValue("$s", scope);
        command.Parameters.AddWithValue("$i", item);
        command.Parameters.AddWithValue("$f", field);
        PrivateInput current = new(item, field, 0, null, null, null, null);
        await using (var r = await command.ExecuteReaderAsync(token))
            if (await r.ReadAsync(token)) current = current with { Version = r.GetInt64(0), Write = Guid.Parse(r.GetString(1)),
                Text = r.IsDBNull(2) ? null : r.GetString(2), Baseline = r.GetString(3) };
        if (write is null) return Result<PrivateInput>.Ok(current);
        command.Parameters.AddWithValue("$w", write.Value.ToString("D"));
        command.CommandText = "SELECT version,hash FROM private_input_receipts WHERE scope=$s AND item=$i AND field=$f AND write=$w;";
        var hash = expected.HasValue ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { expected.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                baseline!, rereview ? "rereview" : text is null ? "discard" : "put", text ?? "" }, UpdateJson.Default.StringArray))) : null;
        await using (var r = await command.ExecuteReaderAsync(token))
            if (await r.ReadAsync(token))
                return hash is not null && hash != r.GetString(1)
                    ? Result<PrivateInput>.Fail(ErrorCode.Conflict, "The input write identity already binds different values.")
                    : Result<PrivateInput>.Ok(current with { Acknowledged = r.GetInt64(0) });
        if (!expected.HasValue) return Result<PrivateInput>.Ok(current);
        if (current.Version != expected || expected >= 9007199254740991L)
            return Result<PrivateInput>.Fail(ErrorCode.Conflict, "Private input changed. Inspect before another write.");
        if (rereview)
        {
            if (origin is null || current.Text is null || current.Text != text)
                return Result<PrivateInput>.Fail(ErrorCode.Conflict, "Re-review requires the original retained text.");
            command.Parameters.AddWithValue("$old", current.Write!.Value.ToString("D"));
            command.Parameters.AddWithValue("$origin", OriginKey(origin));
            command.CommandText = """
                SELECT a.state, a.dispatch FROM private_input_receipts r JOIN update_actions a ON a.origin=$origin AND a.review=r.review
                WHERE r.scope=$s AND r.item=$i AND r.field=$f AND r.write=$old;
                """;
            await using var r = await command.ExecuteReaderAsync(token);
            if (await r.ReadAsync(token) ? r.GetString(0) != "ReviewRequired" || !r.IsDBNull(1) : current.Baseline == baseline)
                return Result<PrivateInput>.Fail(ErrorCode.Conflict, "No changed unsubmitted capture or zero-send ReviewRequired action.");
        }
        else if (current.Version > 0 && current.Text == text)
            return Result<PrivateInput>.Fail(ErrorCode.Conflict, "An unchanged flush does not establish a new review.");
        var next = current.Version + 1;
        command.Parameters.AddWithValue("$v", next);
        command.Parameters.AddWithValue("$h", hash!);
        command.Parameters.AddWithValue("$t", (object?)text ?? DBNull.Value);
        command.Parameters.AddWithValue("$b", baseline!);
        command.Parameters.AddWithValue("$r", Guid.NewGuid().ToString("D"));
        command.CommandText = """
            INSERT INTO private_input_receipts VALUES($s,$i,$f,$w,$v,$h,$r);
            INSERT INTO private_inputs VALUES($s,$i,$f,$v,$w,$t,$b)
                ON CONFLICT(scope,item,field) DO UPDATE SET version=$v,write=$w,text=$t,baseline=$b;
            """;
        await command.ExecuteNonQueryAsync(token);
        return Result<PrivateInput>.Ok(new(item, field, next, write, text, baseline, next));
    }, ct);

    private const string InputSchema = """
        CREATE TABLE IF NOT EXISTS private_inputs(scope TEXT NOT NULL, item TEXT NOT NULL, field TEXT NOT NULL,
            version INTEGER NOT NULL, write TEXT NOT NULL, text TEXT, baseline TEXT NOT NULL, PRIMARY KEY(scope,item,field)) STRICT;
        CREATE TABLE IF NOT EXISTS private_input_receipts(scope TEXT NOT NULL,item TEXT NOT NULL,field TEXT NOT NULL,
            write TEXT NOT NULL,version INTEGER NOT NULL,hash TEXT NOT NULL,review TEXT NOT NULL UNIQUE,PRIMARY KEY(scope,item,field,write)) STRICT;
        """;
    internal Task<Result<InputReview[]>> InputReviewsAsync(string scope, UpdateOrigin origin, string field, Guid? write, CancellationToken ct)
        => UpdateTransaction(async (cmd, token) =>
        {
            cmd.CommandText = InputSchema + """
                SELECT r.write,r.review,a.action,a.submission,a.state FROM private_input_receipts r
                LEFT JOIN update_actions a ON a.review=r.review AND a.origin=$o
                WHERE r.scope=$s AND r.item=$i AND r.field=$f AND (($w IS NULL AND a.action IS NOT NULL) OR r.write=$w)
                ORDER BY r.version DESC LIMIT 32;
                """;
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$i", origin.Item);
            cmd.Parameters.AddWithValue("$o", OriginKey(origin));
            cmd.Parameters.AddWithValue("$f", field);
            cmd.Parameters.AddWithValue("$w", (object?)write?.ToString("D") ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(token);
            var result = new List<InputReview>();
            while (await r.ReadAsync(token))
            {
                var review = Guid.Parse(r.GetString(1));
                result.Add(new(Guid.Parse(r.GetString(0)), review, r.IsDBNull(2) ? null :
                    new(Guid.Parse(r.GetString(2)), Guid.Parse(r.GetString(3)), review, r.GetString(4))));
            }
            return Result<InputReview[]>.Ok(result.ToArray());
        }, ct);
}
