using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Twig.AgentEfficiencyBaseline;

public sealed record Observation(string Id, string Classification, bool DesiredSatisfied,
    string Input, string Output, bool SafetyPassed = true);

internal sealed record MeasuredObservation(Observation Observation, int InputBytes, int InputCharacters,
    int OutputBytes, int OutputCharacters, int? Tokens);

internal sealed record BaselineReport(string Schema, string EvidenceKind, string SourceVersion,
    string SourceAssemblySha256, string Runtime, IReadOnlyList<MeasuredObservation> Cases,
    int FixtureInvocations, int LiveAdoCalls, int? OuterModelCalls, int? Tokens);

internal static class JsonData
{
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), BaselineJsonContext.Default);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BaselineReport))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(string))]
internal partial class BaselineJsonContext : JsonSerializerContext;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--require-fixed"))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/agent-efficiency-baseline -- <new-report.json> [--require-fixed]");
            return 2;
        }

        // Never overwrite a previous measurement, including the checked-in baseline.
        var outputPath = Path.GetFullPath(args[0]);
        if (File.Exists(outputPath))
        {
            Console.Error.WriteLine("Report already exists; choose a fresh path.");
            return 2;
        }

        var observations = new List<Observation>();
        observations.AddRange(await ReadCases.RunAsync());
        observations.AddRange(await WriteCases.RunAsync());
        if (observations.Count == 0 || observations.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != observations.Count)
            throw new InvalidOperationException("Baseline requires nonempty, uniquely identified observations.");
        foreach (var observation in observations)
        {
            // A truncated preview must never masquerade as complete machine evidence.
            using var input = JsonDocument.Parse(observation.Input);
            using var output = JsonDocument.Parse(observation.Output);
        }
        var assembly = typeof(Twig.Commands.ShowCommand).Assembly;
        var report = new BaselineReport(
            "twig.agent-efficiency-baseline.v1", "offline source fixtures; substitutes are not live ADO",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            observations.Select(o => new MeasuredObservation(o, Encoding.UTF8.GetByteCount(o.Input),
                o.Input.EnumerateRunes().Count(), Encoding.UTF8.GetByteCount(o.Output),
                o.Output.EnumerateRunes().Count(), null)).ToArray(),
            observations.Count, 0, null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(stream, report, BaselineJsonContext.Default.BaselineReport);

        var safetyFailures = observations.Count(o => !o.SafetyPassed);
        var unmet = observations.Count(o => !o.DesiredSatisfied);
        Console.WriteLine($"Baseline: {observations.Count} cases, {safetyFailures} safety failures, {unmet} unmet desired outcomes.");
        Console.WriteLine($"Complete inputs/results: {outputPath}");
        Console.WriteLine("A recorded defect is not a product PASS; --require-fixed fails on unmet desired outcomes.");
        return safetyFailures > 0 || (args.Length == 2 && unmet > 0) ? 1 : 0;
    }
}
