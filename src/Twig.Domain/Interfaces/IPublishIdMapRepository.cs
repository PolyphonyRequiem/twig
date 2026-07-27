using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for recording seed-to-ADO ID mappings after publish.
/// <para>
/// Wayfinder 0014 <b>re-keyed</b> this to <see cref="StagedIdentity"/> → ADO id. The previous
/// key was the negative integer, which #280 showed a cache rebuild could reissue to a
/// different seed — a lookup then silently resolved to a previous owner. A minted identity is
/// not reissuable, so the mapping is keyed on something a cache rebuild cannot invalidate.
/// </para>
/// <para>
/// The alias-based overloads survive for the read paths that still start from a number a user
/// typed (<c>twig history</c>). They resolve the alias through the durable register first and
/// return <see langword="null"/> for an unknown alias rather than coercing it (0003 §4).
/// </para>
/// </summary>
public interface IPublishIdMapRepository
{
    /// <summary>Records that the seed identified by <paramref name="identity"/> published as ADO item <paramref name="newId"/>.</summary>
    Task RecordMappingAsync(StagedIdentity identity, int newId, CancellationToken ct = default);

    /// <summary>Resolves a staged identity to the ADO ID it published as, or null.</summary>
    Task<int?> GetNewIdAsync(StagedIdentity identity, CancellationToken ct = default);

    /// <summary>
    /// Resolves a negative display alias to the ADO ID it published as, or null when the alias
    /// is unknown or was never published.
    /// </summary>
    Task<int?> GetNewIdByAliasAsync(StagedAlias alias, CancellationToken ct = default);

    /// <summary>
    /// All recorded mappings, as (identity, display alias, ADO id). The alias is carried for
    /// display and for reconciling legacy rows; nothing joins on it.
    /// </summary>
    Task<IReadOnlyList<PublishMapping>> GetAllMappingsAsync(CancellationToken ct = default);
}
