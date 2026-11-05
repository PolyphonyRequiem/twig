using System.Collections.Immutable;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public sealed class EditorMetadata
    {
        internal EditorMetadata(AdoProvider owner, Discovery discovery, string actor, string process, Uri origin)
            => (Owner, Discovery, Actor, Process, Origin) = (owner, discovery, actor, process, origin);
        internal Discovery Discovery { get; }
        internal AdoProvider Owner { get; }
        internal string Actor { get; }
        internal string Process { get; }
        internal Uri Origin { get; }
        public ProcessSchema Schema => Discovery.Schema;
    }

    public Task<Result<EditorMetadata>> CaptureEditorMetadataAsync(Connection connection, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) => new EditorMetadata(this,
            await Discover(bound, token, includePresentation: true), bound.PrincipalId, bound.ProcessId, bound.OrganizationUrl), ct));

    public Task<Result<QueryPage>> ReadEditorAsync(EditorMetadata capture, ImmutableArray<ItemKey> keys, CancellationToken ct)
        => Guard(() => WithSession(_connection, async (bound, token) =>
        {
            CheckEditor(capture, bound);
            return await Read(bound, capture.Discovery, keys, token);
        }, ct));

    private void CheckEditor(EditorMetadata capture, Bound bound)
    {
        if (capture is null || !ReferenceEquals(capture.Owner, this) || capture.Schema.Connection != _connection
            || capture.Actor != bound.PrincipalId || capture.Process != bound.ProcessId || capture.Origin != bound.OrganizationUrl)
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
            if (itemType is null || itemType.Hidden || definition is null || definition.ReadOnly)
                throw Wire.Refuse(ErrorCode.Unsupported, "The captured native field is not authorable.");
            if (definition.Kind != FieldKind.Identity)
                throw Wire.Refuse(ErrorCode.Unsupported, "This native lookup resolves identity fields only; use evaluated allowed values for picklists.");
            var response = await Get(bound, "_apis/identities?searchFilter=General&filterValue=" + Wire.Escape(query)
                + "&queryMembership=None&api-version=7.1-preview.1", token);
            var values = response.GetProperty("value").EnumerateArray().ToArray();
            if (values.Length > 1000) throw Wire.Refuse(ErrorCode.Unavailable, "Native identity results exceed their bounded response contract.");
            var choices = ImmutableArray.CreateBuilder<EditorChoice>();
            foreach (var value in values.Take(limit))
            {
                var id = Wire.GuidText(Wire.Text(value, "id"));
                if (!value.TryGetProperty("isActive", out var active) || !active.GetBoolean())
                    throw Wire.Refuse(ErrorCode.Unavailable, "Native identity activity evidence is incomplete or inactive.");
                choices.Add(new(FieldValue.Identity(id), Wire.Text(value, "providerDisplayName")));
            }
            if (choices.Select(value => value.Value).Distinct().Count() != choices.Count)
                throw Wire.Invalid("Native identity choices contain duplicate canonical IDs.");
            return new EditorChoices(choices.ToImmutable(), values.Length > limit);
        }, ct));
}
