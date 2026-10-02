using Twig.Domain.Services.Plan;

namespace Twig.Infrastructure.Auth;

/// <summary>Read-only common prerequisites for a later authorized binding transition; never an execution capability.</summary>
internal sealed record IdentityChangeEligibility
{
    public required string ConnectionRef { get; init; }
    public required string Organization { get; init; }
    public required string Project { get; init; }
    public required string WorktreeFingerprint { get; init; }
    public required string WorktreeRoot { get; init; }
    public required long AttachmentRevision { get; init; }
    public string? CurrentIdentityId { get; init; }
    public string? CurrentIdentityName { get; init; }
    public string? CurrentBindingId { get; init; }
    public long? SelectionRevision { get; init; }
    public string? SelectionSource { get; init; }
    public required IReadOnlyList<PendingEditBlocker> PendingEdits { get; init; }
    public required IReadOnlyList<LocalSeedBlocker> LocalSeeds { get; init; }
    public required IReadOnlyList<OpenPublishIntentBlocker> OpenPublishIntents { get; init; }
    public required IReadOnlyList<UnresolvedJournalBlocker> UnresolvedJournals { get; init; }
    public required IReadOnlyList<ReservedClaimBlocker> ReservedClaims { get; init; }
    public required IReadOnlyList<WorktreeGuardBlocker> WorktreeGuards { get; init; }
    public required IReadOnlyList<UnknownRowBlocker> UnknownRows { get; init; }
    public required IReadOnlyList<string> ActionableNextSteps { get; init; }

    public bool IsEligible => CurrentBindingId is not null
        && PendingEdits.Count == 0 && LocalSeeds.Count == 0 && OpenPublishIntents.Count == 0
        && UnresolvedJournals.Count == 0 && ReservedClaims.Count == 0
        && WorktreeGuards.Count == 0 && UnknownRows.Count == 0;
}

internal sealed record PendingEditBlocker(long PendingChangeId, int WorkItemId, string Kind, bool IsSeed);
internal sealed record LocalSeedBlocker(int SeedAlias, string Title, string TypeName);
internal sealed record OpenPublishIntentBlocker(string Identity, string Title, string TypeName, DateTimeOffset RecordedAt);
internal sealed record UnresolvedJournalBlocker(string Digest, string OpId, string SourcePath, PlanOperationState State, string Reason);
internal sealed record ReservedClaimBlocker(string ClaimId, DateTimeOffset MintedAt, string ObservedState,
    int WorkItemId, string PrimaryScopeKind, string CasToken);
internal sealed record WorktreeGuardBlocker(string Guard, string Observed, string Expected);
internal sealed record UnknownRowBlocker(string Source, string Detail);
