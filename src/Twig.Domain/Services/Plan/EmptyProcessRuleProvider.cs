using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// A process-rule provider that authoritatively declares no rules for every type. Register
/// this when a work-item provider genuinely has no process rules; leaving
/// <see cref="IProcessRuleProvider"/> unregistered makes the required-field gate fail closed
/// instead (AB#1169).
/// </summary>
internal sealed class EmptyProcessRuleProvider : IProcessRuleProvider
{
    public static EmptyProcessRuleProvider Instance { get; } = new();

    private EmptyProcessRuleProvider()
    {
    }

    public Task<IReadOnlyList<ProcessRule>> GetRulesAsync(
        string workItemTypeName,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProcessRule>>([]);
}
