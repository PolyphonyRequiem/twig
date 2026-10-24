using Twig.Domain.Capabilities;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Renders a link row's <c>origin</c> from the active provider's capability descriptor
/// (AB#1170). Unknown origin renders as JSON <c>null</c>, never as a guessed token.
/// </summary>
internal static class LinkOriginCell
{
    public static RenderCell For(ProviderCapabilityDescriptor? descriptor, string? friendlyLinkType)
        => RelationshipProvenance.ToToken(RelationshipProvenance.OfProviderObservation(descriptor, friendlyLinkType)) is { } token
            ? RenderCell.String(token)
            : new RenderCell("unknown", new RenderValue.Null());
}
