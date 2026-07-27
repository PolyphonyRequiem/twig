namespace Twig.Domain.ValueObjects;

/// <summary>
/// The durable identity of a staged (unpublished) seed, minted at creation.
/// <para>
/// Wayfinder 0003 replaced the recycled negative integer with this: a self-contained,
/// collision-free identity that needs no allocator, no floor and no <c>Initialize</c>
/// preamble. It is a <b>GUID version 7</b> — sortable by creation time, so "the first seed
/// created" stays answerable without a separate sequence column (0003 §5, owner-confirmed).
/// </para>
/// <para>
/// This is the key. The negative integer that users and scripts see is a separate, purely
/// decorative <see cref="StagedAlias"/>.
/// </para>
/// </summary>
public readonly record struct StagedIdentity
{
    private readonly Guid _value;

    private StagedIdentity(Guid value) => _value = value;

    /// <summary>The underlying GUIDv7.</summary>
    public Guid Value => _value;

    /// <summary>True when this is the default, unminted identity.</summary>
    public bool IsEmpty => _value == Guid.Empty;

    /// <summary>
    /// Mints a fresh identity. Self-contained: it consults no existing state, so there is no
    /// preamble a sixth call site can forget (0003 §2).
    /// </summary>
    public static StagedIdentity New() => new(Guid.CreateVersion7());

    /// <summary>Rehydrates an identity from its persisted string form.</summary>
    public static StagedIdentity FromGuid(Guid value) => new(value);

    /// <summary>
    /// Parses the persisted string form. Returns <see langword="false"/> for anything
    /// unrecognized rather than coercing it to a plausible known value (0003 §4).
    /// </summary>
    public static bool TryParse(string? text, out StagedIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(text) && Guid.TryParse(text, out var guid) && guid != Guid.Empty)
        {
            identity = new StagedIdentity(guid);
            return true;
        }

        identity = default;
        return false;
    }

    /// <summary>The persisted string form ("D" format, lowercase).</summary>
    public override string ToString() => _value.ToString("D");
}
