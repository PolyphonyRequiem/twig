using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscussionPostRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] DiscussionContext Context,
    [property: JsonRequired] string Format,
    [property: JsonRequired] string Text);

internal sealed record DiscussionPostPreparation(
    int Version, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionContext? Context,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Proposal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Digest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiscussionError? Error);
