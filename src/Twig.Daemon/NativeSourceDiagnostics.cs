using System.Security.Cryptography;
using System.Text.Json;
using Bench.Application;
using Twig.Core;

namespace Bench.Server;

internal enum NativeDiagnosticArea { Items, Planning, Sources }

internal sealed class NativeSourceDiagnostics(ILogger logger)
{
    internal const int MaximumSources = 64;
    internal const int MaximumEntries = 256;
    internal const int MaximumDiagnosticCount = 128;
    private readonly Dictionary<Guid, string?[,]> fingerprints = [];
    private readonly Queue<Guid> sourceOrder = [];
    private int entries;

    internal int RetainedSources { get { lock (fingerprints) return fingerprints.Count; } }

    internal void Report(Guid source, NativeDiagnosticArea area, IEnumerable<string> diagnostics, ErrorCode? errorCode = null,
        ObservationMode? mode = null)
    {
        var values = diagnostics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var code = errorCode?.ToString() ?? "None";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values.Prepend(code).ToArray(), MappingJson.Default.StringArray);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        lock (fingerprints)
        {
            if (!fingerprints.TryGetValue(source, out var previous))
            {
                if (values.Length == 0 && errorCode is null) return;
                if (fingerprints.Count == MaximumSources) fingerprints.Remove(sourceOrder.Dequeue());
                fingerprints.Add(source, previous = new string?[Enum.GetValues<NativeDiagnosticArea>().Length,
                    Enum.GetValues<ObservationMode>().Length + 1]);
                sourceOrder.Enqueue(source);
            }
            var context = mode is { } observedMode ? (int)observedMode + 1 : 0;
            if (previous[(int)area, context] == fingerprint) return;
            previous[(int)area, context] = fingerprint;
            if (values.Length == 0 && errorCode is null || entries == MaximumEntries) return;
            entries++;
            if (entries == MaximumEntries)
            {
                logger.LogWarning("Native source diagnostic summary limit reached; further summaries are suppressed for this process. Native evidence is retained.");
                return;
            }
            var count = Math.Min(values.Length, MaximumDiagnosticCount);
            logger.LogWarning("Native source diagnostics: count={DiagnosticCount}, truncated={TruncatedCount}, errorCode={ErrorCode}.",
                count, values.Length - count, code);
        }
    }
}
