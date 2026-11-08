using System.Collections.Immutable;
using Twig.Core;

namespace Twig.Infrastructure.Services;

public sealed class NativeLocalRelationshipReview
{
    internal NativeLocalRelationshipReview(Guid captureId, Proposal proposal) => (CaptureId, Proposal) = (captureId, proposal);
    public Guid CaptureId { get; }
    public Proposal Proposal { get; }
}

public sealed class NativeLocalRelationshipOutcome
{
    internal NativeLocalRelationshipOutcome(Guid captureId, string digest, JournalEntry? journal, bool settled, Error? error)
        => (CaptureId, Digest, Journal, Settled, Error) = (captureId, digest, journal, settled, error);
    public Guid CaptureId { get; }
    public string Digest { get; }
    public JournalEntry? Journal { get; }
    public bool Settled { get; }
    public Error? Error { get; }
}

public sealed partial class NativeItemEditor
{
    private readonly Dictionary<Guid, NativeLocalRelationshipSelection> localPairs = [];

    private async Task<Result<NativeRelationshipForm>> SelectLocalRelationshipAsync(ItemKey key, ItemKey other, CancellationToken ct)
    {
        var selected = await editor.SelectLocalRelationshipAsync(key, other, ct).ConfigureAwait(false);
        if (!selected.IsSuccess) return Result<NativeRelationshipForm>.Fail(selected.Error);
        if (items.Count + localPairs.Count >= 64) { items.Clear(); relationshipEndpoints.Clear(); localPairs.Clear(); }
        var ticket = Guid.NewGuid();
        var form = Form(ticket, selected.Value.Target, FieldValues.Empty);
        if (!form.IsSuccess) return Result<NativeRelationshipForm>.Fail(form.Error);
        localPairs[ticket] = selected.Value;
        return Result<NativeRelationshipForm>.Ok(new(form.Value with
        {
            RelationshipsReadOnly = true,
            Evaluation = form.Value.Evaluation with
            {
                Fields = form.Value.Evaluation.Fields.Select(field => field with { ReadOnly = true }).ToImmutableArray(),
            },
        }, selected.Value.Target.Observation, selected.Value.Endpoint.Observation,
            [selected.Value.Local.Kind], [], [], selected.Value.Local.Edge is { } edge ? [edge] : [])
        { LocalOnly = true });
    }

    public async Task<Result<NativeLocalRelationshipReview>> ReviewLocalRelationshipAsync(Guid ticket, bool adding, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!GitHub || PlanMode || !localPairs.TryGetValue(ticket, out var selection))
                return Result<NativeLocalRelationshipReview>.Fail(ErrorCode.Conflict, "Select the exact published normal GitHub pair for LOCAL-only review.");
            var review = await editor.ReviewLocalRelationshipAsync(selection, adding, ct).ConfigureAwait(false);
            if (review.IsSuccess) localPairs.Remove(ticket);
            return review;
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeLocalRelationshipOutcome>> ApplyLocalRelationshipAsync(Guid captureId, string exactDigest, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await editor.ApplyLocalRelationshipAsync(captureId, exactDigest, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeLocalRelationshipOutcome>> ObserveLocalRelationshipAsync(Guid captureId, string exactDigest, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await editor.ObserveLocalRelationshipAsync(captureId, exactDigest, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeLocalRelationshipOutcome>> ContinueLocalRelationshipAsync(Guid captureId, string exactDigest, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await editor.ContinueLocalRelationshipAsync(captureId, exactDigest, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
