using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="Twig.Mcp.Tools.CreationTools.LinkArtifact"/> (twig_link_artifact MCP tool).
/// Covers happy path, duplicate detection (alreadyLinked), validation errors, and ADO failures.
/// </summary>
public sealed class CreationToolsLinkArtifactTests : CreationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Happy path — creates artifact link and returns confirmation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkArtifact_HappyPath_ReturnsSuccess()
    {
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCreationSut().LinkArtifact(42, "https://example.com/doc", "My Doc");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.GetProperty("url").GetString().ShouldBe("https://example.com/doc");
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeFalse();
        json.GetProperty("message").GetString()!.ShouldContain("#42");

        await _adoService.Received(1).AddArtifactLinkAsync(
            42, "https://example.com/doc", "My Doc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkArtifact_VstfsUri_ReturnsSuccess()
    {
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCreationSut().LinkArtifact(
            100, "vstfs:///Git/Commit/proj-id/repo-id/abc123", "Fixed in Commit");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("workItemId").GetInt32().ShouldBe(100);
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task LinkArtifact_NoName_PassesNullToService()
    {
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCreationSut().LinkArtifact(42, "https://example.com");

        result.IsError.ShouldBeNull();
        await _adoService.Received(1).AddArtifactLinkAsync(
            42, "https://example.com", null, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Duplicate — already linked (HTTP 409 scenario)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkArtifact_AlreadyLinked_ReturnsSuccessWithAlreadyLinkedTrue()
    {
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await CreateCreationSut().LinkArtifact(42, "https://example.com/doc");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeTrue();
        json.GetProperty("message").GetString()!.ShouldContain("already exists");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation errors
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task LinkArtifact_InvalidWorkItemId_ReturnsError(int id)
    {
        var result = await CreateCreationSut().LinkArtifact(id, "https://example.com");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("workItemId must be a positive");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task LinkArtifact_EmptyOrNullUrl_ReturnsError(string? url)
    {
        var result = await CreateCreationSut().LinkArtifact(42, url!);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("url is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkArtifact_AdoThrows_ReturnsError()
    {
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var result = await CreateCreationSut().LinkArtifact(42, "https://example.com");

        result.IsError.ShouldBe(true);
        var text = GetErrorText(result);
        text.ShouldContain("Link failed");
        text.ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace resolution failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkArtifact_InvalidWorkspace_ReturnsError()
    {
        var result = await CreateCreationSut().LinkArtifact(
            42, "https://example.com", workspace: "nonexistent/workspace");

        result.IsError.ShouldBe(true);
    }
}
