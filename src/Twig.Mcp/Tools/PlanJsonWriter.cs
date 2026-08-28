using System.Text.Encodings.Web;
using System.Text.Json;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Plan;

namespace Twig.Mcp.Tools;

/// <summary>
/// Utf8JsonWriter fragments the plan tools share so their JSON shape stays byte-identical.
/// <para>
/// Everything here writes into the caller's writer without any allocation-heavy encoding or
/// rendering pass — pending changes in particular are emitted as verbatim opaque strings, in
/// exactly the order and with exactly the values <see cref="Twig.Domain.Interfaces.IPendingChangeReader"/>
/// returned. No formatter, no coalescing, no logging, no telemetry.
/// </para>
/// </summary>
internal static class PlanJsonWriter
{
    public static void WriteIssues(Utf8JsonWriter writer, IReadOnlyList<PlanValidationIssue> issues)
    {
        writer.WriteStartArray("issues");
        foreach (var issue in issues)
        {
            writer.WriteStartObject();
            writer.WriteString("code", issue.Code);
            writer.WriteString("path", issue.Path);
            writer.WriteString("message", issue.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static void WriteOperationSummaries(
        Utf8JsonWriter writer,
        IReadOnlyList<PlanOperationDefinition> operations)
    {
        writer.WriteStartArray("operations");
        var ordinal = 0;
        foreach (var op in operations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", ordinal++);
            writer.WriteString("opId", op.Id);
            writer.WriteString("kind", op.Kind.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static void WriteJournalOperations(
        Utf8JsonWriter writer,
        IReadOnlyList<PlanJournalOperation> operations)
    {
        writer.WriteStartArray("operations");
        foreach (var op in operations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", op.Ordinal);
            writer.WriteString("opId", op.OpId);
            writer.WriteString("kind", op.Kind.ToString());
            writer.WriteString("state", op.State.ToString());
            WriteTimestamp(writer, "startedAt", op.StartedAt);
            WriteTimestamp(writer, "appliedAt", op.AppliedAt);
            WriteTimestamp(writer, "verifiedAt", op.VerifiedAt);
            if (op.ResultJson is not null) writer.WriteString("result", op.ResultJson);
            else writer.WriteNull("result");
            if (op.Error is not null) writer.WriteString("error", op.Error);
            else writer.WriteNull("error");
            // AB#754: non-fatal server-generated normalization detail on a Verified row.
            // Written unconditionally (null when absent) so an MCP caller can read it
            // without probing, and kept distinct from "error" — a warning never means the
            // operation failed.
            if (op.Warning is not null) writer.WriteString("warning", op.Warning);
            else writer.WriteNull("warning");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static void WritePendingChanges(
        Utf8JsonWriter writer,
        IReadOnlyList<PendingChangeDetail> rows)
    {
        writer.WriteStartArray("pendingChanges");
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteNumber("pendingChangeId", row.PendingChangeId);
            writer.WriteNumber("workItemId", row.WorkItemId);
            writer.WriteString("kind", row.Kind);
            WriteOpaqueString(writer, "field", row.Field);
            WriteOpaqueString(writer, "note", row.Note);
            WriteOpaqueString(writer, "oldValue", row.OldValue);
            WriteOpaqueString(writer, "newValue", row.NewValue);
            writer.WriteString("stagedAt", row.StagedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            WriteSeedRemap(writer, row.SeedRemap);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSeedRemap(Utf8JsonWriter writer, SeedRemapIdentity? remap)
    {
        if (remap is null)
        {
            writer.WriteNull("seedRemap");
            return;
        }

        writer.WriteStartObject("seedRemap");
        writer.WriteString("stagedIdentity", remap.Value.StagedIdentity.ToString());
        writer.WriteNumber("stagedAlias", remap.Value.StagedAlias.Value);
        if (remap.Value.PublishedWorkItemId is { } published)
            writer.WriteNumber("publishedWorkItemId", published);
        else
            writer.WriteNull("publishedWorkItemId");
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an opaque row string with HTML-safe JSON escaping — <c>&lt;</c>, <c>&gt;</c>,
    /// <c>&amp;</c>, and quotes emerge as their <c>\u00XX</c> escapes even though the outer
    /// envelope writer uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>. That
    /// keeps a raw HTML/JS blob from a plan payload safe to embed in a page or an HTML mail
    /// without any downstream sanitizer. The value round-trips exactly through
    /// <see cref="JsonDocument"/> — the escaping only lives on the wire.
    /// </summary>
    /// <remarks>
    /// We hand <see cref="Utf8JsonWriter"/> a <see cref="JsonEncodedText"/> built with
    /// <see cref="JavaScriptEncoder.Default"/>. The writer copies those pre-escaped bytes
    /// verbatim, so the outer writer's own encoder is bypassed for this call only.
    /// </remarks>
    private static void WriteOpaqueString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, JsonEncodedText.Encode(value, JavaScriptEncoder.Default));
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Writes the canonical semantic review model under the <c>reviewModel</c> key.
    /// <para>
    /// The body comes from <see cref="ChangeProposalReviewModelJson"/> — the one writer the CLI
    /// payload, this payload and the journal's <c>review_model_json</c> audit column all share,
    /// so what a reviewer was shown and what the audit row records cannot drift.
    /// </para>
    /// <para>
    /// A null model is emitted as JSON null rather than an omitted key, so a consumer can tell
    /// "no proposal was parsed" from "this transport does not know about the model".
    /// </para>
    /// </summary>
    public static void WriteReviewModel(Utf8JsonWriter writer, ChangeProposalReviewModel? model)
    {
        if (model is null)
        {
            writer.WriteNull("reviewModel");
            return;
        }

        writer.WriteStartObject("reviewModel");
        ChangeProposalReviewModelJson.WriteBody(writer, model);
        writer.WriteEndObject();
    }
}
