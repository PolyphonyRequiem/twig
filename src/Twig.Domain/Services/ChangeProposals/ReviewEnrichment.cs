using System.Text;

namespace Twig.Domain.Services.ChangeProposals;

/// <summary>A before observation, distinct from the requested after value and apply precondition.</summary>
public sealed record ReviewBeforeValue
{
    /// <summary>unknown, absent (explicit cached null), or value (including empty string).</summary>
    public required string State { get; init; }
    /// <summary>Exact observed value when State is value; null otherwise.</summary>
    public string? Value { get; init; }
    /// <summary>Observation provenance. Currently local-cache.</summary>
    public string Source { get; init; } = "local-cache";
    /// <summary>Observed revision, including mismatches; never silently called the expected revision.</summary>
    public int? Revision { get; init; }
    /// <summary>Why unavailable: item-not-cached, revision-mismatch, local-edits, or field-not-cached.</summary>
    public string? Reason { get; init; }
}

/// <summary>Staged cache context, not an attestation of the expected fingerprint.</summary>
public sealed record ReviewSeedDisplay
{
    /// <summary>Decorative negative local alias, never an ADO id.</summary>
    public int DisplayAlias { get; init; }
    /// <summary>Cached draft title.</summary>
    public string? Title { get; init; }
    /// <summary>Cached draft type.</summary>
    public string? Type { get; init; }
    /// <summary>Cached draft state.</summary>
    public string? State { get; init; }
    /// <summary>Cached draft parent, not proof of the publication payload.</summary>
    public int? ParentId { get; init; }
}

/// <summary>
/// Common-affix replacement size, not a minimal edit script. Counts Unicode scalars in the
/// unequal spans of exact source strings (including HTML). Linear time, constant auxiliary
/// storage. Absent/clear map to zero-length bodies only after availability is established.
/// </summary>
public sealed record ReviewTextChange
{
    /// <summary>Stable algorithm identifier.</summary>
    public string Metric => "common-affix-replacement-v1";
    /// <summary>Counts scalar values, not bytes, UTF-16 units or grapheme clusters.</summary>
    public string Unit => "unicode-scalars";
    /// <summary>Size of the replaced before span.</summary>
    public required int Removed { get; init; }
    /// <summary>Size of the replacement after span.</summary>
    public required int Inserted { get; init; }

    /// <summary>Computes the bounded metric. Ill-formed UTF-16 is counted as replacement scalars.</summary>
    public static ReviewTextChange Measure(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var prefix = 0;
        var length = Math.Min(before.Length, after.Length);
        while (prefix < length && before[prefix] == after[prefix]) prefix++;
        // Never divide a surrogate pair just because the high surrogates matched.
        if (prefix > 0 && prefix < length && char.IsHighSurrogate(before[prefix - 1])) prefix--;
        var suffix = 0;
        while (suffix < length - prefix && before[before.Length - suffix - 1] == after[after.Length - suffix - 1]) suffix++;
        if (suffix > 0 && char.IsLowSurrogate(before[before.Length - suffix])) suffix--;
        return new ReviewTextChange
        {
            Removed = Count(before.AsSpan(prefix, before.Length - prefix - suffix)),
            Inserted = Count(after.AsSpan(prefix, after.Length - prefix - suffix)),
        };
    }

    private static int Count(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes()) count++;
        return count;
    }
}
