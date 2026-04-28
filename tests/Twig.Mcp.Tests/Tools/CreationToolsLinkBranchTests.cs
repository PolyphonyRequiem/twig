using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;
using Twig.Domain.Services.Navigation;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="Twig.Mcp.Tools.CreationTools.LinkBranch"/> (twig_link_branch MCP tool).
/// Covers happy path, already-linked idempotency, validation errors, git context unavailable,
/// and BranchLinkService failure scenarios.
/// </summary>
public sealed class CreationToolsLinkBranchTests : CreationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Happy path — links branch and returns confirmation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_HappyPath_ReturnsSuccess()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .Returns("proj-guid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>())
            .Returns("repo-guid");
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, "feature/123-fix-login");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.GetProperty("branchName").GetString().ShouldBe("feature/123-fix-login");
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeFalse();
        json.GetProperty("artifactUri").GetString()!.ShouldContain("vstfs:///Git/Ref/");
        json.GetProperty("message").GetString()!.ShouldContain("#42");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Already linked — idempotent success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_AlreadyLinked_ReturnsSuccessWithAlreadyLinkedTrue()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .Returns("proj-guid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>())
            .Returns("repo-guid");
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, "feature/123-fix-login");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeTrue();
        json.GetProperty("message").GetString()!.ShouldContain("already linked");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation errors
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task LinkBranch_InvalidWorkItemId_ReturnsError(int id)
    {
        var result = await CreateCreationSutWithGitService()
            .LinkBranch(id, "feature/test");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("workItemId must be a positive");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task LinkBranch_EmptyOrNullBranchName_ReturnsError(string? branchName)
    {
        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, branchName!);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("branchName is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git context not configured
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_NoGitContext_ReturnsError()
    {
        // CreateCreationSut() does NOT include git service
        var result = await CreateCreationSut()
            .LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Git context is not configured");
    }

    // ═══════════════════════════════════════════════════════════════
    //  BranchLinkService failure — git context unavailable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_GitContextResolutionFails_ReturnsError()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DNS resolution failed"));

        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        var json = ParseResult(result);
        json.GetProperty("status").GetString().ShouldBe("git-context-unavailable");
        json.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.GetProperty("branchName").GetString().ShouldBe("feature/test");
        json.GetProperty("errorMessage").GetString()!.ShouldContain("Failed to resolve git context");
    }

    [Fact]
    public async Task LinkBranch_NullProjectId_ReturnsError()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>())
            .Returns("repo-guid");

        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        var json = ParseResult(result);
        json.GetProperty("status").GetString().ShouldBe("git-context-unavailable");
        json.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.GetProperty("branchName").GetString().ShouldBe("feature/test");
        json.GetProperty("errorMessage").GetString()!.ShouldContain("could not be resolved");
    }

    // ═══════════════════════════════════════════════════════════════
    //  BranchLinkService failure — ADO API error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_AdoThrows_ReturnsError()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .Returns("proj-guid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>())
            .Returns("repo-guid");
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        var json = ParseResult(result);
        json.GetProperty("status").GetString().ShouldBe("failed");
        json.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.GetProperty("branchName").GetString().ShouldBe("feature/test");
        json.GetProperty("artifactUri").GetString()!.ShouldContain("vstfs:///Git/Ref/");
        json.GetProperty("errorMessage").GetString()!.ShouldContain("Failed to add artifact link");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace resolution failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_InvalidWorkspace_ReturnsError()
    {
        var result = await CreateCreationSutWithGitService()
            .LinkBranch(42, "feature/test", workspace: "nonexistent/workspace");

        result.IsError.ShouldBe(true);
    }
}
