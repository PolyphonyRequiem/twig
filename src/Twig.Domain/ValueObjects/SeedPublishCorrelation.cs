using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace Twig.Domain.ValueObjects;

/// <summary>Exact native staged-create origin, carried visibly in Description, never a Plan selector.</summary>
public sealed record SeedPublishCorrelation(StagedIdentity Identity, DateTimeOffset IntentRecordedAt)
{
    private const string MarkerPrefix = "Twig publish origin v1: ";
    private const string ReservedMarkerPrefix = "Twig publish origin";

    /// <summary>Legacy unique tag calculation for reading older creates only. New creates must not emit it.</summary>
    public string Tag => "twig-publishing-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Identity.ToString())));

    /// <summary>The visible protocol marker with the canonical durable staged GUID.</summary>
    [JsonIgnore]
    public string DescriptionMarkerText => MarkerPrefix + Identity.ToString();

    /// <summary>The standalone visible paragraph appended atomically to the create Description.</summary>
    [JsonIgnore]
    public string DescriptionMarkerHtml => "<p>" + DescriptionMarkerText + "</p>";

    /// <summary>True only when this create added the shared intent tag, rather than inheriting a caller-owned tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OwnsIntentTag { get; init; }

    /// <summary>
    /// Recognizes exactly one complete supported marker paragraph for this identity. Entities and text
    /// whitespace may be normalized; attributes, inline markup, hidden containers and ambiguous markers fail closed.
    /// </summary>
    public bool ContainsDescriptionMarker(string? description) =>
        TryFindDescriptionMarker(description, out var identity, out _, out _) && identity == Identity;

    /// <summary>Removes only the matching raw paragraph; surrounding caller HTML is preserved byte-for-byte.</summary>
    public bool TryRemoveDescriptionMarker(string? description, out string? remainingDescription)
    {
        remainingDescription = description;
        if (!TryFindDescriptionMarker(description, out var identity, out var start, out var length)
            || identity != Identity)
            return false;

        remainingDescription = description!.Remove(start, length);
        return true;
    }

    internal static bool TryReadDescriptionMarkerIdentity(string? description, out StagedIdentity identity) =>
        TryFindDescriptionMarker(description, out identity, out _, out _);

    internal static bool HasDescriptionMarkerContent(string? description) =>
        CountReservedMarkers(description) != 0;

    private static bool TryFindDescriptionMarker(
        string? description, out StagedIdentity identity, out int start, out int length)
    {
        identity = default;
        start = length = 0;
        if (string.IsNullOrEmpty(description) || CountReservedMarkers(description) != 1)
            return false;

        var ancestors = new List<(string Name, bool SupportsMarker)>();
        var unsupportedAncestors = 0;
        var found = false;
        for (var position = 0; position < description.Length;)
        {
            var opening = description.IndexOf('<', position);
            if (opening < 0)
                break;

            if (description.AsSpan(opening).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = description.IndexOf("-->", opening + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                position = commentEnd + 3;
                continue;
            }

            if (!TryReadTag(description, opening, out var end, out var name, out var closing, out var attributes, out var selfClosing))
                return false;

            if (!closing && !selfClosing && !attributes && unsupportedAncestors == 0
                && name.Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                var contentEnd = description.IndexOf('<', end);
                if (contentEnd >= 0
                    && TryReadTag(description, contentEnd, out var paragraphEnd, out var closingName,
                        out var isClosing, out var closingAttributes, out var closingSelfClosing)
                    && isClosing && !closingAttributes && !closingSelfClosing
                    && closingName.Equals("p", StringComparison.OrdinalIgnoreCase))
                {
                    var text = NormalizeWhitespace(WebUtility.HtmlDecode(description[end..contentEnd]));
                    if (text.StartsWith(MarkerPrefix, StringComparison.Ordinal)
                        && Guid.TryParseExact(text.AsSpan(MarkerPrefix.Length), "D", out var guid)
                        && guid != Guid.Empty
                        && text.AsSpan(MarkerPrefix.Length).SequenceEqual(guid.ToString("D")))
                    {
                        if (found)
                            return false;
                        identity = StagedIdentity.FromGuid(guid);
                        start = opening;
                        length = paragraphEnd - opening;
                        found = true;
                        position = paragraphEnd;
                        continue;
                    }
                }
            }

            if (closing)
            {
                if (attributes || selfClosing || ancestors.Count == 0
                    || !name.Equals(ancestors[^1].Name, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!ancestors[^1].SupportsMarker)
                    unsupportedAncestors--;
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            else if (!selfClosing && !IsVoidElement(name))
            {
                var supportsMarker = !attributes && name.Equals("div", StringComparison.OrdinalIgnoreCase);
                ancestors.Add((name.ToString(), supportsMarker));
                if (!supportsMarker)
                    unsupportedAncestors++;
            }
            position = end;
        }

        return found && ancestors.Count == 0;
    }

    private static bool TryReadTag(
        string html, int start, out int end, out ReadOnlySpan<char> name,
        out bool closing, out bool attributes, out bool selfClosing)
    {
        end = start;
        name = default;
        closing = attributes = selfClosing = false;
        var position = start + 1;
        if (position < html.Length && html[position] == '/')
        {
            closing = true;
            position++;
        }
        var nameStart = position;
        while (position < html.Length && char.IsAsciiLetterOrDigit(html[position]))
            position++;
        if (position == nameStart)
            return false;
        name = html.AsSpan(nameStart, position - nameStart);

        var afterName = position;
        var quote = '\0';
        for (; position < html.Length; position++)
        {
            var character = html[position];
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                continue;
            }
            if (character is '\'' or '"')
                quote = character;
            else if (character == '<')
                return false;
            else if (character == '>')
            {
                var suffix = html.AsSpan(afterName, position - afterName).Trim();
                selfClosing = suffix.EndsWith("/", StringComparison.Ordinal);
                if (selfClosing)
                    suffix = suffix[..^1].TrimEnd();
                attributes = !suffix.IsEmpty;
                end = position + 1;
                return true;
            }
        }
        return false;
    }

    private static bool IsVoidElement(ReadOnlySpan<char> name) =>
        name.Equals("br", StringComparison.OrdinalIgnoreCase)
        || name.Equals("hr", StringComparison.OrdinalIgnoreCase)
        || name.Equals("img", StringComparison.OrdinalIgnoreCase)
        || name.Equals("input", StringComparison.OrdinalIgnoreCase)
        || name.Equals("wbr", StringComparison.OrdinalIgnoreCase)
        || name.Equals("area", StringComparison.OrdinalIgnoreCase)
        || name.Equals("base", StringComparison.OrdinalIgnoreCase)
        || name.Equals("col", StringComparison.OrdinalIgnoreCase)
        || name.Equals("embed", StringComparison.OrdinalIgnoreCase)
        || name.Equals("link", StringComparison.OrdinalIgnoreCase)
        || name.Equals("meta", StringComparison.OrdinalIgnoreCase)
        || name.Equals("param", StringComparison.OrdinalIgnoreCase)
        || name.Equals("source", StringComparison.OrdinalIgnoreCase)
        || name.Equals("track", StringComparison.OrdinalIgnoreCase);

    private static int CountReservedMarkers(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return 0;

        var decoded = WebUtility.HtmlDecode(description);
        var rawCount = CountOccurrences(NormalizeWhitespace(decoded));
        var text = new StringBuilder(decoded.Length);
        var inTag = false;
        foreach (var character in decoded)
        {
            if (character == '<')
                inTag = true;
            else if (character == '>' && inTag)
                inTag = false;
            else if (!inTag)
                text.Append(character);
        }
        return Math.Max(rawCount, CountOccurrences(NormalizeWhitespace(text.ToString())));
    }

    private static int CountOccurrences(string text)
    {
        var count = 0;
        var position = 0;
        while ((position = text.IndexOf(ReservedMarkerPrefix, position, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            position += ReservedMarkerPrefix.Length;
        }
        return count;
    }

    private static string NormalizeWhitespace(string text)
    {
        var normalized = new StringBuilder(text.Length);
        var whitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = normalized.Length > 0;
                continue;
            }
            if (whitespace)
                normalized.Append(' ');
            normalized.Append(character);
            whitespace = false;
        }
        return normalized.ToString();
    }
}
