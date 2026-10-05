using Twig.Domain.Services.Plan;

namespace Twig.Domain.Interfaces;

/// <summary>Resolves and verifies the current attached publication authority without ambient fallback.</summary>
public interface IPlanOriginProvider
{
    Task<PlanOrigin> GetOriginAsync(CancellationToken ct = default);
}
