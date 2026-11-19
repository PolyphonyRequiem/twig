using Bench.Server;
using Microsoft.Extensions.Logging;
using Twig.Core;

namespace Twig.Daemon.Tests.Native;

internal static class NativeSourceDiagnosticsTests
{
    internal static void Run()
    {
        var logger = new Capture();
        var diagnostics = new NativeSourceDiagnostics(logger);
        var source = Guid.NewGuid();
        var values = Enumerable.Range(0, 113).Select(index =>
            $"technical-{index}: {{\"token\":\"SECRET_SENTINEL\",\"value\":\"ITEM_CONTENT_SENTINEL\"}}").ToArray();
        diagnostics.Report(source, NativeDiagnosticArea.Items, values);
        diagnostics.Report(source, NativeDiagnosticArea.Items, values.Reverse().Concat(values));
        Check(logger.Messages.Count == 1, "The same diagnostic set must not repeat on polling or reordering.");
        diagnostics.Report(source, NativeDiagnosticArea.Items, values.Append("changed technical set"));
        Check(logger.Messages.Count == 2, "A changed diagnostic set must be summarized.");
        diagnostics.Report(source, NativeDiagnosticArea.Items, []);
        diagnostics.Report(source, NativeDiagnosticArea.Items, values);
        Check(logger.Messages.Count == 3, "Resolved and reappearing diagnostics must be treated as a new change.");
        diagnostics.Report(Guid.NewGuid(), NativeDiagnosticArea.Items, values);
        Check(logger.Messages.Count == 4, "Source diagnostics must be deduplicated independently.");
        diagnostics.Report(source, NativeDiagnosticArea.Planning, Enumerable.Range(0, 1000).Select(index => "metadata-" + index), ErrorCode.Unavailable);
        Check(logger.Messages[^1].Contains("count=128, truncated=872, errorCode=Unavailable", StringComparison.Ordinal),
            "Oversized diagnostic sets must report capped counts and explicit truncation.");
        Check(logger.Messages.All(message => message.Length < 300 && !message.Contains("SECRET_SENTINEL", StringComparison.Ordinal)
            && !message.Contains("ITEM_CONTENT_SENTINEL", StringComparison.Ordinal) && !message.Contains("technical-", StringComparison.Ordinal)),
            "Only counts and safe enum codes may reach the local diagnostic logger.");
        for (var index = 0; index < 100; index++)
            diagnostics.Report(Guid.NewGuid(), NativeDiagnosticArea.Items, ["bounded source"]);
        Check(diagnostics.RetainedSources == NativeSourceDiagnostics.MaximumSources, "Retained source fingerprints must be bounded.");

        var cappedLogger = new Capture();
        var capped = new NativeSourceDiagnostics(cappedLogger);
        for (var index = 0; index < 1000; index++)
            capped.Report(source, NativeDiagnosticArea.Items, ["changed-" + index]);
        Check(cappedLogger.Messages.Count == NativeSourceDiagnostics.MaximumEntries
            && cappedLogger.Messages[^1].Contains("limit reached", StringComparison.Ordinal),
            "Changed diagnostic sets must not produce unlimited log entries.");
        var concurrentLogger = new Capture();
        var concurrent = new NativeSourceDiagnostics(concurrentLogger);
        Parallel.For(0, 20, _ => concurrent.Report(source, NativeDiagnosticArea.Items, values));
        Check(concurrentLogger.Messages.Count == 1, "Concurrent identical reads must produce one summary.");
        Console.WriteLine("PASS native summary-only diagnostics: per-source/set dedup, changes, safe counts, truncation, bounded memory/entries and concurrency");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Capture : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
