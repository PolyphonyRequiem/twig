using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="CreationTools.Link"/> (twig_link MCP tool).
/// Covers happy path, invalid link type, validation errors, ADO failures,
/// and cache sync failure warning.
/// </summary>
public sealed class CreationToolsLinkTests : CreationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Happy path — creates link and returns confirmation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Link_HappyPath_CreatesLinkAndReturnsResult()
    {
        var source = new WorkItemBuilder(100, "Source").AsTask().Build();
        var target = new WorkItemBuilder(200, "Target").AsTask().Build();
        var sourceLinks = new[] { new WorkItemLink(100, 200, "System.LinkTypes.Related") };
        var targetLinks = new[] { new WorkItemLink(200, 100, "System.LinkTypes.Related") };

        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((source, (IReadOnlyList<WorkItemLink>)sourceLinks));
        _adoService.FetchWithLinksAsync(200, Arg.Any<CancellationToken>())
            .Returns((target, (IReadOnlyList<WorkItemLink>)targetLinks));

        var result = await CreateCreationSut().Link(100, 200, "related");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("sourceId").GetInt32().ShouldBe(100);
        json.GetProperty("targetId").GetInt32().ShouldBe(200);
        json.GetProperty("linkType").GetString().ShouldBe("related");
        json.GetProperty("linked").GetBoolean().ShouldBeTrue();
        json.TryGetProperty("warning", out _).ShouldBeFalse();

        await _adoService.Received(1).AddLinkAsync(
            100, 200, "System.LinkTypes.Related", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("parent", "System.LinkTypes.Hierarchy-Reverse")]
    [InlineData("child", "System.LinkTypes.Hierarchy-Forward")]
    [InlineData("related", "System.LinkTypes.Related")]
    [InlineData("predecessor", "System.LinkTypes.Dependency-Reverse")]
    [InlineData("successor", "System.LinkTypes.Dependency-Forward")]
    public async Task Link_AllSupportedTypes_ResolvesCorrectAdoType(string friendly, string expectedAdo)
    {
        var result = await CreateCreationSut().Link(1, 2, friendly);

        result.IsError.ShouldBeNull();
        await _adoService.Received(1).AddLinkAsync(
            1, 2, expectedAdo, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_CaseInsensitive_ResolvesCorrectly()
    {
        var result = await CreateCreationSut().Link(10, 20, "RELATED");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("linkType").GetString().ShouldBe("RELATED");

        await _adoService.Received(1).AddLinkAsync(
            10, 20, "System.LinkTypes.Related", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation errors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Link_InvalidLinkType_ReturnsErrorWithSupportedTypes()
    {
        var result = await CreateCreationSut().Link(1, 2, "blocks");

        result.IsError.ShouldBe(true);
        var text = GetErrorText(result);
        text.ShouldContain("Unknown link type 'blocks'");
        text.ShouldContain("Supported types:");
        foreach (var supported in LinkTypeMapper.SupportedTypes)
            text.ShouldContain(supported);

        await _adoService.DidNotReceive().AddLinkAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Link_EmptyLinkType_ReturnsError()
    {
        var result = await CreateCreationSut().Link(1, 2, "");

        result.IsError.ShouldBe(true);
        var text = GetErrorText(result);
        text.ShouldContain("linkType is required");
        text.ShouldContain("Supported types:");
    }

    [Fact]
    public async Task Link_WhitespaceLinkType_ReturnsError()
    {
        var result = await CreateCreationSut().Link(1, 2, "   ");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("linkType is required");
    }

    [Fact]
    public async Task Link_ZeroSourceId_ReturnsError()
    {
        var result = await CreateCreationSut().Link(0, 200, "related");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("sourceId must be a positive");
    }

    [Fact]
    public async Task Link_NegativeSourceId_ReturnsError()
    {
        var result = await CreateCreationSut().Link(-1, 200, "related");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("sourceId must be a positive");
    }

    [Fact]
    public async Task Link_ZeroTargetId_ReturnsError()
    {
        var result = await CreateCreationSut().Link(100, 0, "related");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("targetId must be a positive");
    }

    [Fact]
    public async Task Link_NegativeTargetId_ReturnsError()
    {
        var result = await CreateCreationSut().Link(100, -5, "related");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("targetId must be a positive");
    }

    [Fact]
    public async Task Link_SameSourceAndTarget_ReturnsError()
    {
        var result = await CreateCreationSut().Link(42, 42, "related");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("sourceId and targetId must be different");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Link_AdoThrows_ReturnsError()
    {
        _adoService.AddLinkAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var result = await CreateCreationSut().Link(100, 200, "related");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Link failed");
        GetErrorText(result).ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache sync failure — link succeeds with warning
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Link_CacheSyncFails_ReturnsSuccessWithWarning()
    {
        _adoService.FetchWithLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unreachable"));

        var result = await CreateCreationSut().Link(100, 200, "parent");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("linked").GetBoolean().ShouldBeTrue();
        json.GetProperty("warning").GetString()!.ShouldContain("cache sync failed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache sync — verifies SyncLinksAsync called for both items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Link_HappyPath_SyncsBothItems()
    {
        var sourceLinks = new[] { new WorkItemLink(100, 200, "System.LinkTypes.Related") };
        var targetLinks = new[] { new WorkItemLink(200, 100, "System.LinkTypes.Related") };

        var sourceItem = new WorkItemBuilder(100, "Source").AsTask().Build();
        var targetItem = new WorkItemBuilder(200, "Target").AsTask().Build();

        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((sourceItem, (IReadOnlyList<WorkItemLink>)sourceLinks));
        _adoService.FetchWithLinksAsync(200, Arg.Any<CancellationToken>())
            .Returns((targetItem, (IReadOnlyList<WorkItemLink>)targetLinks));

        var result = await CreateCreationSut().Link(100, 200, "related");

        result.IsError.ShouldBeNull();

        // Verify SyncLinksAsync was called (which internally calls FetchWithLinksAsync)
        await _adoService.Received(1).FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchWithLinksAsync(200, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static string GetErrorText(ModelContextProtocol.Protocol.CallToolResult result)
    {
        return result.Content[0].ShouldBeOfType<ModelContextProtocol.Protocol.TextContentBlock>().Text;
    }
}
