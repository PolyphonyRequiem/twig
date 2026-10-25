using System.Text.Json.Serialization;

namespace Twig.Domain.ValueObjects;

internal sealed record DiscussionAuthor(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DisplayName);
internal sealed record DiscussionComment(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, DiscussionAuthor Author,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ModifiedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? IsDeleted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Format);
internal sealed record WorkItemDiscussionPage(
    IReadOnlyList<DiscussionComment> Comments, int Count, int? ReportedTotalCount, string? ContinuationToken);

internal sealed record DiscussionPostReceipt(int WorkItemId, string CommentId, string Body, string Format);

/// <summary>Twig adapter limits, not limits promised by the provider.</summary>
internal static class DiscussionReadLimits
{
    internal const int PageSize = 50;
    internal const int MaximumResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumTokenBytes = 8192;

    internal static bool ValidToken(string? token) => token is null
        || (token.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(token) <= MaximumTokenBytes
            && !token.Any(char.IsControl));
}
