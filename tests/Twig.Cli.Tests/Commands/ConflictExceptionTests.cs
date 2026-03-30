using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for FM-006: concurrency conflict (412) exception handling.
/// </summary>
public class ConflictExceptionTests
{
    [Fact]
    public void ExceptionHandler_ConflictException_WritesMessagesAndExitsWithCode1()
    {
        var savedExitCode = Environment.ExitCode;
        try
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new AdoConflictException(42), stderr);

            code.ShouldBe(1);
            Environment.ExitCode.ShouldBe(1);
            var output = stderr.ToString();
            output.ShouldContain("error: Concurrency conflict (revision mismatch).");
            output.ShouldContain("hint: Another change is being processed. Run 'twig refresh' and retry.");
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }
}
