using System.Text.Json;
using System.Text.Json.Nodes;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Dtos;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Maps between ADO REST API DTOs and domain aggregates.
/// Anti-corruption layer — all ADO-specific data transformations live here.
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
    /// Maps an ADO work item response DTO to a domain <see cref="WorkItem"/>.
    /// Extracts parent ID from relation links.
    /// </summary>
    public static WorkItem MapWorkItem(AdoWorkItemResponse dto)
    {
        var fields = dto.Fields ?? new Dictionary<string, object?>();

        var workItem = new WorkItem
        {
            Id = dto.Id,
            Type = ParseWorkItemType(GetStringField(fields, "System.WorkItemType")),
            Title = GetStringField(fields, "System.Title") ?? string.Empty,
            State = GetStringField(fields, "System.State") ?? string.Empty,
            AssignedTo = ParseAssignedTo(fields),
            IterationPath = ParseIterationPath(GetStringField(fields, "System.IterationPath")),
            AreaPath = ParseAreaPath(GetStringField(fields, "System.AreaPath")),
            ParentId = ExtractParentId(dto.Relations),
        };

        workItem.MarkSynced(dto.Rev);

        return workItem;
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
    /// Maps a seed <see cref="WorkItem"/> to an ADO JSON Patch document for creation.
    /// Includes optional parent link.
    /// </summary>
    public static List<AdoPatchOperation> MapSeedToCreatePayload(
        WorkItem seed,
        string orgUrl,
        int? parentId = null)
    {
        var operations = new List<AdoPatchOperation>
        {
            new()
            {
                Op = "add",
                Path = "/fields/System.Title",
                Value = JsonValue.Create(seed.Title),
            },
        };

        if (!string.IsNullOrEmpty(seed.AreaPath.Value))
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = "/fields/System.AreaPath",
                Value = JsonValue.Create(seed.AreaPath.Value),
            });
        }

        if (!string.IsNullOrEmpty(seed.IterationPath.Value))
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = "/fields/System.IterationPath",
                Value = JsonValue.Create(seed.IterationPath.Value),
            });
        }

        if (parentId.HasValue)
        {
            operations.Add(new AdoPatchOperation
            {
                Op = "add",
                Path = "/relations/-",
                Value = new JsonObject
                {
                    ["rel"] = JsonValue.Create(ParentRelationType),
                    ["url"] = JsonValue.Create($"{orgUrl}/_apis/wit/workitems/{parentId.Value}"),
                },
            });
        }

        return operations;
    }

    /// <summary>
    /// Maps an ADO work item response DTO to a domain <see cref="WorkItem"/> plus non-hierarchy links.
    /// </summary>
    public static (WorkItem Item, IReadOnlyList<WorkItemLink> Links) MapWorkItemWithLinks(AdoWorkItemResponse dto)
    {
        var item = MapWorkItem(dto);
        var links = ExtractNonHierarchyLinks(dto.Id, dto.Relations);
        return (item, links);
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

    private static WorkItemType ParseWorkItemType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return WorkItemType.Task; // fallback

        var result = WorkItemType.Parse(typeName);
        return result.IsSuccess ? result.Value : WorkItemType.Task;
    }

    private static IterationPath ParseIterationPath(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return default;

        var result = IterationPath.Parse(raw);
        return result.IsSuccess ? result.Value : default;
    }

    private static AreaPath ParseAreaPath(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return default;

        var result = AreaPath.Parse(raw);
        return result.IsSuccess ? result.Value : default;
    }
}
