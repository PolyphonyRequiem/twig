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
}
