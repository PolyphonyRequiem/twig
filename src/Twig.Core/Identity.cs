namespace Twig.Core;

public sealed record ProviderId
{
    public ProviderId(string value)
    {
        Value = Contract.Text(value, nameof(value));
        if (Value != Value.ToLowerInvariant())
            throw new ArgumentException("Provider identifiers must be canonical lower-case tokens.", nameof(value));
    }

    public string Value { get; }
}

public sealed record Connection
{
    public Connection(ProviderId provider, string resourceScope, string destination, string identityRef)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ResourceScope = Contract.Text(resourceScope, nameof(resourceScope));
        Destination = Contract.Text(destination, nameof(destination));
        IdentityRef = Contract.Text(identityRef, nameof(identityRef));
    }

    public ProviderId Provider { get; }
    public string ResourceScope { get; }
    public string Destination { get; }
    public string IdentityRef { get; }
}

public sealed record ItemKey
{
    private ItemKey(Guid localId, ProviderId? provider, string? resourceScope, string? nativeId)
        => (LocalId, Provider, ResourceScope, NativeId) = (localId, provider, resourceScope, nativeId);

    public Guid LocalId { get; }
    public ProviderId? Provider { get; }
    public string? ResourceScope { get; }
    public string? NativeId { get; }
    public bool IsLocal => Provider is null;

    public static ItemKey Local(Guid id) => id == Guid.Empty
        ? throw new ArgumentException("A local item requires a durable UUID.", nameof(id))
        : new(id, null, null, null);

    public static ItemKey Remote(ProviderId provider, string resourceScope, string nativeId)
        => new(Guid.Empty, provider ?? throw new ArgumentNullException(nameof(provider)),
            Contract.Text(resourceScope, nameof(resourceScope)), Contract.Text(nativeId, nameof(nativeId)));

    public bool BelongsTo(Connection connection)
        => !IsLocal && Provider == connection.Provider && ResourceScope == connection.ResourceScope;
}

public sealed record Revision
{
    public Revision(ItemKey item, string token)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        if (item.IsLocal)
            throw new ArgumentException("Local store versions are not provider revisions.", nameof(item));
        Token = Contract.Text(token, nameof(token));
    }

    public ItemKey Item { get; }
    public string Token { get; }
}

public sealed record PublishMapping
{
    public PublishMapping(ItemKey local, ItemKey remote, string proposalDigest, int operation)
    {
        if (!local.IsLocal || remote.IsLocal)
            throw new ArgumentException("A publish mapping must bind a local UUID to a remote identity.");
        if (operation < 0) throw new ArgumentOutOfRangeException(nameof(operation));
        Local = local;
        Remote = remote;
        ProposalDigest = Contract.Text(proposalDigest, nameof(proposalDigest));
        Operation = operation;
    }

    public ItemKey Local { get; }
    public ItemKey Remote { get; }
    public string ProposalDigest { get; }
    public int Operation { get; }
}
