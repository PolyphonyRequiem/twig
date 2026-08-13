using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;

namespace Twig.DetailHost;

/// <summary>
/// The external-host probe for wayfinder-detail-projection ticket 0003.
/// </summary>
/// <remarks>
/// <para>
/// The whole call chain from a consumer's point of view is three lines — construct nothing,
/// authenticate to nothing, open nothing:
/// </para>
/// <code>
/// var document = WorkItemDetailProjector.Project(layout, snapshot, fieldDefinitions);
/// pane.Load(document, appearance);
/// Console.WriteLine(pane.Render());
/// </code>
/// <para>
/// This program also ASSERTS the acceptance floor and exits non-zero if the probe ever
/// stops proving what it claims. A sample that only prints can rot into a demo.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        // === The entire consumer contract ===
        WorkItemDetailDocument document =
            WorkItemDetailProjector.Project(Fixture.Layout, Fixture.Snapshot, Fixture.FieldDefinitions);
        WorkItemTypeAppearance appearance = Fixture.Appearance; // asked for separately
        // =====================================

        var pane = new HostPane(width: 76, height: 22);
        pane.Load(document, appearance);

        Console.WriteLine("=== caller-owned pane, as first painted ===");
        Console.WriteLine(pane.Render());

        // Scrolling and selection are the host's, so the host can just do them.
        for (var i = 0; i < 6; i++) pane.MoveSelection(1);

        Console.WriteLine();
        Console.WriteLine("=== caller-owned selection moved onto the long value ===");
        Console.WriteLine(pane.Render());

        var expanded = pane.SelectedFullValue;
        if (expanded is null)
        {
            Console.WriteLine();
            Console.WriteLine("PROBE FAILED: the selected long value had no full source value to expand.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== expanded view of the selected row (full source value, never truncated) ===");
        Console.WriteLine(expanded);

        pane.Scroll(12);
        Console.WriteLine();
        Console.WriteLine("=== after caller-owned scroll, revealing the server-rendered pages ===");
        Console.WriteLine(pane.Render());

        var failures = CheckAcceptanceFloor(document);
        failures.AddRange(CheckTwoSinkDifference(document));
        Console.WriteLine();
        if (failures.Count > 0)
        {
            Console.WriteLine("PROBE FAILED:");
            foreach (var failure in failures) Console.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine(
            "PROBE OK: all three field states, a non-custom page, and a contribution control exercised, " +
            "and the editable control set demonstrably followed the sink.");
        return 0;
    }

    /// <summary>
    /// Wayfinder 0005 §7 / 0006 §6 M4: proves the change sink is a real seam by loading the
    /// SAME document twice with two different sinks and showing the editable control set moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Why this is stronger than asserting the two declarations are unequal.</b> Two
    /// unequal <c>HashSet</c>s prove only that someone typed different strings. What the seam
    /// claims is that the <i>host's rendered editable surface</i> is a consequence of which
    /// sink was supplied — so the check below names specific controls and asserts their
    /// editability FLIPPED between the two loads, in both directions, on one unchanged
    /// document.
    /// </para>
    /// <para>
    /// The document is projected once and reused deliberately: it is the same object both
    /// times, which is what makes "the sink caused it" the only available explanation.
    /// <c>WorkItemDetailProjector.Project</c> was never told a sink exists.
    /// </para>
    /// </remarks>
    private static List<string> CheckTwoSinkDifference(WorkItemDetailDocument document)
    {
        var failures = new List<string>();

        var sinkA = new TwigTuiSinkA();
        var sinkB = new ReviewQueueSink(
            Fixture.ReadRevision, Fixture.AdvancedRemoteRevision, Fixture.RemoteValues);

        var capabilityA = new EditCapability(sinkA, Fixture.Type);
        var capabilityB = new EditCapability(sinkB, Fixture.Type);

        var paneA = new HostPane(width: 76, height: 22);
        paneA.Load(document, Fixture.Appearance, capabilityA);

        var paneB = new HostPane(width: 76, height: 22);
        paneB.Load(document, Fixture.Appearance, capabilityB);

        Console.WriteLine();
        Console.WriteLine("=== the SAME document under sink A (twig's staging store) ===");
        Console.WriteLine(paneA.Render());
        Console.WriteLine();
        Console.WriteLine("=== the SAME document under sink B (this host's review queue) ===");
        Console.WriteLine(paneB.Render());

        // 1. The declarations differ at all. Necessary, nowhere near sufficient.
        if (sinkA.PersistableFieldRefs.SetEquals(sinkB.PersistableFieldRefs))
        {
            failures.Add(
                "two-sink difference lost: sink B declares the SAME field set as sink A " +
                $"({string.Join(", ", sinkB.PersistableFieldRefs.Order())}) — a second sink that " +
                "declares what the first one does proves the interface compiles, not that the seam " +
                "carries the decision");
        }

        // 2. Each control that flipped, named. This is the observable consequence: the pane
        //    rendered the same document differently BECAUSE the sink changed.
        (string ControlId, string Reason)[] mustFlip =
        [
            ("System.Title",
                "sink A's staging store can push it; a review queue holds verdicts, not titles"),
            ("System.AssignedTo",
                "a review queue has no authority to reassign an item"),
            ("System.Description",
                "the reviewed content itself — the queue persists it, the staging store cannot"),
            ("Contoso.Compliance.ReviewTicket",
                "a process-specific compliance field only the review host knows about"),
        ];

        foreach (var (controlId, reason) in mustFlip)
        {
            var inA = paneA.IsEditable(controlId);
            var inB = paneB.IsEditable(controlId);
            if (inA == inB)
            {
                failures.Add(
                    $"control '{controlId}' rendered editable={inA} under BOTH sinks — its editability " +
                    $"was supposed to flip when the sink changed ({reason}); the editable control set " +
                    "is no longer a consequence of the sink");
            }
        }

        // 3. The two panes' editable sets must be disjoint, which is what a genuinely
        //    different-in-kind field set means here, not merely "one field longer".
        if (paneA.EditableControlIds.Overlaps(paneB.EditableControlIds))
        {
            failures.Add(
                "the two sinks' rendered editable control sets overlap on " +
                $"[{string.Join(", ", paneA.EditableControlIds.Where(paneB.IsEditable).Order())}] — " +
                "sink B was supposed to differ in KIND from sink A, not by degree");
        }

        // 4. Neither pane may make everything typable. If it did, the pane would be reading
        //    the server's read-only flag (or nothing at all) rather than the sink.
        var fieldControlIds = document.Pages
            .SelectMany(page => page.AllGroups)
            .SelectMany(group => group.Controls)
            .Where(control => control is { Visible: true, IsContribution: false })
            .Select(control => control.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, pane) in new[] { ("A", paneA), ("B", paneB) })
        {
            if (fieldControlIds.SetEquals(pane.EditableControlIds))
            {
                failures.Add(
                    $"sink {label} made EVERY visible field control editable — editability stopped " +
                    "being sink-declared and is tracking the layout instead");
            }
        }

        // 5. A read-only host still needs no sink: the same document, loaded with no
        //    capability, makes nothing typable. The projection never learned a sink exists.
        var readOnlyPane = new HostPane(width: 76, height: 22);
        readOnlyPane.Load(document, Fixture.Appearance);
        if (readOnlyPane.EditableControlIds.Count != 0)
        {
            failures.Add(
                "a pane loaded with NO capability still rendered editable controls — a read-only " +
                "host is supposed to need no store and no sink");
        }

        // 6. The sink's own contract, both directions: a declared field is accepted, an
        //    undeclared one is REFUSED rather than silently dropped.
        var accepted = sinkB.SubmitAsync(
            new FieldEdit("System.Description", "before", "after")).GetAwaiter().GetResult();
        if (accepted is not Conflicted)
        {
            failures.Add(
                "sink B did not report Conflicted for a declared field written against a stale " +
                $"revision (read {Fixture.ReadRevision}, remote {Fixture.AdvancedRemoteRevision}) — " +
                "the collision arm has degraded into the happy path");
        }

        var refused = sinkB.SubmitAsync(
            new FieldEdit("System.Title", "before", "after")).GetAwaiter().GetResult();
        if (refused is not Refused)
        {
            failures.Add(
                "sink B accepted a write to 'System.Title', which it never declared — a sink that " +
                "takes an undeclared field is the silent-loss failure this contract exists to prevent");
        }

        return failures;
    }

    /// <summary>
    /// Ticket 0002 §11: the fixture must exercise all three field states, at least one
    /// non-<c>custom</c> page, and one contribution control. Anything less proves only the
    /// happy path.
    /// </summary>
    private static List<string> CheckAcceptanceFloor(WorkItemDetailDocument document)
    {
        var failures = new List<string>();

        var controls = document.Pages
            .SelectMany(page => page.AllGroups)
            .SelectMany(group => group.Controls)
            .ToList();

        var states = controls
            .Where(control => control.Value is not null)
            .Select(control => control.Value!.State)
            .ToHashSet();

        foreach (var required in Enum.GetValues<DetailFieldState>())
        {
            if (!states.Contains(required)) failures.Add($"no control resolved to {required}");
        }

        if (!document.Pages.Any(page => !page.CarriesFieldControls))
        {
            failures.Add("no non-custom page carried");
        }

        if (!controls.Any(control => control.IsContribution))
        {
            failures.Add("no contribution control carried");
        }

        // The core-field hole ticket 0002 exists to close: System.Title is absent from
        // WorkItemSnapshot.Fields entirely, so a naive host blanks it.
        var title = controls.FirstOrDefault(control =>
            control.Id.Equals("System.Title", StringComparison.OrdinalIgnoreCase));
        if (title?.Value?.State != DetailFieldState.HasValue)
        {
            failures.Add("System.Title did not resolve to HasValue — the core-field hole is open");
        }

        // A long value must carry BOTH forms.
        var description = controls.FirstOrDefault(control =>
            control.Id.Equals("System.Description", StringComparison.OrdinalIgnoreCase));
        if (description?.Value is not { State: DetailFieldState.HasValue, Short: not null, Full: not null })
        {
            failures.Add("System.Description did not carry both a full value and a short form");
        }
        else if (!description.Value.Full!.Contains("never a replacement", StringComparison.Ordinal))
        {
            failures.Add("System.Description's full value was truncated by the projection");
        }

        return failures;
    }
}
