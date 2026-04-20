using System.Text.Json;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

public sealed class WorkspaceToolsTests
{
    private static readonly WorkspaceKey Key1 = new("org1", "project1");
    private static readonly WorkspaceKey Key2 = new("org2", "project2");

    // ═══════════════════════════════════════════════════════════════
    //  Empty registry — returns empty list
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ListWorkspaces_EmptyRegistry_ReturnsEmptyList()
    {
        var (registry, resolver) = BuildDeps(Array.Empty<WorkspaceKey>());
        var sut = new WorkspaceTools(registry, resolver);

        var result = sut.ListWorkspaces();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("workspaces").GetArrayLength().ShouldBe(0);
        root.GetProperty("count").GetInt32().ShouldBe(0);
        root.GetProperty("isSingleWorkspace").GetBoolean().ShouldBe(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single workspace — isSingleWorkspace=true, no active
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ListWorkspaces_SingleWorkspace_ReturnsSingleEntry()
    {
        var (registry, resolver) = BuildDeps(new[] { Key1 });
        var sut = new WorkspaceTools(registry, resolver);

        var result = sut.ListWorkspaces();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("count").GetInt32().ShouldBe(1);
        root.GetProperty("isSingleWorkspace").GetBoolean().ShouldBe(true);

        var ws = root.GetProperty("workspaces")[0];
        ws.GetProperty("org").GetString().ShouldBe("org1");
        ws.GetProperty("project").GetString().ShouldBe("project1");
        ws.GetProperty("key").GetString().ShouldBe("org1/project1");
        ws.GetProperty("isActive").GetBoolean().ShouldBe(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple workspaces — active workspace marked
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ListWorkspaces_MultipleWorkspaces_ActiveMarked()
    {
        var (registry, resolver) = BuildDeps(new[] { Key1, Key2 });
        resolver.ActiveWorkspace = Key2;
        var sut = new WorkspaceTools(registry, resolver);

        var result = sut.ListWorkspaces();

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("count").GetInt32().ShouldBe(2);
        root.GetProperty("isSingleWorkspace").GetBoolean().ShouldBe(false);

        var workspaces = root.GetProperty("workspaces");
        workspaces[0].GetProperty("key").GetString().ShouldBe("org1/project1");
        workspaces[0].GetProperty("isActive").GetBoolean().ShouldBe(false);
        workspaces[1].GetProperty("key").GetString().ShouldBe("org2/project2");
        workspaces[1].GetProperty("isActive").GetBoolean().ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active workspace — all isActive=false
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ListWorkspaces_NoActiveWorkspace_AllInactive()
    {
        var (registry, resolver) = BuildDeps(new[] { Key1, Key2 });
        var sut = new WorkspaceTools(registry, resolver);

        var result = sut.ListWorkspaces();

        var root = ParseResult(result);
        var workspaces = root.GetProperty("workspaces");
        workspaces[0].GetProperty("isActive").GetBoolean().ShouldBe(false);
        workspaces[1].GetProperty("isActive").GetBoolean().ShouldBe(false);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static (IWorkspaceRegistry registry, WorkspaceResolver resolver) BuildDeps(
        IReadOnlyList<WorkspaceKey> keys)
    {
        var registry = Substitute.For<IWorkspaceRegistry>();
        registry.Workspaces.Returns(keys);
        registry.IsSingleWorkspace.Returns(keys.Count == 1);

        var factory = Substitute.For<IWorkspaceContextFactory>();
        var resolver = new WorkspaceResolver(registry, factory);

        return (registry, resolver);
    }

    private static JsonElement ParseResult(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
