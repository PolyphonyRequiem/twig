namespace Twig.Domain.ValueObjects;

/// <summary>
/// The negative-integer <b>display alias</b> of a staged seed.
/// <para>
/// Persisted once at creation and stable across sessions so a script can reference it
/// (0003 §5, owner veto of the original per-view call). The constraints that survive from
/// 0003 §5a and are enforced by the schema, not by prose:
/// <b>never a key, never joined on, never a foreign key target, and never recycled</b> —
/// a discarded seed's alias is retired, not reissued.
/// </para>
/// <para>
/// An allocator whose output is decorative may reuse a durable floor; one whose output is
/// identity may not. <see cref="StagedIdentity"/> is the identity; this is the label hanging
/// off it, so a collision here would be a display annoyance rather than the #280 correctness
/// defect — and even that is prevented, because the floor is durable and monotonic.
/// </para>
/// </summary>
public readonly record struct StagedAlias
{
    private readonly int _value;

    private StagedAlias(int value) => _value = value;

    /// <summary>The negative integer shown to the user.</summary>
    public int Value => _value;

    /// <summary>True when this is the default, unassigned alias.</summary>
    public bool IsEmpty => _value == 0;

    /// <summary>
    /// Rehydrates an alias from its persisted integer form. Only negative values are aliases;
    /// a non-negative integer is an ADO work item ID and is rejected rather than coerced.
    /// </summary>
    public static bool TryFrom(int value, out StagedAlias alias)
    {
        if (value < 0)
        {
            alias = new StagedAlias(value);
            return true;
        }

        alias = default;
        return false;
    }

    /// <summary>
    /// The alias one step below <paramref name="floor"/>. The floor is read from the durable
    /// register, which never deletes a row, so this cannot reissue a retired alias.
    /// </summary>
    public static StagedAlias Below(int floor) => new(Math.Min(floor, 0) - 1);

    public override string ToString() => _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
