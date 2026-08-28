using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// A single ADO backlog level declared by the reference profile: opaque strings
/// T2's harness produced. Twig core never inspects the reference names; it only
/// compares them byte-equal against the live process at command-time.
/// </summary>
public sealed record ReferenceProfileBaseProcess(string ParentRef, string TailoringVersion);

/// <summary>
/// The locked hierarchy vocabulary block. Values are validated at profile-load
/// to byte-equal the T1 §3.2 lock (<c>apex=[Initiative]</c>,
/// <c>requirement=[Investigation, Feature, Bug]</c>, <c>leaf=[Task]</c>); the
/// property exists to make review of the profile document mechanical, not to
/// carry information Twig core reasons about.
/// </summary>
public sealed record ReferenceProfileHierarchy(
    IReadOnlyList<Role> Apex,
    IReadOnlyList<Role> Requirement,
    IReadOnlyList<Role> Leaf);

/// <summary>
/// One row of the profile's <c>types</c> array — the declared binding from a
/// vocabulary <see cref="Role"/> to the live ADO type name, backlog behaviour
/// reference, backlog tier, and ordered state list.
/// </summary>
public sealed record ReferenceProfileType(
    Role Role,
    string TypeName,
    string BacklogRole,
    string BacklogBehaviorRef,
    IReadOnlyList<StateEntry> States);

/// <summary>
/// A profile-declared link-kind row: the meaning-carrying vocabulary edge
/// (<see cref="LinkKind"/>), the natural-language meaning label, and the two
/// well-known ADO relation reference names (nullable — see T1 §3.5).
/// </summary>
public sealed record ReferenceProfileLinkKind(
    LinkKind Kind,
    string Meaning,
    string? ForwardRel,
    string? ReverseRel);

/// <summary>
/// Primary-scope policy declared by the profile: the scope kind (opaque) and the
/// role allow-set. The concrete type-name allow-set is derived by joining the
/// role list through <see cref="ReferenceProfile.PrimaryScopeAllowTypeNames"/>.
/// </summary>
public sealed record ReferenceProfilePrimaryScope(string Kind, IReadOnlyList<Role> EligibleRoles);

/// <summary>
/// The immutable aggregate produced by
/// <see cref="Twig.Domain.Interfaces.IReferenceProfileProvider"/>. Every field
/// corresponds 1:1 to a T1 §3 profile-document field or a T1 §8.1 declaration.
/// Instances survive per process; no caller mutates a returned instance.
/// </summary>
public sealed class ReferenceProfile
{
    public string Identity { get; }
    public string ProfileVersion { get; }
    public ReferenceProfileBaseProcess BaseProcess { get; }
    public ReferenceProfileHierarchy Hierarchy { get; }
    public IReadOnlyList<ReferenceProfileType> Types { get; }
    public IReadOnlyList<ReferenceProfileLinkKind> LinkKinds { get; }
    public ReferenceProfilePrimaryScope PrimaryScope { get; }

    /// <summary>Lowercase hex SHA-256 recorded in the profile's <c>fingerprint.bytes</c> field. See T1 §7.3.</summary>
    public string EmbeddedFingerprint { get; }

    private readonly Dictionary<Role, ReferenceProfileType> _typesByRole;
    private readonly Dictionary<string, Role> _rolesByTypeName;

    public ReferenceProfile(
        string identity,
        string profileVersion,
        ReferenceProfileBaseProcess baseProcess,
        ReferenceProfileHierarchy hierarchy,
        IReadOnlyList<ReferenceProfileType> types,
        IReadOnlyList<ReferenceProfileLinkKind> linkKinds,
        ReferenceProfilePrimaryScope primaryScope,
        string embeddedFingerprint)
    {
        Identity = identity;
        ProfileVersion = profileVersion;
        BaseProcess = baseProcess;
        Hierarchy = hierarchy;
        Types = types;
        LinkKinds = linkKinds;
        PrimaryScope = primaryScope;
        EmbeddedFingerprint = embeddedFingerprint;

        _typesByRole = types.ToDictionary(t => t.Role);
        _rolesByTypeName = types.ToDictionary(
            t => t.TypeName,
            t => t.Role,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the profile's declared binding for <paramref name="role"/>. All five
    /// vocabulary roles are guaranteed present in a validated profile; the method
    /// throws <see cref="KeyNotFoundException"/> otherwise — a validated profile
    /// cannot exhibit that shape, so the throw is a build-time impossibility.
    /// </summary>
    public ReferenceProfileType TypeByRole(Role role) => _typesByRole[role];

    /// <summary>
    /// Reverse index over <see cref="Types"/>: given a live ADO type name, returns
    /// the profile role it binds to, or <c>null</c> when the live type is not
    /// declared. Match is case-insensitive per T1 §3.3 (mirrors
    /// <c>WorkItemTypeComparer</c>).
    /// </summary>
    public Role? RoleByTypeName(string typeName) =>
        _rolesByTypeName.TryGetValue(typeName, out var role) ? role : null;

    /// <summary>
    /// The concrete type-name allow-set for primary-scope attachment. Derived by
    /// joining <see cref="ReferenceProfilePrimaryScope.EligibleRoles"/> through
    /// <see cref="TypeByRole"/>. Ordered by role declaration order in <see cref="Types"/>.
    /// </summary>
    public IReadOnlyList<string> PrimaryScopeAllowTypeNames =>
        PrimaryScope.EligibleRoles.Select(r => _typesByRole[r].TypeName).ToArray();

    /// <summary>
    /// The profile-declared type name that carries the sprint (leaf) backlog role,
    /// i.e. the <see cref="Role.Task"/> binding. This is the ONE type Twig core
    /// accepts as a direct sprint commitment — the "sprint-entry-only-for-Task"
    /// property from T1 §Locked vocabulary is enforced via this query, never via
    /// a literal type name.
    /// </summary>
    public string SprintTierTypeName => _typesByRole[Role.Task].TypeName;
}
