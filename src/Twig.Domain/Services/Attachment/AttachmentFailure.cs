namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Enumerated failure codes surfaced by <see cref="PrimaryScopeAttachmentService"/>.
/// Every failure the ticket's acceptance tests observe is one of these. The codes
/// are stable across releases (AB#736 §8 storage errors are surfaced under
/// <see cref="StorageUnavailable"/> with their opaque identifier preserved in the
/// human-readable message; adding a new code is a schema change).
/// <para>
/// The claim family — <see cref="ClaimNotFoundForScope"/>,
/// <see cref="ScopeNotPrimary"/> — is emitted by
/// <see cref="PrimaryScopeAttachmentService.RequireActiveClaimForScopeAsync"/>
/// and never by any attach/switch/detach path. AB#738 never mints or reads a
/// claim record; a caller that requires a claim on a child scope observes
/// <see cref="ScopeNotPrimary"/> — the "parent-attachment does not authorize
/// child" boundary — or <see cref="NotAttached"/> when the checkout carries no
/// attachment at all. Actual claim registry validation lives in AB#739.
/// </para>
/// </summary>
internal enum AttachmentFailure
{
    /// <summary>Command run outside a managed twig worktree.</summary>
    NotManagedWorktree,
    /// <summary>Worktree is managed but currently carries no primary scope.</summary>
    NotAttached,
    /// <summary>A primary scope is already attached; use explicit switch to change it.</summary>
    AlreadyAttached,
    /// <summary>The requested work item exists but the profile's primary-scope type
    /// allow-set rejected its type. Nothing is written.</summary>
    IneligibleType,
    /// <summary>The requested work item id is unknown to the local cache and could
    /// not be resolved.</summary>
    WorkItemUnknown,
    /// <summary>A caller requires a claim on a scope that is not the current primary
    /// scope — the "parent attachment does not authorize child" boundary. The failing
    /// scope id is carried in the error message.</summary>
    ScopeNotPrimary,
    /// <summary>The current primary scope carries no active local claim reference.
    /// AB#739 mints; AB#738 only observes.</summary>
    ClaimNotFoundForScope,
    /// <summary>Underlying storage returned a named error (AB#736 §8). The opaque
    /// identifier is preserved in the human-readable message.</summary>
    StorageUnavailable,
}
