using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.GitHub;

internal sealed record GitHubBoundReadContext(
    ProviderTargetBindingId BindingId,
    long AttachmentRevision,
    ProviderTargetIdentity Identity,
    GitHubRepositoryTarget Target,
    string WorktreeFingerprint,
    long BindingRevision);

internal sealed class GitHubBoundTargetResolver(
    IProviderTargetAttachmentStore attachment,
    IProviderTargetBindingStore bindings,
    GitHubWorkspaceScope scope)
{
    public async Task<GitHubBoundReadContext> ResolveAsync(CancellationToken ct)
    {
        var read = await attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            throw new InvalidDataException(read.Error.Message);
        if (read.Value.Envelope is not { State: ProviderTargetAttachmentState.Attached, BindingId: { } id } envelope)
            throw new InvalidDataException("provider-target-unbound: run `twig provider discover github --host <host> --repository <owner/name>`, then `twig provider bind github` with the observed expected IDs and attachment revision before reading.");
        var binding = await bindings.FindByIdAsync(id, ct).ConfigureAwait(false);
        if ((!binding.IsSuccess && binding.Error.Code != Twig.Core.ErrorCode.NotFound))
            throw new InvalidDataException(binding.Error.Message);
        var row = binding.Optional();
        if (row is null)
            throw new InvalidDataException(ProviderTargetAttachmentFailures.BindingMissing);
        return new(id, read.Value.Revision, row.Identity,
            GitHubRepositoryTarget.FromIdentity(row.Identity, scope.GraphQlEndpoint),
            envelope.WorktreeFingerprint, row.Revision);
    }

    public async Task ValidateAsync(GitHubBoundReadContext observed, CancellationToken ct)
    {
        var current = await ResolveAsync(ct).ConfigureAwait(false);
        if (current.BindingId != observed.BindingId
            || current.AttachmentRevision != observed.AttachmentRevision
            || current.Identity != observed.Identity
            || current.WorktreeFingerprint != observed.WorktreeFingerprint)
            throw new InvalidDataException("provider-target-selection-changed: the worktree target changed during the read; retry against the current selection.");
    }

    public async Task ValidateCapturedSessionAsync(GitHubBoundReadContext observed, CancellationToken ct)
    {
        var current = await ResolveAsync(ct).ConfigureAwait(false);
        if (current != observed)
            throw new InvalidDataException("provider-target-selection-changed: the captured read session target or binding revision changed; capture a fresh session.");
    }
}
