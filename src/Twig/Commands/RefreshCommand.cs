using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig refresh</c>: builds the WIQL query, delegates fetch/save/conflict logic
/// to <see cref="RefreshOrchestrator"/>, then runs post-refresh metadata and UI updates.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// progress lines stream as Text/Hint nodes on human/minimal formats; on machine
/// formats (json/json-*) the streaming progress is suppressed and a single
/// "refreshComplete" Document is emitted at the end summarising item count,
/// conflicts, iterations, and (when present) the field-definition hash change.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error/warning
/// formatting that intermixes with the orchestrator's own writes.
/// </remarks>
public sealed class RefreshCommand(
    CommandContext ctx,
    IContextStore contextStore,
    IIterationService iterationService,
    TwigPaths paths,
    IProcessTypeStore processTypeStore,
    IFieldDefinitionStore fieldDefinitionStore,
    RefreshOrchestrator orchestrator,
    SprintIterationResolver sprintResolver,
    IGlobalProfileStore? globalProfileStore = null,
    IPromptStateWriter? promptStateWriter = null,
    RendererFactory? rendererFactory = null)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Refresh the local cache from Azure DevOps.</summary>
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("refresh", outputFormat);
        int exitCode;
        try
        {
            int itemCount;
            bool hashChanged;
            (exitCode, itemCount, hashChanged) = await ExecuteCoreAsync(outputFormat, force, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "refresh", outputFormat, exitCode, scope.StartTimestamp,
                extraProperties: new Dictionary<string, string> { ["hash_changed"] = hashChanged.ToString() },
                extraMetrics: new Dictionary<string, double> { ["item_count"] = itemCount });
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<(int ExitCode, int ItemCount, bool HashChanged)> ExecuteCoreAsync(string outputFormat, bool force, CancellationToken ct)
    {
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "ids";
        var telemetryItemCount = 0;
        var telemetryHashChanged = false;

        EmitHumanInfo("Refreshing from ADO...", isMachine);

        var sprintEntries = ctx.Config.Workspace.Sprints;
        var resolvedIterations = await ResolveSprintIterationsAsync(sprintEntries, ct);

        var iteration = resolvedIterations.Count > 0
            ? resolvedIterations[0]
            : await iterationService.GetCurrentIterationAsync(ct);

        if (resolvedIterations.Count > 0)
        {
            foreach (var ri in resolvedIterations)
                EmitHumanInfo($"  Iteration: {ri}", isMachine);
        }
        else
        {
            EmitHumanInfo($"  Iteration: {iteration}", isMachine);
        }

        var wiql = "SELECT [System.Id] FROM WorkItems";
        var whereClauses = new List<string>();

        if (resolvedIterations.Count > 0)
        {
            var iterationClauses = resolvedIterations
                .Select(ip => $"[System.IterationPath] = '{ip.Value.Replace("'", "''")}'");
            var joined = string.Join(" OR ", iterationClauses);
            whereClauses.Add(resolvedIterations.Count == 1 ? joined : $"({joined})");
        }

        var areaPathEntries = ctx.Config.Defaults?.AreaPathEntries;
        if (areaPathEntries is { Count: > 0 })
        {
            var clauses = areaPathEntries
                .Select(entry =>
                {
                    var escaped = entry.Path.Replace("'", "''");
                    var op = entry.IncludeChildren ? "UNDER" : "=";
                    return $"[System.AreaPath] {op} '{escaped}'";
                });
            whereClauses.Add(areaPathEntries.Count == 1
                ? clauses.First()
                : $"({string.Join(" OR ", clauses)})");
        }
        else
        {
            var areaPaths = ctx.Config.Defaults?.AreaPaths;
            if (areaPaths is null || areaPaths.Count == 0)
            {
                var singlePath = ctx.Config.Defaults?.AreaPath;
                if (!string.IsNullOrWhiteSpace(singlePath))
                    areaPaths = [singlePath];
            }

            if (areaPaths is { Count: > 0 })
            {
                var clauses = areaPaths
                    .Select(ap => $"[System.AreaPath] UNDER '{ap.Replace("'", "''")}'");
                whereClauses.Add(areaPaths.Count == 1
                    ? clauses.First()
                    : $"({string.Join(" OR ", clauses)})");
            }
        }

        if (whereClauses.Count > 0)
            wiql += $" WHERE {string.Join(" AND ", whereClauses)}";

        wiql += " ORDER BY [System.Id]";

        var fetchResult = await orchestrator.FetchItemsAsync(wiql, force, ct);

        if (fetchResult.ItemCount == 0)
        {
            EmitHumanInfo("  No items found in current iteration.", isMachine);
        }
        else
        {
            if (fetchResult.PhantomsCleansed > 0)
                _stderr.WriteLine(fmt.FormatInfo($"ℹ Cleansed {fetchResult.PhantomsCleansed} phantom dirty flag(s)"));

            if (fetchResult.Conflicts.Count > 0)
            {
                _stderr.WriteLine(fmt.FormatError("Warning: the following protected items have newer remote revisions (skipped):"));
                foreach (var c in fetchResult.Conflicts)
                    _stderr.WriteLine(fmt.FormatError($"  #{c.Id}: local rev {c.LocalRevision} → remote rev {c.RemoteRevision}"));
                _stderr.WriteLine(fmt.FormatError("Run 'twig sync' first, or use 'twig sync --force' to overwrite."));
            }

            telemetryItemCount = fetchResult.ItemCount;
            if (!isMachine)
                EmitHumanText($"Refreshed {fetchResult.ItemCount} item(s).", Severity.Success);
        }

        await orchestrator.SyncTrackedTreesAsync(ct);

        var policyStr = ctx.Config.Tracking.CleanupPolicy.Replace("-", "");
        if (Enum.TryParse<Twig.Domain.Enums.TrackingCleanupPolicy>(policyStr, ignoreCase: true, out var cleanupPolicy))
        {
            var removed = await orchestrator.ApplyCleanupPolicyAsync(cleanupPolicy, ct);
            if (removed > 0)
                _stderr.WriteLine(fmt.FormatInfo($"ℹ Auto-untracked {removed} item(s) per cleanup policy."));
        }

        await orchestrator.HydrateAncestorsAsync(ct);
        await orchestrator.SyncWorkingSetAsync(iteration, ct);

        string? detectedUserDisplayName = null;
        if (string.IsNullOrWhiteSpace(ctx.Config.User.DisplayName))
        {
            try
            {
                var displayName = await iterationService.GetAuthenticatedUserDisplayNameAsync();
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    ctx.Config.User.DisplayName = displayName;
                    detectedUserDisplayName = displayName;
                    EmitHumanInfo($"  User: {displayName}", isMachine);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _stderr.WriteLine(fmt.FormatInfo($"⚠ Could not detect user identity: {ex.Message}"));
            }
        }

        if (ctx.Config.IsLegacyMode)
            await ctx.Config.SaveAsync(paths.ConfigPath);
        else
            await ctx.Config.SaveUserAsync(paths.ConfigPath);

        await Task.WhenAll(
            SafeSyncAsync(() => ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore), "type data"),
            SafeSyncAsync(() => FieldDefinitionSyncService.SyncAsync(iterationService, fieldDefinitionStore), "field definitions"));

        try
        {
            var allTypes = await processTypeStore.GetAllAsync(ct);
            ctx.Config.TypeAppearances = allTypes
                .Select(r => new TypeAppearanceConfig
                {
                    Name = r.TypeName,
                    Color = r.ColorHex ?? string.Empty,
                    IconId = r.IconId,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(fmt.FormatInfo($"⚠ Could not refresh type appearances: {ex.Message}"));
        }

        async Task SafeSyncAsync(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _stderr.WriteLine(fmt.FormatInfo($"⚠ Could not fetch {label}: {ex.Message}")); }
        }

        if (globalProfileStore is not null && !string.IsNullOrWhiteSpace(ctx.Config.ProcessTemplate))
        {
            try
            {
                var allFields = await fieldDefinitionStore.GetAllAsync(ct);
                if (allFields.Count > 0)
                {
                    var currentHash = FieldDefinitionHasher.ComputeFieldHash(allFields);
                    var existing = await globalProfileStore.LoadMetadataAsync(
                        ctx.Config.Organization, ctx.Config.ProcessTemplate, ct);

                    if (existing is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (existing.FieldDefinitionHash != currentHash)
                        {
                            telemetryHashChanged = true;
                            var updated = new ProfileMetadata(
                                existing.Organization,
                                existing.CreatedAt,
                                now,
                                currentHash,
                                allFields.Count);
                            await globalProfileStore.SaveMetadataAsync(
                                ctx.Config.Organization, ctx.Config.ProcessTemplate, updated, ct);
                            _stderr.WriteLine(fmt.FormatInfo(
                                "ℹ Field definitions changed since last profile sync"));
                        }
                        else
                        {
                            var refreshed = existing with { LastSyncedAt = now };
                            await globalProfileStore.SaveMetadataAsync(
                                ctx.Config.Organization, ctx.Config.ProcessTemplate, refreshed, ct);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        if (isMachine)
            EmitRefreshCompleteRecord(outputFormat ?? "json", resolvedIterations, iteration, fetchResult, telemetryHashChanged, detectedUserDisplayName);

        return (0, telemetryItemCount, telemetryHashChanged);
    }

    private void EmitHumanInfo(string message, bool isMachine)
    {
        if (isMachine) return;
        _rendererFactory.GetRenderer("human").Render(new RenderTree.RenderTree(new[]
        {
            (RenderNode)new RenderNode.Text(message, Severity.Info),
        }));
    }

    private void EmitHumanText(string message, Severity severity)
    {
        _rendererFactory.GetRenderer("human").Render(new RenderTree.RenderTree(new[]
        {
            (RenderNode)new RenderNode.Text(message, severity),
        }));
    }

    private void EmitRefreshCompleteRecord(
        string outputFormat,
        IReadOnlyList<IterationPath> resolvedIterations,
        IterationPath iteration,
        Twig.Domain.Services.Sync.RefreshFetchResult fetchResult,
        bool hashChanged,
        string? detectedUser)
    {
        var iterationStrings = resolvedIterations.Count > 0
            ? resolvedIterations.Select(i => i.Value).ToList()
            : new List<string> { iteration.Value };

        var iterationRows = iterationStrings.Select(s => new RenderRow("iteration",
            new Dictionary<string, RenderCell>(StringComparer.Ordinal) { ["path"] = RenderCell.String(s) })).ToList();
        var iterationsTable = new RenderNode.Table(null,
            new[] { new RenderColumn("path", "IterationPath") },
            iterationRows);

        var conflictRows = fetchResult.Conflicts.Select(c => new RenderRow("conflict",
            new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["id"] = RenderCell.Integer(c.Id),
                ["localRevision"] = RenderCell.Integer(c.LocalRevision),
                ["remoteRevision"] = RenderCell.Integer(c.RemoteRevision),
            })).ToList();
        var conflictsTable = new RenderNode.Table(null,
            new[]
            {
                new RenderColumn("id", "ID"),
                new RenderColumn("localRevision", "Local"),
                new RenderColumn("remoteRevision", "Remote"),
            },
            conflictRows);

        var fields = new List<DocumentField>(6)
        {
            new("itemCount", new RenderNode.KeyValue("itemCount", RenderCell.Integer(fetchResult.ItemCount))),
            new("phantomsCleansed", new RenderNode.KeyValue("phantomsCleansed", RenderCell.Integer(fetchResult.PhantomsCleansed))),
            new("hashChanged", new RenderNode.KeyValue("hashChanged", RenderCell.Boolean(hashChanged))),
            new("iterations", iterationsTable),
            new("conflicts", conflictsTable),
        };
        if (!string.IsNullOrEmpty(detectedUser))
            fields.Add(new("detectedUser", new RenderNode.KeyValue("detectedUser", RenderCell.String(detectedUser))));

        var doc = new RenderNode.Document("refreshComplete", fields);
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { (RenderNode)doc }));
    }

    private async Task<IReadOnlyList<IterationPath>> ResolveSprintIterationsAsync(
        List<SprintEntry>? sprintEntries, CancellationToken ct)
    {
        if (sprintEntries is null or { Count: 0 })
            return [];

        var expressions = new List<IterationExpression>(sprintEntries.Count);
        foreach (var entry in sprintEntries)
        {
            var parseResult = IterationExpression.Parse(entry.Expression);
            if (parseResult.IsSuccess)
                expressions.Add(parseResult.Value);
        }

        if (expressions.Count == 0)
            return [];

        return await sprintResolver.ResolveAllAsync(expressions, ct);
    }
}
