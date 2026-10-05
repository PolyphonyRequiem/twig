using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class AuthErrorTests
{

    [Fact]
    public void ExceptionHandler_404_ShowsWorkItemNotFound()
    {
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoNotFoundException(42);
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(ex, stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("Work item #42 not found");
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_400_StateTransition_ShowsTransitionHint()
    {
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoBadRequestException("The state transition from 'New' to 'Closed' is not allowed.");
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(ex, stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("transition");
            stderr.ToString().ShouldContain("twig sync");
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }
}
