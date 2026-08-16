using System.Reflection;
using Xunit.Sdk;

namespace Twig.TestSupport;

/// <summary>
/// Instrumentation for GitHub issue #311 — a test run intermittently aborts at the
/// 300 s vstest <c>TestSessionTimeout</c> while printing a false-green <c>Passed!</c>
/// summary on a truncated total.
///
/// <para>
/// 🔴 <b>This file is LINK-COMPILED into every test assembly</b> (see
/// <c>Directory.Build.props</c> under <c>tests/</c>). It is deliberately NOT hosted in
/// <c>Twig.TestKit</c>: TestKit carries no xunit reference, and adding one would push
/// xunit into the dependency graph of every consumer — while still failing to reach
/// <c>Twig.RenderTree.Tests</c>, which does not reference TestKit at all. Link-compiling
/// a source file changes no project's package graph and reaches all six assemblies.
/// </para>
///
/// <para>
/// <b>Why it is no longer one file.</b> The original hook (Cli-only) wrote every boundary
/// to the single path named by <c>TWIG_TEST_TRACE</c>, guarded by an in-process
/// <see cref="Lock"/>. That was sound while exactly one assembly was instrumented and each
/// suite ran in its own <c>dotnet test</c> invocation. CI runs ONE invocation across six
/// assemblies <b>in parallel processes</b> — verified in the 2026-08-14 capture, where all
/// six hosts started within 9 s of each other. An in-process lock does nothing across
/// process boundaries, so six hosts appending to one file would interleave mid-line and
/// corrupt the START/END reconciliation that is the entire point of the trace.
/// </para>
///
/// <para>
/// So <c>TWIG_TEST_TRACE</c> now names a <b>directory</b>, and each assembly writes
/// <c>&lt;assembly-name&gt;.tsv</c> within it. Reconciliation is per-file, which is also
/// what makes the trace able to answer the question the CI capture raised: <i>which
/// assembly stalled</i>. A single merged file could not.
/// </para>
///
/// <para>
/// Opt-in by environment variable so a normal run pays nothing: with the variable unset,
/// both hooks return immediately after one null check.
/// </para>
///
/// <para>
/// Reading the output: the LAST <c>START</c> line with no matching <c>END</c> is the test
/// that was in flight when the host died. <c>tools/find-hung-test.sh</c> does that
/// reconciliation across every per-assembly file. All six captures to date show NO test in
/// flight — every START had an END — which is why the stall is understood to be at the
/// dispatch boundary rather than inside a test body.
/// </para>
///
/// <para>
/// The file is opened append-only per write and flushed immediately, because the failure
/// mode being diagnosed is a hard kill of the test host — anything buffered in process
/// memory at that moment is lost, which is precisely the data we need.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class TestProgressTraceAttribute : BeforeAfterTestAttribute
{
    /// <summary>
    /// Directory named by <c>TWIG_TEST_TRACE</c>, or null when tracing is off.
    /// </summary>
    private static readonly string? TraceDir =
        Environment.GetEnvironmentVariable("TWIG_TEST_TRACE");

    private static readonly Lock Gate = new();

    /// <summary>
    /// Resolved once per process. Each assembly gets its own file, keyed by the assembly
    /// under test, so parallel hosts cannot interleave into one another's trace.
    /// </summary>
    private static readonly string? TracePath = ResolveTracePath();

    private static string? ResolveTracePath()
    {
        if (string.IsNullOrWhiteSpace(TraceDir))
            return null;

        try
        {
            Directory.CreateDirectory(TraceDir);

            // Assembly.GetEntryAssembly() is the test host under vstest, not the suite.
            // The declaring assembly of this attribute instance is the linked copy compiled
            // INTO each suite, so it names the suite correctly in every assembly.
            var name = typeof(TestProgressTraceAttribute).Assembly.GetName().Name ?? "unknown";
            return Path.Combine(TraceDir, $"{name}.tsv");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override void Before(MethodInfo methodUnderTest) =>
        Write("START", methodUnderTest);

    public override void After(MethodInfo methodUnderTest) =>
        Write("END", methodUnderTest);

    private static void Write(string boundary, MethodInfo method)
    {
        if (string.IsNullOrWhiteSpace(TracePath))
            return;

        // Parallelization is disabled in some assemblies and not others; the lock costs
        // nothing and keeps each per-assembly file valid regardless.
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
