using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Dtos;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoResponseMapper"/>.
/// No network calls — all DTOs constructed manually.
/// </summary>
public class AdoResponseMapperTests
{
    // ── MapWorkItem ──────────────────────────────────────────────────

    [Fact]
    public void MapWorkItem_BasicFields_MapsCorrectly()
    {
        var dto = CreateWorkItemDto(
            id: 42,
            rev: 5,
            type: "User Story",
            title: "Implement login",
            state: "Active",
            assignedTo: "John Doe",
            iterationPath: @"MyProject\Sprint 1",
            areaPath: @"MyProject\Backend");

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Id.ShouldBe(42);
        result.Revision.ShouldBe(5);
        result.Type.ShouldBe(WorkItemType.UserStory);
        result.Title.ShouldBe("Implement login");
        result.State.ShouldBe("Active");
        result.AssignedTo.ShouldBe("John Doe");
        result.IterationPath.Value.ShouldBe(@"MyProject\Sprint 1");
        result.AreaPath.Value.ShouldBe(@"MyProject\Backend");
        result.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void MapWorkItem_WithParentRelation_ExtractsParentId()
    {
        var dto = CreateWorkItemDto(id: 100, rev: 1, type: "Task", title: "Sub task", state: "New");
        dto.Relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/42",
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.ParentId.ShouldBe(42);
    }

    [Fact]
    public void MapWorkItem_NoRelations_ParentIdIsNull()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "Epic", title: "Top level", state: "New");
        dto.Relations = null;

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.ParentId.ShouldBeNull();
    }

    [Fact]
    public void MapWorkItem_EmptyRelations_ParentIdIsNull()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "Epic", title: "Top level", state: "New");
        dto.Relations = new List<AdoRelation>();

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.ParentId.ShouldBeNull();
    }

    [Fact]
    public void MapWorkItem_ForwardRelationOnly_ParentIdIsNull()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "Epic", title: "Parent", state: "New");
        dto.Relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Forward",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/99",
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.ParentId.ShouldBeNull();
    }

    [Fact]
    public void MapWorkItem_AssignedToIdentityObject_ExtractsDisplayName()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 10,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("A task"),
                ["System.State"] = JsonElement("New"),
                ["System.AssignedTo"] = JsonElement(new { displayName = "Jane Smith", uniqueName = "jane@example.com" }),
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.AssignedTo.ShouldBe("Jane Smith");
    }

    [Fact]
    public void MapWorkItem_NullAssignedTo_ReturnsNull()
    {
        var dto = CreateWorkItemDto(id: 10, rev: 1, type: "Task", title: "No assignee", state: "New", assignedTo: null);

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.AssignedTo.ShouldBeNull();
    }

    [Fact]
    public void MapWorkItem_NullFields_UsesDefaults()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = null,
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Id.ShouldBe(1);
        result.Title.ShouldBeEmpty();
        result.State.ShouldBeEmpty();
    }

    [Fact]
    public void MapWorkItem_CustomWorkItemType_PreservesTypeName()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "CustomType", title: "Custom", state: "New");

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Type.Value.ShouldBe("CustomType");
    }

    // ── ExtractParentId ──────────────────────────────────────────────

    [Fact]
    public void ExtractParentId_ValidUrl_ReturnsId()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/123",
            },
        };

        var result = AdoResponseMapper.ExtractParentId(relations);

        result.ShouldBe(123);
    }

    [Fact]
    public void ExtractParentId_MultipleRelations_FindsParent()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/999",
            },
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/50",
            },
        };

        var result = AdoResponseMapper.ExtractParentId(relations);

        result.ShouldBe(50);
    }

    [Fact]
    public void ExtractParentId_NullUrl_ReturnsNull()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = null,
            },
        };

        var result = AdoResponseMapper.ExtractParentId(relations);

        result.ShouldBeNull();
    }

    [Fact]
    public void ExtractParentId_InvalidUrlFormat_ReturnsNull()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "not-a-valid-url-with-no-trailing-id/",
            },
        };

        var result = AdoResponseMapper.ExtractParentId(relations);

        // URL ends with '/' so lastSlash+1 gives empty string which won't parse
        result.ShouldBeNull();
    }

    // ── MapPatchDocument ─────────────────────────────────────────────

    [Fact]
    public void MapPatchDocument_SingleChange_ReturnsCorrectPatch()
    {
        var changes = new List<FieldChange>
        {
            new("System.State", "New", "Active"),
        };

        var result = AdoResponseMapper.MapPatchDocument(changes);

        result.Count.ShouldBe(1);
        result[0].Op.ShouldBe("replace");
        result[0].Path.ShouldBe("/fields/System.State");
        result[0].Value.ShouldNotBeNull();
        result[0].Value!.GetValue<string>().ShouldBe("Active");
    }

    [Fact]
    public void MapPatchDocument_MultipleChanges_ReturnsAllPatches()
    {
        var changes = new List<FieldChange>
        {
            new("System.State", "New", "Active"),
            new("System.Title", "Old Title", "New Title"),
            new("System.AssignedTo", null, "Jane"),
        };

        var result = AdoResponseMapper.MapPatchDocument(changes);

        result.Count.ShouldBe(3);
        result[0].Path.ShouldBe("/fields/System.State");
        result[1].Path.ShouldBe("/fields/System.Title");
        result[2].Path.ShouldBe("/fields/System.AssignedTo");
    }

    [Fact]
    public void MapPatchDocument_EmptyChanges_ReturnsEmptyList()
    {
        var result = AdoResponseMapper.MapPatchDocument(Array.Empty<FieldChange>());

        result.ShouldBeEmpty();
    }

    // ── MapSeedToCreatePayload ───────────────────────────────────────

    [Fact]
    public void MapSeedToCreatePayload_WithoutParent_ContainsTitleOnly()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(WorkItemType.Task, "New Task");

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/myorg");

        result.ShouldNotBeEmpty();
        result.ShouldContain(op => op.Path == "/fields/System.Title" && op.Value != null && op.Value.GetValue<string>() == "New Task");
        result.ShouldNotContain(op => op.Path == "/relations/-");
    }

    [Fact]
    public void MapSeedToCreatePayload_WithParent_ContainsRelationLink()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(WorkItemType.Task, "Child Task", parentId: 42);

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/myorg", parentId: 42);

        result.ShouldContain(op => op.Path == "/relations/-");
    }

    [Fact]
    public void MapSeedToCreatePayload_WithAreaAndIterationPaths_IncludesThem()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(
            WorkItemType.UserStory,
            "Story",
            areaPath: AreaPath.Parse(@"Proj\Area").Value,
            iterationPath: IterationPath.Parse(@"Proj\Sprint 1").Value);

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/org");

        result.ShouldContain(op => op.Path == "/fields/System.AreaPath");
        result.ShouldContain(op => op.Path == "/fields/System.IterationPath");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoWorkItemResponse CreateWorkItemDto(
        int id,
        int rev,
        string type,
        string title,
        string state,
        string? assignedTo = null,
        string? iterationPath = null,
        string? areaPath = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["System.WorkItemType"] = JsonElement(type),
            ["System.Title"] = JsonElement(title),
            ["System.State"] = JsonElement(state),
        };

        if (assignedTo is not null)
            fields["System.AssignedTo"] = JsonElement(assignedTo);

        if (iterationPath is not null)
            fields["System.IterationPath"] = JsonElement(iterationPath);

        if (areaPath is not null)
            fields["System.AreaPath"] = JsonElement(areaPath);

        return new AdoWorkItemResponse
        {
            Id = id,
            Rev = rev,
            Fields = fields,
        };
    }

    /// <summary>
    /// Creates a JsonElement from a value (simulating what System.Text.Json produces during deserialization).
    /// </summary>
    private static object JsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
