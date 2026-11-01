using System.Collections.Immutable;

namespace Twig.Core;

public enum QueryKind { All, Equal, And, Or, Not, Contains, Under, GreaterOrEqual }
public enum SelectorKind { Pin, Subtree, Query }

public sealed record Query
{
    public Query(QueryKind kind, string? field = null, FieldValue? value = null, IEnumerable<Query>? children = null)
    {
        Kind = kind;
        Field = field;
        Value = value;
        Children = Contract.Copy(children ?? []);
        var valid = kind switch
        {
            QueryKind.All => field is null && value is null && Children.IsEmpty,
            QueryKind.Equal => !string.IsNullOrWhiteSpace(field) && value is not null && Children.IsEmpty,
            QueryKind.Contains or QueryKind.Under => !string.IsNullOrWhiteSpace(field)
                && value?.Kind is FieldKind.Text or FieldKind.Html or FieldKind.Markdown && Children.IsEmpty,
            QueryKind.GreaterOrEqual => !string.IsNullOrWhiteSpace(field)
                && value?.Kind is FieldKind.Integer or FieldKind.Decimal or FieldKind.Instant && Children.IsEmpty,
            QueryKind.And or QueryKind.Or => field is null && value is null && !Children.IsEmpty,
            QueryKind.Not => field is null && value is null && Children.Length == 1,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Query operands do not match its operator.", nameof(kind));
    }

    public QueryKind Kind { get; }
    public string? Field { get; }
    public FieldValue? Value { get; }
    public ImmutableArray<Query> Children { get; }
}

public sealed record Selector
{
    public Selector(SelectorKind kind, ItemKey? item = null, Query? query = null, string? hierarchyRelation = null)
    {
        var valid = kind switch
        {
            SelectorKind.Pin => item is not null && query is null && hierarchyRelation is null,
            SelectorKind.Subtree => item is not null && query is null && !string.IsNullOrWhiteSpace(hierarchyRelation),
            SelectorKind.Query => item is null && query is not null && hierarchyRelation is null,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Selector payload does not match its kind.", nameof(kind));
        (Kind, Item, Query, HierarchyRelation) = (kind, item, query, hierarchyRelation);
    }

    public SelectorKind Kind { get; }
    public ItemKey? Item { get; }
    public Query? Query { get; }
    public string? HierarchyRelation { get; }
}

public sealed record Bench
{
    public Bench(Guid id, string name, IEnumerable<Selector> selectors)
    {
        if (id == Guid.Empty) throw new ArgumentException("A Bench needs a durable identity.", nameof(id));
        Id = id;
        Name = Contract.Text(name, nameof(name));
        Selectors = Contract.Copy(selectors);
    }

    public Guid Id { get; }
    public string Name { get; }
    public ImmutableArray<Selector> Selectors { get; }
}

public enum RuleKind { ParentType, DependencyIntegrity }
public enum FindingKind { Violation, EvidenceUnavailable }

public sealed record Rule
{
    public Rule(string id, RuleKind kind, string? childType = null, IEnumerable<string>? allowedParentTypes = null,
        bool warnOnly = false, int maxNodes = 200)
    {
        Id = Contract.Text(id, nameof(id));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (maxNodes is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        Kind = kind;
        ChildType = childType;
        AllowedParentTypes = Contract.Copy(allowedParentTypes ?? []);
        if (kind == RuleKind.ParentType && (string.IsNullOrWhiteSpace(childType)
            || AllowedParentTypes.IsEmpty || AllowedParentTypes.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("A parent-type rule needs explicit child and allowed parent types.");
        WarnOnly = warnOnly;
        MaxNodes = maxNodes;
    }

    public string Id { get; }
    public RuleKind Kind { get; }
    public string? ChildType { get; }
    public ImmutableArray<string> AllowedParentTypes { get; }
    public bool WarnOnly { get; }
    public int MaxNodes { get; }
}

public sealed record Policy
{
    public Policy(IEnumerable<Rule> rules) => Rules = Contract.Unique(rules, r => r.Id);
    public ImmutableArray<Rule> Rules { get; }
}

public sealed record Finding(string RuleId, FindingKind Kind, ItemKey? Item, string Message,
    bool Blocks, ImmutableArray<Relationship> Evidence);

public sealed record Repair(string EvidenceDigest, string FindingRuleId, string Description, Proposal Proposal);
