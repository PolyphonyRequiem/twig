using System.Text.Json.Serialization;

namespace Twig.Domain.Enums;

/// <summary>
/// The five profile-level abstract identities the reference profile speaks in.
/// Defined by the T1 note (AB#732) §Locked vocabulary. Profile-level rules are
/// authored in roles; the concrete ADO type name each role binds to lives in
/// the reference profile as a declared binding, not in code.
/// </summary>
/// <remarks>
/// Roles are Twig's own abstraction. They are NOT <see cref="Twig.Domain.ValueObjects.WorkItemType"/>
/// values — a role is what the profile means (e.g. "the leaf role"), a work item
/// type is what an ADO process happens to name that role.
/// </remarks>
[JsonConverter(typeof(RoleJsonConverter))]
public enum Role
{
    Initiative = 0,
    Investigation = 1,
    Feature = 2,
    Bug = 3,
    Task = 4,
}
