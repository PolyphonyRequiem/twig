using System.Text.Json.Serialization;
using Twig.Domain.Enums;

namespace Twig.Infrastructure.Services.ReferenceProfile;

/// <summary>
/// Raw JSON shape of the embedded reference profile document
/// (<c>src/Twig.Infrastructure/Resources/ReferenceProfile/profile.json</c>).
/// Field-by-field mirror of the T1 (AB#732) §3 schema. All types are registered
/// in <see cref="Serialization.TwigJsonContext"/> so no reflection is required
/// under <c>JsonSerializerIsReflectionEnabledByDefault=false</c>.
/// </summary>
/// <remarks>
/// These DTOs are shapes for on-disk JSON. The domain-facing aggregate
/// <see cref="Twig.Domain.ValueObjects.ReferenceProfile"/> is built from them by
/// <see cref="EmbeddedReferenceProfileProvider"/> after validation.
/// </remarks>
internal sealed class ReferenceProfileDto
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public string? Identity { get; set; }
    public string? ProfileVersion { get; set; }
    public BaseProcessDto? BaseProcess { get; set; }
    public HierarchyDto? Hierarchy { get; set; }
    public List<TypeDto>? Types { get; set; }
    public List<LinkKindDto>? LinkKinds { get; set; }
    public PrimaryScopeDto? PrimaryScope { get; set; }
    public FingerprintDto? Fingerprint { get; set; }
}

internal sealed class BaseProcessDto
{
    public string? ParentRef { get; set; }
    public string? TailoringVersion { get; set; }
}

internal sealed class HierarchyDto
{
    public List<Role>? Apex { get; set; }
    public List<Role>? Requirement { get; set; }
    public List<Role>? Leaf { get; set; }
}

internal sealed class TypeDto
{
    public Role? Role { get; set; }
    public string? TypeName { get; set; }
    public string? BacklogRole { get; set; }
    public string? BacklogBehaviorRef { get; set; }
    public List<StateDto>? States { get; set; }
}

internal sealed class StateDto
{
    public string? Name { get; set; }
    public StateCategory? Category { get; set; }
}

internal sealed class LinkKindDto
{
    public LinkKind? Kind { get; set; }
    public string? Meaning { get; set; }
    public string? ForwardRel { get; set; }
    public string? ReverseRel { get; set; }
    public string? ArtifactCategory { get; set; }
}

internal sealed class PrimaryScopeDto
{
    public string? Kind { get; set; }

    /// <summary>
    /// Raw role tokens, NOT <see cref="Role"/>. T1 §6.6 declares a dedicated
    /// identifier for an unknown role here (<c>primary-scope-unknown-role</c>),
    /// and a strongly-typed list cannot produce it: the canonical converter
    /// throws on any token outside the five, which the loader can only report as
    /// <c>profile-schema-invalid</c>. Resolving these tokens explicitly in
    /// <c>TryBuild</c> is what keeps that identifier reachable.
    /// </summary>
    public List<string>? EligibleRoles { get; set; }
}

internal sealed class FingerprintDto
{
    public string? Algorithm { get; set; }
    public string? Bytes { get; set; }
}
