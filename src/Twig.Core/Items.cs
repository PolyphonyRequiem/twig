using System.Collections.Immutable;

namespace Twig.Core;

public enum RelationshipOrigin { Provider, Local }

public sealed record WorkItem
{
    public WorkItem(ItemKey key, string type, FieldValues fields, Revision? revision,
        bool relationshipsComplete = false)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Type = Contract.Text(type, nameof(type));
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        if (revision is not null && revision.Item != key)
            throw new ArgumentException("A revision belongs to one exact remote identity.", nameof(revision));
        Revision = revision;
        RelationshipsComplete = relationshipsComplete;
    }

    public ItemKey Key { get; }
    public string Type { get; }
    public FieldValues Fields { get; }
    public Revision? Revision { get; }
    public bool RelationshipsComplete { get; }
}

public sealed record Relationship(ItemKey From, ItemKey To, string Kind, RelationshipOrigin Origin,
    long LocalVersion = 0, bool Deleted = false);

public sealed record AttachmentRef(string NativeId, string Name, Uri? DownloadUri);

public sealed record Draft
{
    public Draft(ItemKey key, string type, WorkItem? baseline, FieldValues changes)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Type = Contract.Text(type, nameof(type));
        if (key.IsLocal != (baseline is null) || baseline is not null && (baseline.Key != key || baseline.Revision is null))
            throw new ArgumentException("A remote draft requires its revision-bound base; a seed has no remote base.", nameof(baseline));
        Baseline = baseline;
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    public ItemKey Key { get; }
    public string Type { get; }
    public WorkItem? Baseline { get; }
    public FieldValues Changes { get; }
}

public sealed record MergeResult(FieldValues Fields, ImmutableArray<string> Conflicts);
