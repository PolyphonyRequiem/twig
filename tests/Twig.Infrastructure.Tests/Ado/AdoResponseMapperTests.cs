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

    [Theory]
    [InlineData("CustomType")]
    [InlineData("Deliverable")]
    [InlineData("Initiative")]
    [InlineData("Scenario")]
    public void MapWorkItem_CustomWorkItemType_PreservesTypeName(string typeName)
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: typeName, title: "Custom", state: "New");

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Type.Value.ShouldBe(typeName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    public void MapWorkItem_EmptyOrWhitespaceType_FallsBackToTask(string typeName)
    {
        var dto = CreateWorkItemDto(id: 42, rev: 1, type: typeName, title: "Test", state: "Active");

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Type.ShouldBe(WorkItemType.Task);
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

    [Fact]
    public void MapSeedToCreatePayload_IncludesPopulatedFields()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(WorkItemType.Task, "Task with fields");
        seed.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "A description",
            ["Microsoft.VSTS.Common.Priority"] = "1",
        });

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/org");

        result.ShouldContain(op => op.Path == "/fields/System.Description" && op.Value!.GetValue<string>() == "A description");
        result.ShouldContain(op => op.Path == "/fields/Microsoft.VSTS.Common.Priority" && op.Value!.GetValue<string>() == "1");
    }

    [Fact]
    public void MapSeedToCreatePayload_SkipsEmptyFields()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(WorkItemType.Task, "Task");
        seed.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "",
            ["Microsoft.VSTS.Common.Priority"] = null,
        });

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/org");

        result.ShouldNotContain(op => op.Path == "/fields/System.Description");
        result.ShouldNotContain(op => op.Path == "/fields/Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void MapSeedToCreatePayload_SkipsReadOnlyFields()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(WorkItemType.Task, "Task");
        seed.ImportFields(new Dictionary<string, string?>
        {
            ["System.Id"] = "999",
            ["System.Rev"] = "5",
            ["System.CreatedDate"] = "2024-01-01",
            ["System.ChangedDate"] = "2024-01-02",
            ["System.CreatedBy"] = "alice",
            ["System.ChangedBy"] = "bob",
            ["System.Watermark"] = "123",
            ["System.WorkItemType"] = "Task",
        });

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/org");

        result.ShouldNotContain(op => op.Path == "/fields/System.Id");
        result.ShouldNotContain(op => op.Path == "/fields/System.Rev");
        result.ShouldNotContain(op => op.Path == "/fields/System.CreatedDate");
        result.ShouldNotContain(op => op.Path == "/fields/System.ChangedDate");
        result.ShouldNotContain(op => op.Path == "/fields/System.CreatedBy");
        result.ShouldNotContain(op => op.Path == "/fields/System.ChangedBy");
        result.ShouldNotContain(op => op.Path == "/fields/System.Watermark");
        result.ShouldNotContain(op => op.Path == "/fields/System.WorkItemType");
    }

    [Fact]
    public void MapSeedToCreatePayload_DoesNotDuplicateExplicitlyHandledFields()
    {
        var seed = Domain.Aggregates.WorkItem.CreateSeed(
            WorkItemType.Task, "My Task",
            areaPath: AreaPath.Parse(@"Proj\Area").Value,
            iterationPath: IterationPath.Parse(@"Proj\Sprint").Value);
        seed.ImportFields(new Dictionary<string, string?>
        {
            ["System.Title"] = "My Task",
            ["System.AreaPath"] = @"Proj\Area",
            ["System.IterationPath"] = @"Proj\Sprint",
            ["Microsoft.VSTS.Common.Priority"] = "2",
        });

        var result = AdoResponseMapper.MapSeedToCreatePayload(seed, "https://dev.azure.com/org");

        // Title, AreaPath, IterationPath should appear exactly once each
        result.Count(op => op.Path == "/fields/System.Title").ShouldBe(1);
        result.Count(op => op.Path == "/fields/System.AreaPath").ShouldBe(1);
        result.Count(op => op.Path == "/fields/System.IterationPath").ShouldBe(1);
        // Custom field should be included
        result.ShouldContain(op => op.Path == "/fields/Microsoft.VSTS.Common.Priority");
    }

    // ── Field Import Loop (MapWorkItem with field population) ────────

    [Fact]
    public void MapWorkItem_CoreFields_ExcludedFromFieldsDictionary()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.Id"] = JsonElement(1),
                ["System.Rev"] = JsonElement(1),
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["System.AssignedTo"] = JsonElement("Alice"),
                ["System.IterationPath"] = JsonElement(@"Project\Sprint1"),
                ["System.AreaPath"] = JsonElement(@"Project\Area"),
                ["Microsoft.VSTS.Common.Priority"] = JsonElement("2"),
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        // Core fields should NOT appear in Fields dictionary
        result.Fields.ShouldNotContainKey("System.Id");
        result.Fields.ShouldNotContainKey("System.Rev");
        result.Fields.ShouldNotContainKey("System.WorkItemType");
        result.Fields.ShouldNotContainKey("System.Title");
        result.Fields.ShouldNotContainKey("System.State");
        result.Fields.ShouldNotContainKey("System.AssignedTo");
        result.Fields.ShouldNotContainKey("System.IterationPath");
        result.Fields.ShouldNotContainKey("System.AreaPath");

        // Non-core field should be imported
        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
    }

    [Fact]
    public void MapWorkItem_WithFieldDefLookup_ReadOnlyNonDisplayWorthy_Excluded()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["System.Watermark"] = JsonElement(42),
                ["Microsoft.VSTS.Common.Priority"] = JsonElement("1"),
            },
        };

        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.Watermark"] = new FieldDefinition("System.Watermark", "Watermark", "integer", true),
            ["Microsoft.VSTS.Common.Priority"] = new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
        };

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        // Read-only non-display-worthy field excluded
        result.Fields.ShouldNotContainKey("System.Watermark");
        // Editable importable field included
        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
    }

    [Fact]
    public void MapWorkItem_WithFieldDefLookup_DisplayWorthyReadOnly_Included()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["System.Tags"] = JsonElement("backend; api"),
                ["System.CreatedDate"] = JsonElement("2025-01-15T10:00:00Z"),
                ["System.Description"] = JsonElement("<p>Some HTML</p>"),
            },
        };

        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.Tags"] = new FieldDefinition("System.Tags", "Tags", "plainText", true),
            ["System.CreatedDate"] = new FieldDefinition("System.CreatedDate", "Created Date", "dateTime", true),
            ["System.Description"] = new FieldDefinition("System.Description", "Description", "html", true),
        };

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldContainKey("System.Tags");
        result.Fields["System.Tags"].ShouldBe("backend; api");
        result.Fields.ShouldContainKey("System.CreatedDate");
        result.Fields["System.CreatedDate"].ShouldBe("2025-01-15T10:00:00Z");
        result.Fields.ShouldContainKey("System.Description");
        result.Fields["System.Description"].ShouldBe("<p>Some HTML</p>");
    }

    [Fact]
    public void MapWorkItem_NullFieldDefLookup_ImportsAllNonCoreFields()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["Microsoft.VSTS.Common.Priority"] = JsonElement("2"),
                ["Custom.MyField"] = JsonElement("custom_value"),
                ["System.Watermark"] = JsonElement(42),
            },
        };

        // No field def lookup — fallback imports all non-core fields
        var result = AdoResponseMapper.MapWorkItem(dto, fieldDefLookup: null);

        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields.ShouldContainKey("Custom.MyField");
        result.Fields.ShouldContainKey("System.Watermark");
        // Core fields still excluded
        result.Fields.ShouldNotContainKey("System.Title");
        result.Fields.ShouldNotContainKey("System.State");
    }

    [Fact]
    public void MapWorkItem_IdentityObjectInArbitraryField_ResolvesDisplayName()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["System.CreatedBy"] = JsonElement(new { displayName = "Alice", uniqueName = "alice@example.com" }),
                ["System.ChangedBy"] = JsonElement(new { displayName = "Bob" }),
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Fields.ShouldContainKey("System.CreatedBy");
        result.Fields["System.CreatedBy"].ShouldBe("Alice");
        result.Fields.ShouldContainKey("System.ChangedBy");
        result.Fields["System.ChangedBy"].ShouldBe("Bob");
    }

    [Fact]
    public void MapWorkItem_IdentityObjectWithUniqueNameOnly_FallsBackToUniqueName()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["System.CreatedBy"] = JsonElement(new { uniqueName = "alice@example.com" }),
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Fields.ShouldContainKey("System.CreatedBy");
        result.Fields["System.CreatedBy"].ShouldBe("alice@example.com");
    }

    [Fact]
    public void MapWorkItem_HtmlFieldValue_StoredAsIs()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["System.Description"] = JsonElement("<div><p>Rich <b>HTML</b> content</p></div>"),
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        result.Fields.ShouldContainKey("System.Description");
        result.Fields["System.Description"].ShouldBe("<div><p>Rich <b>HTML</b> content</p></div>");
    }

    [Fact]
    public void MapWorkItem_WithFieldDefLookup_NonImportableDataType_Excluded()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["Custom.BoolField"] = JsonElement(true),
                ["Custom.TreeField"] = JsonElement("Some\\Path"),
                ["Microsoft.VSTS.Common.Priority"] = JsonElement("1"),
            },
        };

        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Custom.BoolField"] = new FieldDefinition("Custom.BoolField", "Bool", "boolean", false),
            ["Custom.TreeField"] = new FieldDefinition("Custom.TreeField", "Tree", "treePath", false),
            ["Microsoft.VSTS.Common.Priority"] = new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
        };

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        // Non-importable data types excluded
        result.Fields.ShouldNotContainKey("Custom.BoolField");
        result.Fields.ShouldNotContainKey("Custom.TreeField");
        // Importable data type included
        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void MapWorkItem_NullFieldValue_SkippedInFieldsDictionary()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["Microsoft.VSTS.Common.Priority"] = null,
                ["Custom.NonNull"] = JsonElement("value"),
            },
        };

        var result = AdoResponseMapper.MapWorkItem(dto);

        // Null values are not imported
        result.Fields.ShouldNotContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields.ShouldContainKey("Custom.NonNull");
    }

    [Fact]
    public void MapWorkItemWithLinks_ForwardsFieldDefLookup()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("Active"),
                ["Microsoft.VSTS.Common.Priority"] = JsonElement("3"),
                ["System.Watermark"] = JsonElement(99),
            },
        };

        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.VSTS.Common.Priority"] = new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            ["System.Watermark"] = new FieldDefinition("System.Watermark", "Watermark", "integer", true),
        };

        var (item, links) = AdoResponseMapper.MapWorkItemWithLinks(dto, lookup);

        item.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        item.Fields.ShouldNotContainKey("System.Watermark");
    }

    // ── Field import pipeline ────────────────────────────────────────

    [Fact]
    public void MapWorkItem_PopulatesFieldsFromResponse()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 3,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("A task"),
                ["System.State"] = JsonElement("Active"),
                ["Microsoft.VSTS.Common.Priority"] = JsonElement("2"),
                ["Custom.Team"] = JsonElement("Platform"),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new FieldDefinition("Custom.Team", "Team", "string", false));

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        result.Fields.ShouldContainKey("Custom.Team");
        result.Fields["Custom.Team"].ShouldBe("Platform");
    }

    [Fact]
    public void MapWorkItem_CoreFieldsExcludedFromFields()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.Id"] = JsonElement(1),
                ["System.Rev"] = JsonElement(1),
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["System.AssignedTo"] = JsonElement("Alice"),
                ["System.IterationPath"] = JsonElement(@"Project\Sprint1"),
                ["System.AreaPath"] = JsonElement(@"Project\Area"),
                ["Custom.Priority"] = JsonElement("1"),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("Custom.Priority", "Priority", "string", false));

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldNotContainKey("System.Id");
        result.Fields.ShouldNotContainKey("System.Rev");
        result.Fields.ShouldNotContainKey("System.WorkItemType");
        result.Fields.ShouldNotContainKey("System.Title");
        result.Fields.ShouldNotContainKey("System.State");
        result.Fields.ShouldNotContainKey("System.AssignedTo");
        result.Fields.ShouldNotContainKey("System.IterationPath");
        result.Fields.ShouldNotContainKey("System.AreaPath");
        result.Fields.ShouldContainKey("Custom.Priority");
    }

    [Fact]
    public void MapWorkItem_ReadOnlyNonDisplayWorthy_FilteredOut()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["System.Watermark"] = JsonElement(12345),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.Watermark", "Watermark", "integer", true));

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldNotContainKey("System.Watermark");
    }

    [Fact]
    public void MapWorkItem_NullDefinitions_ImportsAllNonCore()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["Custom.Priority"] = JsonElement("1"),
                ["Custom.Team"] = JsonElement("Backend"),
            },
        };

        // No fieldDefLookup — fallback: import all non-core
        var result = AdoResponseMapper.MapWorkItem(dto, fieldDefLookup: null);

        result.Fields.ShouldContainKey("Custom.Priority");
        result.Fields["Custom.Priority"].ShouldBe("1");
        result.Fields.ShouldContainKey("Custom.Team");
        result.Fields["Custom.Team"].ShouldBe("Backend");
    }

    [Fact]
    public void MapWorkItem_IdentityObjectField_ResolvesToDisplayName()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["System.CreatedBy"] = JsonElement(new { displayName = "Alice Smith", uniqueName = "alice@example.com" }),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.CreatedBy", "Created By", "string", true));

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldContainKey("System.CreatedBy");
        result.Fields["System.CreatedBy"].ShouldBe("Alice Smith");
    }

    [Fact]
    public void MapWorkItem_IdentityObjectField_UniqueNameFallback()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["System.ChangedBy"] = JsonElement(new { uniqueName = "bob@example.com" }),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.ChangedBy", "Changed By", "string", true));

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldContainKey("System.ChangedBy");
        result.Fields["System.ChangedBy"].ShouldBe("bob@example.com");
    }

    [Fact]
    public void MapWorkItem_HtmlFieldStoredAsIs()
    {
        var htmlContent = "<div><p>Rich <strong>description</strong></p></div>";
        var dto = new AdoWorkItemResponse
        {
            Id = 1, Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["System.Description"] = JsonElement(htmlContent),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.Description", "Description", "html", true));

        var result = AdoResponseMapper.MapWorkItem(dto, lookup);

        result.Fields.ShouldContainKey("System.Description");
        result.Fields["System.Description"].ShouldBe(htmlContent);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static Dictionary<string, FieldDefinition> BuildLookup(params FieldDefinition[] defs)
    {
        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs) lookup[d.ReferenceName] = d;
        return lookup;
    }

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
