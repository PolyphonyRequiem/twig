namespace Twig.Domain.ValueObjects;

/// <summary>
/// Twig's own provider-neutral identity for a work item in the durable local store (Epic #1168).
/// <para>
/// A <b>GUID version 7</b>, minted locally and never derived from any provider's identifier.
/// Provider identities (an ADO work item number, a GitHub node ID) map onto it through the
/// durable external-identity table; they are never the key. ADO's numeric ID remains the
/// display alias users and scripts address on the CLI and in JSON.
/// </para>
/// </summary>
public readonly record struct LocalWorkItemId
{
    private readonly Guid _value;

    private LocalWorkItemId(Guid value) => _value = value;

    /// <summary>The underlying GUIDv7.</summary>
    public Guid Value => _value;

    /// <summary>True when this is the default, unminted identity.</summary>
    public bool IsEmpty => _value == Guid.Empty;

    /// <summary>Mints a fresh, time-ordered identity. Consults no existing state.</summary>
    public static LocalWorkItemId New() => new(Guid.CreateVersion7());

    /// <summary>Rehydrates an identity. Rejects <see cref="Guid.Empty"/>.</summary>
    public static LocalWorkItemId FromGuid(Guid value)
        => value == Guid.Empty
            ? throw new ArgumentException("A local work item identity cannot be empty.", nameof(value))
            : new(value);

    /// <summary>
    /// Parses the persisted string form. Only the canonical lowercase "D" spelling is accepted:
    /// stored ids are compared ordinally (keys, related-pair normalization), so an alternative
    /// spelling of the same GUID would alias it. Returns <see langword="false"/> for anything
    /// else, including the empty GUID, rather than coercing it.
    /// </summary>
    public static bool TryParse(string? text, out LocalWorkItemId identity)
    {
        if (text is not null
            && Guid.TryParseExact(text, "D", out var guid)
            && guid != Guid.Empty
            && string.Equals(guid.ToString("D"), text, StringComparison.Ordinal))
        {
            identity = new LocalWorkItemId(guid);
            return true;
        }

        identity = default;
        return false;
    }

    /// <summary>The persisted string form ("D" format, lowercase).</summary>
    public override string ToString() => _value.ToString("D");
}
