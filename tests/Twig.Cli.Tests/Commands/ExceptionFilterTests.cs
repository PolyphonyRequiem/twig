using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class ExceptionFilterTests
{
    [Fact]
    public void Handle_OperationCanceled_SetsExitCode130() =>
        WithExitCodeRestored(() =>
        {
            var code = ExceptionHandler.Handle(new OperationCanceledException());
            code.ShouldBe(130);
            Environment.ExitCode.ShouldBe(130);
        });

    [Fact]
    public void Handle_GeneralException_SetsExitCode1AndWritesToStderr() =>
        WithExitCodeRestored(() =>
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new InvalidOperationException("test error message"), stderr);
            code.ShouldBe(1);
            Environment.ExitCode.ShouldBe(1);
            stderr.ToString().ShouldContain("test error message");
        });

    [Fact]
    public void Handle_TaskCanceledException_SetsExitCode130() =>
        WithExitCodeRestored(() =>
        {
            var code = ExceptionHandler.Handle(new TaskCanceledException());
            code.ShouldBe(130);
        });

    [Fact]
    public void Handle_NullReferenceException_SetsExitCode1AndWritesMessageToStderr() =>
        WithExitCodeRestored(() =>
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new NullReferenceException("something was null"), stderr);
            code.ShouldBe(1);
            stderr.ToString().ShouldContain("something was null");
        });

    [Fact]
    public void Handle_AdoConflictException_Returns1WithHint() =>
        WithExitCodeRestored(() =>
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new AdoConflictException(42), stderr);
            code.ShouldBe(1);
            Environment.ExitCode.ShouldBe(1);
            var output = stderr.ToString();
            output.ShouldContain("error: Concurrency conflict (revision mismatch).");
            output.ShouldContain("hint: Another change is being processed. Run 'twig sync' and retry.");
        });

    [Fact]
    public void Handle_EditorNotFoundException_SetsExitCode1AndWritesMessageToStderr() =>
        WithExitCodeRestored(() =>
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new Twig.Commands.EditorNotFoundException(), stderr);
            code.ShouldBe(1);
            stderr.ToString().ShouldContain("No editor found");
        });

    [Fact]
    public void Handle_WorkspaceNotFoundException_SetsExitCode1AndWritesClearMessage() =>
        WithExitCodeRestored(() =>
        {
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(new Twig.Infrastructure.Config.WorkspaceNotFoundException(), stderr);
            code.ShouldBe(1);
            Environment.ExitCode.ShouldBe(1);
            var output = stderr.ToString();
            output.ShouldContain("No twig workspace found");
            output.ShouldContain("twig init");
            // Should NOT say "Cache corrupted"
            output.ShouldNotContain("Cache corrupted");
        });

    [Fact]
    public void Handle_WorkspaceNotFoundException_NotMappedToCacheCorrupted() =>
        WithExitCodeRestored(() =>
        {
            // WorkspaceNotFoundException inherits from InvalidOperationException —
            // ensure it's caught BEFORE the cache-corruption handler
            var stderr = new StringWriter();
            ExceptionHandler.Handle(new Twig.Infrastructure.Config.WorkspaceNotFoundException(), stderr);
            stderr.ToString().ShouldNotContain("corrupted");
        });

    private static void WithExitCodeRestored(Action action)
    {
        var saved = Environment.ExitCode;
        try { action(); }
        finally { Environment.ExitCode = saved; }
    }
}
