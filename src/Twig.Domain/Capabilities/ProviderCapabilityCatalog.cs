using Twig.Domain.Enums;

namespace Twig.Domain.Capabilities;

/// <summary>
/// Built-in capability declarations for the providers Twig ships. Provider adapters expose
/// these through their contract; the catalog is the single place they are spelled.
/// </summary>
/// <remarks>
/// Both declarations are native-only. GitHub documents native sub-issues (hierarchy) and
/// blocked-by/blocking issue dependencies; Twig's predecessor maps to GitHub's blocking issue.
/// GitHub has no typed "related" relationship (mentions and cross-references are not edges),
/// so it is reported Unsupported rather than silently emulated. Emulation is added only for a
/// verified semantic gap, and its storage is the declaring provider's decision.
/// </remarks>
internal static class ProviderCapabilityCatalog
{
    public const string AdoProviderKind = "ado";
    public const string GitHubProviderKind = "github";

    public static ProviderCapabilityDescriptor Ado { get; } = new(
        AdoProviderKind,
        [
            new(LinkKind.ParentChild, CapabilityImplementation.Native, EmulationStorage.None),
            new(LinkKind.PredecessorSuccessor, CapabilityImplementation.Native, EmulationStorage.None),
            new(LinkKind.Related, CapabilityImplementation.Native, EmulationStorage.None),
        ]);

    public static ProviderCapabilityDescriptor GitHub { get; } = new(
        GitHubProviderKind,
        [
            new(LinkKind.ParentChild, CapabilityImplementation.Native, EmulationStorage.None),
            new(LinkKind.PredecessorSuccessor, CapabilityImplementation.Native, EmulationStorage.None),
            new(LinkKind.Related, CapabilityImplementation.Unsupported, EmulationStorage.None),
        ])
    {
        ProposalConcurrency = "observed-non-atomic",
        ProposalOperations =
        [
            "create",
            "batch:System.Title", "batch:System.Description", "batch:System.State", "batch:github.milestoneId",
            "batch:github.labels.add", "batch:github.labels.remove",
            "batch:github.assignees.add", "batch:github.assignees.remove",
            "add-link:parent", "remove-link:parent",
            "add-link:predecessor", "remove-link:predecessor",
            "add-link:successor", "remove-link:successor",
        ],
    };

    /// <summary>Looks up a built-in declaration. Ordinal: provider kinds are canonical tokens.</summary>
    public static bool TryGet(string? providerKind, out ProviderCapabilityDescriptor descriptor)
    {
        descriptor = providerKind switch
        {
            AdoProviderKind => Ado,
            GitHubProviderKind => GitHub,
            _ => null!,
        };
        return descriptor is not null;
    }
}
