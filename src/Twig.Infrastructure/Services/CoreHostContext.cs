using Twig.Core;

namespace Twig.Infrastructure.Services;

internal static class CoreHostContext
{
    internal static IEnumerable<ItemKey> KnownLocalKeys(StoreSnapshot snapshot)
        => snapshot.RetainedItems.Select(draft => draft.Key).Concat(snapshot.Mappings.Select(mapping => mapping.Local))
            .Concat(snapshot.RetainedItems.OfType<Seed>().SelectMany(draft => draft.Links.Select(link => link.Other)
                .Concat(draft.Parent is null ? [] : new[] { draft.Parent })))
            .Concat(snapshot.Proposals.SelectMany(proposal => proposal.Operations)
                .SelectMany(op => op.Links.Select(link => link.Other).Prepend(op.Item)
                    .Concat(op.Other is null ? [] : new[] { op.Other })))
            .Concat(snapshot.Relationships.SelectMany(edge => new[] { edge.From, edge.To }))
            .Concat(snapshot.Benches.SelectMany(bench => bench.Selectors).Where(selector => selector.Item is not null)
                .Select(selector => selector.Item!))
            .Where(key => key.IsLocal).Distinct();

    internal static int LocalAlias(ItemKey key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(key.LocalId.ToByteArray());
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) & int.MaxValue;
        return value == 0 ? int.MinValue : -value;
    }
}
