using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider
{
    public const string Title = "github.title";
    public const string Body = "github.body";
    public const string StateField = "github.state";
    public const string Labels = "github.labelIds";
    public const string Assignees = "github.assigneeIds";
    public const string Milestone = "github.milestoneId";
    public const string Hierarchy = "github.parent-child";
    public const string Dependency = "github.blocks";
    public const string Related = "github.related";
    public const string Untyped = "github.issue";
    public const string RuleDiscovery = "unsupported: GitHub does not expose process/workflow field rules; complete governed host evidence is required for writes";

    private sealed record Discovery(ProcessSchema Schema, ImmutableHashSet<string> Fields,
        ImmutableHashSet<string> Mutations, ImmutableHashSet<string> UpdateInputs,
        ImmutableHashSet<string> CreateInputs, ImmutableHashSet<string> NativeRelations, ProviderCapabilities Capabilities);

    public Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) => (await Discover(bound, token)).Schema, ct));

    private const string Metadata = """
        query {
          issue: __type(name: "Issue") { fields { name type { kind name ofType { kind name ofType { kind name } } } } }
          issueType: __type(name: "IssueType") { fields { name } }
          repository: __type(name: "Repository") { fields { name } }
          states: __type(name: "IssueState") { enumValues { name } }
          mutations: __type(name: "Mutation") { fields { name } }
          update: __type(name: "UpdateIssueInput") { inputFields { name type { kind name ofType { kind name ofType { kind name } } } } }
          create: __type(name: "CreateIssueInput") { inputFields { name type { kind name ofType { kind name ofType { kind name } } } } }
        }
        """;

    private async Task<Discovery> Discover(Bound bound, CancellationToken ct)
    {
        var metadata = await Graph(bound, Metadata, null, ct);
        var fields = Names(Wire.Member(metadata, "issue"), "fields");
        var repositoryFields = Names(Wire.Member(metadata, "repository"), "fields");
        var mutations = Names(Wire.Member(metadata, "mutations"), "fields");
        var update = Names(Wire.Member(metadata, "update"), "inputFields");
        var create = Names(Wire.Member(metadata, "create"), "inputFields");
        var states = Names(Wire.Member(metadata, "states"), "enumValues");
        var gaps = new List<string>();
        CheckTypes(Wire.Member(metadata, "issue"), "fields",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = "ID", ["title"] = "String", ["body"] = "String", ["state"] = "IssueState",
                ["updatedAt"] = "DateTime", ["repository"] = "Repository", ["issueType"] = "IssueType",
                ["milestone"] = "Milestone", ["labels"] = "LabelConnection", ["assignees"] = "UserConnection",
                ["parent"] = "Issue", ["subIssues"] = "IssueConnection",
                ["blockedBy"] = "IssueConnection", ["blocking"] = "IssueConnection",
            }, gaps);
        var inputTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "ID", ["repositoryId"] = "ID", ["title"] = "String", ["body"] = "String",
            ["state"] = "IssueState", ["labelIds"] = "[ID]", ["assigneeIds"] = "[ID]",
            ["milestoneId"] = "ID", ["issueTypeId"] = "ID", ["clientMutationId"] = "String",
        };
        CheckTypes(Wire.Member(metadata, "update"), "inputFields", inputTypes, gaps);
        CheckTypes(Wire.Member(metadata, "create"), "inputFields", inputTypes, gaps);
        if (!new[] { "id", "repository", "title", "body", "state", "updatedAt" }.All(fields.Contains)
            || !repositoryFields.Contains("issues") || !states.SetEquals(["OPEN", "CLOSED"]))
            gaps.Add("Unsupported/incomplete native Issue protocol fields or state semantics.");
        var native = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (fields.Contains("parent") != fields.Contains("subIssues")
            || fields.Contains("blockedBy") != fields.Contains("blocking")
            || !fields.Contains("parent") && (mutations.Contains("addSubIssue") || mutations.Contains("removeSubIssue"))
            || !fields.Contains("blockedBy") && (mutations.Contains("addBlockedBy") || mutations.Contains("removeBlockedBy")))
            throw Wire.Refuse(ErrorCode.Unsupported, "Partial native relation schema cannot be replaced with a local fallback.");
        if (fields.Contains("parent") && fields.Contains("subIssues")) native.Add(Hierarchy);
        if (fields.Contains("blockedBy") && fields.Contains("blocking")) native.Add(Dependency);
        var relations = new[]
        {
            new RelationKind(Hierarchy, RelationShape.Hierarchy, true),
            new RelationKind(Dependency, RelationShape.Dependency, true),
            new RelationKind(Related, RelationShape.Association, false),
        };
        var definitions = new List<FieldDef>
        {
            new(Title, FieldKind.Text), new(Body, FieldKind.Markdown), new(StateField, FieldKind.Text),
            new("github.updatedAt", FieldKind.Instant, true),
        };
        if (fields.Contains("labels")) definitions.Add(new(Labels, FieldKind.StringList));
        if (fields.Contains("assignees")) definitions.Add(new(Assignees, FieldKind.StringList));
        if (fields.Contains("milestone")) definitions.Add(new(Milestone, FieldKind.Identity));
        var types = new List<string> { Untyped };
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (fields.Contains("issueType"))
        {
            if (!Names(Wire.Member(metadata, "issueType"), "fields").Contains("isEnabled"))
                gaps.Add("IssueType enabled/disabled authoring semantics are unavailable.");
            if (!repositoryFields.Contains("issueTypes"))
                gaps.Add("Issue types are exposed but their authoritative repository catalog is unavailable.");
            else
            {
                var data = await Graph(bound,
                    "query($id: ID!) { node(id: $id) { id ... on Repository { owner { id } issueTypes(first: 100) { nodes { id name isEnabled } pageInfo { hasNextPage endCursor } } } } }",
                    json => json.WriteString("id", _repository), ct);
                var repo = Wire.Member(data, "node"); Repository(repo);
                var catalog = Wire.Member(repo, "issueTypes");
                NoNext(catalog);
                foreach (var type in Wire.Member(catalog, "nodes").EnumerateArray())
                {
                    var id = Wire.Id(Wire.Text(type, "id"));
                    if (id == Untyped || types.Contains(id, StringComparer.Ordinal)) throw Wire.Invalid("Duplicate/invalid issue type identity.");
                    types.Add(id);
                    if (!Wire.Member(type, "isEnabled").GetBoolean()) hidden.Add(id);
                }
            }
        }
        var itemTypes = types.Select(id => new ItemType(id, StateField, definitions,
            states.Order(StringComparer.Ordinal).Select(s => new State(s, s == "OPEN" ? StateCategory.Proposed : StateCategory.Completed)),
            states.SelectMany(a => states.Where(b => b != a).Select(b => new Transition(a, b))),
            [new(Title, RuleAction.Required, [new(ConditionKind.Was, StateField, FieldValue.Null)]),
             new(StateField, RuleAction.Allowed, [], states.Order(StringComparer.Ordinal).Select(FieldValue.String))],
            hidden: hidden.Contains(id))).ToArray();
        itemTypes = itemTypes.Select(t => new ItemType(t.Reference, t.StateField, t.Fields, t.States, t.Transitions,
            t.Rules.Append(new FieldRule(Title, RuleAction.Unknown, [],
                [FieldValue.Raw(bound.Authority, "github.acquired-authority")], disabled: true))
                .Append(new FieldRule(Title, RuleAction.Unknown, [],
                    [FieldValue.Raw(RuleDiscovery, "github.native-rule-discovery")], disabled: true)), t.Hidden)).ToArray();
        var nativeSchema = new ProcessSchema(_connection, itemTypes, relations, gaps);
        var rules = await bound.Session.ReadGovernedRulesAsync(nativeSchema, ct);
        if (!rules.IsSuccess) gaps.Add($"{RuleDiscovery}; {rules.Error.Code}: {rules.Error.Message}");
        else if (rules.Value.Connection != _connection || rules.Value.ActorId != bound.Actor
            || rules.Value.NativeFingerprint != nativeSchema.Fingerprint
            || string.IsNullOrWhiteSpace(rules.Value.PolicyIdentity) || string.IsNullOrWhiteSpace(rules.Value.PolicyRevision)
            || !rules.Value.Types.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(types))
            gaps.Add("Governed rule evidence is incomplete or bound to a different native schema.");
        else
        {
            // Evidence identity participates in consent even when the governed rule arrays are empty.
            itemTypes = itemTypes.Select(t => new ItemType(t.Reference, t.StateField, t.Fields, t.States, t.Transitions,
                t.Rules.Concat(rules.Value.Types[t.Reference]).Append(new FieldRule(Title, RuleAction.Unknown, [],
                    [FieldValue.Raw(Wire.Utf8(Wire.Json(json =>
                    {
                        json.WriteStartArray(); json.WriteStringValue(rules.Value.PolicyIdentity);
                        json.WriteStringValue(rules.Value.PolicyRevision); json.WriteEndArray();
                    })), "github.governed-rule-evidence")], disabled: true)), t.Hidden)).ToArray();
        }
        var operations = new List<OpKind>();
        if (mutations.Contains("createIssue") && create.Contains("repositoryId") && create.Contains("title")) operations.Add(OpKind.Create);
        if (mutations.Contains("updateIssue") && update.Contains("id")) operations.Add(OpKind.Batch);
        if (mutations.Contains("addComment")) operations.Add(OpKind.Comment);
        if (native.Any(r => RelationMutation(r, true, mutations) && RelationMutation(r, false, mutations)))
        {
            operations.Add(OpKind.Relate); operations.Add(OpKind.Unrelate);
        }
        var capabilities = new ProviderCapabilities(operations, native, relations.Select(r => r.Reference).Except(native), false, false);
        _capabilities = capabilities;
        return new(new(_connection, itemTypes, relations, gaps), fields, mutations, update, create, native.ToImmutable(), capabilities);
    }

    private static bool RelationMutation(string relation, bool add, ImmutableHashSet<string> mutations)
        => mutations.Contains((add ? "add" : "remove") + (relation == Hierarchy ? "SubIssue" : "BlockedBy"));

    private static ImmutableHashSet<string> Names(JsonElement type, string member)
    {
        if (type.ValueKind == JsonValueKind.Null) return ImmutableHashSet<string>.Empty;
        var names = Wire.Member(type, member).EnumerateArray().Select(e => Wire.Text(e, "name")).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw Wire.Invalid("Native schema contains duplicate/invalid members.");
        return names.ToImmutableHashSet(StringComparer.Ordinal);
    }

    private static void NoNext(JsonElement connection)
    {
        var info = Wire.Member(connection, "pageInfo");
        if (Wire.Member(info, "hasNextPage").GetBoolean())
            throw Wire.Refuse(ErrorCode.Unsupported, "Native acquisition exceeds the bounded first 100 entries; no completeness is claimed.");
    }

    private static void CheckTypes(JsonElement type, string member, IReadOnlyDictionary<string, string> expected, List<string> gaps)
    {
        if (type.ValueKind == JsonValueKind.Null) return;
        foreach (var field in Wire.Member(type, member).EnumerateArray())
            if (expected.TryGetValue(Wire.Text(field, "name"), out var signature)
                && Signature(Wire.Member(field, "type")) != signature)
                gaps.Add($"Unsupported native signature for '{Wire.Text(field, "name")}'.");
    }

    private static string Signature(JsonElement type) => Wire.Text(type, "kind") switch
    {
        "NON_NULL" => Signature(Wire.Member(type, "ofType")),
        "LIST" => "[" + Signature(Wire.Member(type, "ofType")) + "]",
        _ => Wire.Text(type, "name"),
    };
}
