using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="GitGuard.EnsureGitRepoAsync"/> (EPIC-002 T-009).
/// </summary>
public class GitGuardTests
{
    private readonly IOutputFormatter _fmt;

    public GitGuardTests()
    {
        var factory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _fmt = factory.GetFormatter("human");
    }

    [Fact]
    public async Task NullGitService_ReturnsInvalid()
    {
        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(null, _fmt);

        isValid.ShouldBeFalse();
        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task NotInWorkTree_ReturnsInvalid()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, _fmt);

        isValid.ShouldBeFalse();
        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task IsInsideWorkTreeThrows_ReturnsInvalid()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));

        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, _fmt);

        isValid.ShouldBeFalse();
        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task ValidRepo_ReturnsValid()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, _fmt);

        isValid.ShouldBeTrue();
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task NullGitService_WritesErrorToStderr()
    {
        var swErr = new StringWriter();
        Console.SetError(swErr);
        try
        {
            await GitGuard.EnsureGitRepoAsync(null, _fmt);
            swErr.ToString().ShouldContain("Git is not available");
        }
        finally
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    [Fact]
    public async Task NotInWorkTree_WritesErrorToStderr()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var swErr = new StringWriter();
        Console.SetError(swErr);
        try
        {
            await GitGuard.EnsureGitRepoAsync(gitService, _fmt);
            swErr.ToString().ShouldContain("Not inside a git repository");
        }
        finally
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    [Fact]
    public async Task ExceptionThrown_WritesErrorToStderr()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var swErr = new StringWriter();
        Console.SetError(swErr);
        try
        {
            await GitGuard.EnsureGitRepoAsync(gitService, _fmt);
            swErr.ToString().ShouldContain("Not inside a git repository");
        }
        finally
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }
}
