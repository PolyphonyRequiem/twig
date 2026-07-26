using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Regression tests for PolyphonyRequiem/twig#271 — <c>ExceptionHandler</c> must branch on
/// <see cref="SqliteException.SqliteErrorCode"/> rather than reporting every SQLite failure as
/// cache corruption.
/// <para>
/// The destructive `twig init --force` hint must appear ONLY for codes that mean the database is
/// genuinely unreadable (11 SQLITE_CORRUPT, 26 SQLITE_NOTADB). Recommending it for anything else
/// tells the user to destroy a healthy cache — and every staged note in it — to work around what
/// is usually a twig logic bug. That is exactly what happened in #268.
/// </para>
/// </summary>
public class SqliteErrorMappingTests
{
    private static string Handle(Exception ex)
    {
        var savedExitCode = Environment.ExitCode;
        try
        {
            var stderr = new StringWriter();
            ExceptionHandler.Handle(ex, stderr).ShouldBe(1);
            return stderr.ToString();
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    // ── The bug: a constraint violation is not corruption ──────────────────────────────

    /// <summary>
    /// The #268 scenario verbatim: a FOREIGN KEY violation must NOT claim corruption and must NOT
    /// recommend the destructive rebuild.
    /// </summary>
    [Fact]
    public void ConstraintViolation_DoesNotClaimCorruption_OrRecommendDestructiveRebuild()
    {
        var output = Handle(new SqliteException("FOREIGN KEY constraint failed", 19));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("constraint violation");
        output.ShouldContain("bug in twig");
        output.ShouldContain("data is intact");
    }

    /// <summary>
    /// Microsoft.Data.Sqlite surfaces EXTENDED codes — a real FK violation arrives as 787, not 19.
    /// If the handler compared the raw value it would fall through to the unknown-code branch and
    /// the #268 message would still be wrong. The low 8 bits carry the primary code.
    /// </summary>
    [Fact]
    public void ConstraintViolation_ExtendedCode787_IsTreatedAsConstraint()
    {
        var output = Handle(new SqliteException("FOREIGN KEY constraint failed", 787));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("constraint violation");
    }

    // ── Genuine corruption keeps the rebuild hint ──────────────────────────────────────

    [Theory]
    [InlineData(11)] // SQLITE_CORRUPT
    [InlineData(26)] // SQLITE_NOTADB
    public void GenuineCorruption_KeepsTheRebuildHint(int code)
    {
        var output = Handle(new SqliteException("database disk image is malformed", code));

        output.ShouldContain("Cache corrupted");
        output.ShouldContain("twig init --force");
    }

    // ── Everything else: actionable, non-destructive ───────────────────────────────────

    [Theory]
    [InlineData(5)]  // SQLITE_BUSY
    [InlineData(6)]  // SQLITE_LOCKED
    public void LockedDatabase_SuggestsClosingOtherProcesses(int code)
    {
        var output = Handle(new SqliteException("database is locked", code));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("locked by another process");
        output.ShouldContain("twig-mcp");
    }

    [Theory]
    [InlineData(8)]  // SQLITE_READONLY
    [InlineData(14)] // SQLITE_CANTOPEN
    public void PermissionProblem_SuggestsCheckingPermissions(int code)
    {
        var output = Handle(new SqliteException("attempt to write a readonly database", code));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("permissions");
    }

    [Fact]
    public void DiskFull_SuggestsFreeingSpace()
    {
        var output = Handle(new SqliteException("database or disk is full", 13));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("disk space");
    }

    /// <summary>An unrecognised code must report itself honestly, not guess corruption.</summary>
    [Fact]
    public void UnknownCode_ReportsTheCodeInsteadOfGuessingCorruption()
    {
        var output = Handle(new SqliteException("something unusual", 999));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("999");
    }

    // ── Wrapped exceptions take the same path ─────────────────────────────────────────

    /// <summary>
    /// `SqliteCacheStore` wraps open-time failures in `InvalidOperationException` (I-003). The
    /// handler must unwrap and branch on the INNER code, otherwise a locked database at startup
    /// still gets the corruption message.
    /// </summary>
    [Fact]
    public void WrappedConstraintViolation_IsUnwrappedAndMappedByCode()
    {
        var inner = new SqliteException("FOREIGN KEY constraint failed", 19);
        var output = Handle(new InvalidOperationException("Failed to open the twig cache", inner));

        output.ShouldNotContain("Cache corrupted");
        output.ShouldNotContain("--force");
        output.ShouldContain("constraint violation");
    }

    [Fact]
    public void WrappedCorruption_StillReportsCorruption()
    {
        var inner = new SqliteException("database disk image is malformed", 11);
        var output = Handle(new InvalidOperationException("Failed to open the twig cache", inner));

        output.ShouldContain("Cache corrupted");
        output.ShouldContain("twig init --force");
    }
}
