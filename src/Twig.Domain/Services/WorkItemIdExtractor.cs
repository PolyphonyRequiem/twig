using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Extracts work item IDs from branch names using configurable regex patterns.
/// Delegates to <see cref="BranchNameTemplate.ExtractWorkItemId"/> with the configured
/// or default pattern.
/// </summary>
public static class WorkItemIdExtractor
{
    /// <summary>
    /// Extracts a work item ID from <paramref name="branchName"/> using the given regex <paramref name="pattern"/>.
    /// Falls back to <see cref="BranchNameTemplate.DefaultPattern"/> when <paramref name="pattern"/> is null or empty.
    /// Returns <c>null</c> if no work item ID is found.
    /// </summary>
    public static int? Extract(string branchName, string? pattern = null)
    {
        var effectivePattern = string.IsNullOrWhiteSpace(pattern)
            ? BranchNameTemplate.DefaultPattern
            : pattern;

        return BranchNameTemplate.ExtractWorkItemId(branchName, effectivePattern);
    }
}
