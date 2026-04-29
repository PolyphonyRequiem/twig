using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="AdminTools.Area"/> (twig_area MCP tool).
/// Covers fresh fetch, cache hit, stale cache with ADO fallback, ADO offline with stale cache,
/// ADO offline with no cache, workspace resolution, and hierarchical tree structure.
/// </summary>
public sealed class AdminToolsAreaTests : ReadToolsTestBase
{
    private static readonly AreaTreeNode SampleTree = new(
        "TestProject",
        "TestProject",
        [
            new AreaTreeNode("Frontend", "TestProject\\Frontend", [
                new AreaTreeNode("React", "TestProject\\Frontend\\React", []),
            ]),
            new AreaTreeNode("Backend", "TestProject\\Backend", []),
        ]);

    private static readonly AreaTreeNode EmptyTree = new("TestProject", "TestProject", []);

    // ═══════════════════════════════════════════════════════════════
    //  Fresh fetch — no cache, ADO returns tree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_NoCacheAdoAvailable_ReturnsFreshTree()
    {
        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(SampleTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("fromCache").GetBoolean().ShouldBeFalse();
        data.GetProperty("fetchedAt").GetString().ShouldNotBeNullOrEmpty();

        var areas = data.GetProperty("areas");
        areas.GetArrayLength().ShouldBe(1);

        var root = areas[0];
        root.GetProperty("name").GetString().ShouldBe("TestProject");
        root.GetProperty("path").GetString().ShouldBe("TestProject");
        root.GetProperty("children").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Area_NoCacheAdoAvailable_CachesResult()
    {
        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(SampleTree);

        var sut = CreateAdminSut();
        await sut.Area();

        // Verify cache was written
        await _contextStore.Received(1).SetValueAsync(
            "area_tree_json", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _contextStore.Received(1).SetValueAsync(
            "area_tree_fetched_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache hit — fresh cache, no ADO call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_FreshCache_ReturnsCachedWithoutAdoCall()
    {
        var cachedJson = """{"name":"TestProject","path":"TestProject","children":[{"name":"Area1","path":"TestProject\\Area1","children":[]}]}""";
        var freshTime = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");

        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns(cachedJson);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns(freshTime);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("fromCache").GetBoolean().ShouldBeTrue();
        data.GetProperty("fetchedAt").GetString().ShouldBe(freshTime);

        // Should NOT call ADO
        await _iterationService.DidNotReceive().GetAreaTreeAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stale cache — ADO available, returns fresh data
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_StaleCacheAdoAvailable_FetchesFreshFromAdo()
    {
        var cachedJson = """{"name":"OldProject","path":"OldProject","children":[]}""";
        var staleTime = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");

        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns(cachedJson);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns(staleTime);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(SampleTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("fromCache").GetBoolean().ShouldBeFalse();

        var areas = data.GetProperty("areas");
        areas[0].GetProperty("name").GetString().ShouldBe("TestProject");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stale cache — ADO unreachable, returns stale data
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_StaleCacheAdoUnreachable_ReturnsStaleData()
    {
        var cachedJson = """{"name":"StaleProject","path":"StaleProject","children":[]}""";
        var staleTime = DateTimeOffset.UtcNow.AddHours(-3).ToString("o");

        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns(cachedJson);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns(staleTime);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Network error"));

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        data.GetProperty("fromCache").GetBoolean().ShouldBeTrue();
        data.GetProperty("areas")[0].GetProperty("name").GetString().ShouldBe("StaleProject");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No cache, ADO unreachable — returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_NoCacheAdoUnreachable_ReturnsError()
    {
        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Network error"));

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Cannot fetch area tree");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace not found — returns error envelope
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_InvalidWorkspace_ReturnsError()
    {
        var sut = CreateAdminSut();
        var result = await sut.Area(workspace: "invalid/workspace");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Hierarchical structure — verifies nested children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_DeepTree_PreservesHierarchy()
    {
        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(SampleTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        var root = data.GetProperty("areas")[0];
        var frontend = root.GetProperty("children")[0];
        frontend.GetProperty("name").GetString().ShouldBe("Frontend");
        frontend.GetProperty("children").GetArrayLength().ShouldBe(1);

        var react = frontend.GetProperty("children")[0];
        react.GetProperty("name").GetString().ShouldBe("React");
        react.GetProperty("children").GetArrayLength().ShouldBe(0);

        var backend = root.GetProperty("children")[1];
        backend.GetProperty("name").GetString().ShouldBe("Backend");
        backend.GetProperty("children").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty tree — project with no child areas
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_EmptyTree_ReturnsRootWithNoChildren()
    {
        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(EmptyTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);

        var root = data.GetProperty("areas")[0];
        root.GetProperty("name").GetString().ShouldBe("TestProject");
        root.GetProperty("children").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Envelope shape — success envelope has context block
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_SuccessEnvelope_HasContextBlock()
    {
        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(EmptyTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        var envelope = ParseEnvelope(result);
        envelope.GetProperty("success").GetBoolean().ShouldBeTrue();
        envelope.TryGetProperty("data", out _).ShouldBeTrue();
        envelope.TryGetProperty("context", out _).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache TTL boundary — exactly 1 hour is stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_CacheExactlyOneHourOld_TriggersRefresh()
    {
        var cachedJson = """{"name":"Old","path":"Old","children":[]}""";
        var exactlyOneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");

        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns(cachedJson);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns(exactlyOneHourAgo);
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(SampleTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("fromCache").GetBoolean().ShouldBeFalse();

        // Should have called ADO
        await _iterationService.Received(1).GetAreaTreeAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid fetchedAt timestamp — treated as stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Area_InvalidFetchedAtTimestamp_TreatsAsStale()
    {
        var cachedJson = """{"name":"Old","path":"Old","children":[]}""";

        _contextStore.GetValueAsync("area_tree_json", Arg.Any<CancellationToken>())
            .Returns(cachedJson);
        _contextStore.GetValueAsync("area_tree_fetched_at", Arg.Any<CancellationToken>())
            .Returns("not-a-valid-date");
        _iterationService.GetAreaTreeAsync(Arg.Any<CancellationToken>())
            .Returns(SampleTree);

        var sut = CreateAdminSut();
        var result = await sut.Area();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("fromCache").GetBoolean().ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private AdminTools CreateAdminSut()
    {
        var resolver = BuildResolver(DefaultConfig);
        return new AdminTools(resolver);
    }
}
