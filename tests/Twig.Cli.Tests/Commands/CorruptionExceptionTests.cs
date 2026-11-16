using Microsoft.Data.Sqlite;
using Shouldly;
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
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new SqliteException("database disk image is malformed", 11);
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(ex, stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("Database is unreadable");
            stderr.ToString().ShouldContain("fresh isolated Core workspace");
            stderr.ToString().ShouldNotContain("twig init --force");
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public void ExceptionHandler_WrappedSqliteException_ShowsCorruptionMessage()
    {
        var savedExitCode = Environment.ExitCode;
        try
        {
            // I-003: SqliteCacheStore wraps SqliteException in InvalidOperationException
            var inner = new SqliteException("database disk image is malformed", 11);
            var ex = new InvalidOperationException("Cache corrupted", inner);
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(ex, stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("Database is unreadable");
            stderr.ToString().ShouldContain("fresh isolated Core workspace");
            stderr.ToString().ShouldNotContain("twig init --force");
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

}
