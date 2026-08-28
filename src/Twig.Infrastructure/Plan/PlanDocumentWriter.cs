using System.Text.Json;
using Twig.Domain.Services.Plan;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Serializes a <see cref="PlanDefinition"/> back into a Plan v1 JSON document.
/// <para>
/// This is the inverse of <see cref="PlanDocumentParser"/> and exists so a Change Recipe can
/// render structured operations rather than hand-assembling JSON text. The output is fed
/// straight back through the parser, which is what makes a rendered proposal's digest
/// identical to the digest the same document earns when it is later validated, previewed and
/// applied — there is one canonicalization path, not two.
/// </para>
/// <para>
/// AOT-safe: writes through <see cref="Utf8JsonWriter"/> only. No reflection-based
/// serialization, consistent with <c>JsonSerializerIsReflectionEnabledByDefault=false</c>.
/// </para>
/// <para>
/// The writer emits properties in schema order and does not sort them. Sorting is the
/// canonicalizer's job; doing it here as well would imply the two agreed, and a future
/// divergence would be silent.
/// </para>
/// </summary>
public static class PlanDocumentWriter
{
    /// <summary>
    /// Writes <paramref name="definition"/> as a Plan v1 JSON document.
    /// </summary>
    public static string Write(PlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            writer.WriteNumber("version", definition.Version);

            writer.WriteStartObject("workspace");
            writer.WriteString("organization", definition.Workspace.Organization);
            writer.WriteString("project", definition.Workspace.Project);
            writer.WriteEndObject();

            writer.WriteStartArray("operations");
            foreach (var op in definition.Operations)
                WriteOperation(writer, op);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteOperation(Utf8JsonWriter writer, PlanOperationDefinition op)
    {
        writer.WriteStartObject();
        writer.WriteString("id", op.Id);
        writer.WriteString("kind", WireKind(op.Kind));

        switch (op)
        {
            case BatchOperation batch:
                writer.WriteNumber("workItemId", batch.WorkItemId);
                writer.WriteNumber("expectedRevision", batch.ExpectedRevision);
                writer.WriteStartObject("fields");
                foreach (var (field, value) in batch.Fields)
                {
                    if (value is null)
                        writer.WriteNull(field);
                    else
                        writer.WriteString(field, value);
                }
                writer.WriteEndObject();
                break;

            case AddLinkOperation add:
                WriteLink(writer, add.WorkItemId, add.ExpectedRevision, add.Relation, add.OtherId);
                break;

            case RemoveLinkOperation remove:
                WriteLink(writer, remove.WorkItemId, remove.ExpectedRevision, remove.Relation, remove.OtherId);
                break;

            case PublishSeedOperation seed:
                writer.WriteString("stagedIdentity", seed.StagedIdentity.Value.ToString());
                writer.WriteString("expectedFingerprint", seed.ExpectedFingerprint);
                break;

            case DeleteOperation delete:
                writer.WriteNumber("workItemId", delete.WorkItemId);
                writer.WriteNumber("expectedRevision", delete.ExpectedRevision);
                break;

            default:
                throw new NotSupportedException(
                    $"Plan operation kind '{op.Kind}' has no writer. The five kinds are a closed set; " +
                    "adding a sixth requires updating the parser, the writer, and the review model together.");
        }

        writer.WriteEndObject();
    }

    private static void WriteLink(Utf8JsonWriter writer, int workItemId, int expectedRevision, string relation, int otherId)
    {
        writer.WriteNumber("workItemId", workItemId);
        writer.WriteNumber("expectedRevision", expectedRevision);
        writer.WriteString("relation", relation);
        writer.WriteNumber("otherId", otherId);
    }

    /// <summary>
    /// Maps the enum to its exact wire spelling. The document vocabulary is a contract with
    /// files already on disk, so these strings are never derived from the enum name.
    /// </summary>
    public static string WireKind(PlanOperationKind kind) => kind switch
    {
        PlanOperationKind.Batch => "batch",
        PlanOperationKind.AddLink => "add-link",
        PlanOperationKind.RemoveLink => "remove-link",
        PlanOperationKind.PublishSeed => "publish-seed",
        PlanOperationKind.Delete => "delete",
        _ => throw new NotSupportedException($"Unknown plan operation kind '{kind}'."),
    };
}
