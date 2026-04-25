using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Hints;

/// <summary>
/// Tests for passive branch detection hint in <see cref="HintEngine"/>.
/// </summary>
public class BranchDetectionHintTests
{
    private readonly IGitService _gitService;
    private readonly IWorkItemRepository _workItemRepo;

    public BranchDetectionHintTests()
    {
        _gitService = Substitute.For<IGitService>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
    }

    private static HintEngine CreateEngine(bool hintsEnabled = true)
    {
        return new HintEngine(new DisplayConfig { Hints = hintsEnabled });
    }

    // ── Matching branch emits hint ──────────────────────────────────

    [Fact]
    public async Task MatchingBranch_NoContext_EmitsHint()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login-timeout");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldNotBeNull();
        hint.ShouldContain("#12345");
        hint.ShouldContain("twig set 12345");
    }

    // ── Non-matching branch emits nothing ───────────────────────────

    [Fact]
    public async Task NonMatchingBranch_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    [Fact]
    public async Task MatchingBranch_IdNotInCache_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/99999-unknown");
        _workItemRepo.ExistsByIdAsync(99999, Arg.Any<CancellationToken>()).Returns(false);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    // ── No git repo emits nothing ───────────────────────────────────

    [Fact]
    public async Task NoGitService_EmitsNothing()
    {
        var engine = CreateEngine();

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: null,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    [Fact]
    public async Task NotInWorkTree_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    [Fact]
    public async Task GitThrowsException_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    // ── Already has context emits nothing ────────────────────────────

    [Fact]
    public async Task AlreadyHasContext_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login-timeout");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: 999, // Active context exists
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    // ── Suppression ─────────────────────────────────────────────────

    [Fact]
    public async Task HintsDisabled_EmitsNothing()
    {
        var engine = CreateEngine(hintsEnabled: false);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login-timeout");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern);

        hint.ShouldBeNull();
    }

    [Fact]
    public async Task JsonFormat_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login-timeout");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern,
            outputFormat: "json");

        hint.ShouldBeNull();
    }

    [Fact]
    public async Task MinimalFormat_EmitsNothing()
    {
        var engine = CreateEngine();
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login-timeout");
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var hint = await engine.GetBranchDetectionHintAsync(
            activeContextId: null,
            gitService: _gitService,
            workItemRepo: _workItemRepo,
            branchPattern: BranchNameTemplate.DefaultPattern,
            outputFormat: "minimal");

        hint.ShouldBeNull();
    }
}
