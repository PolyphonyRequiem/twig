using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence.ProviderTargets;

internal enum ProviderTargetAttachmentState
{
    Attached,
    Detached,
}

internal sealed record ProviderTargetAttachmentEnvelope(
    long Revision,
    string WorktreeFingerprint,
    DateTimeOffset UpdatedAt,
    ProviderTargetAttachmentState State,
    ProviderTargetBindingId? BindingId);

internal readonly record struct VersionedProviderTargetAttachment(
    ProviderTargetAttachmentEnvelope? Envelope,
    long Revision);

internal readonly record struct ProviderTargetAttachmentWriteOutcome(long WrittenRevision);

internal sealed record ProviderTargetAttachmentDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int Version,
    long Revision,
    string WorktreeFingerprint,
    string UpdatedAt,
    string State,
    string? BindingId)
{
    public const string CurrentSchema = "twig-provider-target-attachment/v1";
    public const int CurrentVersion = 1;
    public const string FileName = "provider-target.json";
    public const string LockFileName = "provider-target.json.lock";
    public const string StateAttached = "attached";
    public const string StateDetached = "detached";
}

internal interface IProviderTargetAttachmentStore
{
    Task<Twig.Domain.Common.Result<VersionedProviderTargetAttachment>> ReadWithRevisionAsync(
        CancellationToken ct = default);

    Task<Twig.Domain.Common.Result<ProviderTargetAttachmentWriteOutcome>> WriteAsync(
        ProviderTargetBindingId bindingId,
        long expectedRevision,
        CancellationToken ct = default);

    Task<Twig.Domain.Common.Result<ProviderTargetAttachmentWriteOutcome>> ClearAsync(
        ProviderTargetBindingId expectedBindingId,
        long expectedRevision,
        CancellationToken ct = default);
}

internal sealed record ProviderTargetAttachmentCommit(
    ProviderTargetBinding Binding,
    long AttachmentRevision);

internal sealed class ProviderTargetAttachmentService(
    Twig.Domain.Interfaces.IProviderTargetBindingStore bindings,
    IProviderTargetAttachmentStore attachment)
{
    /// <summary>
    /// Commits the durable identity row before CAS-replacing the worktree pointer.
    /// A pointer-write failure intentionally leaves the idempotent row for retry.
    /// </summary>
    public async Task<Twig.Domain.Common.Result<ProviderTargetAttachmentCommit>> AttachAsync(
        ProviderTargetIdentity identity,
        string? locator,
        string? displayName,
        long expectedRevision,
        CancellationToken ct = default)
    {
        var binding = await bindings.GetOrCreateAsync(identity, locator, displayName, ct).ConfigureAwait(false);
        if (!binding.IsSuccess)
            return Twig.Domain.Common.Result.Fail<ProviderTargetAttachmentCommit>(binding.Error);

        var written = await attachment.WriteAsync(binding.Value.Id, expectedRevision, ct).ConfigureAwait(false);
        if (!written.IsSuccess)
            return Twig.Domain.Common.Result.Fail<ProviderTargetAttachmentCommit>(written.Error);

        return Twig.Domain.Common.Result.Ok(new ProviderTargetAttachmentCommit(
            binding.Value,
            written.Value.WrittenRevision));
    }
}

internal static class ProviderTargetAttachmentFailures
{
    public const string InvalidDocument = "provider-target-attachment-invalid";
    public const string BindingMissing = "provider-target-binding-missing";
    public const string BindingMismatch = "provider-target-attachment-binding-mismatch";
    public const string RevisionMismatch = "provider-target-attachment-revision-mismatch";
    public const string WorktreeFingerprintMismatch = "provider-target-worktree-fingerprint-mismatch";
    public const string LockUnavailable = "provider-target-attachment-lock-unavailable";
    public const string ReadFailed = "provider-target-attachment-read-failed";
    public const string WriteFailed = "provider-target-attachment-write-failed";
    public const string WorktreeUnavailable = "provider-target-attachment-worktree-unavailable";
}
