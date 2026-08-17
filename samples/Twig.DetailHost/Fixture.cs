using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.DetailHost;

/// <summary>
/// Representative work-item and form-layout data, hand-built the way a host's own caller
/// would hand it over.
/// </summary>
/// <remarks>
/// <para>
/// The layout is constructed from fixture data rather than through
/// <c>IFormLayoutProvider</c> on purpose: that interface's only implementation is
/// internal to Twig.Infrastructure and needs an <c>HttpClient</c> plus ADO
/// authentication. A probe that went through it would be proving Twig's acquisition path,
/// not the consumer boundary.
/// </para>
/// <para>
/// <b>The fixture is built to the acceptance floor, not to the happy path.</b> It carries
/// all three field-value states, a non-<c>custom</c> page, a contribution group and a
/// contribution control, an invisible control, a read-only control, a process-specific
/// field, and a long HTML value.
/// </para>
/// </remarks>
internal static class Fixture
{
    /// <summary>Field metadata the host's caller happens to have. Optional to the projection.</summary>
    internal static IReadOnlyDictionary<string, FieldDefinition> FieldDefinitions { get; } =
        new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            // Importable and present -> HasValue.
            ["System.Description"] = new("System.Description", "Description", "html", true),
            ["Microsoft.VSTS.Common.Priority"] = new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            ["Contoso.Compliance.ReviewTicket"] = new("Contoso.Compliance.ReviewTicket", "Review ticket", "string", false),

            // Importable but the server sent nothing -> EmptyOnServer.
            ["Microsoft.VSTS.Common.AcceptanceCriteria"] =
                new("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance Criteria", "html", false),

            // FieldImportFilter refuses booleans outright -> NotCarriedByTwig.
            ["Contoso.Compliance.SignedOff"] = new("Contoso.Compliance.SignedOff", "Signed off", "boolean", false),

            // Server read-only and not on the display-worthy allowlist -> NotCarriedByTwig.
            ["Microsoft.VSTS.Common.StackRank"] = new("Microsoft.VSTS.Common.StackRank", "Stack Rank", "double", true),
        };

    internal static WorkItemSnapshot Snapshot { get; } = new()
    {
        Id = 155,
        Revision = 12,
        TypeName = "User Story",
        Title = "Expose a hostable work-item detail projection",
        State = "Active",
        AssignedTo = "Daniel Green",
        IterationPath = @"Twig\Sprint 9",
        AreaPath = @"Twig\Projection",
        Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.Description"] =
                "<div>A read-only host must be able to render the server-authored form without " +
                "importing Twig.Infrastructure, a database, or a terminal toolkit.</div>" +
                "<div>The projection carries the complete source value so an expanded view is " +
                "always possible; the short form is a convenience over it, never a replacement.</div>",
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Contoso.Compliance.ReviewTicket"] = "SEC-4471",
            // Deliberately absent: AcceptanceCriteria, SignedOff, StackRank.
        },
    };

    /// <summary>Twig's look-and-feel opinion. Travels SEPARATELY from the document.</summary>
    internal static WorkItemTypeAppearance Appearance { get; } =
        new("User Story", "#009CCC", "story");

    /// <summary>
    /// The item's type, which an editing host must supply alongside the sink.
    /// </summary>
    /// <remarks>
    /// Parsed rather than hand-constructed because <c>WorkItemType</c>'s constructor is
    /// private — a consumer gets one the same way any caller does.
    /// </remarks>
    internal static WorkItemType Type { get; } = WorkItemType.Parse(Snapshot.TypeName).Value;

    /// <summary>
    /// What the shared review queue can see on the remote item, for the collision arm.
    /// </summary>
    /// <remarks>
    /// The remote value differs from what the fixture snapshot carries — 0006 §8's
    /// "fixtures must not degrade into the happy path": a conflict arm whose remote value
    /// matched the local one would report a collision nobody could observe.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string?> RemoteValues { get; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
            ["Contoso.Compliance.ReviewTicket"] = "SEC-4471-REOPENED",
        };

    /// <summary>The revision the host read the item at.</summary>
    internal const int ReadRevision = 12;

    /// <summary>
    /// Where the shared queue's item actually is — deliberately ahead of
    /// <see cref="ReadRevision"/>, so the conflict branch is genuinely reachable.
    /// </summary>
    internal const int AdvancedRemoteRevision = 14;

    /// <summary>
    /// A remote revision that has NOT moved since the host read — the settled case, where the
    /// queue accepts the write instead of reporting a collision.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Without this the sink's accept path was unreachable from the probe.</b> Every
    /// submit the floor made went through <see cref="AdvancedRemoteRevision"/>, so the sink
    /// could have returned any revision it liked — or stopped queueing entirely — and nothing
    /// would have gone red. That is the AB#341 shape: a floor that gates a behaviour it never
    /// executes. Deliberately equal to <see cref="ReadRevision"/> rather than restating <c>12</c>,
    /// so a fixture edit that moves the read revision cannot silently make this one stale.
    /// </remarks>
    internal const int SettledRemoteRevision = ReadRevision;

    // ═══════════════════════════════════════════════════════════════════════════
    //  0006 §8, M5: "a state with a legal and an illegal target"
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The process rules a host's caller happens to have, built the way an arms-length
    /// consumer must build them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This is not optional decoration — without it the M5 arm cannot run at all.</b>
    /// <c>EditCapability</c> takes its process configuration as an optional constructor
    /// argument, and with none supplied <c>OfferedStates</c> returns EMPTY while
    /// <c>Validate</c> ACCEPTS every state move by design ("absent metadata degrades to
    /// I-don't-know, never to a confident refusal"). A transition check written against a
    /// capability built without one would fail its legal arm and pass its illegal arm
    /// <i>vacuously</i>.
    /// </para>
    /// <para>
    /// Built through <see cref="ProcessConfiguration.FromRecords"/> rather than through
    /// <c>Twig.TestKit</c>'s <c>ProcessConfigBuilder</c> on purpose: this sample's single
    /// <c>ProjectReference</c> to <c>Twig.Domain</c> IS the boundary evidence ticket 0003
    /// exists to produce, and reaching into a test kit would defeat it. A real consumer has
    /// no test kit either.
    /// </para>
    /// <para>
    /// <b>Two types, not one, and that is load-bearing.</b> <see cref="IllegalTarget"/> is a
    /// state that genuinely exists in this process — on <c>Bug</c> — but not on
    /// <c>User Story</c>. An illegal target invented as a nonsense string would also be
    /// refused, but by a check no weaker than "is this word known anywhere", which is not
    /// the per-type scoping the offer filter claims.
    /// </para>
    /// </remarks>
    internal static ProcessConfiguration Process { get; } = ProcessConfiguration.FromRecords(
    [
        new ProcessTypeRecord
        {
            TypeName = "User Story",
            States =
            [
                new StateEntry("New", StateCategory.Proposed, null),
                new StateEntry("Active", StateCategory.InProgress, null),
                new StateEntry("Resolved", StateCategory.Resolved, null),
                new StateEntry("Closed", StateCategory.Completed, null),
                new StateEntry("Removed", StateCategory.Removed, null),
            ],
        },
        new ProcessTypeRecord
        {
            TypeName = "Bug",
            States =
            [
                new StateEntry("New", StateCategory.Proposed, null),
                new StateEntry("Escalated", StateCategory.InProgress, null),
                new StateEntry("Closed", StateCategory.Completed, null),
            ],
        },
    ]);

    /// <summary>
    /// The state the item is actually in — the one an offer list is computed FROM.
    /// </summary>
    /// <remarks>
    /// Deliberately read off <see cref="Snapshot"/> rather than restated, so a fixture edit
    /// that moved the item cannot leave the transition arm silently evaluating a state the
    /// rendered document is not in.
    /// </remarks>
    internal static string CurrentState { get; } = Snapshot.State;

    /// <summary>A target the process permits from <see cref="CurrentState"/>.</summary>
    internal const string LegalTarget = "Resolved";

    /// <summary>
    /// A target the process does NOT permit from <see cref="CurrentState"/>, because it
    /// belongs to a different work item type.
    /// </summary>
    internal const string IllegalTarget = "Escalated";

    /// <summary>
    /// The EXACT set <c>OfferedStates(<see cref="CurrentState"/>)</c> must return.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Exact, not "contains the legal one and not the illegal one."</b> An
    /// <c>OfferedStates</c> gutted to <c>return []</c> satisfies every absence check and half
    /// the presence checks a looser assertion would make, leaving the whole arm hollow — the
    /// fixture-degradation class AGENTS.md records <c>ConflictResolver.Resolve</c> as the
    /// worked example of. Every state of the type except the current one, which is what the
    /// offer-time filter claims to compute.
    /// </remarks>
    internal static IReadOnlyList<string> ExpectedOfferedStates { get; } =
        ["New", "Resolved", "Closed", "Removed"];

    internal static FormLayout Layout { get; } = new(
        WorkItemTypeReferenceName: "Microsoft.VSTS.WorkItemTypes.UserStory",
        ProcessId: "adcc42ab-9882-485e-a3ed-7678f01f66bc",
        Pages:
        [
            new LayoutPage(
                Id: "Details",
                Label: "Details",
                PageType: "custom",
                Visible: true,
                IsContribution: false,
                Sections:
                [
                    new LayoutSection("Section1",
                    [
                        new LayoutGroup("g-identity", "Identity", Visible: true, IsContribution: false,
                        [
                            // Core field: excluded from Fields, resolved from the snapshot's
                            // own property. HasValue, not NotCarriedByTwig.
                            new LayoutControl("System.Title", "Title", "FieldControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            new LayoutControl("System.State", "State", "FieldControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            new LayoutControl("System.AssignedTo", "Assigned To", "IdentityControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            // Server read-only, and the server also hid it. Both facts are
                            // reported; neither is enforced or filtered on.
                            new LayoutControl("Microsoft.VSTS.Common.StackRank", "Stack Rank", "FieldControl",
                                ReadOnly: true, Visible: false, IsContribution: false),
                        ]),
                        new LayoutGroup("g-detail", "Detail", Visible: true, IsContribution: false,
                        [
                            new LayoutControl("System.Description", "Description", "HtmlFieldControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            new LayoutControl("Microsoft.VSTS.Common.AcceptanceCriteria", "Acceptance Criteria",
                                "HtmlFieldControl", ReadOnly: false, Visible: true, IsContribution: false),
                        ]),
                    ]),
                    new LayoutSection("Section2",
                    [
                        new LayoutGroup("g-planning", "Planning", Visible: true, IsContribution: false,
                        [
                            new LayoutControl("Microsoft.VSTS.Common.Priority", "Priority", "FieldControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            new LayoutControl("System.IterationPath", "Iteration", "WorkItemClassificationControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                        ]),
                        new LayoutGroup("g-compliance", "Compliance", Visible: true, IsContribution: false,
                        [
                            // Process-specific field. Ordinary control, ordinary reference name.
                            new LayoutControl("Contoso.Compliance.ReviewTicket", "Review ticket", "FieldControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            // Boolean: FieldImportFilter refuses it, so the document says so
                            // rather than blanking it.
                            new LayoutControl("Contoso.Compliance.SignedOff", "Signed off", "FieldControl",
                                ReadOnly: false, Visible: true, IsContribution: false),
                            // A third-party add-in with a name and a position but no field.
                            new LayoutControl("ms.vss-work-web.risk-assessment-control", "Risk assessment",
                                "Contribution", ReadOnly: false, Visible: true, IsContribution: true),
                        ]),
                        // A whole add-in group.
                        new LayoutGroup("contoso.audit-group", "Audit trail",
                            Visible: true, IsContribution: true, Controls: []),
                    ]),
                ]),

            // Server-rendered surfaces. Carried flagged, not filtered: the host may want a
            // disabled tab, and a projection that dropped them would leave no way back.
            new LayoutPage("Links", "Links", "links", Visible: true, IsContribution: false, Sections: []),
            new LayoutPage("History", "History", "history", Visible: true, IsContribution: false, Sections: []),
        ]);
}
