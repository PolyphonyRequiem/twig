using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

internal interface IProcessRuleProvider
{
    Task<IReadOnlyList<ProcessRule>> GetRulesAsync(
        string workItemTypeName,
        CancellationToken ct = default);
}
