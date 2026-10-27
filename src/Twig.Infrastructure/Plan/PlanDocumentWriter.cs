using System.Text.Json;
using Twig.Domain.Services.Plan;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Serializes a <see cref="PlanDefinition"/> back into its versioned JSON document.
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
    /// Writes <paramref name="definition"/> as a versioned plan JSON document.
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
                WriteOperation(writer, op, definition.Version);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteOperation(Utf8JsonWriter writer, PlanOperationDefinition op, int version)
    {
        if (op is ProviderMutationOperation providerOperation)
        {
            if (version != 2)
                throw new InvalidOperationException("Provider-identity operations require plan version 2.");
            WriteProviderMutation(writer, providerOperation);
            return;
        }
        if (version != 1)
            throw new InvalidOperationException($"Legacy operation kind '{op.Kind}' cannot be written in plan version {version}.");

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

            case AppendCommentOperation append:
                writer.WriteNumber("workItemId", append.WorkItemId);
                writer.WriteString("savedBenchId", append.SavedBenchId);
                writer.WriteString("expectedBenchContentsDigest", append.ExpectedBenchContentsDigest);
                writer.WriteString("expectedIdentityId", append.ExpectedIdentityId);
                writer.WriteString("format", append.Format);
                writer.WriteString("text", append.Text);
                writer.WriteString("effectiveBody", append.EffectiveBody);
                break;

            default:
                throw new NotSupportedException(
                    $"Plan operation kind '{op.Kind}' has no writer. The versioned kinds are a closed set; " +
                    "adding another kind requires updating the parser, the writer, and the review model together.");
        }

        writer.WriteEndObject();
    }

    private static void WriteProviderMutation(Utf8JsonWriter writer, ProviderMutationOperation op)
    {
        writer.WriteStartObject();
        writer.WriteString("id", op.Id);
        writer.WriteString("kind", WireKind(op.Kind));
        writer.WriteStartObject("target");
        WriteProviderReference(writer, "item", op.Target.Item);
        writer.WriteString("expectedProviderRevision", op.Target.ExpectedProviderRevision.Value);
        writer.WriteEndObject();

        switch (op.OperationKind)
        {
            case PlanOperationKind.Batch when op.Fields is not null && op.Relation is null && op.Other is null:
                writer.WriteStartObject("fields");
                foreach (var (field, value) in op.Fields)
                {
                    if (value is null) writer.WriteNull(field);
                    else writer.WriteString(field, value);
                }
                writer.WriteEndObject();
                break;
            case PlanOperationKind.AddLink or PlanOperationKind.RemoveLink
                when op.Relation is not null && op.Other is not null && op.Fields is null:
                writer.WriteString("relation", op.Relation);
                WriteProviderReference(writer, "other", op.Other);
                if (op.Concurrency != "exact-revision")
                    writer.WriteString("concurrency", op.Concurrency);
                break;
            case PlanOperationKind.Delete when op.Relation is null && op.Other is null && op.Fields is null:
                break;
            default:
                throw new InvalidOperationException(
                    $"Provider mutation kind '{op.OperationKind}' has missing or incompatible members.");
        }

        writer.WriteEndObject();
    }

    private static void WriteProviderReference(
        Utf8JsonWriter writer, string propertyName, ProviderWorkItemReference reference)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("localId", reference.LocalId.ToString());
        writer.WriteStartObject("externalIdentity");
        writer.WriteString("providerKind", reference.ExternalIdentity.ProviderKind);
        writer.WriteString("resourceScope", reference.ExternalIdentity.ResourceScope);
        writer.WriteString("nativeId", reference.ExternalIdentity.NativeId);
        writer.WriteEndObject();
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
        PlanOperationKind.AppendComment => "append-comment",
        _ => throw new NotSupportedException($"Unknown plan operation kind '{kind}'."),
    };
}
