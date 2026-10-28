using Twig.Domain.Enums;

namespace Twig.Domain.Capabilities;

/// <summary>One relationship capability as a provider adapter declares it.</summary>
internal sealed record RelationshipCapability(
    LinkKind Kind,
    CapabilityImplementation Implementation,
    EmulationStorage Storage);

/// <summary>
/// A provider adapter's static, versioned declaration of how it implements each Twig
/// relationship kind, including where emulated data is stored. Validated on construction:
/// a descriptor that cannot be honoured is refused rather than partially applied.
/// </summary>
internal sealed record ProviderCapabilityDescriptor
{
    /// <summary>Relationship kinds every descriptor must declare exactly once.</summary>
    public static IReadOnlyList<LinkKind> RequiredKinds { get; } =
        [LinkKind.ParentChild, LinkKind.PredecessorSuccessor, LinkKind.Related];

    public string ProviderKind { get; }

    public IReadOnlyList<RelationshipCapability> Relationships { get; }

    /// <summary>Fixed proposal effects implemented by this adapter; not live permission evidence.</summary>
    public IReadOnlyList<string> ProposalOperations { get; init; } = [];

    public string? ProposalConcurrency { get; init; }

    public ProviderCapabilityDescriptor(string providerKind, IReadOnlyList<RelationshipCapability> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);
        if (string.IsNullOrWhiteSpace(providerKind))
            throw Invalid("provider kind is required.");

        var seen = new HashSet<LinkKind>();
        foreach (var capability in relationships)
        {
            ArgumentNullException.ThrowIfNull(capability);
            if (capability.Kind == LinkKind.Artifact)
                throw Invalid("artifact links are not a relationship capability.");
            if (!Enum.IsDefined(capability.Kind) || !Enum.IsDefined(capability.Implementation) || !Enum.IsDefined(capability.Storage))
                throw Invalid("capability uses an undefined value.");
            if (!seen.Add(capability.Kind))
                throw Invalid($"'{LinkKindTokens.ToToken(capability.Kind)}' is declared more than once.");

            var emulated = capability.Implementation == CapabilityImplementation.Emulated;
            if (emulated && capability.Storage == EmulationStorage.None)
                throw Invalid($"emulated '{LinkKindTokens.ToToken(capability.Kind)}' must declare its storage.");
            if (!emulated && capability.Storage != EmulationStorage.None)
                throw Invalid($"non-emulated '{LinkKindTokens.ToToken(capability.Kind)}' cannot declare emulation storage.");
        }

        foreach (var required in RequiredKinds)
        {
            if (!seen.Contains(required))
                throw Invalid($"'{LinkKindTokens.ToToken(required)}' is not declared.");
        }

        ProviderKind = providerKind;
        Relationships = relationships.ToArray();
    }

    public RelationshipCapability For(LinkKind kind)
    {
        foreach (var capability in Relationships)
        {
            if (capability.Kind == kind)
                return capability;
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Relationship kind is not a declared capability.");
    }

    private static ArgumentException Invalid(string detail) =>
        new($"capability-descriptor-invalid: {detail}");
}

/// <summary>
/// Runtime evidence about whether a declared capability is usable on this connection.
/// <see cref="ReasonCode"/> explains a non-available result (for example <c>denied</c>,
/// <c>not-found</c>, <c>throttled</c>, <c>timeout</c> or <c>not-probed</c>).
/// </summary>
internal sealed record CapabilityAvailabilityEvidence(
    LinkKind Kind,
    CapabilityAvailability Availability,
    string? ReasonCode);

/// <summary>One relationship kind as reported to users and agents.</summary>
internal sealed record CapabilityReportEntry(
    LinkKind Kind,
    CapabilityImplementation Implementation,
    EmulationStorage Storage,
    CapabilityAvailability Availability,
    string? ReasonCode);

/// <summary>
/// A provider's declared capabilities combined with current availability evidence.
/// Kinds without evidence are reported <see cref="CapabilityAvailability.Unknown"/> /
/// <c>not-probed</c>; absence of evidence never turns a capability into Unsupported.
/// </summary>
internal sealed record CapabilityReport(string ProviderKind, IReadOnlyList<CapabilityReportEntry> Relationships)
{
    public IReadOnlyList<string> ProposalOperations { get; init; } = [];
    public string? ProposalConcurrency { get; init; }
    public const string NotProbedReason = "not-probed";

    public static CapabilityReport Create(
        ProviderCapabilityDescriptor descriptor,
        IReadOnlyList<CapabilityAvailabilityEvidence>? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var byKind = new Dictionary<LinkKind, CapabilityAvailabilityEvidence>();
        foreach (var item in evidence ?? [])
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!descriptor.Relationships.Any(r => r.Kind == item.Kind))
                throw new ArgumentException($"capability-evidence-invalid: '{item.Kind}' is not declared by '{descriptor.ProviderKind}'.");
            if (!byKind.TryAdd(item.Kind, item))
                throw new ArgumentException($"capability-evidence-invalid: '{item.Kind}' has more than one availability result.");
        }

        var entries = descriptor.Relationships
            .Select(capability => byKind.TryGetValue(capability.Kind, out var found)
                ? new CapabilityReportEntry(capability.Kind, capability.Implementation, capability.Storage, found.Availability, found.ReasonCode)
                : new CapabilityReportEntry(capability.Kind, capability.Implementation, capability.Storage, CapabilityAvailability.Unknown, NotProbedReason))
            .ToArray();

        return new CapabilityReport(descriptor.ProviderKind, entries)
        {
            ProposalOperations = descriptor.ProposalOperations,
            ProposalConcurrency = descriptor.ProposalConcurrency,
        };
    }
}
