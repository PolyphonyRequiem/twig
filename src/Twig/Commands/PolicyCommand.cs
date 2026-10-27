using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Policy;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Policy;
using Twig.Infrastructure.Serialization;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Explicit cached compliance assessment and proposal authoring, never tracker mutation.</summary>
internal sealed class PolicyCommand(
    PolicyReconciliationService reconciliation,
    TwigPaths paths,
    TwigConfiguration config,
    RendererFactory renderers,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stdout = null,
    TextWriter? stderr = null)
{
    public Task<int> AssessAsync(string ids, string outputFormat, CancellationToken ct = default) =>
        ExecuteAsync(ids, null, outputFormat, false, ct);

    public Task<int> ReconcileAsync(string ids, string evidence, string finding, string strategy,
        int? parent, string outputFormat, bool proposalOnly = false, CancellationToken ct = default) =>
        ExecuteAsync(ids, new(evidence, finding, strategy, parent), outputFormat, proposalOnly, ct);

    private async Task<int> ExecuteAsync(string ids, PolicyRepairSelection? selection, string outputFormat,
        bool proposalOnly, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var output = stdout ?? Console.Out;
        var error = stderr ?? Console.Error;
        try
        {
            var format = OutputFormats.Normalize(outputFormat);
            if (format is not ("human" or "minimal" or "json" or "json-full" or "json-compact"))
                throw new ArgumentException("Policy assessment supports human, json and minimal output, not ids.");
            if (proposalOnly && format is not ("json" or "json-full" or "json-compact"))
                throw new ArgumentException("--proposal-only requires JSON output (-o json).");
            var scope = ParseIds(ids);
            if (selection is not null && (string.IsNullOrWhiteSpace(selection.EvidenceDigest)
                || string.IsNullOrWhiteSpace(selection.FindingId) || string.IsNullOrWhiteSpace(selection.StrategyId)))
                throw new ArgumentException("Reconciliation requires --evidence, --finding and --strategy from a current assessment.");
            var loaded = await new DeclaredPolicyLoader(paths.PolicyPath).LoadAsync(ct);
            if (loaded.Failure is { } failure)
                throw new InvalidOperationException(failure.Reason);
            var policy = loaded.Policy ?? throw new InvalidOperationException(
                "No twig.policy.json is declared. No compliance claim can be made; declare the repository's rules first.");
            if (policy.Rules.Count == 0)
                throw new InvalidOperationException("The declared policy contains no rules. No compliance claim can be made.");
            if (selection is null)
            {
                var result = await reconciliation.AssessAsync(policy, scope, ct);
                RenderAssessment(result, format, output);
                return exit = result.Compliant ? 0 : 1;
            }
            if (string.IsNullOrWhiteSpace(config.Organization) || string.IsNullOrWhiteSpace(config.Project))
                throw new InvalidOperationException("A configured connection is required to author a native proposal.");
            var repair = await reconciliation.ProposeAsync(policy, scope, selection,
                new PlanWorkspace { Organization = config.Organization, Project = config.Project }, ct);
            if (repair.CanPropose)
            {
                var parsed = new PlanDocumentParser().Parse(PlanDocumentWriter.Write(repair.Proposal!));
                if (!parsed.IsValid || parsed.CanonicalJson is null)
                    throw new InvalidOperationException("The repair did not produce a valid native proposal: "
                        + string.Join("; ", parsed.Issues.Select(i => i.Message)));
                if (proposalOnly)
                    output.WriteLine(parsed.CanonicalJson);
                else if (format is "json" or "json-full" or "json-compact")
                {
                    // Write the existing versioned proposal shape, not polymorphic reflection JSON.
                    using var document = JsonDocument.Parse(parsed.CanonicalJson);
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject();
                        writer.WriteBoolean("canPropose", true);
                        writer.WriteString("digest", parsed.Digest);
                        writer.WriteString("verification", "projected-only; refresh and reassess after apply");
                        writer.WritePropertyName("before");
                        JsonSerializer.Serialize(writer, repair.Before, TwigJsonContext.Default.PolicyAssessment);
                        writer.WritePropertyName("projected");
                        JsonSerializer.Serialize(writer, repair.Projected, TwigJsonContext.Default.PolicyAssessment);
                        writer.WritePropertyName("proposal");
                        document.RootElement.WriteTo(writer);
                        writer.WriteEndObject();
                    }
                    output.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
                }
                else
                {
                    RenderAssessment(repair.Projected!, format, output);
                    output.WriteLine(parsed.CanonicalJson);
                    output.WriteLine($"Proposal digest: {parsed.Digest}. Not applied or authorized.");
                    output.WriteLine("Save the proposal JSON, then use twig proposal preview/apply with the reviewed digest and authorization. Refresh and reassess after apply.");
                }
                return exit = 0;
            }
            if (!proposalOnly && format is ("json" or "json-full" or "json-compact"))
            {
                output.WriteLine(JsonSerializer.Serialize(repair, TwigJsonContext.Default.PolicyRepairResult));
            }
            else if (!proposalOnly)
                RenderAssessment(repair.Projected ?? repair.Before, format, output);
            CommandError.Write(renderers, error, outputFormat, repair.Error ?? "Repair could not be proposed.");
            return exit;
        }
        catch (ArgumentException ex)
        {
            CommandError.Write(renderers, error, outputFormat, ex.Message);
            return exit = 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CommandError.Write(renderers, error, outputFormat, ex.Message);
            return exit;
        }
        finally
        {
            TelemetryHelper.TrackCommand(telemetryClient, selection is null ? "policy assess" : "policy reconcile",
                outputFormat, exit, started);
        }
    }

    private static int[] ParseIds(string ids)
    {
        var tokens = ids.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length is 0 or > PolicyReconciliationService.MaxScope
            || tokens.Any(token => !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0))
            throw new ArgumentException($"--ids requires 1 to {PolicyReconciliationService.MaxScope} comma-separated positive IDs.");
        return tokens.Select(token => int.Parse(token, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
    }

    private static void RenderAssessment(PolicyAssessment result, string format, TextWriter output)
    {
        if (format is "json" or "json-full" or "json-compact")
        {
            output.WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.PolicyAssessment));
            return;
        }
        output.WriteLine(format == "minimal"
            ? $"{result.Status}\t{result.EvidenceDigest}\t{string.Join(',', result.Scope)}\tcached\tdependencies:{string.Join(',', result.DependencyScope)}"
            : $"Policy: {result.Status}; scope: {string.Join(", ", result.Scope)} (cached, not a whole-board or live compliance claim). Evidence: {result.EvidenceDigest}");
        if (format == "human")
            output.WriteLine($"Dependency scope (incident cached observations): {string.Join(", ", result.DependencyScope)}");
        foreach (var finding in result.Findings)
        {
            output.WriteLine($"{finding.Id}\t{finding.RuleId}\t{finding.Code}\t#{finding.WorkItemId}\t{finding.Reason}");
            if (format == "human")
            {
                var evidence = finding.Evidence;
                if (evidence.ParentId is { } parent)
                    output.WriteLine($"  Parent: #{parent} ({evidence.ParentType}); item type: {evidence.ItemType}; allowed: {string.Join(", ", evidence.AllowedValues)}");
                if (evidence.Edges.Count > 0)
                    output.WriteLine($"  Dependencies: {string.Join(", ", evidence.Edges.Select(e => $"#{e.From} -> #{e.To}"))}");
                if (evidence.MissingIds.Count > 0)
                    output.WriteLine($"  Missing evidence: {string.Join(", ", evidence.MissingIds)}");
            }
            foreach (var strategy in finding.Strategies)
                output.WriteLine($"  {strategy.Id}\t{strategy.Description}");
            if (finding.StrategiesTruncated)
                output.WriteLine($"  Alternatives limited to {PolicyReconciliationService.MaxStrategies}; not an exhaustive choice list.");
        }
        if (format == "human")
            foreach (var limit in result.EvidenceLimits)
                output.WriteLine(limit);
    }
}
