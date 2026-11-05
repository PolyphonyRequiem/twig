using System.Collections.Immutable;
using Twig.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider
{
    public sealed class EditorMetadata
    {
        internal EditorMetadata(GitHubProvider owner, Discovery discovery, string authority)
            => (Owner, Discovery, Authority) = (owner, discovery, authority);
        internal Discovery Discovery { get; }
        internal GitHubProvider Owner { get; }
        internal string Authority { get; }
        public ProcessSchema Schema => Discovery.Schema;
    }

    public Task<Result<EditorMetadata>> CaptureEditorMetadataAsync(Connection connection, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
            new EditorMetadata(this, await Discover(bound, token, includePresentation: true), bound.Authority), ct));

    public Task<Result<QueryPage>> ReadEditorAsync(EditorMetadata capture, ImmutableArray<ItemKey> keys, CancellationToken ct)
        => Guard(() => WithSession(_connection, async (bound, token) =>
        {
            CheckEditor(capture, bound);
            return await Refresh(bound, capture.Discovery, keys, token, captureOnly: true);
        }, ct));

    private void CheckEditor(EditorMetadata capture, Bound bound)
    {
        if (capture is null || !ReferenceEquals(capture.Owner, this) || capture.Schema.Connection != _connection || capture.Authority != bound.Authority)
            throw Wire.Refuse(ErrorCode.Conflict, "The native editor actor/resource/schema capture changed.");
    }

    public Task<Result<EditorChoices>> EditorChoicesAsync(EditorMetadata capture, string type, string field, string query, int limit,
        CancellationToken ct)
        => Guard(() => WithSession(_connection, async (bound, token) =>
        {
            CheckEditor(capture, bound);
            if (limit is < 1 or > 50 || query is null || query.Length > 128 || query.Any(char.IsControl))
                throw Wire.Refuse(ErrorCode.InvalidInput, "Native choice queries require bounded text and 1-50 results.");
            var itemType = capture.Schema.Types.SingleOrDefault(value => value.Reference == type);
            var definition = itemType?.Fields.SingleOrDefault(value => value.Reference == field);
            var member = field switch { Assignees => "assignableUsers", Labels => "labels", Milestone => "milestones", _ => null };
            if (itemType is null || itemType.Hidden || definition is null || definition.ReadOnly || member is null)
                throw Wire.Refuse(ErrorCode.Unsupported, "This discovered field has no native canonical choice lookup.");
            var label = field switch { Assignees => "login", Labels => "name", _ => "title" };
            var document = "query($id: ID!, $count: Int!, $query: String!) { node(id: $id) { __typename id ... on Repository { owner { id } "
                + member + "(first: $count, query: $query) { nodes { id " + label
                + " } pageInfo { hasNextPage endCursor } } } } }";
            var data = await Graph(bound, document, json =>
            {
                json.WriteString("id", _repository); json.WriteNumber("count", limit); json.WriteString("query", query);
            }, token);
            var repository = Wire.Member(data, "node");
            Repository(repository);
            var page = Wire.Member(repository, member);
            var nodes = page.GetProperty("nodes").EnumerateArray().ToArray();
            if (nodes.Length > limit) throw Wire.Invalid("Native choice page exceeded the requested bound.");
            var choices = nodes.Select(node => new EditorChoice(field == Milestone
                ? FieldValue.Identity(Wire.Id(Wire.Text(node, "id"))) : FieldValue.Strings([Wire.Id(Wire.Text(node, "id"))]),
                Wire.Text(node, label))).ToImmutableArray();
            if (choices.Select(choice => choice.Value).Distinct().Count() != choices.Length)
                throw Wire.Invalid("Native choices contain duplicate canonical IDs.");
            var more = Wire.Member(page, "pageInfo").GetProperty("hasNextPage").GetBoolean();
            if (more && string.IsNullOrWhiteSpace(Wire.Text(Wire.Member(page, "pageInfo"), "endCursor")))
                throw Wire.Invalid("Native choice continuation is incomplete.");
            return new EditorChoices(choices, more);
        }, ct));
}
