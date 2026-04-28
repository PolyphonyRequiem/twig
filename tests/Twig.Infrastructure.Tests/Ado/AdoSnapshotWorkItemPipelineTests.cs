using System.Text.Json;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Dtos;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// End-to-end pipeline tests verifying the ADO → Snapshot → WorkItem chain.
/// Given an <see cref="AdoWorkItemResponse"/>, asserts that
/// <see cref="AdoResponseMapper.MapToSnapshot"/> + <see cref="WorkItemMapper.Map"/>
/// produces a <see cref="WorkItem"/> with the expected properties.
/// </summary>
public sealed class AdoSnapshotWorkItemPipelineTests
{
    private readonly WorkItemMapper _mapper = new();

    // ── Basic property propagation ──────────────────────────────────

    [Fact]
    public void Pipeline_BasicFields_PropagateToWorkItem()
    {
        var dto = CreateDto(
            id: 42,
            rev: 5,
            type: "User Story",
            title: "Implement login",
            state: "Active",
            assignedTo: "John Doe",
            iterationPath: @"MyProject\Sprint 1",
            areaPath: @"MyProject\Backend");

        var item = RunPipeline(dto);

        item.Id.ShouldBe(42);
        item.Revision.ShouldBe(5);
        item.Type.ShouldBe(WorkItemType.UserStory);
        item.Title.ShouldBe("Implement login");
        item.State.ShouldBe("Active");
        item.AssignedTo.ShouldBe("John Doe");
        item.IterationPath.Value.ShouldBe(@"MyProject\Sprint 1");
        item.AreaPath.Value.ShouldBe(@"MyProject\Backend");
    }

    [Fact]
    public void Pipeline_ParentRelation_PropagatedToWorkItem()
    {
        var dto = CreateDto(id: 100, rev: 1, type: "Task", title: "Child", state: "New");
        dto.Relations =
        [
            new AdoRelation
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/50",
            },
        ];

        var item = RunPipeline(dto);

        item.ParentId.ShouldBe(50);
    }

    [Fact]
    public void Pipeline_NoRelations_ParentIdIsNull()
    {
        var dto = CreateDto(id: 1, rev: 1, type: "Epic", title: "Top", state: "New");

        var item = RunPipeline(dto);

        item.ParentId.ShouldBeNull();
    }

    // ── Type parsing ────────────────────────────────────────────────

    [Theory]
    [InlineData("Task", "Task")]
    [InlineData("Bug", "Bug")]
    [InlineData("Epic", "Epic")]
    [InlineData("User Story", "User Story")]
    [InlineData("Feature", "Feature")]
    [InlineData("Issue", "Issue")]
    [InlineData("CustomType", "CustomType")]
    public void Pipeline_WorkItemType_ParsedToValueObject(string adoTypeName, string expectedValue)
    {
        var dto = CreateDto(id: 1, rev: 1, type: adoTypeName, title: "Test", state: "New");

        var item = RunPipeline(dto);

        item.Type.Value.ShouldBe(expectedValue);
    }

    [Fact]
    public void Pipeline_EmptyTypeName_FallsBackToTask()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
            },
        };

        var item = RunPipeline(dto);

        item.Type.ShouldBe(WorkItemType.Task);
    }

    // ── Identity resolution ─────────────────────────────────────────

    [Fact]
    public void Pipeline_AssignedToIdentityObject_ResolvesDisplayName()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
                ["System.AssignedTo"] = ToJsonElement(new { displayName = "Jane Smith", uniqueName = "jane@example.com" }),
            },
        };

        var item = RunPipeline(dto);

        item.AssignedTo.ShouldBe("Jane Smith");
    }

    [Fact]
    public void Pipeline_NullAssignedTo_PreservesNull()
    {
        var dto = CreateDto(id: 1, rev: 1, type: "Task", title: "Test", state: "New");

        var item = RunPipeline(dto);

        item.AssignedTo.ShouldBeNull();
    }

    // ── Field propagation ───────────────────────────────────────────

    [Fact]
    public void Pipeline_CustomFields_PropagateToWorkItemFields()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 3,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("Active"),
                ["Microsoft.VSTS.Common.Priority"] = ToJsonElement("2"),
                ["Custom.Team"] = ToJsonElement("Platform"),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new FieldDefinition("Custom.Team", "Team", "string", false));

        var item = RunPipeline(dto, lookup);

        item.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        item.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        item.Fields.ShouldContainKey("Custom.Team");
        item.Fields["Custom.Team"].ShouldBe("Platform");
    }

    [Fact]
    public void Pipeline_CoreFields_ExcludedFromWorkItemFields()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.Id"] = ToJsonElement(1),
                ["System.Rev"] = ToJsonElement(1),
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
                ["System.AssignedTo"] = ToJsonElement("Alice"),
                ["System.IterationPath"] = ToJsonElement(@"Project\Sprint1"),
                ["System.AreaPath"] = ToJsonElement(@"Project\Area"),
                ["Microsoft.VSTS.Common.Priority"] = ToJsonElement("1"),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false));

        var item = RunPipeline(dto, lookup);

        item.Fields.ShouldNotContainKey("System.Id");
        item.Fields.ShouldNotContainKey("System.Rev");
        item.Fields.ShouldNotContainKey("System.WorkItemType");
        item.Fields.ShouldNotContainKey("System.Title");
        item.Fields.ShouldNotContainKey("System.State");
        item.Fields.ShouldNotContainKey("System.AssignedTo");
        item.Fields.ShouldNotContainKey("System.IterationPath");
        item.Fields.ShouldNotContainKey("System.AreaPath");
        item.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
    }

    [Fact]
    public void Pipeline_IdentityFieldInArbitraryField_ResolvedInWorkItemFields()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
                ["System.CreatedBy"] = ToJsonElement(new { displayName = "Alice", uniqueName = "alice@example.com" }),
            },
        };

        var item = RunPipeline(dto);

        item.Fields.ShouldContainKey("System.CreatedBy");
        item.Fields["System.CreatedBy"].ShouldBe("Alice");
    }

    [Fact]
    public void Pipeline_HtmlContent_PreservedThroughPipeline()
    {
        var html = "<div><p>Rich <strong>description</strong></p></div>";
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
                ["System.Description"] = ToJsonElement(html),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.Description", "Description", "html", true));

        var item = RunPipeline(dto, lookup);

        item.Fields.ShouldContainKey("System.Description");
        item.Fields["System.Description"].ShouldBe(html);
    }

    [Fact]
    public void Pipeline_ReadOnlyNonDisplayWorthy_ExcludedFromWorkItem()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
                ["System.Watermark"] = ToJsonElement(12345),
                ["Microsoft.VSTS.Common.Priority"] = ToJsonElement("1"),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.Watermark", "Watermark", "integer", true),
            new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false));

        var item = RunPipeline(dto, lookup);

        item.Fields.ShouldNotContainKey("System.Watermark");
        item.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
    }

    // ── Sync state ──────────────────────────────────────────────────

    [Fact]
    public void Pipeline_NonZeroRevision_WorkItemIsSynced()
    {
        var dto = CreateDto(id: 1, rev: 7, type: "Task", title: "Test", state: "New");

        var item = RunPipeline(dto);

        item.Revision.ShouldBe(7);
        item.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Pipeline_DefaultSeedProperties_NotSeed()
    {
        var dto = CreateDto(id: 1, rev: 3, type: "Task", title: "Test", state: "Active");

        var item = RunPipeline(dto);

        item.IsSeed.ShouldBeFalse();
        item.SeedCreatedAt.ShouldBeNull();
        item.LastSyncedAt.ShouldBeNull();
    }

    // ── Null/missing edge cases ─────────────────────────────────────

    [Fact]
    public void Pipeline_NullFields_WorkItemHasDefaults()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 99,
            Rev = 1,
            Fields = null,
        };

        var item = RunPipeline(dto);

        item.Id.ShouldBe(99);
        item.Revision.ShouldBe(1);
        item.Type.ShouldBe(WorkItemType.Task);
        item.Title.ShouldBe(string.Empty);
        item.State.ShouldBe(string.Empty);
        item.AssignedTo.ShouldBeNull();
        item.Fields.Count.ShouldBe(0);
    }

    [Fact]
    public void Pipeline_NullIterationAndAreaPath_DefaultValueObjects()
    {
        var dto = CreateDto(id: 1, rev: 1, type: "Bug", title: "Test", state: "New");

        var item = RunPipeline(dto);

        item.IterationPath.ShouldBe(default(IterationPath));
        item.AreaPath.ShouldBe(default(AreaPath));
    }

    // ── WithLinks pipeline ──────────────────────────────────────────

    [Fact]
    public void Pipeline_WithLinks_ProducesWorkItemAndLinks()
    {
        var dto = CreateDto(id: 42, rev: 5, type: "Task", title: "Test", state: "Active");
        dto.Relations =
        [
            new AdoRelation
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/10",
            },
            new AdoRelation
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/200",
            },
            new AdoRelation
            {
                Rel = "System.LinkTypes.Dependency-Forward",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/300",
            },
        ];

        var (snapshot, links) = AdoResponseMapper.MapToSnapshotWithLinks(dto);
        var item = _mapper.Map(snapshot);

        item.Id.ShouldBe(42);
        item.ParentId.ShouldBe(10);
        links.Count.ShouldBe(2);
        links.ShouldContain(l => l.LinkType == LinkTypes.Related && l.TargetId == 200);
        links.ShouldContain(l => l.LinkType == LinkTypes.Successor && l.TargetId == 300);
    }

    [Fact]
    public void Pipeline_WithLinks_NoRelations_EmptyLinks()
    {
        var dto = CreateDto(id: 1, rev: 1, type: "Task", title: "Solo", state: "New");
        dto.Relations = null;

        var (snapshot, links) = AdoResponseMapper.MapToSnapshotWithLinks(dto);
        var item = _mapper.Map(snapshot);

        item.Id.ShouldBe(1);
        item.ParentId.ShouldBeNull();
        links.ShouldBeEmpty();
    }

    // ── Field definition filtering through pipeline ─────────────────

    [Fact]
    public void Pipeline_NullFieldDefLookup_ImportsAllNonCoreToWorkItem()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("New"),
                ["Microsoft.VSTS.Common.Priority"] = ToJsonElement("2"),
                ["Custom.MyField"] = ToJsonElement("custom_value"),
                ["System.Watermark"] = ToJsonElement(42),
            },
        };

        var item = RunPipeline(dto, fieldDefLookup: null);

        item.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        item.Fields.ShouldContainKey("Custom.MyField");
        item.Fields["Custom.MyField"].ShouldBe("custom_value");
        item.Fields.ShouldContainKey("System.Watermark");
        item.Fields.ShouldNotContainKey("System.Title");
        item.Fields.ShouldNotContainKey("System.State");
    }

    [Fact]
    public void Pipeline_DisplayWorthyReadOnly_IncludedInWorkItem()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = ToJsonElement("Task"),
                ["System.Title"] = ToJsonElement("Test"),
                ["System.State"] = ToJsonElement("Active"),
                ["System.Tags"] = ToJsonElement("backend; api"),
                ["System.CreatedDate"] = ToJsonElement("2025-01-15T10:00:00Z"),
            },
        };

        var lookup = BuildLookup(
            new FieldDefinition("System.Tags", "Tags", "plainText", true),
            new FieldDefinition("System.CreatedDate", "Created Date", "dateTime", true));

        var item = RunPipeline(dto, lookup);

        item.Fields.ShouldContainKey("System.Tags");
        item.Fields["System.Tags"].ShouldBe("backend; api");
        item.Fields.ShouldContainKey("System.CreatedDate");
        item.Fields["System.CreatedDate"].ShouldBe("2025-01-15T10:00:00Z");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private WorkItem RunPipeline(AdoWorkItemResponse dto,
        IReadOnlyDictionary<string, FieldDefinition>? fieldDefLookup = null)
    {
        var snapshot = AdoResponseMapper.MapToSnapshot(dto, fieldDefLookup);
        return _mapper.Map(snapshot);
    }

    private static Dictionary<string, FieldDefinition> BuildLookup(params FieldDefinition[] defs)
    {
        var lookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in defs) lookup[d.ReferenceName] = d;
        return lookup;
    }

    private static AdoWorkItemResponse CreateDto(
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
            ["System.WorkItemType"] = ToJsonElement(type),
            ["System.Title"] = ToJsonElement(title),
            ["System.State"] = ToJsonElement(state),
        };

        if (assignedTo is not null)
            fields["System.AssignedTo"] = ToJsonElement(assignedTo);

        if (iterationPath is not null)
            fields["System.IterationPath"] = ToJsonElement(iterationPath);

        if (areaPath is not null)
            fields["System.AreaPath"] = ToJsonElement(areaPath);

        return new AdoWorkItemResponse
        {
            Id = id,
            Rev = rev,
            Fields = fields,
        };
    }

    private static object ToJsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
