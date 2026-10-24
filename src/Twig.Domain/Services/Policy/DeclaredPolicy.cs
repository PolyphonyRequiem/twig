namespace Twig.Domain.Services.Policy;

/// <summary>
/// A repository-owned set of declared, mechanical work-management rules (AB#1169, D2).
/// </summary>
/// <remarks>
/// <para>
/// Not a DSL. Every rule is one of a fixed set of kinds implemented in Twig; the policy file
/// supplies only their data — type names, label prefixes, limits. Twig code names no type,
/// label or field, which keeps the engine process-agnostic: the team that owns the process
/// owns the declaration.
/// </para>
/// <para>
/// Judgment calls — whether work is meaningful, attestation sufficiency, review readiness —
/// are deliberately not expressible here (D1). See
/// <c>docs/specs/cloudvault-policy-decisions.spec.md</c>.
/// </para>
/// </remarks>
public sealed record DeclaredPolicy
{
    /// <summary>The only schema version this build understands.</summary>
    public const int SupportedVersion = 1;

    /// <summary>The rules, in declared order. Ids are unique.</summary>
    public required IReadOnlyList<DeclaredPolicyRule> Rules { get; init; }
}

/// <summary>What a violated rule does to the proposal.</summary>
public enum PolicyEnforcement
{
    /// <summary>A violation refuses the proposal (default).</summary>
    Refuse,

    /// <summary>A violation is reported but does not block.</summary>
    Warn,
}

/// <summary>One declared rule. Concrete kinds are sealed and fixed.</summary>
public abstract record DeclaredPolicyRule
{
    /// <summary>Policy-unique rule id, echoed on every decision it produces.</summary>
    public required string Id { get; init; }

    /// <summary>What a violation of this rule does.</summary>
    public PolicyEnforcement Enforcement { get; init; } = PolicyEnforcement.Refuse;

    /// <summary>The kind discriminator as written in the policy file.</summary>
    public abstract string Kind { get; }
}

/// <summary>Rule kind discriminators as written in <c>twig.policy.json</c>.</summary>
public static class DeclaredPolicyRuleKinds
{
    /// <summary><see cref="ParentTypeRule"/>.</summary>
    public const string ParentType = "parent-type";

    /// <summary><see cref="DependencyIntegrityRule"/>.</summary>
    public const string DependencyIntegrity = "dependency-integrity";

    /// <summary><see cref="LabelApplicabilityRule"/>.</summary>
    public const string LabelApplicability = "label-applicability";

    /// <summary>Pseudo-kind for a policy file that could not be loaded.</summary>
    public const string PolicyLoad = "policy-load";
}

/// <summary>
/// A child of type <see cref="ChildType"/> may only be parented under one of
/// <see cref="AllowedParentTypes"/>. Evaluated on new parent edges only.
/// </summary>
public sealed record ParentTypeRule : DeclaredPolicyRule
{
    /// <inheritdoc />
    public override string Kind => DeclaredPolicyRuleKinds.ParentType;

    /// <summary>The constrained child type.</summary>
    public required string ChildType { get; init; }

    /// <summary>Parent types the child may have. Non-empty.</summary>
    public required IReadOnlyList<string> AllowedParentTypes { get; init; }
}

/// <summary>
/// New predecessor/successor edges must not be self-links, duplicates, or close a cycle.
/// </summary>
public sealed record DependencyIntegrityRule : DeclaredPolicyRule
{
    /// <summary>Default traversal bound.</summary>
    public const int DefaultMaxNodes = 200;

    /// <inheritdoc />
    public override string Kind => DeclaredPolicyRuleKinds.DependencyIntegrity;

    /// <summary>
    /// Upper bound on items visited per cycle search. Exceeding it yields
    /// <see cref="PolicyOutcome.Unavailable"/> rather than an unbounded walk.
    /// </summary>
    public int MaxNodes { get; init; } = DefaultMaxNodes;
}

/// <summary>
/// Labels beginning with <see cref="Prefix"/> apply only to <see cref="AllowedTypes"/>; with
/// <see cref="Exclusive"/>, an item carries at most one value of the prefix.
/// </summary>
public sealed record LabelApplicabilityRule : DeclaredPolicyRule
{
    /// <summary>Default label field.</summary>
    public const string DefaultField = "System.Tags";

    /// <inheritdoc />
    public override string Kind => DeclaredPolicyRuleKinds.LabelApplicability;

    /// <summary>Label prefix, compared case-insensitively.</summary>
    public required string Prefix { get; init; }

    /// <summary>Types that may carry a label with this prefix. Non-empty.</summary>
    public required IReadOnlyList<string> AllowedTypes { get; init; }

    /// <summary>When true, more than one value of the prefix on one item is a violation.</summary>
    public bool Exclusive { get; init; }

    /// <summary>Semicolon-separated label field.</summary>
    public string Field { get; init; } = DefaultField;
}
