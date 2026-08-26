using System.Threading;
using System.Threading.Tasks;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Public projection over the internal primary-scope attachment service. The
/// service itself is internal (it is a DI-only implementation detail); this
/// interface is what public surfaces — the CLI's <c>twig show</c>-family, the
/// MCP status tool — resolve when they need to render the primary scope block
/// on the status projection.
/// <para>
/// The result payload carries only presentation-ready fields. Missing surface
/// (unmanaged worktree or unattached) is signalled by the two booleans on
/// <see cref="StatusProjection"/>.
/// </para>
/// </summary>
public interface IAttachmentStatusProjection
{
    Task<StatusProjection> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Public status payload for the AB#738 attachment surface. A plain immutable
/// class so it stays PublicAPI-friendly across every downstream reference; a
/// record would generate op_Equality, deconstruct, and a clone method that would
/// need per-member public-API tracking without adding contract value here.
/// </summary>
public sealed class StatusProjection
{
    public StatusProjection(
        bool isManagedWorktree,
        bool hasPrimaryScope,
        int? primaryScopeWorkItemId,
        string? primaryScopeTitle,
        string? primaryScopeType)
    {
        IsManagedWorktree = isManagedWorktree;
        HasPrimaryScope = hasPrimaryScope;
        PrimaryScopeWorkItemId = primaryScopeWorkItemId;
        PrimaryScopeTitle = primaryScopeTitle;
        PrimaryScopeType = primaryScopeType;
    }

    public bool IsManagedWorktree { get; }
    public bool HasPrimaryScope { get; }
    public int? PrimaryScopeWorkItemId { get; }
    public string? PrimaryScopeTitle { get; }
    public string? PrimaryScopeType { get; }
}
