using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for auth error handling (FM-002, FM-003): correct remediation messages
/// for Azure CLI and PAT authentication failures.
/// </summary>
public class AuthErrorTests
{
    [Fact]
    public void ExceptionHandler_AzCliAuthError_ShowsAzLoginHint()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoAuthenticationException("Azure CLI returned exit code 1. Run 'az login' to authenticate.");
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("az login");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_PatAuthError_ShowsPatHint()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoAuthenticationException("No PAT found. Set the TWIG_PAT environment variable.");
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("PAT");
            stderr.ToString().ShouldContain("$TWIG_PAT");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_GenericAuthError_ShowsAzLoginHint()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoAuthenticationException("Authentication failed. Check your credentials or run 'az login'.");
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("az login");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_404_ShowsWorkItemNotFound()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoNotFoundException(42);
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("Work item #42 not found");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_400_StateTransition_ShowsTransitionHint()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoBadRequestException("The state transition from 'New' to 'Closed' is not allowed.");
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("transition");
            stderr.ToString().ShouldContain("twig refresh");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }
}
