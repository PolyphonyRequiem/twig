using System.Reflection;
using Xunit.Sdk;

namespace Twig.Cli.Tests.TestSupport;

/// <summary>
/// Instrumentation for twig#311 — the Cli suite intermittently aborts at the 300 s
/// vstest session timeout while printing a false-green <c>Passed!</c> summary.
///
/// The abort point is non-deterministic (observed at 2377 / 2834 / 737 tests), and
/// vstest's console output does NOT name the test that was in flight when the session
/// was killed, so nothing currently identifies the suspect.
///
/// This attribute is applied ONCE at assembly level (see AssemblyAttributes.cs). xUnit
/// collects <see cref="BeforeAfterTestAttribute"/>s from the method, the class, AND the
/// assembly, so a single declaration wraps every test in the suite.
///
/// It writes one flushed line per boundary to the file named by the
/// <c>TWIG_TEST_TRACE</c> environment variable. Opt-in by env var so a normal run pays
/// nothing: when the variable is unset the hooks return immediately.
///
/// Reading the output: the LAST <c>START</c> line with no matching <c>END</c> is the test
/// that was in flight when the host died. <c>tools/find-hung-test.sh</c> does that
/// reconciliation.
///
/// The file is opened append-only per write and flushed immediately, because the failure
/// mode being diagnosed is a hard kill of the test host — anything buffered in process
/// memory at that moment is lost, which is precisely the data we need.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class TestProgressTraceAttribute : BeforeAfterTestAttribute
{
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("TWIG_TEST_TRACE");

    private static readonly Lock Gate = new();

    public override void Before(MethodInfo methodUnderTest) =>
        Write("START", methodUnderTest);

    public override void After(MethodInfo methodUnderTest) =>
        Write("END", methodUnderTest);

    private static void Write(string boundary, MethodInfo method)
    {
        if (string.IsNullOrWhiteSpace(TracePath))
            return;

        // Parallelization is disabled assembly-wide, but the lock costs nothing and
        // keeps the trace valid if that ever changes.
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O}\t{boundary}\t{method.DeclaringType?.FullName}.{method.Name}{Environment.NewLine}");

        lock (Gate)
        {
            try
            {
                File.AppendAllText(TracePath, line);
            }
            catch (IOException)
            {
                // Instrumentation must never fail a test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
