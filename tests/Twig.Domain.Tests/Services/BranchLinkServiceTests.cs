using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class BranchLinkServiceTests
{
    private readonly IAdoGitService _adoGitService = Substitute.For<IAdoGitService>();
    private readonly IAdoWorkItemService _adoWorkItemService = Substitute.For<IAdoWorkItemService>();
    private readonly BranchLinkService _sut;

    private const string ProjectId = "proj-guid-123";
    private const string RepoId = "repo-guid-456";

    public BranchLinkServiceTests()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns(ProjectId);
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns(RepoId);
        _adoWorkItemService.AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _sut = new BranchLinkService(_adoGitService, _adoWorkItemService);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — new link
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_Success_ReturnsLinked()
    {
        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.Linked);
        result.WorkItemId.ShouldBe(42);
        result.BranchName.ShouldBe("feature/42-login-fix");
        result.ArtifactUri.ShouldNotBeNullOrWhiteSpace();
        result.IsSuccess.ShouldBeTrue();
        result.ErrorMessage.ShouldBe("");
    }

    [Fact]
    public async Task LinkBranchAsync_Success_BuildsCorrectArtifactUri()
    {
        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        var expectedUri = $"vstfs:///Git/Ref/{ProjectId}/{RepoId}/GB{Uri.EscapeDataString("feature/42-login-fix")}";
        result.ArtifactUri.ShouldBe(expectedUri);
    }

    [Fact]
    public async Task LinkBranchAsync_Success_CallsAddArtifactLink()
    {
        await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        await _adoWorkItemService.Received(1).AddArtifactLinkAsync(
            42,
            Arg.Is<string>(u => u.StartsWith("vstfs:///Git/Ref/")),
            "Branch",
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Already linked (idempotent)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_AlreadyLinked_ReturnsAlreadyLinked()
    {
        _adoWorkItemService.AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.AlreadyLinked);
        result.IsSuccess.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git context unavailable — null IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_NullProjectId_ReturnsGitContextUnavailable()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.GitContextUnavailable);
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LinkBranchAsync_NullRepoId_ReturnsGitContextUnavailable()
    {
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.GitContextUnavailable);
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task LinkBranchAsync_EmptyProjectId_ReturnsGitContextUnavailable()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns(" ");

        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.GitContextUnavailable);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git context unavailable — exception during resolution
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_GitServiceThrows_ReturnsGitContextUnavailable()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.GitContextUnavailable);
        result.ErrorMessage.ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Artifact link failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_AddArtifactLinkThrows_ReturnsFailed()
    {
        _adoWorkItemService.AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("HTTP 400"));

        var result = await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        result.Status.ShouldBe(BranchLinkStatus.Failed);
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("HTTP 400");
        result.ArtifactUri.ShouldNotBeNullOrWhiteSpace();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Input validation
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LinkBranchAsync_InvalidBranchName_ThrowsArgumentException(string? branchName)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _sut.LinkBranchAsync(42, branchName!));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_Cancelled_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.LinkBranchAsync(42, "feature/42-login-fix", cts.Token));
    }

    // ═══════════════════════════════════════════════════════════════
    //  BuildArtifactUri — static helper
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildArtifactUri_SimpleBranch_FormatsCorrectly()
    {
        var uri = BranchLinkService.BuildArtifactUri("proj-1", "repo-2", "main");

        uri.ShouldBe("vstfs:///Git/Ref/proj-1/repo-2/GBmain");
    }

    [Fact]
    public void BuildArtifactUri_BranchWithSlash_EncodesCorrectly()
    {
        var uri = BranchLinkService.BuildArtifactUri("proj-1", "repo-2", "feature/my-branch");

        uri.ShouldBe($"vstfs:///Git/Ref/proj-1/repo-2/GB{Uri.EscapeDataString("feature/my-branch")}");
    }

    [Fact]
    public void BuildArtifactUri_BranchWithSpecialChars_EncodesCorrectly()
    {
        var uri = BranchLinkService.BuildArtifactUri("proj-1", "repo-2", "users/john@example/fix");

        uri.ShouldContain("GB");
        uri.ShouldStartWith("vstfs:///Git/Ref/proj-1/repo-2/GB");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Does not call AddArtifactLink when context resolution fails
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task LinkBranchAsync_GitContextUnavailable_DoesNotCallAddArtifactLink()
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        await _sut.LinkBranchAsync(42, "feature/42-login-fix");

        await _adoWorkItemService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
