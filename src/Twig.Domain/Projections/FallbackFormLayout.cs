using Twig.Domain.ValueObjects;

namespace Twig.Domain.Projections;

/// <summary>
/// Builds a Twig-authored <see cref="FormLayout"/> for an item whose server layout is
/// absent — the <c>null</c> return from <c>IFormLayoutProvider.GetFormLayoutAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fallback is a layout, not a second field-selection path.</b> A host that
/// degrades by keeping its own hard-coded field list ends up with two implementations of
/// "which fields do we show" that drift apart. Producing a <see cref="FormLayout"/>
/// instead means every host keeps exactly one walk: project this, or project the
/// server's, and paint the same document either way.
/// </para>
/// <para>
/// <b>It cannot simply enumerate <see cref="WorkItemSnapshot.Fields"/>.</b> That
/// dictionary is missing all eight core fields — <c>FieldImportFilter.CoreFieldRefs</c>
/// excludes them because they were promoted to snapshot properties — so a form built from
/// it alone would have no Title, State, or Assigned To. The arrangement is therefore the
/// eight core fields first, in a stable Twig-authored order, then every carried field in
/// the snapshot's own order.
/// </para>
/// <para>
/// <b>Every control it emits is resolvable.</b> It names only fields Twig demonstrably
/// carries, so projecting it yields <see cref="DetailFieldState.HasValue"/> or
/// <see cref="DetailFieldState.EmptyOnServer"/> and never
/// <see cref="DetailFieldState.NotCarriedByTwig"/>. That is the honest shape: with no
/// server layout, Twig does not know which fields the form *should* have, so it claims
/// only what it can show and does not invent absent rows.
/// </para>
/// <para>
/// It is distinguishable from a real layout: <see cref="FormLayout.ProcessId"/> is
/// <see cref="FallbackProcessId"/> and the single page's id is <see cref="FallbackPageId"/>,
/// so a host that wants to say "this arrangement is Twig's, not your server's" can.
/// An <i>empty</i> served layout is a different fact and is never routed here — the parse
/// already distinguishes the two, and an empty layout means the server said there are no
/// controls.
/// </para>
/// </remarks>
public static class FallbackFormLayout
{
    /// <summary><see cref="FormLayout.ProcessId"/> on a Twig-authored fallback layout.</summary>
    public const string FallbackProcessId = "twig.fallback";

    /// <summary>Id of the single page a fallback layout carries.</summary>
    public const string FallbackPageId = "twig.fallback.page";

    /// <summary>Id of the single group a fallback layout carries.</summary>
    public const string FallbackGroupId = "twig.fallback.group";

    /// <summary>
    /// The eight core fields, in the order a fallback form presents them. Their values
    /// come from <see cref="WorkItemSnapshot"/>'s own properties, which is why they can be
    /// named here at all.
    /// </summary>
    private static readonly (string RefName, string Label, bool ReadOnly)[] CoreControls =
    [
        ("System.Id", "ID", true),
        ("System.WorkItemType", "Type", true),
        ("System.Title", "Title", false),
        ("System.State", "State", false),
        ("System.AssignedTo", "Assigned To", false),
        ("System.IterationPath", "Iteration", true),
        ("System.AreaPath", "Area", true),
        ("System.Rev", "Revision", true),
    ];

    /// <summary>Control type reported for every control a fallback layout emits.</summary>
    public const string FallbackControlType = "FieldControl";

    /// <summary>
    /// Builds the fallback layout for <paramref name="snapshot"/>.
    /// </summary>
    /// <param name="snapshot">The item whose carried fields shape the arrangement.</param>
    public static FormLayout For(WorkItemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var controls = new List<LayoutControl>(CoreControls.Length + snapshot.Fields.Count);

        foreach (var (refName, label, readOnly) in CoreControls)
            controls.Add(new LayoutControl(refName, label, FallbackControlType, readOnly, true, false));

        foreach (var refName in snapshot.Fields.Keys)
        {
            controls.Add(new LayoutControl(
                refName, LabelFor(refName), FallbackControlType, true, true, false));
        }

        var group = new LayoutGroup(FallbackGroupId, "Fields", true, false, controls);
        var section = new LayoutSection("twig.fallback.section", [group]);
        var page = new LayoutPage(FallbackPageId, "Details", "custom", true, false, [section]);

        return new FormLayout(
            string.IsNullOrEmpty(snapshot.TypeName) ? "Unknown" : snapshot.TypeName,
            FallbackProcessId,
            [page]);
    }

    /// <summary><c>true</c> when <paramref name="layout"/> is a Twig-authored fallback.</summary>
    public static bool IsFallback(FormLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return string.Equals(layout.ProcessId, FallbackProcessId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Derives a display label from a reference name — the trailing segment, space-separated
    /// on case boundaries. The server supplies real labels; without one this is the best
    /// Twig can honestly do, and it never invents a name the reference name does not contain.
    /// </summary>
    private static string LabelFor(string referenceName)
    {
        var lastDot = referenceName.LastIndexOf('.');
        var leaf = lastDot >= 0 && lastDot < referenceName.Length - 1
            ? referenceName[(lastDot + 1)..]
            : referenceName;

        var builder = new System.Text.StringBuilder(leaf.Length + 4);
        for (var i = 0; i < leaf.Length; i++)
        {
            if (i > 0 && char.IsUpper(leaf[i]) && !char.IsUpper(leaf[i - 1]))
                builder.Append(' ');
            builder.Append(leaf[i]);
        }

        return builder.ToString();
    }
}
