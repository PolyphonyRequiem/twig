namespace Twig.Infrastructure.Ado.Dtos;

internal sealed record AdoCommentListResponse
{
    public int? WorkItemId { get; init; }
    public List<AdoDiscussionComment>? Comments { get; init; }
    public int? Count { get; init; }
    public int? TotalCount { get; init; }
    public string? ContinuationToken { get; init; }
    public string? NextPage { get; init; }
}

internal sealed record AdoDiscussionComment
{
    public int? Id { get; init; }
    public int? WorkItemId { get; init; }
    public int? Version { get; init; }
    public AdoDiscussionIdentity? CreatedBy { get; init; }
    public DateTimeOffset? CreatedDate { get; init; }
    public DateTimeOffset? ModifiedDate { get; init; }
    public bool? IsDeleted { get; init; }
    public string? Text { get; init; }
    public string? Format { get; init; }
}

internal sealed record AdoDiscussionIdentity
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
}
