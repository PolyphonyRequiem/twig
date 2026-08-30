using System.Text.Json.Serialization;

namespace Twig.Domain.Enums;

/// <summary>
/// The four link-kind vocabulary edges the reference profile understands.
/// Defined by the T1 note (AB#732) §Locked vocabulary and §3.5. Each kind maps
/// to a fixed pair of ADO reference names inside the profile document; Twig
/// core never spells those reference names.
/// </summary>
/// <remarks>
/// <see cref="LinkKind"/> is Twig's own abstraction. It is NOT a
/// <see cref="Twig.Domain.ValueObjects.SeedLinkTypes"/> string (seed protocol) and NOT
/// an ADO relation reference name — a kind is what the edge means.
/// </remarks>
[JsonConverter(typeof(LinkKindJsonConverter))]
public enum LinkKind
{
    ParentChild = 0,
    PredecessorSuccessor = 1,
    Related = 2,
    Artifact = 3,
}
