using System.Text.Json.Serialization;
using Twig.Domain.Services.Plan;

namespace Twig.Domain.Services.Policy;

/// <summary>Current-state compliance is independent of a proposal's enforcement outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PolicyFindingCode>))]
internal enum PolicyFindingCode
{
    ParentType,
    SelfDependency,
    DuplicateDependency,
    DependencyCycle,
    EvidenceUnavailable,
}

/// <summary>A directed dependency, where From must finish before To.</summary>
internal sealed record PolicyDependencyEdge(int From, int To);

/// <summary>Typed facts supporting a finding. Reason text is presentation, never repair input.</summary>
internal sealed record PolicyFindingEvidence
{
    public string? ItemType { get; init; }
    public int? ParentId { get; init; }
    public string? ParentType { get; init; }
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
    public IReadOnlyList<PolicyDependencyEdge> Edges { get; init; } = [];
    public IReadOnlyList<int> MissingIds { get; init; } = [];
    public int Count { get; init; }
}

/// <summary>An explicit alternative, not an authorization or an automatically selected repair.</summary>
internal sealed record PolicyRepairStrategy
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public bool RequiresParentId { get; init; }
    public PolicyDependencyEdge? RemoveEdge { get; init; }
}

/// <summary>A current violation or evidence gap, including warn-enforced violations.</summary>
internal sealed record PolicyFinding
{
    public required string Id { get; init; }
    public required string RuleId { get; init; }
    public required string Kind { get; init; }
    public required PolicyFindingCode Code { get; init; }
    public required int WorkItemId { get; init; }
    public required string Reason { get; init; }
    public required PolicyFindingEvidence Evidence { get; init; }
    public required IReadOnlyList<PolicyRepairStrategy> Strategies { get; init; }
    public bool StrategiesTruncated { get; init; }
}

/// <summary>Cached observation provenance. A missing timestamp is never verified-empty evidence.</summary>
internal sealed record PolicyItemEvidence(
    int WorkItemId, int? Revision, DateTimeOffset? LastSyncedAt, DateTimeOffset? LinksVerifiedAt);

/// <summary>Bounded assessment of explicitly named published items and their dependency component.</summary>
internal sealed record PolicyAssessment
{
    public required IReadOnlyList<int> Scope { get; init; }
    public IReadOnlyList<int> DependencyScope { get; init; } = [];
    public required string EvidenceDigest { get; init; }
    public required IReadOnlyList<PolicyItemEvidence> Items { get; init; }
    public required IReadOnlyList<PolicyFinding> Findings { get; init; }
    public string Freshness => "cached";
    public string ComplianceBasis => "parent rules over named scope; dependency rules over the incident cached component in dependencyScope";
    public IReadOnlyList<string> EvidenceLimits =>
    [
        "Not live or whole-board certification; refresh and reassess after apply.",
        "Parent rules do not certify out-of-scope children, including children of a retyped parent.",
        "Dependency observations are source-local and cached at different times; contradictory reciprocal observations are unavailable. Uncached relations are not certified absent.",
        "The cache stores distinct logical relations and cannot certify physical relation multiplicity on the server.",
    ];
    public bool Complete => Findings.All(f => f.Code != PolicyFindingCode.EvidenceUnavailable);
    public bool Compliant => Complete && Findings.Count == 0;
    public string Status => !Complete ? "unavailable" : Compliant ? "compliant" : "noncompliant";
}

/// <summary>One caller-selected alternative bound to the exact assessed evidence.</summary>
internal sealed record PolicyRepairSelection(
    string EvidenceDigest, string FindingId, string StrategyId, int? ParentId = null);

/// <summary>Projected compliance is not post-write verification. The ordinary proposal gates still apply.</summary>
internal sealed record PolicyRepairResult
{
    public required PolicyAssessment Before { get; init; }
    public PolicyAssessment? Projected { get; init; }
    [JsonIgnore]
    public PlanDefinition? Proposal { get; init; }
    public string? Error { get; init; }
    public bool CanPropose => Proposal is not null && Error is null;
}
