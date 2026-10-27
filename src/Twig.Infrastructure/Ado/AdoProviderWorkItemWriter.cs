using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Policy;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// ADO's <see cref="IProviderRevisionCodec"/>: the integer work item revision as a canonical
/// positive decimal token, using the same <see cref="AdoProviderTokens"/> encoding as the
/// authoritative reader so there is exactly one ADO spelling of a revision.
/// </summary>
internal sealed class AdoProviderRevisionCodec : IProviderRevisionCodec
{
    public static AdoProviderRevisionCodec Instance { get; } = new();

    private AdoProviderRevisionCodec()
    {
    }

    public string ProviderKind => ProviderCapabilityCatalog.AdoProviderKind;

    /// <remarks>
    /// A revision of zero or below is how the cache spells "never synced". It is refused as
    /// missing rather than sent, so it can never reach ADO, be read as a conflict, or start a
    /// refetch-and-retry.
    /// </remarks>
    public ProviderRevisionPrecondition FromCacheRevision(int revision)
    {
        if (revision <= 0)
            throw new ProviderRevisionPreconditionRefusedException(
                ProviderRevisionPreconditionRefusal.Missing, ProviderKind);

        return new ProviderRevisionPrecondition(ProviderKind, AdoProviderTokens.EncodeRevision(revision));
    }

    public int ToCacheRevision(ProviderRevisionToken revision)
    {
        if (revision is null || !AdoProviderTokens.TryDecodePositive(revision.Value, out var decoded))
            throw UnreadableWrittenRevision();

        return decoded;
    }

    /// <summary>Encodes the revision ADO reported after a write; this runs post-transport.</summary>
    internal ProviderRevisionToken EncodeWrittenRevision(int revision)
        => revision > 0 ? AdoProviderTokens.EncodeRevision(revision) : throw UnreadableWrittenRevision();

    private static FormatException UnreadableWrittenRevision() => new(
        "ADO returned no canonical revision after the write; the write may have been applied. Refresh the item to read its current revision.");

    /// <summary>
    /// Decodes <paramref name="expected"/> to ADO's integer revision, refusing a missing,
    /// foreign or non-canonical precondition. Pure: performs no I/O.
    /// </summary>
    internal int DecodePrecondition(ProviderRevisionPrecondition? expected)
    {
        if (expected is null)
            throw new ProviderRevisionPreconditionRefusedException(
                ProviderRevisionPreconditionRefusal.Missing, ProviderKind);
        if (!string.Equals(expected.ProviderKind, ProviderKind, StringComparison.Ordinal))
            throw new ProviderRevisionPreconditionRefusedException(
                ProviderRevisionPreconditionRefusal.ForeignProvider, ProviderKind);
        if (!AdoProviderTokens.TryDecodePositive(expected.Expected.Value, out var decoded))
            throw new ProviderRevisionPreconditionRefusedException(
                ProviderRevisionPreconditionRefusal.MalformedToken, ProviderKind);

        return decoded;
    }
}

/// <summary>
/// ADO adapter for the provider mutation boundary (Feature #1178). It validates the opaque
/// revision precondition before any transport call, then delegates to
/// <see cref="IAdoWorkItemService"/> unchanged, so ADO requests stay byte-identical.
/// </summary>
internal sealed class AdoProviderWorkItemWriter(IAdoWorkItemService ado)
    : IProviderWorkItemWriter, IExecutionPolicyFactReader
{
    private static AdoProviderRevisionCodec Codec => AdoProviderRevisionCodec.Instance;

    Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)>
        IExecutionPolicyFactReader.FetchPolicyFactsAsync(IReadOnlyList<int> ids, CancellationToken ct)
        => ado is AdoRestClient client
            ? client.FetchPolicyFactsAsync(ids, ct)
            : throw new InvalidOperationException("The provider cannot certify complete native policy relationships.");

    public async Task<ProviderRevisionToken> PatchAsync(
        int id,
        IReadOnlyList<FieldChange> changes,
        ProviderRevisionPrecondition expected,
        CancellationToken ct = default)
    {
        var expectedRevision = Codec.DecodePrecondition(expected);
        var newRevision = await ado.PatchAsync(id, changes, expectedRevision, ct).ConfigureAwait(false);
        return Codec.EncodeWrittenRevision(newRevision);
    }

    public Task AddCommentAsync(int id, string text, CancellationToken ct = default)
        => ado.AddCommentAsync(id, text, ct);

    public Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
        => ado.AddLinkAsync(sourceId, targetId, adoLinkType, ct);

    public Task AddLinkWithCommentAsync(
        int sourceId, int targetId, string adoLinkType, string? comment, CancellationToken ct = default)
        => ado.AddLinkWithCommentAsync(sourceId, targetId, adoLinkType, comment, ct);

    public Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default)
        => ado.RemoveLinkAsync(sourceId, targetId, adoLinkType, ct);

    public Task<WorkItem> FetchAsync(int id, CancellationToken ct = default)
        => ado.FetchAsync(id, ct);

    public Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(
        int id, CancellationToken ct = default)
        => ado.FetchWithLinksAsync(id, ct);

    public Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(
        IReadOnlyList<int> ids, CancellationToken ct = default)
        => ado.FetchBatchWithLinksAsync(ids, ct);

    public Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default)
        => ado.FetchChildrenAsync(parentId, ct);

    public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default)
        => ado.QueryByWiqlAsync(wiql, ct);

    public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default)
        => ado.QueryByWiqlAsync(wiql, top, ct);

    public Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
        => ado.FetchBatchAsync(ids, ct);
}
