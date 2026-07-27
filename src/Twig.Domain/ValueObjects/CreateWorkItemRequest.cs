namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable DTO for the <c>CreateAsync</c> write path.
/// Carries only the data needed to create a work item in ADO,
/// decoupling callers from the full <see cref="Aggregates.WorkItem"/> aggregate.
/// </summary>
public sealed record CreateWorkItemRequest
{
    public required string TypeName { get; init; }
    public required string Title { get; init; }
    public string? AreaPath { get; init; }
    public string? IterationPath { get; init; }
    public int? ParentId { get; init; }

    /// <summary>
    /// When true, stamps <see cref="PublishIntent.IntentTag"/> on the created item so an
    /// interrupted publish can be found again (wayfinder 0015, from 0001 §4).
    /// <para>
    /// ADO publishes no idempotency key for creates — no <c>clientRequestId</c>, no dedupe
    /// token, no conditional create — so an ambiguous timeout is indistinguishable from a
    /// failure and a blind retry duplicates the item. A tag is the mechanism because it is
    /// per-work-item data rather than schema: stamping one needs no change to the
    /// organisation's process template, unlike a custom field.
    /// </para>
    /// <para>
    /// It is a flag rather than a per-create value on purpose: the tag is a single constant, so
    /// twig adds at most ONE tag to the project's shared vocabulary no matter how many items it
    /// publishes, and removes it again once the publish is recorded.
    /// </para>
    /// </summary>
    public bool StampIntentTag { get; init; }

    public IReadOnlyDictionary<string, string?> Fields { get; init; }
        = new Dictionary<string, string?>();
}
