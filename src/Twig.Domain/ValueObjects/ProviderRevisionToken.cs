namespace Twig.Domain.ValueObjects;

/// <summary>
/// An opaque provider-owned concurrency token.
/// </summary>
/// <remarks>
/// The adapter owns encoding and comparison. A missing token is represented by <see langword="null"/>,
/// never by a synthetic zero value.
/// </remarks>
public sealed record ProviderRevisionToken
{
    public ProviderRevisionToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>The opaque provider token, such as an ETag or encoded native revision.</summary>
    public string Value { get; }
}
