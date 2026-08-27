namespace Twig.Infrastructure.Ado;

/// <summary>
/// AB#728 / AB#739 stable-identity read seam.
///
/// Separated from <see cref="Twig.Domain.Interfaces.IAdoWorkItemService"/>
/// deliberately: the general <c>System.AssignedTo</c> projection returns the
/// display name only, which is the user-facing semantics AB#728 pinned.
/// Claim verification still needs the byte-comparable stable identity
/// (<c>uniqueName</c> / UPN), so this dedicated seam reads it directly from
/// the raw ADO response without changing the aggregate projection.
///
/// A missing <c>uniqueName</c> (bare string, absent identity object) MUST
/// surface as <c>null</c>. Callers never fall back to display comparison.
/// </summary>
internal interface IAdoAssignedIdentityReader
{
    Task<string?> ReadAssignedUniqueNameAsync(int workItemId, CancellationToken ct = default);
}
