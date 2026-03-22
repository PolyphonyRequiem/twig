using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for <see cref="ExceptionHandler"/> — the testable exit-code mapping logic
/// extracted from <see cref="ExceptionFilter"/>.
/// </summary>
public class ExceptionFilterTests
{
    [Fact]
    public void Handle_OperationCanceled_SetsExitCode130()
    {
        var saved = Environment.ExitCode;
        try
        {
            var code = ExceptionHandler.Handle(new OperationCanceledException());

            code.ShouldBe(130);
            Environment.ExitCode.ShouldBe(130);
        }
        finally
        {
            Environment.ExitCode = saved;
        }
    }

    [Fact]
    public void Handle_GeneralException_SetsExitCode1AndWritesToStderr()
    {
        var saved = Environment.ExitCode;
        try
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new InvalidOperationException("test error message"), stderr);

            code.ShouldBe(1);
            Environment.ExitCode.ShouldBe(1);
            stderr.ToString().ShouldContain("test error message");
        }
        finally
        {
            Environment.ExitCode = saved;
        }
    }

    [Fact]
    public void Handle_TaskCanceledException_SetsExitCode130()
    {
        // TaskCanceledException derives from OperationCanceledException
        var saved = Environment.ExitCode;
        try
        {
            var code = ExceptionHandler.Handle(new TaskCanceledException());

            code.ShouldBe(130);
        }
        finally
        {
            Environment.ExitCode = saved;
        }
    }

    [Fact]
    public void Handle_NullReferenceException_SetsExitCode1AndWritesMessageToStderr()
    {
        var saved = Environment.ExitCode;
        try
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new NullReferenceException("something was null"), stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("something was null");
        }
        finally
        {
            Environment.ExitCode = saved;
        }
    }

    [Fact]
    public void Handle_EditorNotFoundException_SetsExitCode1AndWritesMessageToStderr()
    {
        var saved = Environment.ExitCode;
        try
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new Twig.Commands.EditorNotFoundException(), stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("No editor found");
        }
        finally
        {
            Environment.ExitCode = saved;
        }
    }
}
