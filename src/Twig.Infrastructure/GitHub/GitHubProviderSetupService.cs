using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence.ProviderTargets;

namespace Twig.Infrastructure.GitHub;

internal sealed record GitHubProviderSetupReport(
    string Provider, string ResourceScope, string OwnerId, string RepositoryId, string Locator,
    long AttachmentRevision, string? CurrentBindingId, string? BindingId, bool BindingValidated);

/// <summary>Explicit local target selection; never changes workspace configuration or remote permissions.</summary>
internal sealed class GitHubProviderSetupService(
    Func<IProviderTargetBindingStore> resolveBindings,
    Func<IProviderTargetAttachmentStore> resolveAttachment,
    Func<CancellationToken, Task<VersionedProviderTargetAttachment>> observeAttachment,
    Func<GitHubWorkspaceScope, HttpClient> createClient)
{
    public async Task<GitHubProviderSetupReport> DiscoverAsync(
        string host, string repository, CancellationToken ct = default)
    {
        var scope = Scope(host, repository);
        var observed = await ObserveAsync(scope, ct).ConfigureAwait(false);
        var current = await observeAttachment(ct).ConfigureAwait(false);
        return Report(observed, current.Revision, current.Envelope?.BindingId?.ToString(), null, false);
    }

    public async Task<GitHubProviderSetupReport> BindAsync(
        string host, string repository, string expectedOwnerId, string expectedRepositoryId,
        long expectedAttachmentRevision, string? replaceBinding, CancellationToken ct = default)
    {
        var scope = Scope(host, repository);
        var expected = new GitHubRepositoryTarget(expectedOwnerId, expectedRepositoryId);
        if (expectedAttachmentRevision < 0)
            throw new ArgumentException("Expected attachment revision must be nonnegative.");
        if (replaceBinding is not null && !ProviderTargetBindingId.TryParse(replaceBinding, out _))
            throw new ArgumentException("Replacement binding must be a nonempty binding ID from discovery.");
        var observed = await ObserveAsync(scope, ct).ConfigureAwait(false);
        if (observed.Target != expected)
            throw new InvalidDataException(
                "provider-discovery-identity-mismatch: fresh repository identity differs from the expected observation; discover again.");
        var attachment = resolveAttachment();
        var current = await ReadAsync(attachment, ct).ConfigureAwait(false);
        RequireRevision(current, expectedAttachmentRevision);
        var bindings = resolveBindings();
        var identity = observed.Target.ToIdentity(scope.GraphQlEndpoint);
        var currentId = current.Envelope?.BindingId;
        if (currentId is { } id)
        {
            var row = await bindings.FindByIdAsync(id, ct).ConfigureAwait(false);
            if (!row.IsSuccess)
                throw new InvalidDataException(row.Error);
            if (row.Value is null)
                throw new InvalidDataException(ProviderTargetAttachmentFailures.BindingMissing);
            if ((row.Value.Identity != identity || replaceBinding is not null)
                && !string.Equals(replaceBinding, id.ToString(), StringComparison.Ordinal))
                throw new InvalidDataException(
                    "provider-target-replacement-required: repeat with --replace-binding set to the current binding ID from discovery.");
        }
        else if (replaceBinding is not null)
        {
            throw new InvalidDataException(
                "provider-target-replacement-required: there is no attached binding to replace.");
        }

        var latest = await ReadAsync(attachment, ct).ConfigureAwait(false);
        RequireRevision(latest, expectedAttachmentRevision);
        if (latest.Envelope?.BindingId != currentId)
            throw new InvalidDataException(ProviderTargetAttachmentFailures.BindingMismatch);

        // An intervening pointer CAS can leave an unreferenced durable row, never a wrong pointer.
        // Existing metadata is intentionally retained even when a renamed locator was observed.
        var committed = await new ProviderTargetAttachmentService(bindings, attachment)
            .AttachAsync(identity, observed.Locator, null, expectedAttachmentRevision, ct).ConfigureAwait(false);
        if (!committed.IsSuccess)
            throw new InvalidDataException(committed.Error);
        return Report(observed, committed.Value.AttachmentRevision, currentId?.ToString(),
            committed.Value.Binding.Id.ToString(), true);
    }

    internal static GitHubWorkspaceScope Scope(string host, string repository)
    {
        if (!GitHubWorkspaceScope.TryCreate(new() { Host = host, Repository = repository }, out var scope, out var problem))
            throw new ArgumentException(problem);
        return scope!;
    }

    private async Task<GitHubRepositoryObservation> ObserveAsync(GitHubWorkspaceScope scope, CancellationToken ct)
    {
        using var client = createClient(scope);
        return await new GitHubRepositoryDiscovery(client, scope).DiscoverAsync(ct).ConfigureAwait(false);
    }

    private static async Task<VersionedProviderTargetAttachment> ReadAsync(
        IProviderTargetAttachmentStore attachment, CancellationToken ct)
    {
        var result = await attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidDataException(result.Error);
        return result.Value;
    }

    private static void RequireRevision(VersionedProviderTargetAttachment current, long expected)
    {
        if (current.Revision != expected)
            throw new InvalidDataException(ProviderTargetAttachmentFailures.RevisionMismatch);
    }

    private static GitHubProviderSetupReport Report(GitHubRepositoryObservation observation,
        long revision, string? currentBinding, string? binding, bool bindingValidated) =>
        new("github", observation.ResourceScope, observation.Target.OwnerId, observation.Target.RepositoryId,
            observation.Locator, revision, currentBinding, binding, bindingValidated);
}
