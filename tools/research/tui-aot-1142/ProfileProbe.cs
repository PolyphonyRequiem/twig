using System.Diagnostics;
using System.Text.Json;

namespace Twig.Infrastructure.Diagnostics;

// Research-only source injected into the isolated pinned snapshot, never production.
internal static class ProfileProbe
{
    private readonly record struct Observation(string Name, int Iteration, int Instance, double Milliseconds, long Value);
    private static readonly List<Observation> Rows = [];
    private static readonly object Sync = new();
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("TWIG_1142_PROBE") == "1";
    private static int _iteration;
    private static int _instance;

    internal static int NewInstance() => Interlocked.Increment(ref _instance);
    internal static void SetIteration(int iteration) => _iteration = iteration;
    internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void End(string name, long started, int instance = 0)
    {
        if (!Enabled) return;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (Sync) Rows.Add(new(name, _iteration, instance, elapsed, 0));
    }

    internal static void Dimension(string name, long value)
    {
        if (!Enabled) return;
        lock (Sync) Rows.Add(new(name, _iteration, 0, 0, value));
    }

    internal static int Repetitions()
    {
        var text = Environment.GetEnvironmentVariable("TWIG_1142_REPEAT");
        if (text is null) return 1;
        if (!int.TryParse(text, out var count) || count < 1 || count > 101)
            throw new ArgumentException("Research repetition count must be between 1 and 101.");
        return count;
    }

    internal static void Flush()
    {
        if (!Enabled) return;
        var output = Environment.GetEnvironmentVariable("TWIG_1142_PROBE_OUTPUT")
            ?? throw new InvalidOperationException("Research probe output is required.");
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        writer.WriteStartArray("observations");
        lock (Sync)
        {
            foreach (var row in Rows)
            {
                writer.WriteStartObject();
                writer.WriteString("name", row.Name);
                writer.WriteNumber("iteration", row.Iteration);
                writer.WriteNumber("instance", row.Instance);
                writer.WriteNumber("milliseconds", row.Milliseconds);
                writer.WriteNumber("value", row.Value);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
