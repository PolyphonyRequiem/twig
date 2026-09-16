using System.Text.Json;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// Concise, trustworthy per-operation diagnostics derived from an existing
/// <see cref="PlanJournalOperation"/>. Reads only fields the journal already exposes —
/// <see cref="PlanJournalOperation.State"/>, <see cref="PlanJournalOperation.RequestJson"/>,
/// and <see cref="PlanJournalOperation.ResultJson"/> — and yields a projection every
/// surface (CLI, MCP) can render identically.
/// <para>
/// The <see cref="Disposition"/> is derived from journal <see cref="PlanJournalOperation.State"/>
/// alone; a null <see cref="Code"/> means the row predates the additive diagnostics payload
/// (AB#881) OR the executor produced a legacy shape. Known state is preserved and no
/// per-code detail is fabricated — <see cref="Fields"/>, <see cref="MissingFields"/>,
/// <see cref="ExpectedRevision"/>, and <see cref="ObservedRevision"/> stay defaulted /
/// empty. Callers that want the full evidence trail read raw
/// <see cref="PlanJournalOperation.ResultJson"/>, <see cref="PlanJournalOperation.Warning"/>,
/// and <see cref="PlanJournalOperation.Error"/> — this projection deliberately never
/// echoes their arbitrary bodies (they may carry raw ADO response fragments with
/// user-authored values).
/// </para>
/// </summary>
/// <remarks>
/// <see cref="From"/> parses <see cref="PlanJournalOperation.ResultJson"/> defensively via
/// <see cref="JsonDocument"/>. Legacy rows (null, canonical <c>{"revision":N}</c> /
/// <c>{"deleted":true}</c> / <c>{"identity":…,"publishedId":N}</c>), truncated payloads,
/// and unknown diagnostic shapes never throw — an unknown payload yields the base
/// <see cref="Disposition"/> with defaults, and a partial payload contributes only the
/// fields it can parse. A stray <c>{"rev":N}</c> PATCH-acknowledgment shape is treated
/// as an unknown payload — <c>rev</c> is not a proven post-op readback and is
/// deliberately never surfaced as <see cref="ObservedRevision"/>.
/// <see cref="ExpectedRevision"/> falls back to <c>expectedRevision</c> read from
/// <see cref="PlanJournalOperation.RequestJson"/> so the bound the operation was
/// authored against surfaces even on older rows.
/// </remarks>
public sealed record PlanOperationDiagnostics
{
    /// <summary>
    /// Journal-state-derived disposition. Uncollapsed: partial lifecycle states
    /// (<c>in-flight</c>, <c>awaiting-verification</c>, <c>not-started</c>) are distinct
    /// from terminal <c>verified</c> / <c>failed</c> / <c>outcome-unknown</c>. Never
    /// null.
    /// </summary>
    public required string Disposition { get; init; }

    /// <summary>
    /// Diagnostic code the executor recorded on <see cref="PlanJournalOperation.ResultJson"/>,
    /// or null when the row predates the additive payload or carried an unrecognised body.
    /// Callers that need the full explanation read the raw <c>resultJson</c>/<c>warning</c>/
    /// <c>error</c> fields.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>
    /// Revision the plan authored the operation against. Read from the diagnostics payload
    /// when present, else from <see cref="PlanJournalOperation.RequestJson"/>'s
    /// <c>expectedRevision</c>. Null when neither source names one (e.g. publish-seed).
    /// </summary>
    public int? ExpectedRevision { get; init; }

    /// <summary>
    /// Post-op server revision the readback observed. Null when the diagnostics payload
    /// did not name one (link-only readbacks, deletes, and any unavailable readback).
    /// </summary>
    public int? ObservedRevision { get; init; }

    /// <summary>
    /// Per-field classifications the readback assigned. Bounded field NAMES and
    /// classification tokens only — never actual values from the field body. Order
    /// matches the payload; empty when the row carries no diagnostics.
    /// </summary>
    public IReadOnlyList<PlanOperationDiagnosticField> Fields { get; init; }
        = Array.Empty<PlanOperationDiagnosticField>();

    /// <summary>
    /// Required-field reference names the executor observed missing. Bounded reference
    /// names only, no values. Empty when the row carries no diagnostics or no missing
    /// fields.
    /// </summary>
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// One-line human-safe summary bounded to a fixed vocabulary of dispositions, codes,
    /// classification tokens, revision integers, and field reference names. Never
    /// contains raw <see cref="PlanJournalOperation.Error"/> or
    /// <see cref="PlanJournalOperation.Warning"/> text — those are arbitrary and may
    /// carry sensitive values.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Projects a journal row into diagnostics. Never throws on malformed or legacy
    /// payloads: state is always derived, and any parse issue quietly leaves Code null
    /// and detail lists empty so callers preserve the known lifecycle disposition
    /// without pretending to know evidence they do not have.
    /// </summary>
    public static PlanOperationDiagnostics From(PlanJournalOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var disposition = DispositionFor(operation.State);

        string? code = null;
        int? expectedRevision = null;
        int? observedRevision = null;
        IReadOnlyList<PlanOperationDiagnosticField> fields = Array.Empty<PlanOperationDiagnosticField>();
        IReadOnlyList<string> missingFields = Array.Empty<string>();

        // Defensive parse of ResultJson. Legacy authoritative-readback shapes
        // ({"revision":N}, {"deleted":true}, {"identity":…,"publishedId":N}) are honored
        // for their revision hint but carry no diagnostics object. The pre-readback
        // executor also emitted {"rev":N}, which is the PATCH acknowledgment — NOT an
        // authoritative readback — so it is deliberately never surfaced as
        // ObservedRevision (a caller would otherwise mistake a bare PATCH ack for a
        // proven post-op revision). A JsonException, an unexpected root kind, or an
        // unknown diagnostics shape leaves diagnostics defaulted.
        if (!string.IsNullOrEmpty(operation.ResultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(operation.ResultJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = doc.RootElement;
                    observedRevision = ReadRevision(root, "revision");
                    if (root.TryGetProperty("diagnostics", out var diag)
                        && diag.ValueKind == JsonValueKind.Object)
                    {
                        var candidateCode = ReadString(diag, "code");
                        code = candidateCode is "verified" or "field-mismatch" or "missing-required-fields"
                            or "revision-not-advanced" or "revision-conflict" or "readback-unavailable"
                            ? candidateCode : null;
                        expectedRevision = ReadRevision(diag, "expectedRevision");
                        var observedFromDiag = ReadRevision(diag, "observedRevision");
                        if (observedFromDiag is not null)
                            observedRevision = observedFromDiag;
                        fields = ReadFields(diag);
                        missingFields = ReadStringArray(diag, "missingFields");
                    }
                }
            }
            catch (JsonException)
            {
                // Legacy or truncated payload: keep known disposition, drop derived detail.
            }
        }

        // Fallback: expectedRevision from RequestJson when the diagnostics payload did
        // not name one. Batch/link/delete all serialize expectedRevision at the root
        // (publish-seed does not).
        if (expectedRevision is null && !string.IsNullOrEmpty(operation.RequestJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(operation.RequestJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    expectedRevision = ReadRevision(doc.RootElement, "expectedRevision");
            }
            catch (JsonException)
            {
                // RequestJson should always parse — it was canonicalized at import — but
                // do not throw from a projection.
            }
        }

        var summary = BuildSummary(
            disposition, code, expectedRevision, observedRevision, fields, missingFields, operation);

        return new PlanOperationDiagnostics
        {
            Disposition = disposition,
            Code = code,
            ExpectedRevision = expectedRevision,
            ObservedRevision = observedRevision,
            Fields = fields,
            MissingFields = missingFields,
            Summary = summary,
        };
    }

    private static string DispositionFor(PlanOperationState state) => state switch
    {
        PlanOperationState.Verified => "verified",
        PlanOperationState.Failed => "failed",
        PlanOperationState.Indeterminate => "outcome-unknown",
        PlanOperationState.Applying => "in-flight",
        PlanOperationState.Applied => "awaiting-verification",
        PlanOperationState.Confirmed => "not-started",
        PlanOperationState.Planned => "not-started",
        _ => "unknown",
    };

    private static int? ReadRevision(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var v))
        {
            return v;
        }
        return null;
    }


    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static IReadOnlyList<PlanOperationDiagnosticField> ReadFields(JsonElement diag)
    {
        if (!diag.TryGetProperty("fields", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<PlanOperationDiagnosticField>();

        var list = new List<PlanOperationDiagnosticField>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var field = ReadString(el, "field");
            var classification = ReadString(el, "classification");
            if (field is null || classification is null) continue;
            list.Add(new PlanOperationDiagnosticField
            {
                Field = field,
                Classification = classification,
            });
        }
        return list;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (s is not null) list.Add(s);
            }
        }
        return list;
    }

    /// <summary>
    /// Bounded-vocabulary one-liner. Contents restricted to: the disposition token, the
    /// diagnostic code token, integer revisions, classification tokens, and reference
    /// names from the diagnostics payload — never <see cref="PlanJournalOperation.Error"/>
    /// or <see cref="PlanJournalOperation.Warning"/> bodies, which are arbitrary and may
    /// carry sensitive values.
    /// </summary>
    private static string BuildSummary(
        string disposition,
        string? code,
        int? expectedRevision,
        int? observedRevision,
        IReadOnlyList<PlanOperationDiagnosticField> fields,
        IReadOnlyList<string> missingFields,
        PlanJournalOperation operation)
    {
        // No diagnostic code: fall back to disposition alone (plus a "detail withheld"
        // hint when the journal captured evidence a caller can go read explicitly).
        if (code is null)
        {
            if (!string.IsNullOrEmpty(operation.Error) || !string.IsNullOrEmpty(operation.Warning))
                return $"{disposition} — diagnostic detail unavailable; inspect JSON evidence";
            return disposition;
        }

        return code switch
        {
            "verified" when fields.Count == 0 => "verified",
            "verified" =>
                $"verified: {fields.Count} checked field(s): {FormatFieldClassifications(fields)}",
            "field-mismatch" =>
                $"field mismatch: {FormatFieldClassifications(fields)}",
            "missing-required-fields" =>
                $"missing required field(s): {FormatNames(missingFields)}",
            "revision-not-advanced" =>
                $"server revision did not advance past expected={FormatRevision(expectedRevision)} (observed={FormatRevision(observedRevision)}); matching fields alone do not prove this write; reconcile before any newly reviewed replacement",
            "revision-conflict" =>
                $"revision conflict: expected={FormatRevision(expectedRevision)} (server={FormatRevision(observedRevision)}); a newly reviewed proposal is required",
            "readback-unavailable" =>
                "readback unavailable — outcome could not be verified",
            _ => $"{disposition} — diagnostic detail unavailable",
        };
    }

    private static string FormatFieldClassifications(IReadOnlyList<PlanOperationDiagnosticField> fields)
    {
        if (fields.Count == 0) return "(none)";
        var count = Math.Min(fields.Count, 8);
        var parts = new string[count];
        for (var i = 0; i < count; i++)
            parts[i] = $"{BoundedName(fields[i].Field)} ({BoundedName(fields[i].Classification)})";
        return string.Join(", ", parts) + (fields.Count > count ? $"; +{fields.Count - count} more in JSON" : "");
    }

    private static string FormatNames(IReadOnlyList<string> names)
    {
        var count = Math.Min(names.Count, 8);
        var parts = new string[count];
        for (var i = 0; i < count; i++) parts[i] = BoundedName(names[i]);
        return string.Join(", ", parts) + (names.Count > count ? $"; +{names.Count - count} more in JSON" : "");
    }

    private static string BoundedName(string name)
        => name.Length <= 128 ? name : name[..128] + "...";

    private static string FormatRevision(int? revision) =>
        revision is null ? "(unknown)" : revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A field the readback classified, addressed by its ADO reference name and tagged with a
/// bounded classification token: <c>exact</c>, <c>cleared</c>, <c>canonicalized-html</c>,
/// <c>canonicalized-identity</c>, <c>server-generated</c>, <c>mismatch</c>, or
/// <c>clear-failed</c>. Values are deliberately absent — a projection MUST NOT carry the
/// field body, which can be arbitrary and sensitive; the raw
/// <see cref="PlanJournalOperation.ResultJson"/> holds the full evidence.
/// </summary>
public sealed record PlanOperationDiagnosticField
{
    /// <summary>ADO reference name (e.g. <c>System.Title</c>).</summary>
    public required string Field { get; init; }

    /// <summary>Bounded classification token (see the record's summary).</summary>
    public required string Classification { get; init; }
}
