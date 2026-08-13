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
