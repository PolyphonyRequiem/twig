using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;

namespace Twig.Formatters;

/// <summary>
/// Abstraction for formatting CLI output. Three implementations:
/// Human (ANSI), JSON (stable schema), Minimal (pipe-friendly).
/// </summary>
public interface IOutputFormatter
{
    string FormatWorkItem(WorkItem item, bool showDirty);
    string FormatTree(WorkTree tree, int maxChildren, int? activeId);
    string FormatWorkspace(Workspace ws, int staleDays);
    string FormatSprintView(Workspace ws, int staleDays);
    string FormatFieldChange(FieldChange change);
    string FormatError(string message);
    string FormatSuccess(string message);
    string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches);
    string FormatHint(string hint);
    string FormatInfo(string message);

    // ── Git context formatting (EPIC-006) ───────────────────────────

    /// <summary>Formats a branch info row for status enrichment.</summary>
    string FormatBranchInfo(string branchName);

    /// <summary>Formats a PR status row for status enrichment.</summary>
    string FormatPrStatus(int prId, string title, string status);

    /// <summary>Formats an annotated log entry with optional work item badge.</summary>
    string FormatAnnotatedLogEntry(string hash, string message, string? workItemType, string? workItemState, int? workItemId);

    /// <summary>
    /// Returns a one-line summary suitable for quick-glance status display.
    /// Format: <c>#ID ● Type — Title [State]</c>.
    /// JSON and Minimal formatters return empty string (no change to their output).
    /// </summary>
    string FormatStatusSummary(WorkItem item);

    // ── Seed view formatting (EPIC-004) ─────────────────────────────

    /// <summary>Formats the seed view dashboard grouped by parent.</summary>
    string FormatSeedView(
        IReadOnlyList<SeedViewGroup> groups,
        int totalWritableFields,
        int staleDays,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null);

    // ── Seed link formatting ────────────────────────────────────────

    /// <summary>Formats a list of virtual seed links for display.</summary>
    string FormatSeedLinks(IReadOnlyList<SeedLink> links);

    // ── Seed validation formatting ──────────────────────────────────

    /// <summary>Formats seed validation results for display.</summary>
    string FormatSeedValidation(IReadOnlyList<SeedValidationResult> results);

    // ── Seed reconcile formatting ───────────────────────────────────

    /// <summary>Formats the result of a seed reconcile operation.</summary>
    string FormatSeedReconcileResult(SeedReconcileResult result);

    // ── Seed publish formatting ─────────────────────────────────────

    /// <summary>Formats a single seed publish result for display.</summary>
    string FormatSeedPublishResult(SeedPublishResult result);

    /// <summary>Formats a batch seed publish result for display.</summary>
    string FormatSeedPublishBatchResult(SeedPublishBatchResult result);
}
