using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed record RetainedAssessmentReview(
    string Digest, string Fingerprint, int ModelVersion, string ModelJson,
    DateTimeOffset CapturedAt, long Revision, IReadOnlyList<ReviewAssessment> Assessments);

/// <summary>Caller and surface are reported attribution, never proof of a human gesture or identity.</summary>
internal sealed record ReviewAssessment(
    long Revision, string Caller, string Surface, string Section, string? OperationId,
    string Text, DateTimeOffset RecordedAt, long? SupersedesRevision);

internal sealed record AssessmentAppendResult(bool Recorded, RetainedAssessmentReview Review);

/// <summary>
/// Native-only persistence foundation. Accepts a native-built model, not a browser-supplied
/// replacement. Assessments never update proposal state, authorization, readiness or audit columns.
/// </summary>
internal sealed class SqliteReviewAssessmentRepository(SqliteCacheStore store, TimeProvider clock)
{
    internal RetainedAssessmentReview Retain(ChangeProposalReviewModel model, PlanOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(origin);
        RequireDigest(model.Digest);
        using var operation = store.AcquireOperation();
        RequireOwnTransaction();
        using var transaction = store.GetConnection().BeginTransaction(deferred: false);
        var originJson = SerializeOrigin(origin);
        using (var journal = Command(transaction, """
            SELECT organization, project, origin_json FROM pending.proposal_journals WHERE digest = @digest;
            """))
        {
            journal.Parameters.AddWithValue("@digest", model.Digest);
            using var reader = journal.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(2) ||
                reader.GetString(0) != model.Workspace.Organization || reader.GetString(1) != model.Workspace.Project ||
                reader.GetString(2) != originJson)
                throw new InvalidOperationException("review-origin-mismatch: retain requires the exact native journal workspace and origin.");
        }
        using (var operations = Command(transaction, """
            SELECT ordinal, op_id FROM pending.proposal_operations WHERE digest = @digest ORDER BY ordinal;
            """))
        {
            operations.Parameters.AddWithValue("@digest", model.Digest);
            using var reader = operations.ExecuteReader();
            var ordinal = 0;
            while (reader.Read())
            {
                if (ordinal >= model.Operations.Count || model.Operations[ordinal].Ordinal != reader.GetInt32(0) ||
                    model.Operations[ordinal].OpId != reader.GetString(1))
                    throw new InvalidOperationException("review-operation-mismatch: retain requires complete native operation identity and order.");
                ordinal++;
            }
            if (ordinal != model.Operations.Count)
                throw new InvalidOperationException("review-operation-mismatch: retain requires every native operation.");
        }
        var modelJson = ChangeProposalReviewModelJson.Serialize(model);
        var fingerprint = Fingerprint(originJson, modelJson);
        using (var insert = Command(transaction, """
            INSERT OR IGNORE INTO pending.review_observations
                (digest, fingerprint, model_version, origin_json, model_json, captured_at, revision)
            VALUES (@digest, @fingerprint, @version, @origin, @model, @at, 0);
            """))
        {
            insert.Parameters.AddWithValue("@digest", model.Digest);
            insert.Parameters.AddWithValue("@fingerprint", fingerprint);
            insert.Parameters.AddWithValue("@version", model.ModelVersion);
            insert.Parameters.AddWithValue("@origin", originJson);
            insert.Parameters.AddWithValue("@model", modelJson);
            insert.Parameters.AddWithValue("@at", Format(clock.GetUtcNow()));
            insert.ExecuteNonQuery();
        }
        var retained = ReadCore(model.Digest, fingerprint, originJson, transaction);
        transaction.Commit();
        return retained;
    }

    internal RetainedAssessmentReview Read(string digest, string fingerprint, PlanOrigin origin)
    {
        RequireDigest(digest);
        RequireDigest(fingerprint);
        using var operation = store.AcquireOperation();
        RequireOwnTransaction();
        using var transaction = store.GetConnection().BeginTransaction(deferred: true);
        var review = ReadCore(digest, fingerprint, SerializeOrigin(origin), transaction);
        transaction.Commit();
        return review;
    }

    internal AssessmentAppendResult Append(
        string digest, string fingerprint, PlanOrigin origin, long expectedRevision,
        string caller, string surface, string section, string? operationId, string text,
        long? supersedesRevision = null)
    {
        RequireDigest(digest);
        RequireDigest(fingerprint);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        RequireText(caller, 512, nameof(caller));
        RequireText(surface, 512, nameof(surface));
        RequireText(text, 16384, nameof(text));
        if (section is not ("overview" or "effects" or "evidence" or "review"))
            throw new ArgumentException("Unknown review section.", nameof(section));
        using var operation = store.AcquireOperation();
        RequireOwnTransaction();
        using var transaction = store.GetConnection().BeginTransaction(deferred: false);
        var originJson = SerializeOrigin(origin);
        var review = ReadCore(digest, fingerprint, originJson, transaction);
        if (review.Revision != expectedRevision)
            return new(false, review);
        if (operationId is not null)
        {
            using var model = JsonDocument.Parse(review.ModelJson);
            if (section != "effects" || !model.RootElement.GetProperty("operations").EnumerateArray()
                .Any(item => item.GetProperty("opId").GetString() == operationId))
                throw new ArgumentException("Feedback operation must belong to this exact retained review.", nameof(operationId));
        }
        if (supersedesRevision is { } supersedes &&
            (!review.Assessments.Any(item => item.Revision == supersedes &&
                item.Caller == caller && item.Surface == surface && item.Section == section && item.OperationId == operationId) ||
             review.Assessments.Any(item => item.SupersedesRevision == supersedes)))
            throw new ArgumentException("Supersession requires an unsuperseded entry with the same reported caller, surface and scope.",
                nameof(supersedesRevision));
        using (var update = Command(transaction, """
            UPDATE pending.review_observations SET revision = revision + 1
            WHERE digest = @digest AND fingerprint = @fingerprint AND revision = @expected;
            """))
        {
            update.Parameters.AddWithValue("@digest", digest);
            update.Parameters.AddWithValue("@fingerprint", fingerprint);
            update.Parameters.AddWithValue("@expected", expectedRevision);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("review-revision-conflict: no assessment recorded.");
        }
        using (var insert = Command(transaction, """
            INSERT INTO pending.review_assessments
                (digest, fingerprint, revision, caller, surface, section, operation_id, text, recorded_at, supersedes_revision)
            VALUES (@digest, @fingerprint, @revision, @caller, @surface, @section, @op, @text, @at, @supersedes);
            """))
        {
            insert.Parameters.AddWithValue("@digest", digest);
            insert.Parameters.AddWithValue("@fingerprint", fingerprint);
            insert.Parameters.AddWithValue("@revision", expectedRevision + 1);
            insert.Parameters.AddWithValue("@caller", caller);
            insert.Parameters.AddWithValue("@surface", surface);
            insert.Parameters.AddWithValue("@section", section);
            insert.Parameters.AddWithValue("@op", (object?)operationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@text", text);
            insert.Parameters.AddWithValue("@at", Format(clock.GetUtcNow()));
            insert.Parameters.AddWithValue("@supersedes", (object?)supersedesRevision ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        var recorded = ReadCore(digest, fingerprint, originJson, transaction);
        transaction.Commit();
        return new(true, recorded);
    }

    private RetainedAssessmentReview ReadCore(string digest, string fingerprint, string originJson, SqliteTransaction transaction)
    {
        int version;
        string modelJson;
        DateTimeOffset captured;
        long revision;
        using (var query = Command(transaction, """
            SELECT model_version, model_json, captured_at, revision, origin_json FROM pending.review_observations
            WHERE digest = @digest AND fingerprint = @fingerprint;
            """))
        {
            query.Parameters.AddWithValue("@digest", digest);
            query.Parameters.AddWithValue("@fingerprint", fingerprint);
            using var reader = query.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("review-not-found: no observation for this exact digest and fingerprint; no replacement was captured.");
            version = reader.GetInt32(0);
            modelJson = reader.GetString(1);
            captured = Parse(reader.GetString(2));
            revision = reader.GetInt64(3);
            if (reader.GetString(4) != originJson || Fingerprint(originJson, modelJson) != fingerprint)
                throw new InvalidOperationException("review-origin-mismatch: observation cannot be rebound to another native origin.");
        }
        using var entries = Command(transaction, """
            SELECT revision, caller, surface, section, operation_id, text, recorded_at, supersedes_revision
            FROM pending.review_assessments WHERE digest = @digest AND fingerprint = @fingerprint ORDER BY revision;
            """);
        entries.Parameters.AddWithValue("@digest", digest);
        entries.Parameters.AddWithValue("@fingerprint", fingerprint);
        var assessments = new List<ReviewAssessment>();
        using var rows = entries.ExecuteReader();
        while (rows.Read())
            assessments.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                rows.IsDBNull(4) ? null : rows.GetString(4), rows.GetString(5), Parse(rows.GetString(6)),
                rows.IsDBNull(7) ? null : rows.GetInt64(7)));
        return new(digest, fingerprint, version, modelJson, captured, revision, assessments.AsReadOnly());
    }

    private SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var command = store.GetConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private void RequireOwnTransaction()
    {
        if (store.ActiveTransaction is not null)
            throw new InvalidOperationException("Review assessment persistence cannot nest a workspace transaction.");
    }

    private static string Fingerprint(string originJson, string modelJson)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"twig.review-observation.v1\n{originJson}\n{modelJson}")));

    private static string SerializeOrigin(PlanOrigin origin)
        => JsonSerializer.Serialize(origin, TwigJsonContext.Default.PlanOrigin);

    private static void RequireDigest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Expected an exact lowercase SHA-256 reference.", nameof(value));
    }

    private static void RequireText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Contains('\0'))
            throw new ArgumentException($"Expected nonblank text of at most {maximum} characters without NUL.", name);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
}
