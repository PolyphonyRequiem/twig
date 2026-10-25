using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Persists exact admitted intent using the existing journal. Implementations must fence
/// writers while checking the draft and cached observation. This does not export or apply.
/// </summary>
internal interface IDraftProposalCaptureStore
{
    /// <summary>
    /// Retains served-evidence-bound intent only while the exact journal is Planned and has
    /// no confirmation or authorization evidence. The lifecycle fence shares the capture transaction.
    /// </summary>
    Task<PlanJournal> CaptureQualifiedAsync(
        RecoverableWorkItemDraft expectedDraft,
        WorkItemSnapshot expectedSnapshot,
        PlanOrigin origin,
        PlanWorkspace workspace,
        string proposalDirectory,
        string evidenceFingerprint,
        CancellationToken ct = default);

    Task<PlanJournal> CaptureAsync(
        RecoverableWorkItemDraft expectedDraft,
        WorkItemSnapshot expectedSnapshot,
        PlanOrigin origin,
        PlanWorkspace workspace,
        string proposalDirectory,
        CancellationToken ct = default);

    Task<PlanJournal> CaptureObservedAsync(
        RecoverableWorkItemDraft expectedDraft,
        WorkItemSnapshot expectedSnapshot,
        PlanOrigin origin,
        PlanWorkspace workspace,
        string proposalDirectory,
        string evidenceFingerprint,
        CancellationToken ct = default);
}
