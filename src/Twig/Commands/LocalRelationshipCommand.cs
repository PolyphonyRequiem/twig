using System.Diagnostics;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Manages explicit Twig-local Related links by provider-neutral local identity.</summary>
internal sealed class LocalRelationshipCommand(
    ILocalWorkItemStore localWorkItems,
    LocalRelationshipService relationships,
    Func<ProviderCapabilityDescriptor> capabilityDescriptor,
    RendererFactory rendererFactory,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderrWriter = null)
{
    private readonly TextWriter _stderrWriter = stderrWriter ?? Console.Error;

    public Task<int> AddRelatedAsync(
        string? sourceText,
        string? targetText,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default) =>
        RunAsync("link local related", outputFormat, async () =>
        {
            if (!TryReadIds(sourceText, targetText, outputFormat, out var source, out var target))
                return 2;

            if (!await IsLiveAsync(source, outputFormat, ct))
                return 1;
            if (!await IsLiveAsync(target, outputFormat, ct))
                return 1;

            var result = await relationships.AddLocalOnlyAsync(
                capabilityDescriptor(), source, target, LinkKind.Related, ct);
            if (result.Status is LocalRelationshipStatus.Added or LocalRelationshipStatus.AlreadyPresent)
            {
                var status = result.Status == LocalRelationshipStatus.Added ? "added" : "already-present";
                var message = result.Status == LocalRelationshipStatus.Added
                    ? $"Added Twig-local Related link between {source} and {target}."
                    : $"Twig-local Related link between {source} and {target} already exists.";
                WriteMutation(outputFormat, "localRelationshipAdded", status, message, result.Relationship!);
                return 0;
            }

            WriteFailure(outputFormat, result, source, target, adding: true);
            return 1;
        });

    public Task<int> UnrelateAsync(
        string? sourceText,
        string? targetText,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default) =>
        RunAsync("link local unrelate", outputFormat, async () =>
        {
            if (!TryReadIds(sourceText, targetText, outputFormat, out var source, out var target))
                return 2;

            var result = await relationships.RemoveLocalOnlyAsync(source, target, LinkKind.Related, ct);
            if (result.Status == LocalRelationshipStatus.Removed)
            {
                var message = $"Removed Twig-local Related link between {source} and {target}.";
                WriteMutation(outputFormat, "localRelationshipRemoved", "removed", message, result.Relationship!);
                return 0;
            }
            if (result.Status == LocalRelationshipStatus.NotPresent)
            {
                WriteError(outputFormat, $"No Twig-local Related link exists between {source} and {target}.");
                return 1;
            }

            WriteFailure(outputFormat, result, source, target, adding: false);
            return 1;
        });

    public Task<int> ListAsync(
        string? localIdText,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default) =>
        RunAsync("link local list", outputFormat, async () =>
        {
            if (!SupportsOutput(outputFormat))
            {
                WriteError(outputFormat, "Output format must be human, json, or minimal.");
                return 2;
            }
            if (!TryParseId(localIdText, "local ID", outputFormat, out var localId))
                return 2;

            var edges = await relationships.GetLocalOnlyRelatedAsync(localId, ct);
            WriteList(outputFormat, localId, edges);
            return 0;
        });

    private async Task<bool> IsLiveAsync(LocalWorkItemId id, string outputFormat, CancellationToken ct)
    {
        var item = await localWorkItems.GetAsync(id, ct);
        if (item is { IsDeleted: false })
            return true;

        WriteError(outputFormat, item is null
            ? $"Local work item {id} does not exist."
            : $"Local work item {id} is deleted.");
        return false;
    }

    private bool TryReadIds(
        string? sourceText,
        string? targetText,
        string outputFormat,
        out LocalWorkItemId source,
        out LocalWorkItemId target)
    {
        source = default;
        target = default;
        if (!SupportsOutput(outputFormat))
        {
            WriteError(outputFormat, "Output format must be human, json, or minimal.");
            return false;
        }
        if (!TryParseId(sourceText, "source local ID", outputFormat, out source)
            || !TryParseId(targetText, "target local ID", outputFormat, out target))
            return false;
        return true;
    }

    private bool TryParseId(string? value, string label, string outputFormat, out LocalWorkItemId id)
    {
        if (value is { Length: 36 }
            && value[14] == '7'
            && value[19] is '8' or '9' or 'a' or 'b'
            && LocalWorkItemId.TryParse(value, out id))
            return true;

        id = default;
        WriteError(outputFormat, $"The {label} must be a canonical lowercase GUIDv7 identity.");
        return false;
    }

    private static bool SupportsOutput(string outputFormat)
        => OutputFormats.Normalize(outputFormat) is "human" or "minimal" or "json" or "json-full" or "json-compact";

    private void WriteFailure(
        string outputFormat,
        LocalRelationshipResult result,
        LocalWorkItemId source,
        LocalWorkItemId target,
        bool adding)
    {
        var message = result.Status switch
        {
            LocalRelationshipStatus.NotLocalOnly =>
                $"The provider must declare Related unsupported before creating an explicit Twig-local link. Nothing was changed ({result.ReasonCode ?? "relationship-local-only-requires-unsupported"}).",
            LocalRelationshipStatus.StorageMismatch =>
                $"A Related edge between {source} and {target} exists under a different storage route; nothing was changed.",
            LocalRelationshipStatus.Invalid when result.ReasonCode == LocalRelationshipService.SelfEdgeReason =>
                "A work item cannot have a Related link to itself.",
            LocalRelationshipStatus.Invalid =>
                $"The local relationship is invalid ({result.ReasonCode ?? "relationship-invalid"}).",
            LocalRelationshipStatus.Conflict =>
                "The local relationship changed concurrently; no stale write was applied.",
            _ => adding
                ? "The explicit Twig-local Related link was not created."
                : "The Twig-local Related link was not removed.",
        };
        WriteError(outputFormat, message);
    }

    private void WriteMutation(
        string outputFormat,
        string recordKind,
        string status,
        string message,
        LocalRelationship relationship)
    {
        var format = OutputFormats.Normalize(outputFormat);
        if (format is "json" or "json-full" or "json-compact")
        {
            var fields = RelationshipFields(relationship);
            fields["status"] = RenderCell.String(status);
            fields["message"] = RenderCell.String(message);
            Render(new RenderNode.Record(recordKind, fields), outputFormat);
            return;
        }

        Render(new RenderNode.Text(message, Severity.Success), outputFormat);
    }

    private void WriteList(
        string outputFormat,
        LocalWorkItemId localId,
        IReadOnlyList<LocalRelationship> edges)
    {
        var format = OutputFormats.Normalize(outputFormat);
        if (format is "json" or "json-full" or "json-compact")
        {
            var rows = edges.Select(edge => RelationshipCell(edge)).ToArray();
            Render(new RenderNode.Record("localRelationships", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["localId"] = RenderCell.String(localId.ToString()),
                ["localRelationships"] = new RenderCell(
                    string.Join(Environment.NewLine, edges.Select(Describe)),
                    new RenderValue.Array(rows)),
            }), outputFormat);
            return;
        }

        if (format == "minimal")
        {
            Render(new RenderTree.RenderTree(edges.Select(edge => new RenderNode.Text(
                $"{edge.Source}\t{edge.Target}\trelated\ttwig-local\tlocal-only\tnot-applicable")).ToArray()), outputFormat);
            return;
        }

        var lines = new List<RenderNode>
        {
            new RenderNode.Text($"Twig-local Related links for {localId}:"),
        };
        lines.AddRange(edges.Count == 0
            ? [new RenderNode.Text("No Twig-local Related links.")]
            : edges.Select(edge => new RenderNode.Text($"  {Describe(edge)}")));
        Render(new RenderTree.RenderTree(lines), outputFormat);
    }

    private static Dictionary<string, RenderCell> RelationshipFields(LocalRelationship relationship) =>
        new(StringComparer.Ordinal)
        {
            ["relationship"] = RelationshipCell(relationship),
        };

    private static RenderCell RelationshipCell(LocalRelationship relationship)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["kind"] = RenderCell.String(LinkKindNames.ToStableName(relationship.Kind)),
            ["source"] = RenderCell.String(relationship.Source.ToString()),
            ["target"] = RenderCell.String(relationship.Target.ToString()),
            ["origin"] = RenderCell.String("twig-local"),
            ["storage"] = RenderCell.String("local-only"),
            ["publication"] = RenderCell.String("not-applicable"),
        };
        return new RenderCell(Describe(relationship), new RenderValue.Object(fields));
    }

    private static string Describe(LocalRelationship relationship)
        => $"{relationship.Source} --related-- {relationship.Target} (twig-local; local-only; not-applicable)";

    private void WriteError(string outputFormat, string message)
        => CommandError.Write(rendererFactory, _stderrWriter, outputFormat, message);

    private void Render(RenderNode node, string outputFormat)
        => rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree([node]));

    private void Render(RenderTree.RenderTree tree, string outputFormat)
        => rendererFactory.GetRenderer(outputFormat).Render(tree);

    private async Task<int> RunAsync(
        string command,
        string outputFormat,
        Func<Task<int>> action)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var exitCode = 1;
        try
        {
            exitCode = await action();
            return exitCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteError(outputFormat, $"Local relationship operation failed: {ex.Message}");
            return exitCode;
        }
        finally
        {
            TelemetryHelper.TrackCommand(telemetryClient, command, outputFormat, exitCode, startedAt);
        }
    }
}
