using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig refresh</c>: builds the WIQL query, delegates fetch/save/conflict logic
/// to <see cref="RefreshOrchestrator"/>, then runs post-refresh metadata and UI updates.
/// </summary>
public sealed class RefreshCommand(
    CommandContext ctx,
    IContextStore contextStore,
    IIterationService iterationService,
    TwigPaths paths,
    IProcessTypeStore processTypeStore,
    IFieldDefinitionStore fieldDefinitionStore,
    RefreshOrchestrator orchestrator,
    IGlobalProfileStore? globalProfileStore = null,
    IPromptStateWriter? promptStateWriter = null)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;

    /// <summary>Refresh the local cache from Azure DevOps.</summary>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="force">When true, bypass the dirty guard and overwrite protected items.</param>
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var (exitCode, itemCount, hashChanged) = await ExecuteCoreAsync(outputFormat, force, ct);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "refresh", outputFormat, exitCode, startTimestamp,
            extraProperties: new Dictionary<string, string> { ["hash_changed"] = hashChanged.ToString() },
            extraMetrics: new Dictionary<string, double> { ["item_count"] = itemCount });
        return exitCode;
    }

    private async Task<(int ExitCode, int ItemCount, bool HashChanged)> ExecuteCoreAsync(string outputFormat, bool force, CancellationToken ct)
    {
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        var telemetryItemCount = 0;
        var telemetryHashChanged = false;

        Console.WriteLine(fmt.FormatInfo("Refreshing from ADO..."));

        // Get current iteration
        var iteration = await iterationService.GetCurrentIterationAsync();
        Console.WriteLine(fmt.FormatInfo($"  Iteration: {iteration}"));

        // Query all items in the current sprint, scoped to team area paths if configured
        // Escape single quotes to prevent WIQL injection from unusual iteration/area path values.
        var sanitizedPath = iteration.Value.Replace("'", "''");
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.IterationPath] = '{sanitizedPath}'";

        // Build area path filter: prefer AreaPathEntries (with IncludeChildren), fall back to AreaPaths/AreaPath
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
            wiql += $" AND ({string.Join(" OR ", clauses)})";
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
                wiql += $" AND ({string.Join(" OR ", clauses)})";
            }
        }

        wiql += " ORDER BY [System.Id]";

        // Delegate fetch/save/conflict logic to the orchestrator
        var fetchResult = await orchestrator.FetchItemsAsync(wiql, force, ct);

        if (fetchResult.ItemCount == 0)
        {
            Console.WriteLine(fmt.FormatInfo("  No items found in current iteration."));
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
            Console.WriteLine(fmt.FormatSuccess($"Refreshed {fetchResult.ItemCount} item(s)."));
        }

        // Ancestor hydration and working set sync — delegated to orchestrator
        await orchestrator.SyncTrackedTreesAsync(ct);

        // Apply cleanup policy after tree sync — parse kebab-case config string to enum
        var policyStr = ctx.Config.Tracking.CleanupPolicy.Replace("-", "");
        if (Enum.TryParse<Twig.Domain.Enums.TrackingCleanupPolicy>(policyStr, ignoreCase: true, out var cleanupPolicy))
        {
            var removed = await orchestrator.ApplyCleanupPolicyAsync(cleanupPolicy, ct);
            if (removed > 0)
                _stderr.WriteLine(fmt.FormatInfo($"ℹ Auto-untracked {removed} item(s) per cleanup policy."));
        }

        await orchestrator.HydrateAncestorsAsync(ct);
        await orchestrator.SyncWorkingSetAsync(iteration, ct);

        // Refresh user display name if not yet set
        if (string.IsNullOrWhiteSpace(ctx.Config.User.DisplayName))
        {
            try
            {
                var displayName = await iterationService.GetAuthenticatedUserDisplayNameAsync();
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    ctx.Config.User.DisplayName = displayName;
                    Console.WriteLine(fmt.FormatInfo($"  User: {displayName}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _stderr.WriteLine(fmt.FormatInfo($"⚠ Could not detect user identity: {ex.Message}"));
            }
        }

        // Fetch and persist type appearances (always, regardless of work item count)
        var appearances = await iterationService.GetWorkItemTypeAppearancesAsync();
        ctx.Config.TypeAppearances = appearances
            .Select(a => new TypeAppearanceConfig { Name = a.Name, Color = a.Color ?? string.Empty, IconId = a.IconId })
            .ToList();
        await ctx.Config.SaveAsync(paths.ConfigPath);

        // Sync process types and field definitions concurrently (FR-5)
        await Task.WhenAll(
            SafeSyncAsync(() => ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore), "type data"),
            SafeSyncAsync(() => FieldDefinitionSyncService.SyncAsync(iterationService, fieldDefinitionStore), "field definitions"));

        async Task SafeSyncAsync(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _stderr.WriteLine(fmt.FormatInfo($"⚠ Could not fetch {label}: {ex.Message}")); }
        }

        // Update global profile metadata with current field definition hash
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
                // FR-09: Profile I/O failures must never block refresh
            }
        }

        // Update cache freshness timestamp so subsequent reads don't show stale indicators
        await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return (0, telemetryItemCount, telemetryHashChanged);
    }
}