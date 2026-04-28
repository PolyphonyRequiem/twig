using System.Text.Json;
using System.Text.Json.Nodes;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Maps between ADO REST API DTOs and domain value objects.
/// Anti-corruption layer — all ADO-specific data transformations live here.
/// Produces <see cref="WorkItemSnapshot"/> records; domain aggregate construction
/// is deferred to <see cref="WorkItemMapper"/>.
/// </summary>
internal static class AdoResponseMapper
{
    private const string ParentRelationType = "System.LinkTypes.Hierarchy-Reverse";

    private static readonly Dictionary<string, string> NonHierarchyRelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System.LinkTypes.Related"] = LinkTypes.Related,
        ["System.LinkTypes.Dependency-Forward"] = LinkTypes.Successor,
        ["System.LinkTypes.Dependency-Reverse"] = LinkTypes.Predecessor,
    };

    /// <summary>
    /// Maps an ADO work item response DTO to an immutable <see cref="WorkItemSnapshot"/>.
    /// Produces raw/primitive data — value object parsing is deferred to <see cref="WorkItemMapper"/>.
    /// </summary>
    public static WorkItemSnapshot MapToSnapshot(AdoWorkItemResponse dto,
        IReadOnlyDictionary<string, FieldDefinition>? fieldDefLookup = null)
    {
        var fields = dto.Fields ?? new Dictionary<string, object?>();

        var filteredFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in fields)
        {
            FieldDefinition? fieldDef = null;
            fieldDefLookup?.TryGetValue(kvp.Key, out fieldDef);
            if (!FieldImportFilter.ShouldImport(kvp.Key, fieldDef)) continue;
            var value = ParseFieldValue(kvp.Value);
            if (value is not null) filteredFields[kvp.Key] = value;
        }

        return new WorkItemSnapshot
        {
            Id = dto.Id,
            Revision = dto.Rev,
            TypeName = GetStringField(fields, "System.WorkItemType") ?? string.Empty,
            Title = GetStringField(fields, "System.Title") ?? string.Empty,
            State = GetStringField(fields, "System.State") ?? string.Empty,
            AssignedTo = ParseAssignedTo(fields),
            IterationPath = GetStringField(fields, "System.IterationPath"),
            AreaPath = GetStringField(fields, "System.AreaPath"),
            ParentId = ExtractParentId(dto.Relations),
            Fields = filteredFields,
        };
    }

    /// <summary>
    /// Maps an ADO work item response DTO to a <see cref="WorkItemSnapshot"/> plus non-hierarchy links.
    /// </summary>
    public static (WorkItemSnapshot Snapshot, IReadOnlyList<WorkItemLink> Links) MapToSnapshotWithLinks(
        AdoWorkItemResponse dto,
        IReadOnlyDictionary<string, FieldDefinition>? fieldDefLookup = null)
    {
        var snapshot = MapToSnapshot(dto, fieldDefLookup);
        var links = ExtractNonHierarchyLinks(dto.Id, dto.Relations);
        return (snapshot, links);
    }

    /// <summary>
    /// Maps a list of <see cref="FieldChange"/> values to ADO JSON Patch operations.
    /// </summary>
    public static List<AdoPatchOperation> MapPatchDocument(IReadOnlyList<FieldChange> changes)
    {
        var operations = new List<AdoPatchOperation>(changes.Count);

        foreach (var change in changes)
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "replace",
                Path = $"/fields/{change.FieldName}",
                Value = change.NewValue is not null ? JsonValue.Create(change.NewValue) : null,
            });
        }

        return operations;
    }

    /// <summary>
    /// Fields that are handled explicitly in the create payload (Title, AreaPath, IterationPath)
    /// or are read-only/computed and must not be sent to ADO on create.
    /// Shared constant extracted from <see cref="Twig.Domain.Services.SeedEditorFormat"/> excluded set.
    /// </summary>
    private static readonly HashSet<string> CreatePayloadExcludedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        // Already handled explicitly in the payload
        "System.Title",
        "System.AreaPath",
        "System.IterationPath",
        // Read-only / computed fields (same set as SeedEditorFormat.ExcludedFields)
        "System.Id",
        "System.Rev",
        "System.CreatedDate",
        "System.ChangedDate",
        "System.Watermark",
        "System.CreatedBy",
        "System.ChangedBy",
        "System.AuthorizedDate",
        "System.RevisedDate",
        "System.BoardColumn",
        "System.BoardColumnDone",
        "System.BoardLane",
        // Other read-only fields
        "System.WorkItemType",
    };

    /// <summary>
    /// Maps a <see cref="CreateWorkItemRequest"/> to an ADO JSON Patch document for creation.
    /// Includes optional parent link and all populated non-readonly fields from request.Fields.
    /// </summary>
    public static List<AdoPatchOperation> MapSeedToCreatePayload(
        CreateWorkItemRequest request,
        string orgUrl)
    {
        var operations = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/fields/System.Title",
                Value = JsonValue.Create(request.Title),
            },
        };

        if (!string.IsNullOrEmpty(request.AreaPath))
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = "/fields/System.AreaPath",
                Value = JsonValue.Create(request.AreaPath),
            });
        }

        if (!string.IsNullOrEmpty(request.IterationPath))
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = "/fields/System.IterationPath",
                Value = JsonValue.Create(request.IterationPath),
            });
        }

        if (request.ParentId.HasValue)
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = "/relations/-",
                Value = new JsonObject
                {
                    ["rel"] = JsonValue.Create(ParentRelationType),
                    ["url"] = JsonValue.Create($"{orgUrl}/_apis/wit/workitems/{request.ParentId.Value}"),
                },
            });
        }

        // Include all populated, non-excluded fields from request.Fields
        foreach (var (refName, value) in request.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (CreatePayloadExcludedFields.Contains(refName)) continue;

            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = $"/fields/{refName}",
                Value = JsonValue.Create(value),
            });
        }

        InjectTwigTag(operations);

        return operations;
    }

    private static void InjectTwigTag(List<AdoPatchOperation> operations)
    {
        const string tagPath = "/fields/System.Tags";
        const string twigTag = "twig";

        var existingIndex = operations.FindIndex(op =>
            string.Equals(op.Path, tagPath, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            var current = operations[existingIndex].Value?.GetValue<string>() ?? "";
            operations[existingIndex].Value = JsonValue.Create(MergeTwigTag(current, twigTag));
        }
        else
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = tagPath,
                Value = JsonValue.Create(twigTag),
            });
        }
    }

    /// <summary>
    /// Merges a tag into a semicolon-separated tag string with case-insensitive deduplication.
    /// Returns the original string unchanged if the tag is already present.
    /// </summary>
    internal static string MergeTwigTag(string existingTags, string tag)
    {
        if (string.IsNullOrWhiteSpace(existingTags))
            return tag;

        var tags = existingTags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tags)
        {
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                return existingTags;
        }

        return $"{existingTags}; {tag}";
    }

    /// <summary>
    /// Extracts non-hierarchy links (Related, Predecessor, Successor) from the relations array.
    /// Uses the same URL-suffix ID parsing pattern as <see cref="ExtractParentId"/>.
    /// </summary>
    internal static List<WorkItemLink> ExtractNonHierarchyLinks(int sourceId, List<AdoRelation>? relations)
    {
        if (relations is null || relations.Count == 0)
            return [];

        var links = new List<WorkItemLink>();

        foreach (var relation in relations)
        {
            if (string.IsNullOrEmpty(relation.Rel) || !NonHierarchyRelMap.TryGetValue(relation.Rel, out var linkType))
                continue;

            if (string.IsNullOrEmpty(relation.Url))
                continue;

            var lastSlash = relation.Url.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < relation.Url.Length - 1)
            {
                var idStr = relation.Url[(lastSlash + 1)..];
                if (int.TryParse(idStr, out var targetId))
                {
                    links.Add(new WorkItemLink(sourceId, targetId, linkType));
                }
            }
        }

        return links;
    }

    /// <summary>
    /// Extracts the parent work item ID from the relations array.
    /// The parent is stored as a <c>System.LinkTypes.Hierarchy-Reverse</c> relation.
    /// The ID is parsed from the URL suffix.
    /// </summary>
    internal static int? ExtractParentId(List<AdoRelation>? relations)
    {
        if (relations is null || relations.Count == 0)
            return null;

        foreach (var relation in relations)
        {
            if (!string.Equals(relation.Rel, ParentRelationType, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrEmpty(relation.Url))
                continue;

            // URL format: https://dev.azure.com/{org}/{project}/_apis/wit/workItems/{id}
            var lastSlash = relation.Url.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < relation.Url.Length - 1)
            {
                var idStr = relation.Url[(lastSlash + 1)..];
                if (int.TryParse(idStr, out var parentId))
                    return parentId;
            }
        }

        return null;
    }

    private static string? GetStringField(Dictionary<string, object?> fields, string fieldName)
    {
        if (!fields.TryGetValue(fieldName, out var value))
            return null;

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                _ => element.ToString(),
            };
        }

        return value?.ToString();
    }

    /// <summary>
    /// Parses an arbitrary field value from the ADO response into a string representation.
    /// Handles JSON primitives, identity objects (with displayName/uniqueName), and nulls.
    /// </summary>
    private static string? ParseFieldValue(object? value)
    {
        if (value is null) return null;

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object => ExtractIdentityDisplayName(element) ?? element.ToString(),
                _ => element.ToString(),
            };
        }

        return value.ToString();
    }

    /// <summary>
    /// Extracts a display name from an identity-like JSON object.
    /// Falls back to uniqueName if displayName is not present.
    /// </summary>
    private static string? ExtractIdentityDisplayName(JsonElement element)
    {
        if (element.TryGetProperty("displayName", out var displayName)) return displayName.GetString();
        if (element.TryGetProperty("uniqueName", out var uniqueName)) return uniqueName.GetString();
        return null;
    }

    /// <summary>
    /// Parses AssignedTo which can be a string or an identity object with displayName.
    /// </summary>
    private static string? ParseAssignedTo(Dictionary<string, object?> fields)
    {
        if (!fields.TryGetValue("System.AssignedTo", out var value) || value is null)
            return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;

            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();

            // Identity object: { displayName: "...", uniqueName: "...", ... }
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("displayName", out var displayName))
                    return displayName.GetString();

                if (element.TryGetProperty("uniqueName", out var uniqueName))
                    return uniqueName.GetString();
            }
        }

        return value.ToString();
    }


}
