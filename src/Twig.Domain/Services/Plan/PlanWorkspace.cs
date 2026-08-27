namespace Twig.Domain.Services.Plan;

/// <summary>
/// The ADO organization/project pair a plan targets. Both values are required and are
/// carried through the canonical digest — a plan produced against workspace A cannot be
/// silently applied against workspace B because the digests would differ.
/// </summary>
public sealed record PlanWorkspace
{
    /// <summary>ADO organization name (URL slug).</summary>
    public required string Organization { get; init; }

    /// <summary>ADO project name.</summary>
    public required string Project { get; init; }
}
