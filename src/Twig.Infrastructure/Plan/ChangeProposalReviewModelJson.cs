using System.Text;
using System.Text.Json;
using Twig.Domain.Services.ChangeProposals;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// The single serializer for the canonical semantic review model (design record T2 §4.1,
/// <c>modelVersion</c> 1).
/// <para>
/// 🔴 <b>One writer, every consumer.</b> The MCP preview payload, and the
/// <c>review_model_json</c> audit column written at authorization time, are produced here.
/// A second writer would let "what the authorizer was shown" and "what the audit row records"
/// drift apart silently — which is the one divergence an audit trail cannot tolerate, because
/// nothing downstream could detect it.
/// </para>
/// <para>
/// Written with <see cref="Utf8JsonWriter"/> directly rather than a serializer context: the
/// shape is a fixed wire contract, and hand-writing it keeps it AOT- and trim-clean with no
/// reflection and no per-call allocation beyond the buffer.
/// </para>
/// <para>
/// Every material entry is emitted in full — affected items, operations, preconditions,
/// consequences, authorization choices and blockers are never summarised or truncated. Dropping
/// one would make the reviewer authorize a mutation they were never shown.
/// </para>
/// </summary>
public static class ChangeProposalReviewModelJson
{
    /// <summary>
    /// Writes the model's members into the object the caller has already opened. The caller owns
    /// the surrounding <c>WriteStartObject</c>/<c>WriteEndObject</c>, so the same body serves a
    /// named property on a larger envelope and a standalone document.
    /// </summary>
    public static void WriteBody(Utf8JsonWriter writer, ChangeProposalReviewModel model)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(model);

        writer.WriteString("model", model.Model);
        writer.WriteNumber("modelVersion", model.ModelVersion);

        // Verbatim. A renderer or serializer that recomputed this could bind an authorization to
        // a digest the proposal never had.
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
    }

    /// <summary>
    /// Serializes the model as a standalone compact JSON document — the exact string persisted
    /// in the journal's <c>review_model_json</c> column.
    /// </summary>
    public static string Serialize(ChangeProposalReviewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            WriteBody(writer, model);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }
}
