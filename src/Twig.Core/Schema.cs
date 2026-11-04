using System.Collections.Immutable;

namespace Twig.Core;

public enum StateCategory { Unknown, Proposed, InProgress, Resolved, Completed, Removed }
public enum RelationShape { Hierarchy, Dependency, Association, Resource }
public enum ConditionKind { Unknown, Equal, NotEqual, Was, Changed, NotChanged }
public enum RuleAction { Unknown, Required, ReadOnly, Allowed, Supplied }

public sealed record State(string Name, StateCategory Category, string? Color = null);
public sealed record Transition(string From, string To);
public sealed record RuleCondition(ConditionKind Kind, string Field, FieldValue? Value = null);

public sealed record FieldRule
{
    public FieldRule(string field, RuleAction action, IEnumerable<RuleCondition> conditions,
        IEnumerable<FieldValue>? values = null, bool disabled = false)
    {
        Field = Contract.Text(field, nameof(field));
        Action = action;
        Conditions = Contract.Copy(conditions);
        Values = Contract.Copy(values ?? []);
        Disabled = disabled;
    }

    public string Field { get; }
    public RuleAction Action { get; }
    public ImmutableArray<RuleCondition> Conditions { get; }
    public ImmutableArray<FieldValue> Values { get; }
    public bool Disabled { get; }
}

public sealed record FieldDef(string Reference, FieldKind Kind, bool ReadOnly = false, string? Name = null);
public sealed record RelationKind(string Reference, RelationShape Shape, bool Directed);

public enum LayoutAvailability { Served, Locked, Unavailable }
public sealed record LayoutControl(string Id, string Label, string ControlType, bool ReadOnly, bool Visible,
    bool IsContribution, string? Watermark = null, int? Height = null);
public sealed record LayoutGroup(string Id, string Label, bool Visible, bool IsContribution,
    ImmutableArray<LayoutControl> Controls);
public sealed record LayoutSection(string Id, ImmutableArray<LayoutGroup> Groups);
public sealed record LayoutPage(string Id, string Label, string PageType, bool Visible, bool IsContribution,
    ImmutableArray<LayoutSection> Sections);
public sealed record FormLayout(string ProcessId, string TypeReference, LayoutAvailability Availability,
    ImmutableArray<LayoutPage> Pages, ImmutableArray<LayoutControl> SystemControls);
public sealed record TypePresentation(string Name, string? Color, string? IconId, FormLayout? Layout = null, string? IconSvg = null);

public sealed record ItemType
{
    public ItemType(string reference, string stateField, IEnumerable<FieldDef> fields, IEnumerable<State> states,
        IEnumerable<Transition> transitions, IEnumerable<FieldRule> rules, bool hidden = false,
        TypePresentation? presentation = null)
    {
        Reference = Contract.Text(reference, nameof(reference));
        StateField = Contract.Text(stateField, nameof(stateField));
        Fields = Contract.Unique(fields, f => Contract.Text(f.Reference, nameof(fields)));
        States = Contract.Unique(states, s => Contract.Text(s.Name, nameof(states)));
        Transitions = Contract.Copy(transitions);
        Rules = Contract.Copy(rules);
        Hidden = hidden;
        Presentation = presentation;
    }

    public string Reference { get; }
    public string StateField { get; }
    public ImmutableArray<FieldDef> Fields { get; }
    public ImmutableArray<State> States { get; }
    public ImmutableArray<Transition> Transitions { get; }
    public ImmutableArray<FieldRule> Rules { get; }
    public bool Hidden { get; }
    // Display metadata is deliberately excluded from the mutation/schema digest.
    public TypePresentation? Presentation { get; }
}

public sealed record ProcessSchema
{
    public ProcessSchema(Connection connection, IEnumerable<ItemType> types, IEnumerable<RelationKind> relations,
        IEnumerable<string>? gaps = null, string? webUrl = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Types = Contract.Unique(types, t => t.Reference);
        Relations = Contract.Unique(relations, r => Contract.Text(r.Reference, nameof(relations)));
        Gaps = Contract.Copy(gaps ?? []);
        if (webUrl is not null && (!Uri.TryCreate(webUrl, UriKind.Absolute, out var address)
            || address.Scheme != "https" || address.UserInfo.Length != 0 || address.Query.Length != 0
            || address.Fragment.Length != 0))
            throw new ArgumentException("An absolute HTTPS source web URL without credentials is required.", nameof(webUrl));
        WebUrl = webUrl;
    }

    public Connection Connection { get; }
    public ImmutableArray<ItemType> Types { get; }
    public ImmutableArray<RelationKind> Relations { get; }
    public ImmutableArray<string> Gaps { get; }
    public string? WebUrl { get; }
    public string Fingerprint => Core.SchemaDigest(this);
}
