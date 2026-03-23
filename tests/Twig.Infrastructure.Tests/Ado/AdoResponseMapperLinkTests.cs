using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Dtos;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoResponseMapper.ExtractNonHierarchyLinks"/> and
/// <see cref="AdoResponseMapper.MapWorkItemWithLinks"/>.
/// No network calls — all DTOs constructed manually.
/// </summary>
public class AdoResponseMapperLinkTests
{
    // ── ExtractNonHierarchyLinks ─────────────────────────────────────

    [Fact]
    public void ExtractNonHierarchyLinks_RelatedLink_ReturnsRelated()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/200",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.Count.ShouldBe(1);
        result[0].SourceId.ShouldBe(100);
        result[0].TargetId.ShouldBe(200);
        result[0].LinkType.ShouldBe(LinkTypes.Related);
    }

    [Fact]
    public void ExtractNonHierarchyLinks_PredecessorLink_ReturnsPredecessor()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Dependency-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/300",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.Count.ShouldBe(1);
        result[0].SourceId.ShouldBe(100);
        result[0].TargetId.ShouldBe(300);
        result[0].LinkType.ShouldBe(LinkTypes.Predecessor);
    }

    [Fact]
    public void ExtractNonHierarchyLinks_SuccessorLink_ReturnsSuccessor()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Dependency-Forward",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/400",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.Count.ShouldBe(1);
        result[0].SourceId.ShouldBe(100);
        result[0].TargetId.ShouldBe(400);
        result[0].LinkType.ShouldBe(LinkTypes.Successor);
    }

    [Fact]
    public void ExtractNonHierarchyLinks_NullRelations_ReturnsEmpty()
    {
        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractNonHierarchyLinks_EmptyRelations_ReturnsEmpty()
    {
        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, new List<AdoRelation>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractNonHierarchyLinks_MixedTypes_ReturnsOnlyNonHierarchy()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/1",
            },
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Forward",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/2",
            },
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/200",
            },
            new()
            {
                Rel = "System.LinkTypes.Dependency-Forward",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/300",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.Count.ShouldBe(2);
        result.ShouldContain(l => l.LinkType == LinkTypes.Related && l.TargetId == 200);
        result.ShouldContain(l => l.LinkType == LinkTypes.Successor && l.TargetId == 300);
    }

    [Fact]
    public void ExtractNonHierarchyLinks_UnrecognizedRelType_SkipsIt()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.SomeUnknownType",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/500",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractNonHierarchyLinks_NullRel_SkipsIt()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = null,
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/500",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractNonHierarchyLinks_NullUrl_SkipsIt()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = null,
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractNonHierarchyLinks_InvalidUrlFormat_SkipsIt()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "not-a-url-with-trailing-slash/",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractNonHierarchyLinks_MultipleLinksOfSameType_ReturnsAll()
    {
        var relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/200",
            },
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/201",
            },
        };

        var result = AdoResponseMapper.ExtractNonHierarchyLinks(100, relations);

        result.Count.ShouldBe(2);
        result[0].TargetId.ShouldBe(200);
        result[1].TargetId.ShouldBe(201);
    }

    // ── MapWorkItemWithLinks ─────────────────────────────────────────

    [Fact]
    public void MapWorkItemWithLinks_ReturnsItemAndLinks()
    {
        var dto = CreateWorkItemDto(id: 42, rev: 5, type: "Task", title: "Test", state: "Active");
        dto.Relations = new List<AdoRelation>
        {
            new()
            {
                Rel = "System.LinkTypes.Hierarchy-Reverse",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/10",
            },
            new()
            {
                Rel = "System.LinkTypes.Related",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/200",
            },
            new()
            {
                Rel = "System.LinkTypes.Dependency-Forward",
                Url = "https://dev.azure.com/myorg/myproject/_apis/wit/workItems/300",
            },
        };

        var (item, links) = AdoResponseMapper.MapWorkItemWithLinks(dto);

        item.Id.ShouldBe(42);
        item.ParentId.ShouldBe(10);
        links.Count.ShouldBe(2);
        links.ShouldContain(l => l.LinkType == LinkTypes.Related && l.TargetId == 200);
        links.ShouldContain(l => l.LinkType == LinkTypes.Successor && l.TargetId == 300);
    }

    [Fact]
    public void MapWorkItemWithLinks_NoRelations_ReturnsEmptyLinks()
    {
        var dto = CreateWorkItemDto(id: 42, rev: 1, type: "Task", title: "Test", state: "New");
        dto.Relations = null;

        var (item, links) = AdoResponseMapper.MapWorkItemWithLinks(dto);

        item.Id.ShouldBe(42);
        links.ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoWorkItemResponse CreateWorkItemDto(
        int id, int rev, string type, string title, string state)
    {
        var fields = new Dictionary<string, object?>
        {
            ["System.WorkItemType"] = JsonElement(type),
            ["System.Title"] = JsonElement(title),
            ["System.State"] = JsonElement(state),
        };

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
