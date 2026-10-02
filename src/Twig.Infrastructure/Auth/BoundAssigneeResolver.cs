using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Auth;

/// <summary>Resolves authoring defaults from the admitted connection, never user display preferences.</summary>
internal static class BoundAssigneeResolver
{
    internal static async Task<(string? UniqueName, string? ErrorMessage, bool IsUnavailable)> ResolveAsync(
        IIterationService iterationService, CancellationToken ct)
    {
        try
        {
            var identity = await iterationService.GetAuthenticatedUserIdentityAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(identity.UniqueName)
                ? (null, "Cannot resolve bound ADO identity for default assignee: the connection returned no canonical uniqueName. Supply an explicit assignee or repair the selected identity.", false)
                : (identity.UniqueName, null, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, $"Cannot resolve bound ADO identity for default assignee: {ex.GetType().Name}: {ex.Message}. Supply an explicit assignee or repair the selected identity.", true);
        }
    }
}
