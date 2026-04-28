using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="CreationTools.LinkBranch"/> (twig_link_branch MCP tool).
/// Covers invalid ID, empty branch name, workspace resolution failure, missing git context,
/// happy path (Linked), already-linked, git-context-unavailable, and ADO failure.
/// </summary>
public sealed class CreationToolsLinkBranchTests : CreationToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Invalid ID — returns error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task LinkBranch_InvalidId_ReturnsError(int id)
    {
        var result = await CreateCreationSut().LinkBranch(id, "feature/test");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("workItemId must be a positive");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty / whitespace branch name — returns error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LinkBranch_EmptyOrWhitespaceBranchName_ReturnsError(string branchName)
    {
        var result = await CreateCreationSut().LinkBranch(42, branchName);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("branchName is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Workspace resolution failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_InvalidWorkspace_ReturnsError()
    {
        var result = await CreateCreationSut().LinkBranch(42, "feature/test", workspace: "nonexistent/workspace");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Missing git context — BranchLinkService is null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_NoGitContext_ReturnsError()
    {
        // CreateCreationSut() does not include the git service — BranchLinkService is null
        var result = await CreateCreationSut().LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Git context is not configured");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — branch linked successfully
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_HappyPath_ReturnsLinkedResult()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCreationSutWithGitService().LinkBranch(42, "feature/42-fix");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.GetProperty("branchName").GetString().ShouldBe("feature/42-fix");
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeFalse();
        json.GetProperty("message").GetString()!.ShouldContain("#42");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Already linked — idempotent success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_AlreadyLinked_ReturnsAlreadyLinkedTrue()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await CreateCreationSutWithGitService().LinkBranch(42, "main");

        result.IsError.ShouldBeNull();
        var json = ParseResult(result);
        json.GetProperty("alreadyLinked").GetBoolean().ShouldBeTrue();
        json.GetProperty("message").GetString()!.ShouldContain("already linked");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git context unavailable — GetProjectIdAsync throws
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_GitContextFails_ReturnsError()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unreachable"));

        var result = await CreateCreationSutWithGitService().LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("git-context-unavailable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git context resolves but IDs are null/empty
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranch_NullProjectId_ReturnsError()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");

        var result = await CreateCreationSutWithGitService().LinkBranch(42, "feature/test");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("git-context-unavailable");
    }
}
