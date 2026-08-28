using System.Text.Encodings.Web;
using System.Text.Json;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

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
    /// Writes the canonical semantic review model.
    /// <para>
    /// Every material entry is emitted in full — operations, preconditions, consequences,
    /// authorization choices and blockers are never summarised or truncated. A transport that
    /// drops a material entry silently makes the reviewer authorize a mutation they were never
    /// shown, which is the exact failure this model exists to prevent.
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
        writer.WriteString("model", model.Model);
        writer.WriteNumber("modelVersion", model.ModelVersion);
        writer.WriteString("digest", model.Digest);

        writer.WriteStartObject("workspace");
        writer.WriteString("organization", model.Workspace.Organization);
        writer.WriteString("project", model.Workspace.Project);
        writer.WriteEndObject();

        WriteNullableString(writer, "rationale", model.Rationale);

        if (model.Recipe is { } recipe)
        {
            writer.WriteStartObject("recipe");
            writer.WriteString("recipeId", recipe.RecipeId);
            writer.WriteNumber("version", recipe.Version);
            writer.WriteEndObject();
        }
        else
        {
            // Null means ad hoc — authored by hand, with no template to navigate back to.
            writer.WriteNull("recipe");
        }

        writer.WriteStartArray("affectedItems");
        foreach (var item in model.AffectedItems)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", item.Id);
            WriteNullableString(writer, "type", item.Type);
            WriteNullableString(writer, "title", item.Title);
            WriteNullableString(writer, "state", item.State);
            writer.WriteString("role", item.Role);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("operations");
        foreach (var op in model.Operations)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ordinal", op.Ordinal);
            writer.WriteString("opId", op.OpId);
            writer.WriteString("kind", op.Kind);

            writer.WriteStartObject("target");
            if (op.Target.WorkItemId is { } workItemId)
                writer.WriteNumber("workItemId", workItemId);
            else
                writer.WriteNull("workItemId");
            WriteNullableString(writer, "stagedIdentity", op.Target.StagedIdentity);
            writer.WriteEndObject();

            writer.WriteString("summary", op.Summary);

            writer.WriteStartArray("preconditions");
            foreach (var pre in op.Preconditions)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", pre.Kind);
                writer.WriteString("value", pre.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("consequences");
            foreach (var con in op.Consequences)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", con.Kind);
                WriteNullableString(writer, "field", con.Field);
                WriteNullableString(writer, "to", con.To);
                WriteNullableString(writer, "relation", con.Relation);
                if (con.OtherId is { } otherId)
                    writer.WriteNumber("otherId", otherId);
                else
                    writer.WriteNull("otherId");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("authorizationChoices");
        foreach (var choice in model.AuthorizationChoices)
            writer.WriteStringValue(choice);
        writer.WriteEndArray();

        writer.WriteStartArray("blockers");
        foreach (var blocker in model.Blockers)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", blocker.Kind);
            if (blocker.WorkItemId is { } blockedId)
                writer.WriteNumber("workItemId", blockedId);
            else
                writer.WriteNull("workItemId");
            writer.WriteString("detail", blocker.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}
