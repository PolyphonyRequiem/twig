using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for corruption recovery ExceptionHandler path (FM-008):
/// SqliteException shows corruption message with recovery hint.
/// </summary>
public class CorruptionExceptionTests
{
    [Fact]
    public void ExceptionHandler_SqliteException_ShowsCorruptionMessage()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new SqliteException("database disk image is malformed", 11);
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("Cache corrupted");
            stderr.ToString().ShouldContain("twig init --force");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_WrappedSqliteException_ShowsCorruptionMessage()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            // I-003: SqliteCacheStore wraps SqliteException in InvalidOperationException
            var inner = new SqliteException("database disk image is malformed", 11);
            var ex = new InvalidOperationException("Cache corrupted", inner);
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("Cache corrupted");
            stderr.ToString().ShouldContain("twig init --force");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_AdoNotFoundException_ShowsWorkItemMessage()
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
            stderr.ToString().ShouldContain("#42");
            stderr.ToString().ShouldContain("not found");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_AdoNotFoundException_NullId_ShowsGenericMessage()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoNotFoundException(null);
            var code = ExceptionHandler.Handle(ex);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("not found");
        }
        finally
        {
            Console.SetError(savedErr);
            Environment.ExitCode = savedExitCode;
        }
    }
}
