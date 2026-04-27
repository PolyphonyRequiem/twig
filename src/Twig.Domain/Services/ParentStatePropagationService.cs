using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Outcomes for a parent state propagation attempt.
/// </summary>
public enum ParentPropagationOutcome
{
    /// <summary>Child did not transition to InProgress — no propagation needed.</summary>
    NotApplicable,
    /// <summary>Child has no parent — nothing to propagate to.</summary>
    NoParent,
    /// <summary>Parent is already InProgress or beyond — no-op.</summary>
    AlreadyActive,
    /// <summary>Parent was successfully transitioned to InProgress.</summary>
    Propagated,
    /// <summary>Propagation failed (network error, 409 conflict, etc.).</summary>
    Failed,
}

/// <summary>
/// Result of a parent state propagation attempt.
/// </summary>
public sealed record ParentPropagationResult
{
    public ParentPropagationOutcome Outcome { get; init; }
    public string? ParentOldState { get; init; }
    public string? ParentNewState { get; init; }
    public int? ParentId { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Detects when a child work item transitions to <see cref="StateCategory.InProgress"/> and,
/// if the parent is still in <see cref="StateCategory.Proposed"/>, automatically activates it.
/// Best-effort — never throws; failures are captured in <see cref="ParentPropagationResult"/>.
/// </summary>
public sealed class ParentStatePropagationService(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IProcessConfigurationProvider processConfigProvider,
    ProtectedCacheWriter protectedCacheWriter)
{
    /// <summary>
    /// If <paramref name="childNewCategory"/> is <see cref="StateCategory.InProgress"/> and the child's
    /// parent exists and is in <see cref="StateCategory.Proposed"/>, transitions the parent to InProgress.
    /// </summary>
    public async Task<ParentPropagationResult> TryPropagateToParentAsync(
        WorkItem child,
        StateCategory childNewCategory,
        CancellationToken ct = default)
    {
        try
        {
            if (childNewCategory != StateCategory.InProgress)
                return new ParentPropagationResult { Outcome = ParentPropagationOutcome.NotApplicable };

            if (!child.ParentId.HasValue)
                return new ParentPropagationResult { Outcome = ParentPropagationOutcome.NoParent };

            // Cache-first parent lookup — avoids unnecessary ADO round-trip
            var parent = await workItemRepo.GetByIdAsync(child.ParentId.Value, ct);
            bool parentFromAdo = parent is null;
            if (parentFromAdo)
            {
                try
                {
                    parent = await adoService.FetchAsync(child.ParentId.Value, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new ParentPropagationResult
                    {
                        Outcome = ParentPropagationOutcome.Failed,
                        ParentId = child.ParentId,
                        Error = ex.Message,
                    };
                }
            }

            // Defensive: FetchAsync is non-nullable per IAdoWorkItemService contract, so this path
            // is unreachable in production. Retained to satisfy the nullable checker.
            System.Diagnostics.Debug.Assert(parent is not null, "IAdoWorkItemService.FetchAsync returned null despite non-nullable contract");
            if (parent is null)
                return new ParentPropagationResult
                {
                    Outcome = ParentPropagationOutcome.Failed,
                    ParentId = child.ParentId,
                    Error = "ADO fetch returned null for parent work item despite non-nullable contract",
                };

            var processConfig = processConfigProvider.GetConfiguration();
            if (!processConfig.TypeConfigs.TryGetValue(parent.Type, out var typeConfig))
                return new ParentPropagationResult
                {
                    Outcome = ParentPropagationOutcome.Failed,
                    ParentId = parent.Id,
                    Error = $"Parent type \"{parent.Type}\" not found in process configuration",
                };

            var parentCategory = StateCategoryResolver.Resolve(parent.State, typeConfig.StateEntries);
            if (parentCategory != StateCategory.Proposed)
                return new ParentPropagationResult
                {
                    Outcome = ParentPropagationOutcome.AlreadyActive,
                    ParentId = parent.Id,
                    ParentOldState = parent.State,
                };

            var resolveResult = StateResolver.ResolveByCategory(StateCategory.InProgress, typeConfig.StateEntries);
            if (!resolveResult.IsSuccess)
                return new ParentPropagationResult
                {
                    Outcome = ParentPropagationOutcome.Failed,
                    ParentId = parent.Id,
                    ParentOldState = parent.State,
                    Error = resolveResult.Error,
                };

            var newState = resolveResult.Value;
            var oldState = parent.State;

            var changes = new[] { new FieldChange("System.State", oldState, newState) };
            // Cache hit: refresh from ADO for current revision before patching.
            // Cache miss: parent was just fetched above — revision is already current.
            var revisionSource = parentFromAdo ? parent : await adoService.FetchAsync(parent.Id, ct);
            var newRevision = await adoService.PatchAsync(parent.Id, changes, revisionSource.Revision, ct);

            parent.ChangeState(newState);
            parent.MarkSynced(newRevision);
            await protectedCacheWriter.SaveProtectedAsync(parent, ct);

            return new ParentPropagationResult
            {
                Outcome = ParentPropagationOutcome.Propagated,
                ParentId = parent.Id,
                ParentOldState = oldState,
                ParentNewState = newState,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ParentPropagationResult
            {
                Outcome = ParentPropagationOutcome.Failed,
                ParentId = child.ParentId,
                Error = ex.Message,
            };
        }
    }
}
