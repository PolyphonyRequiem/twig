using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider
{
    private sealed record MutationShape(string Mutation, byte[] Payload);

    private async Task<MutationShape> Shape(Bound bound, Discovery schema, Op op, Guid attempt, CancellationToken ct)
    {
        if (op.Origin == RelationshipOrigin.Local)
        {
            if (op.Kind is not (OpKind.Relate or OpKind.Unrelate) || !schema.Capabilities.LocalRelations.Contains(op.Relation!))
                throw Wire.Refuse(ErrorCode.Unsupported, "Local transport requires explicitly absent native semantics and a local-origin link.");
            _ = Edge(op, schema);
            return new("local", []);
        }
        if (!schema.Capabilities.Operations.Contains(op.Kind) || op.Concurrency != ConcurrencyMode.ObservedNonAtomic)
            throw Wire.Refuse(ErrorCode.Unsupported, "Only declared observed-non-atomic effects are supported.");
        if (op.Kind == OpKind.Batch && (op.Fields.Count == 0 || op.Type is not null))
            throw Wire.Refuse(ErrorCode.Unsupported, "A nonempty observed field effect is required; type changes and atomic batches are not supported.");
        if (op.Kind == OpKind.Batch && op.Body is not null)
            throw Wire.Refuse(ErrorCode.Unsupported, "GitHub update and comment are separate transport effects; no atomic batch note is supported.");
        if (op.Kind == OpKind.Create && (op.Other is not null || op.Resource is not null || !op.Links.IsEmpty))
            throw Wire.Refuse(ErrorCode.Unsupported, "This native GitHub create schema cannot couple the reviewed parent/resource/creation-link effects in one creation call.");
        var mutation = op.Kind switch
        {
            OpKind.Create => "createIssue",
            OpKind.Batch => "updateIssue",
            OpKind.Comment => "addComment",
            OpKind.Relate or OpKind.Unrelate => (op.Kind == OpKind.Relate ? "add" : "remove")
                + (op.Relation == Hierarchy ? "SubIssue" : "BlockedBy"),
            _ => throw Wire.Refuse(ErrorCode.Unsupported, "Unsupported native effect."),
        };
        if (!schema.Mutations.Contains(mutation))
            throw Wire.Refuse(ErrorCode.Unsupported, "The mutation is absent from the acquired native schema; no local fallback.");
        var inputs = op.Kind == OpKind.Create ? schema.CreateInputs : schema.UpdateInputs;
        if (op.Kind is OpKind.Create or OpKind.Batch)
        {
            foreach (var (field, value) in op.Fields)
            {
                if (field == StateField && op.Kind == OpKind.Create)
                {
                    if (value.Kind != FieldKind.Text || value.Text != "OPEN")
                        throw Wire.Refuse(ErrorCode.Unsupported, "GitHub creation can only declare its native OPEN initial state.");
                    continue;
                }
                var member = NativeField(field);
                if (!inputs.Contains(member)) throw Wire.Refuse(ErrorCode.Unsupported, $"Native input '{member}' is not supported by this API schema.");
                if (field == Title && (value.Kind != FieldKind.Text || value.IsEmpty)
                    || field == Body && value.Kind != FieldKind.Markdown
                    || field == StateField && (value.Kind != FieldKind.Text || value.Text is not ("OPEN" or "CLOSED"))
                    || field is Labels or Assignees && (value.Kind != FieldKind.StringList
                        || !value.Values.SequenceEqual(value.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
                    || field == Milestone && value.Kind is not (FieldKind.Identity or FieldKind.Null))
                    throw Wire.Refuse(ErrorCode.Unsupported, "Native field payload requires exact kind, explicit Markdown clears and canonical identity sets.");
                if (field is Labels or Assignees)
                    foreach (var id in value.Values) await Resource(bound, id, field == Labels ? "Label" : "User", ct);
                if (field == Milestone && value.Kind != FieldKind.Null) await Resource(bound, value.Text!, "Milestone", ct);
            }
            if (op.Kind == OpKind.Create && op.Type != Untyped && !inputs.Contains("issueTypeId"))
                throw Wire.Refuse(ErrorCode.Unsupported, "This API cannot create an issue with the reviewed native type.");
        }
        if (op.Kind == OpKind.Comment && op.Body?.Kind != FieldKind.Markdown)
            throw Wire.Refuse(ErrorCode.Unsupported, "GitHub comments require reviewed Markdown; no implicit rich-text conversion.");
        if (op.Kind is OpKind.Relate or OpKind.Unrelate)
        {
            var edge = Edge(op, schema);
            var observations = await Read(bound, schema, [op.Item, op.Other!], ct);
            if (observations.Relationships.Contains(edge) == (op.Kind == OpKind.Relate))
                throw Wire.Refuse(ErrorCode.Conflict, "Native edge presence does not match the reviewed change.");
        }
        var inputType = char.ToUpperInvariant(mutation[0]) + mutation[1..] + "Input";
        var result = op.Kind switch
        {
            OpKind.Create or OpKind.Batch => "issue { id repository { id owner { id } } }",
            OpKind.Comment => "commentEdge { node { id } }",
            _ => "",
        };
        var document = $"mutation($input: {inputType}!) {{ {mutation}(input: $input) {{ clientMutationId {result} }} }}";
        var payload = Wire.Request(document, json =>
        {
            json.WriteStartObject("input"); json.WriteString("clientMutationId", attempt.ToString("D"));
            if (op.Kind == OpKind.Create) json.WriteString("repositoryId", _repository);
            else if (op.Kind == OpKind.Batch) json.WriteString("id", Id(op.Item));
            else if (op.Kind == OpKind.Comment) json.WriteString("subjectId", Id(op.Item));
            else if (op.Relation == Hierarchy)
            {
                json.WriteString("issueId", Id(op.Item)); json.WriteString("subIssueId", Id(op.Other!));
                if (op.Kind == OpKind.Relate) json.WriteBoolean("replaceParent", false);
            }
            else
            {
                json.WriteString("issueId", Id(op.Other!)); json.WriteString("blockingIssueId", Id(op.Item));
            }
            if (op.Kind == OpKind.Create && op.Type != Untyped) json.WriteString("issueTypeId", op.Type);
            foreach (var (field, value) in op.Fields)
            {
                if (op.Kind == OpKind.Create && field == StateField) continue;
                json.WritePropertyName(NativeField(field));
                if (value.Kind == FieldKind.Null) json.WriteNullValue();
                else if (value.Kind == FieldKind.StringList)
                {
                    json.WriteStartArray(); foreach (var id in value.Values) json.WriteStringValue(id); json.WriteEndArray();
                }
                else json.WriteStringValue(value.Text);
            }
            if (op.Kind == OpKind.Comment) json.WriteString("body", op.Body!.Text);
            json.WriteEndObject();
        });
        return new(mutation, payload);
    }

    private static string NativeField(string reference) => reference switch
    {
        Title => "title", Body => "body", StateField => "state",
        Labels => "labelIds", Assignees => "assigneeIds", Milestone => "milestoneId",
        _ => throw Wire.Refuse(ErrorCode.Unsupported, "Field has no supported one-request native input shape."),
    };

    private async Task Resource(Bound bound, string id, string kind, CancellationToken ct)
    {
        Wire.Id(id);
        var data = await Graph(bound,
            "query($id: ID!) { node(id: $id) { __typename id ... on Label { repository { id owner { id } } } ... on Milestone { repository { id owner { id } } } } }",
            json => json.WriteString("id", id), ct);
        var node = Wire.Member(data, "node");
        if (Wire.Text(node, "__typename") != kind || Wire.Text(node, "id") != id)
            throw Wire.Refuse(ErrorCode.Conflict, "Field resource kind/identity changed.");
        if (kind != "User") Repository(Wire.Member(node, "repository"));
    }

    private static Relationship Edge(Op op, Discovery schema)
    {
        if (op.Other is null || op.Other == op.Item || op.Relation is null
            || !schema.Schema.Relations.Any(r => r.Reference == op.Relation))
            throw Wire.Refuse(ErrorCode.Unsupported, "Distinct explicit endpoints and discovered relation semantics required.");
        var from = op.Item;
        var to = op.Other;
        if (op.Relation == Related && string.CompareOrdinal(OrderKey(from), OrderKey(to)) > 0) (from, to) = (to, from);
        return new(from, to, op.Relation, op.Origin);
    }

    private static string OrderKey(ItemKey key) => Wire.Utf8(Wire.Json(json =>
    {
        json.WriteStartArray(); json.WriteStringValue(key.IsLocal ? key.LocalId.ToString("D") : null);
        json.WriteStringValue(key.Provider?.Value); json.WriteStringValue(key.ResourceScope);
        json.WriteStringValue(key.NativeId); json.WriteEndArray();
    }));
}
