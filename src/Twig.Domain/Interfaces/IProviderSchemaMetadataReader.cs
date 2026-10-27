using Twig.Domain.Capabilities;

namespace Twig.Domain.Interfaces;

internal interface IProviderSchemaMetadataReader
{
    Task<ProviderSchemaMetadata> DiscoverSchemaMetadataAsync(CancellationToken ct = default);
}
