using System.Diagnostics.CodeAnalysis;
using Twig.Domain.Aggregates;

namespace Twig.Domain.Services;

/// <summary>
/// Extension methods for <see cref="ActiveItemResult"/> that collapse the repeated
/// pattern-match block into a single call.
/// </summary>
internal static class ActiveItemResultExtensions
{
    /// <summary>
    /// Attempts to extract the <see cref="WorkItem"/> from the result.
    /// Returns <c>true</c> with <paramref name="item"/> for <see cref="ActiveItemResult.Found"/>
    /// and <see cref="ActiveItemResult.FetchedFromAdo"/>.
    /// Returns <c>false</c> with <paramref name="errorId"/>/<paramref name="errorReason"/>
    /// for <see cref="ActiveItemResult.Unreachable"/>, or <c>false</c> with all nulls
    /// for <see cref="ActiveItemResult.NoContext"/> / unknown.
    /// </summary>
    internal static bool TryGetWorkItem(
        this ActiveItemResult result,
        [NotNullWhen(true)] out WorkItem? item,
        out int? errorId,
        out string? errorReason)
    {
        switch (result)
        {
            case ActiveItemResult.Found f:
                item = f.WorkItem;
                errorId = null;
                errorReason = null;
                return true;

            case ActiveItemResult.FetchedFromAdo f:
                item = f.WorkItem;
                errorId = null;
                errorReason = null;
                return true;

            case ActiveItemResult.Unreachable u:
                item = null;
                errorId = u.Id;
                errorReason = u.Reason;
                return false;

            default: // NoContext or unknown
                item = null;
                errorId = null;
                errorReason = null;
                return false;
        }
    }
}
