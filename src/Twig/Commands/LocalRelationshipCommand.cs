using Twig.Infrastructure.Telemetry;
using System.Diagnostics;
using System.Globalization;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Formatters;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Explicit local associations through the existing Core proposal and journal.</summary>
internal sealed class LocalRelationshipCommand(
    CoreWorkspaceFactory factory,
    RendererFactory rendererFactory,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderrWriter = null)
{
    private readonly TextWriter _stderr = stderrWriter ?? Console.Error;

    public Task<int> AddRelatedAsync(string? sourceText, string? targetText,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => MutateAsync(sourceText, targetText, true, outputFormat, ct);

    public Task<int> UnrelateAsync(string? sourceText, string? targetText,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => MutateAsync(sourceText, targetText, false, outputFormat, ct);

    private Task<int> MutateAsync(string? sourceText, string? targetText, bool adding, string output, CancellationToken ct)
        => RunAsync(adding ? "link local related" : "link local unrelate", output, async () =>
        {
            if (!SupportsOutput(output, false)) return Error(output, "Use human, json, json-full, json-compact or minimal output.", 2);
            if (!ValidTarget(sourceText) || !ValidTarget(targetText))
                return Error(output, "Use one explicit <target> and --id <source>, each a nonzero native number, negative seed alias or canonical lowercase GUIDv7.", 2);
            var opened = await factory.OpenAsync(ct);
            if (!opened.IsSuccess) return Error(output, opened.Error.Message);
            using var handle = opened.Value;
            var workspace = handle.Workspace;
            var source = await ResolveTargetAsync(workspace, sourceText!, true, ct);
            if (!source.IsSuccess) return Error(output, source.Error.Message);
            var target = await ResolveTargetAsync(workspace, targetText!, true, ct);
            if (!target.IsSuccess) return Error(output, target.Error.Message);
            var captured = await workspace.SnapshotAsync(ct);
            if (!captured.IsSuccess) return Error(output, captured.Error.Message);
            var state = captured.Value;
            var from = Logical(state, source.Value);
            var to = Logical(state, target.Value);
            if (from == to) return Error(output, "A work item cannot be related to itself.", 2);
            var prepared = await workspace.PrepareLocalRelationshipAsync(source.Value, target.Value, adding, ct, state.Version);
            if (!prepared.IsSuccess) return Error(output, prepared.Error.Message);
            var op = prepared.Value.Operations.Single();
            var applied = await workspace.ApplyAsync(prepared.Value, prepared.Value.Digest, ct);
            if (!applied.IsSuccess) return Error(output, applied.Error.Message);
            if (!applied.Value.EffectsVerified || applied.Value.Error is not null)
                return Error(output, applied.Value.Error?.Message ?? "The local CAS outcome was not Verified; inspect the retained journal before any retry.");
            var after = await workspace.SnapshotAsync(ct);
            if (!after.IsSuccess) return Error(output, after.Error.Message);
            var edge = after.Value.Relationships.Single(value => value.Origin == RelationshipOrigin.Local
                && value.Kind == op.Relation && Touches(after.Value, value, from, to));
            Render(new RenderNode.Record("localRelationship", new Dictionary<string, RenderCell>
            {
                ["status"] = RenderCell.String("Verified"),
                ["effect"] = RenderCell.String(adding ? "related" : "unrelated"),
                ["digest"] = RenderCell.String(prepared.Value.Digest),
                ["relationship"] = RelationshipCell(edge),
                ["verification"] = RenderCell.String("local CAS; no native tracker mutation"),
            }), output);
            return 0;
        });

    public Task<int> ListAsync(string? targetText, string? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => RunAsync("link local list", outputFormat, async () =>
        {
            if (!SupportsOutput(outputFormat, true)) return Error(outputFormat, "Use human, json, json-full, json-compact, minimal or ids output.", 2);
            if (targetText is not null && id is not null && targetText != id)
                return Error(outputFormat, "Conflicting targets: use a positional target or --id, not different targets.", 2);
            var text = targetText ?? id;
            if (!ValidTarget(text)) return Error(outputFormat, "List requires an explicit native number, negative seed alias or canonical lowercase GUIDv7.", 2);
            var opened = await factory.OpenAsync(ct);
            if (!opened.IsSuccess) return Error(outputFormat, opened.Error.Message);
            using var handle = opened.Value;
            var workspace = handle.Workspace;
            var target = await ResolveTargetAsync(workspace, text!, false, ct);
            if (!target.IsSuccess) return Error(outputFormat, target.Error.Message);
            var captured = await workspace.SnapshotAsync(ct);
            if (!captured.IsSuccess) return Error(outputFormat, captured.Error.Message);
            var state = captured.Value;
            var key = Logical(state, target.Value);
            var edges = state.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Local && !edge.Deleted
                && (Logical(state, edge.From) == key || Logical(state, edge.To) == key)).ToArray();
            if (edges.Where((edge, index) => edges.Take(index).Any(prior => prior.Kind == edge.Kind
                && Touches(state, prior, Logical(state, edge.From), Logical(state, edge.To)))).Any())
                return Error(outputFormat, "Conflicting logical local edges; no canonical row was selected.");
            if (OutputFormats.Normalize(outputFormat) == "ids")
            {
                var ids = edges.Select(edge =>
                {
                    var neighbor = Logical(state, Logical(state, edge.From) == key ? edge.To : edge.From);
                    if (neighbor.IsLocal && (!state.Drafts.Any(draft => draft.Key == neighbor && draft.Baseline is null)
                        || CoreHostContext.KnownLocalKeys(state).Count(value =>
                            CoreHostContext.LocalAlias(value) == CoreHostContext.LocalAlias(neighbor)) != 1))
                        return Result<int>.Fail(ErrorCode.Conflict, "The neighbor alias has no unique live seed evidence.");
                    return CoreWorkspace.DisplayId(workspace.Connection, state, neighbor);
                }).ToArray();
                if (ids.Any(result => !result.IsSuccess))
                    return Error(outputFormat, "Neighbor IDs are not uniquely cached; use json to retain explicit endpoint identity.", 2);
                Render(new RenderTree.RenderTree(ids.Select(result => new RenderNode.Record("neighbor",
                    new Dictionary<string, RenderCell> { ["id"] = RenderCell.Integer(result.Value) })).ToArray()), outputFormat);
            }
            else Render(new RenderNode.Record("localRelationships", new Dictionary<string, RenderCell>
            {
                ["target"] = RenderCell.String(text!),
                ["origin"] = RenderCell.String("LOCAL"),
                ["storage"] = RenderCell.String("local-only; non-native"),
                ["localRelationships"] = new RenderCell(string.Join(Environment.NewLine, edges.Select(Describe)),
                    new RenderValue.Array(edges.Select(RelationshipCell).ToArray())),
            }), outputFormat);
            return 0;
        });

    private static bool ValidTarget(string? text) => text is not null && (
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number != 0
        || text.Length == 36 && text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b'
            && Guid.TryParseExact(text, "D", out var guid) && guid.ToString("D") == text);

    private static async Task<Result<ItemKey>> ResolveTargetAsync(CoreWorkspace workspace, string text, bool nativeLookup, CancellationToken ct)
    {
        var stored = await workspace.SnapshotAsync(ct);
        if (!stored.IsSuccess) return Result<ItemKey>.Fail(stored.Error);
        ItemKey key;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var locator))
        {
            if (locator > 0) return await workspace.ResolveTargetAsync(locator, nativeLookup, ct);
            var matches = CoreHostContext.KnownLocalKeys(stored.Value).Where(value => CoreHostContext.LocalAlias(value) == locator).ToArray();
            if (matches.Length != 1) return Result<ItemKey>.Fail(ErrorCode.Conflict, "The seed alias is unknown or ambiguous.");
            key = matches[0];
        }
        else key = ItemKey.Local(Guid.ParseExact(text, "D"));
        var mapped = Logical(stored.Value, key);
        return stored.Value.Drafts.Any(draft => draft.Key == key && draft.Baseline is null)
            || mapped != key && mapped.BelongsTo(workspace.Connection) && stored.Value.Items.Any(item => item.Key == mapped)
            ? Result<ItemKey>.Ok(key)
            : Result<ItemKey>.Fail(ErrorCode.Unavailable, "The UUID has no live seed or original Verified publication with an observed native endpoint.");
    }

    private static ItemKey Logical(StoreSnapshot state, ItemKey key)
        => CoreWorkspace.LocalLogical(state, key);

    private static bool Touches(StoreSnapshot state, Relationship edge, ItemKey from, ItemKey to)
        => CoreWorkspace.LocalTouches(state, edge, from, to);

    private static bool SupportsOutput(string output, bool list)
        => OutputFormats.Normalize(output) is "human" or "minimal" or "json" or "json-full" or "json-compact"
            || list && OutputFormats.Normalize(output) == "ids";

    private static string Describe(Relationship edge)
        => $"{EndpointText(edge.From)} --{edge.Kind}-- {EndpointText(edge.To)} (LOCAL; local-only; non-native; version {edge.LocalVersion}; deleted {edge.Deleted})";

    private static string EndpointText(ItemKey key)
        => key.IsLocal ? key.LocalId.ToString("D") : key.NativeId!;

    private static RenderCell EndpointCell(ItemKey key)
        => new(EndpointText(key), new RenderValue.Object(key.IsLocal
            ? new Dictionary<string, RenderCell>
            {
                ["localId"] = RenderCell.String(key.LocalId.ToString("D")),
            }
            : new Dictionary<string, RenderCell>
            {
                ["provider"] = RenderCell.String(key.Provider!.Value),
                ["scope"] = RenderCell.String(key.ResourceScope!),
                ["nativeId"] = RenderCell.String(key.NativeId!),
            }));

    private static RenderCell RelationshipCell(Relationship edge)
        => new(Describe(edge), new RenderValue.Object(new Dictionary<string, RenderCell>
        {
            ["source"] = EndpointCell(edge.From),
            ["target"] = EndpointCell(edge.To),
            ["kind"] = RenderCell.String(edge.Kind),
            ["origin"] = RenderCell.String("LOCAL"),
            ["storage"] = RenderCell.String("local-only"),
            ["publication"] = RenderCell.String("non-native"),
            ["localVersion"] = RenderCell.Integer(edge.LocalVersion),
            ["deleted"] = RenderCell.Boolean(edge.Deleted),
        }));

    private int Error(string output, string message, int exit = 1)
    {
        CommandError.Write(rendererFactory, _stderr, output, message);
        return exit;
    }

    private void Render(RenderNode node, string output) => Render(new RenderTree.RenderTree([node]), output);
    private void Render(RenderTree.RenderTree tree, string output) => rendererFactory.GetRenderer(output).Render(tree);

    private async Task<int> RunAsync(string command, string output, Func<Task<int>> action)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try { return exit = await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return exit = Error(output, "Local relationship operation failed: " + ex.Message);
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetryClient, command, output, exit, started); }
            catch (Exception) { Debug.WriteLine("Optional local relationship telemetry failed; command outcome was preserved."); }
        }
    }
}
