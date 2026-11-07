using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Providers.Ado;
using Twig.Providers.GitHub;

namespace Twig.Infrastructure.Services;

internal sealed record NativeEditorMetadata(ProcessSchema Schema,
    Func<string, string, string, int, CancellationToken, Task<Result<EditorChoices>>> Choices,
    Func<ImmutableArray<ItemKey>, CancellationToken, Task<Result<QueryPage>>> Read,
    ImmutableArray<string> EditableFields = default);

internal sealed partial class CoreWorkspace
{
    internal async Task<Result<NativeEditorMetadata>> CaptureEditorMetadataAsync(CancellationToken ct)
    {
        if (provider is AdoProvider ado)
        {
            var capture = await ado.CaptureEditorMetadataAsync(connection, ct).ConfigureAwait(false);
            return capture.IsSuccess ? Result<NativeEditorMetadata>.Ok(new(capture.Value.Schema,
                (type, field, query, limit, token) => ado.EditorChoicesAsync(capture.Value, type, field, query, limit, token),
                (keys, token) => ado.ReadEditorAsync(capture.Value, keys, token)))
                : Result<NativeEditorMetadata>.Fail(capture.Error);
        }
        if (provider is GitHubProvider github)
        {
            var capture = await github.CaptureEditorMetadataAsync(connection, ct).ConfigureAwait(false);
            return capture.IsSuccess ? Result<NativeEditorMetadata>.Ok(new(capture.Value.Schema,
                (type, field, query, limit, token) => github.EditorChoicesAsync(capture.Value, type, field, query, limit, token),
                (keys, token) => github.ReadEditorAsync(capture.Value, keys, token), capture.Value.EditableFields))
                : Result<NativeEditorMetadata>.Fail(capture.Error);
        }
        return Result<NativeEditorMetadata>.Fail(ErrorCode.Unsupported, "The selected provider has no native editor capture.");
    }

    internal string DraftFingerprint(Draft? draft) => Fingerprint(connection, draft);
    internal static string Fingerprint(Connection connection, Draft? draft) => draft is null ? "none" : new Proposal(connection, "native-draft", 0,
        new[] { new Op("draft", draft.Key.IsLocal ? OpKind.Create : OpKind.Batch, draft.Key, draft.Baseline?.Revision,
            draft.Changes, draft.Type, draft.Parent, draft.ParentRelation, resource: draft.Resource, links: draft.Links) }
        .Concat(draft.Baseline is { } baseline ? [new Op("baseline", OpKind.Batch, baseline.Key, baseline.Revision,
            baseline.Fields, baseline.Type)] : [])).Digest;

    internal async Task<Result<CoreDraftPreview>> PreviewEditorDraftAsync(ItemKey key, ImmutableArray<CreationLink> links,
        Revision? relationshipBaseline, CancellationToken ct, ImmutableArray<NativeEditorEndpoint> endpoints = default,
        CorePlanReviewCapture? review = null)
    {
        var preview = await PreviewDraftAsync(key, ct, review: review).ConfigureAwait(false);
        if (!preview.IsSuccess || links.IsEmpty) return preview;
        if (key.IsLocal) return Result<CoreDraftPreview>.Fail(ErrorCode.InvalidInput, "Seed links must remain in their exact native seed draft.");
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreDraftPreview>.Fail(current.Error);
        var observed = current.Value.Items.SingleOrDefault(item => item.Key == key);
        if (observed is null || observed.Revision != relationshipBaseline)
            return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The native relationship baseline changed; exact directed intent was preserved.");
        foreach (var endpoint in links.Select(link => link.Other).Distinct())
        {
            var read = await ReadAsync(endpoint, ct, review?.Metadata).ConfigureAwait(false);
            if (!read.IsSuccess) return Result<CoreDraftPreview>.Fail(read.Error);
            if (endpoints.IsDefault || !endpoints.Any(proof => proof.Key == CleanCoreAdoAccess.ItemText(endpoint)
                && proof.Revision == read.Value.Revision?.Token))
                return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The exact captured native endpoint revision changed; no relationship was published.");
        }
        var effect = preview.Value.Proposal?.Operations.Single(value => value.Item == key);
        var prepared = await PrepareAsync([new("edit", OpKind.Batch, key, observed.Revision,
            effect?.Fields ?? FieldValues.Empty, effect?.Type,             concurrency: Concurrency, links: links)], ct, review).ConfigureAwait(false);
        return prepared.IsSuccess ? Result<CoreDraftPreview>.Ok(new(preview.Value.Draft, prepared.Value))
            : Result<CoreDraftPreview>.Fail(prepared.Error);
    }
}
