using System.Collections.Immutable;

namespace Twig.Core;

public enum RelationshipOrigin { Provider, Local }

public sealed record CreationLink
{
    public CreationLink(ItemKey other, string relation, bool outgoing)
    {
        Other = other ?? throw new ArgumentNullException(nameof(other));
        Relation = Contract.Text(relation, nameof(relation));
        Outgoing = outgoing;
    }

    public ItemKey Other { get; }
    public string Relation { get; }
    public bool Outgoing { get; }
}

public sealed record WorkItem
{
    public WorkItem(ItemKey key, string type, FieldValues fields, Revision? revision,
        bool relationshipsComplete = false, IEnumerable<ResourceLink>? resources = null, bool resourcesComplete = false)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Type = Contract.Text(type, nameof(type));
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        if (revision is not null && revision.Item != key)
            throw new ArgumentException("A revision belongs to one exact remote identity.", nameof(revision));
        Revision = revision;
        RelationshipsComplete = relationshipsComplete;
        Resources = Contract.Copy(resources ?? []);
        ResourcesComplete = resourcesComplete;
    }

    public ItemKey Key { get; }
    public string Type { get; }
    public FieldValues Fields { get; }
    public Revision? Revision { get; }
    public bool RelationshipsComplete { get; }
    public ImmutableArray<ResourceLink> Resources { get; }
    public bool ResourcesComplete { get; }
}

public sealed record Relationship(ItemKey From, ItemKey To, string Kind, RelationshipOrigin Origin,
    long LocalVersion = 0, bool Deleted = false);

public sealed record AttachmentRef(string NativeId, string Name, Uri? DownloadUri);

public sealed record ResourceLink
{
    public ResourceLink(string relation, string target, string? name = null, string? comment = null)
    {
        Relation = Contract.Text(relation, nameof(relation));
        Target = Contract.Text(target, nameof(target));
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || target.Any(char.IsControl))
            throw new ArgumentException("A resource link requires an absolute locator without embedded credentials.", nameof(target));
        Name = name;
        Comment = comment;
    }

    public string Relation { get; }
    public string Target { get; }
    public string? Name { get; }
    public string? Comment { get; }
}

public sealed record Draft
{
    public Draft(ItemKey key, string type, WorkItem? baseline, FieldValues changes,
        ItemKey? parent = null, string? parentRelation = null, ResourceLink? resource = null,
        IEnumerable<CreationLink>? links = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Type = Contract.Text(type, nameof(type));
        if (key.IsLocal != (baseline is null) || baseline is not null && (baseline.Key != key || baseline.Revision is null))
            throw new ArgumentException("A remote draft requires its revision-bound base; a seed has no remote base.", nameof(baseline));
        Baseline = baseline;
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));
        if ((parent is null) != (parentRelation is null) || parent == key
            || !key.IsLocal && (parent is not null || resource is not null))
            throw new ArgumentException("Creation-bound parent/resource intent belongs to a seed and requires a distinct parent with its relation.");
        Parent = parent;
        ParentRelation = parentRelation is null ? null : Contract.Text(parentRelation, nameof(parentRelation));
        Resource = resource;
        Links = Contract.Copy(links ?? []);
        if (!key.IsLocal && !Links.IsEmpty || Links.Any(link => link.Other == key || string.IsNullOrWhiteSpace(link.Relation))
            || Links.Distinct().Count() != Links.Length)
            throw new ArgumentException("Creation links require a seed, distinct endpoints and explicit unique relations.", nameof(links));
    }

    public ItemKey Key { get; }
    public string Type { get; }
    public WorkItem? Baseline { get; }
    public FieldValues Changes { get; }
    public ItemKey? Parent { get; }
    public string? ParentRelation { get; }
    public ResourceLink? Resource { get; }
    public ImmutableArray<CreationLink> Links { get; }
}

public sealed record MergeResult(FieldValues Fields, ImmutableArray<string> Conflicts);
