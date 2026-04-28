using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Dtos;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoResponseMapper.MapToSnapshot"/> and
/// <see cref="AdoResponseMapper.MapToSnapshotWithLinks"/>.
/// Verifies that ADO DTOs are correctly mapped to <see cref="WorkItemSnapshot"/>.
/// </summary>
public sealed class AdoResponseMapperSnapshotTests
{
    // ── MapToSnapshot ──────────────────────────────────────────────────

    [Fact]
    public void MapToSnapshot_BasicFields_MapsCorrectly()
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

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.Id.ShouldBe(42);
        result.Revision.ShouldBe(5);
        result.TypeName.ShouldBe("User Story");
        result.Title.ShouldBe("Implement login");
        result.State.ShouldBe("Active");
        result.AssignedTo.ShouldBe("John Doe");
        result.IterationPath.ShouldBe(@"MyProject\Sprint 1");
        result.AreaPath.ShouldBe(@"MyProject\Backend");
    }

    [Fact]
    public void MapToSnapshot_WithParentRelation_ExtractsParentId()
    {
        var dto = CreateWorkItemDto(id: 100, rev: 1, type: "Task", title: "Sub task", state: "New");
        dto.Relations =
        [
            new AdoRelation
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/50",
            },
        ];

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.ParentId.ShouldBe(50);
    }

    [Fact]
    public void MapToSnapshot_NoRelations_ParentIdIsNull()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "Epic", title: "Top level", state: "New");

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.ParentId.ShouldBeNull();
    }

    [Fact]
    public void MapToSnapshot_NullAssignedTo_ReturnsNull()
    {
        var dto = CreateWorkItemDto(id: 10, rev: 1, type: "Task", title: "No assignee", state: "New", assignedTo: null);

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.AssignedTo.ShouldBeNull();
    }

    [Fact]
    public void MapToSnapshot_NullFields_UsesDefaults()
    {
        var dto = new AdoWorkItemResponse
        {
            Id = 99,
            Rev = 1,
            Fields = null,
        };

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.Id.ShouldBe(99);
        result.TypeName.ShouldBe(string.Empty);
        result.Title.ShouldBe(string.Empty);
        result.State.ShouldBe(string.Empty);
        result.Fields.Count.ShouldBe(0);
    }

    [Fact]
    public void MapToSnapshot_AssignedToIdentityObject_ExtractsDisplayName()
    {
        var identityJson = JsonSerializer.Deserialize<JsonElement>(
            """{"displayName":"Jane Smith","uniqueName":"jane@example.com","id":"abc-123"}""");

        var dto = new AdoWorkItemResponse
        {
            Id = 1,
            Rev = 1,
            Fields = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonElement("Task"),
                ["System.Title"] = JsonElement("Test"),
                ["System.State"] = JsonElement("New"),
                ["System.AssignedTo"] = identityJson,
            },
        };

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.AssignedTo.ShouldBe("Jane Smith");
    }

    [Fact]
    public void MapToSnapshot_FilteredFields_CoreFieldsExcluded()
    {
        var dto = CreateWorkItemDto(
            id: 1, rev: 1, type: "Task", title: "Test", state: "New",
            iterationPath: @"MyProject\Sprint 1", areaPath: @"MyProject");

        // Add a non-core field
        dto.Fields!["Microsoft.VSTS.Common.Priority"] = JsonElement("2");

        var result = AdoResponseMapper.MapToSnapshot(dto);

        // Core fields should be excluded from the Fields dictionary
        result.Fields.ShouldNotContainKey("System.WorkItemType");
        result.Fields.ShouldNotContainKey("System.Title");
        result.Fields.ShouldNotContainKey("System.State");
        result.Fields.ShouldNotContainKey("System.AssignedTo");
        result.Fields.ShouldNotContainKey("System.IterationPath");
        result.Fields.ShouldNotContainKey("System.AreaPath");

        // Non-core fields should be included
        result.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        result.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
    }

    [Fact]
    public void MapToSnapshot_DefaultSeedProperties()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "Task", title: "Test", state: "New");

        var result = AdoResponseMapper.MapToSnapshot(dto);

        result.IsSeed.ShouldBeFalse();
        result.SeedCreatedAt.ShouldBeNull();
        result.LastSyncedAt.ShouldBeNull();
        result.IsDirty.ShouldBeFalse();
    }

    // ── MapToSnapshotWithLinks ────────────────────────────────────────

    [Fact]
    public void MapToSnapshotWithLinks_ReturnsSnapshotAndLinks()
    {
        var dto = CreateWorkItemDto(id: 10, rev: 2, type: "Epic", title: "Parent", state: "Active");
        dto.Relations =
        [
            new AdoRelation
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/_apis/wit/workitems/20",
            },
            new AdoRelation
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/_apis/wit/workItems/5",
            },
        ];

        var (snapshot, links) = AdoResponseMapper.MapToSnapshotWithLinks(dto);

        snapshot.Id.ShouldBe(10);
        snapshot.ParentId.ShouldBe(5);
        links.Count.ShouldBe(1);
        links[0].SourceId.ShouldBe(10);
        links[0].TargetId.ShouldBe(20);
    }

    [Fact]
    public void MapToSnapshotWithLinks_NoRelations_ReturnsEmptyLinks()
    {
        var dto = CreateWorkItemDto(id: 1, rev: 1, type: "Task", title: "Solo", state: "New");

        var (snapshot, links) = AdoResponseMapper.MapToSnapshotWithLinks(dto);

        snapshot.Id.ShouldBe(1);
        links.ShouldBeEmpty();
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

    private static object JsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
