using Twig.Domain.ValueObjects;
using Twig.RenderTree;

namespace Twig.Domain.Services.Process;

/// <summary>
/// Projects an assembled <see cref="ProcessDescription"/> into the render tree that becomes
/// the emitted document. The ONE model-to-document projection, shared by the CLI verb and the
/// agent surface.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This type exists because routing both surfaces through
/// <see cref="ProcessDescriptionAssembler"/> is necessary for one document format but NOT
/// sufficient (AB#241).</b> The assembler is the single ordering authority and produces the
/// document MODEL; the model still has to be projected to bytes, and until this ticket that
/// projection lived privately inside the CLI command in the <c>twig</c> executable — a project
/// <c>Twig.Mcp</c> does not and must not reference. An agent surface written the obvious way
/// would therefore have written its own projection, and "the agent gets the same document with
/// fewer types" would have been a convention two surfaces drift apart on rather than the
/// structural fact acceptance criterion 2 requires. Sharing the assembler alone would have left
/// a byte-identity test comparing two independently-authored serializers.
/// </para>
/// <para>
/// 🔴 <b>Ordering is NOT decided here, and neither is selection.</b> Every collection arrives
/// from the assembler already sorted and every method below walks it in the order given.
/// Selection of WHICH TYPES happened in the assembler before this type ever sees the model.
/// </para>
/// <para>
/// 🔴 <b>There is no per-part selection here and there must never be one.</b> This projection
/// takes a description and a completeness flag; it has no parameter naming which parts of a
/// type to emit, and adding one would be the filter Solution S3 bans — a reader handed a
/// filtered document cannot recover what was dropped and cannot tell that anything was. The
/// <c>isComplete</c> flag is NOT such a filter: it selects between two whole RENDERINGS, and
/// the abridged one declares itself and names the format that carries everything.
/// </para>
/// <para>
/// Lives in the domain layer for the same reason the assembler does: it is surface-neutral, and
/// putting it in either surface would make the other one reach across. <c>internal</c>,
/// consistent with Implementation Decision 9 — the file is the only public promise.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c>
/// — the seam section, Solution S2 and S3, Implementation Decisions 8, 9, 10.
/// </para>
/// </remarks>
internal static class ProcessDescriptionDocument
{
    /// <summary>
    /// 🔴 The <c>-o</c> value that produces the COMPLETE document, and the one the abridged
    /// rendering's banner names.
    /// </summary>
    /// <remarks>
    /// Named here once and read by both the CLI's completeness predicate and the banner below,
    /// so the banner cannot come to name a format that does not produce the complete document.
    /// A banner pointing at a nonexistent format would satisfy a bare string-presence test while
    /// telling the reader a lie.
    /// </remarks>
    internal const string CompleteFormat = "json";

    /// <summary>
    /// Renders the complete document to a string — the exact bytes every surface emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>The render SETTINGS are part of byte-identity, and naming them here is the point
    /// (AB#241).</b> Sharing the assembler and the projection makes both surfaces produce the
    /// same render tree, but a render tree is not bytes: two callers constructing
    /// <see cref="JsonRenderer"/> with different options produce different documents from an
    /// identical tree. Independent review caught exactly that — the agent surface hardcoded
    /// <c>indented: true</c> while the CLI reached it through its renderer factory, leaving
    /// byte-identity resting on two literals agreeing, which is the convention this ticket
    /// exists to replace with a structural fact.
    /// </para>
    /// <para>
    /// The CLI still goes through its factory, because it must honour <c>-o</c> and the
    /// abridged renderings; this method is the path for a surface that has no format choice.
    /// The shared constant below is what keeps the two from drifting.
    /// </para>
    /// </remarks>
    internal static string Render(ProcessDescription description)
    {
        var buffer = new StringWriter();
        new JsonRenderer(buffer, Indented).Render(BuildTree(description, isComplete: true));
        return buffer.ToString();
    }

    /// <summary>
    /// Whether the complete document is pretty-printed.
    /// </summary>
    /// <remarks>
    /// 🔴 Named once and asserted against by the byte-identity tests, so a change here moves
    /// every surface together. <c>RendererFactory</c>'s <c>json</c> arms pass the same value;
    /// they are a different layer and cannot reference this, so if you change one, change both —
    /// the byte-identity test reds if they disagree, which is the mechanism that makes this
    /// note enforceable rather than a hope.
    /// </remarks>
    internal const bool Indented = true;

    /// <remarks>
    /// 🔴 <b>Ordering is NOT decided here.</b> Every collection arrives from the assembler
    /// already sorted and this method walks it in the order given. Re-sorting, grouping, or
    /// projecting through a dictionary at this layer would put a second ordering authority
    /// in the system and byte-stability would depend on both agreeing forever.
    /// </remarks>
    internal static RenderTree.RenderTree BuildTree(ProcessDescription description, bool isComplete)
    {

        var fields = new List<DocumentField>
        {
            new(Key: "header", Node: BuildHeader(description.Header)),
        };

        if (!isComplete)
        {
            // 🔴 The self-declaration. This is the CONDITION on which an abridged rendering
            // was accepted, not decoration: two abridged renderings can diff clean while a
            // real difference sits in the omitted part, and a summary that does not admit it
            // is a summary is exactly the cheap lie this feature exists to prevent.
            //
            // 🔴 NOT HumanOnly. `minimal` and `ids` are machine formats that also render
            // abridged, and tagging this human-only would hand a machine consumer a truncated
            // document carrying no notice that anything was dropped — the worst reader to
            // leave uninformed, because it cannot notice the omission the way a person might.
            // The complete format is NAMED from the constant that selects it, so the banner
            // cannot come to point at a format that does not exist.
            fields.Add(new DocumentField(
                Key: "abridged",
                Node: new RenderNode.Text(
                    $"ABRIDGED RENDERING — this is a summary and omits detail. "
                    + $"The complete document is produced by -o {CompleteFormat}.")));
        }

        fields.Add(new DocumentField(
            Key: "types",
            Node: new RenderNode.TreeView(BuildTypesBranch(description, isComplete)),
            HumanOverride: isComplete ? null : new RenderNode.Section(null, BuildAbridgedTypeLines(description))));

        fields.Add(new DocumentField(
            Key: "typeCount",
            Node: new RenderNode.KeyValue("typeCount", RenderCell.Integer(description.Types.Count)),
            Audience: RenderAudience.MachineOnly));

        return new RenderTree.RenderTree([new RenderNode.Document(null, fields)]);
    }

    private static RenderNode BuildHeader(ProcessDescriptionHeader header)
    {
        var routeRows = header.RouteApiVersions
            .Select(route => new RenderRow("route", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["route"] = RenderCell.String(route.Route),
                ["apiVersion"] = RenderCell.String(route.ApiVersion),
            }))
            .ToList();

        var gapRows = header.KnownGaps
            .Select(gap => new RenderRow("gap", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["subject"] = RenderCell.String(gap.Subject),
                ["detail"] = RenderCell.String(gap.Detail),
                ["trackedIn"] = RenderCell.String(gap.TrackedIn),
            }))
            .ToList();

        return new RenderNode.Document("descriptionHeader",
        [
            new DocumentField("organization", new RenderNode.KeyValue("organization", RenderCell.String(header.Organization))),
            new DocumentField("project", new RenderNode.KeyValue("project", RenderCell.String(header.ProjectName))),
            new DocumentField("processId", new RenderNode.KeyValue("processId", RenderCell.String(header.ProcessId))),
            new DocumentField("processName", new RenderNode.KeyValue("processName", RenderCell.String(header.ProcessName))),
            // 🔴 The single permitted variance between two runs, and it sits HERE — in the
            // header, where a diff tool can be pointed past it — never interleaved into the
            // body. Round-tripped through "O" so the string form is culture-invariant: a
            // culture-formatted timestamp would make the same instant render differently on
            // two machines, which is a second source of variance disguised as one.
            new DocumentField("capturedAt", new RenderNode.KeyValue(
                "capturedAt",
                RenderCell.String(header.CapturedAtUtc.ToUniversalTime().ToString(
                    "O", System.Globalization.CultureInfo.InvariantCulture)))),
            new DocumentField("descriptorVersion", new RenderNode.KeyValue(
                "descriptorVersion", RenderCell.String(header.DescriptorVersion))),
            // Machine-only SHAPE: the human rendering draws these tables as empty box
            // skeletons because they carry no column metadata. The same facts reach a human
            // through the prose blocks below — the acceptance criteria require the header to
            // CARRY the pinned api-version per route and the known gaps, and "carry" is not
            // satisfied by a rendering that drops them.
            new DocumentField("routeApiVersions", new RenderNode.Table(null, [], routeRows),
                Audience: RenderAudience.MachineOnly),
            new DocumentField("knownGaps", new RenderNode.Table(null, [], gapRows),
                Audience: RenderAudience.MachineOnly),
            // 🔴 The human form of the pinned versions. Two descriptions taken months apart
            // must not differ merely because the server moved, and a reader of the DEFAULT
            // rendering needs to be able to see which version each route was read at.
            new DocumentField(
                "routeApiVersionsHuman",
                new RenderNode.Section("Pinned api-version per route:",
                [
                    .. header.RouteApiVersions.Select(route => (RenderNode)new RenderNode.Text(
                        $"  {route.Route,-52} {route.ApiVersion}")),
                ]),
                Audience: RenderAudience.HumanOnly),
            // 🔴 The human reader is told the same reservation, in prose. An incomplete
            // document that only admits its incompleteness in the machine format would let
            // the person most likely to over-trust it never see the warning.
            //
            // 🔴 The EMPTY case is rendered POSITIVELY rather than as a bare heading (AB#237
            // emptied this list). "KNOWN INCOMPLETE — do not treat this document as
            // authoritative about:" followed by nothing is a worse artifact than either
            // state: it reads as a warning whose subject was lost, so a reader cannot tell
            // whether the document has no reservations or the renderer dropped them. Dropping
            // the section entirely was the other candidate and is worse still — silence is
            // exactly what an older, genuinely-incomplete document also produces in a format
            // that omitted the section, so the reader could not distinguish "makes no
            // reservations" from "does not implement reservations". Saying so in one line
            // makes the absence a CLAIM, which is what a diff of two documents needs.
            new DocumentField(
                "knownGapsHuman",
                header.KnownGaps.Count == 0
                    ? new RenderNode.Section(null,
                    [
                        new RenderNode.Text(
                            "This document declares no known gaps: it makes no reservations "
                            + "about its own completeness."),
                    ])
                    : new RenderNode.Section("KNOWN INCOMPLETE — do not treat this document as authoritative about:",
                    [
                        .. header.KnownGaps.Select(gap => (RenderNode)new RenderNode.Text(
                            $"  {gap.Subject} ({gap.TrackedIn}): {gap.Detail}")),
                    ]),
                Audience: RenderAudience.HumanOnly),
        ]);
    }

    private static RenderTreeBranch BuildTypesBranch(ProcessDescription description, bool isComplete)
    {
        var typeBranches = new List<RenderTreeBranch>(description.Types.Count);

        foreach (var type in description.Types)
        {
            var children = new List<RenderTreeBranch>();

            // The abridged rendering carries identity and counts only. The complete one
            // carries every field, state and transition — omission is the primary failure
            // mode, so nothing is dropped from -o json.
            if (isComplete)
            {
                foreach (var field in type.Fields)
                {
                    children.Add(new RenderTreeBranch(
                        new RenderRow("field", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            ["referenceName"] = RenderCell.String(field.ReferenceName),
                            ["name"] = RenderCell.String(field.Name),
                            ["type"] = RenderCell.String(field.Type),
                            // 🔴 MERGED requiredness, not the fields route's boolean (AB#236).
                            // A field made mandatory only by a rule reads as not-required on
                            // the fields route, so a bare boolean here would be wrong about
                            // exactly the fields a caller most needs — and wrong silently.
                            // `requiredness` names the case; `requiredWhen` carries the
                            // conditions, which is what makes "conditional" actionable rather
                            // than a warning the reader cannot use.
                            ["requiredness"] = RenderCell.String(
                                RequirednessToken(field.Requiredness.Kind)),
                            ["requiredWhen"] = field.Requiredness.Conditions.Count == 0
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.String(DescribeConditions(field.Requiredness)),
                            // 🔴 Whether the field's value is restricted to a list, read as
                            // an explicit server fact (AB#237). The mirror of `requiredness`:
                            // that one could understate what the process demands, this one
                            // could OVERSTATE it — telling a caller its value must come from
                            // a list when the server accepts anything. `unconstrained` is a
                            // positive claim here, not a default, which is why `unknown` is a
                            // separate token rather than folded into it.
                            ["valueConstraint"] = RenderCell.String(
                                ValueConstraintToken(field.ValueConstraint.Kind)),
                            ["valueList"] = field.ValueConstraint.ListName is null
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.String(field.ValueConstraint.ListName),
                            // Walked in the assembler's sorted order and joined; no ordering
                            // is decided here.
                            ["allowedValues"] = field.ValueConstraint.Values.Count == 0
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.String(string.Join(", ", field.ValueConstraint.Values)),
                            ["defaultValue"] = field.DefaultValue is null
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.String(field.DefaultValue),
                            ["customization"] = RenderCell.String(field.Customization),
                            ["isLocked"] = RenderCell.Boolean(field.IsLocked),
                            ["description"] = RenderCell.String(field.Description),
                        }),
                        []));
                }

                foreach (var state in type.States)
                {
                    children.Add(new RenderTreeBranch(
                        new RenderRow("state", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            ["name"] = RenderCell.String(state.Name),
                            ["stateCategory"] = RenderCell.String(state.StateCategory),
                            ["order"] = RenderCell.Integer(state.Order),
                            ["color"] = RenderCell.String(state.Color),
                            ["customization"] = RenderCell.String(state.Customization),
                            ["isHidden"] = RenderCell.Boolean(state.IsHidden),
                        }),
                        []));
                }

                foreach (var transition in type.Transitions)
                {
                    children.Add(new RenderTreeBranch(
                        new RenderRow("transition", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            // An empty fromState is the INITIAL transition — what state a
                            // new work item enters. Carried as-is; it is a real fact.
                            ["fromState"] = RenderCell.String(transition.FromState),
                            ["toState"] = RenderCell.String(transition.ToState),
                        }),
                        []));
                }

                // 🔴 EVERY rule, inherited ones included (AB#238). There is deliberately no
                // filter here and adding one is the reversal this ticket most fears: a
                // derived type carries ~54 rules of which one or two were authored, so
                // dropping the inherited ones is tempting and wrong. A difference that
                // exists only in the omitted part diffs clean, and a reader handed a
                // filtered document cannot tell anything was dropped. `customization` on
                // every row is what makes the filtering available to the READER instead.
                foreach (var rule in type.Rules)
                {
                    children.Add(new RenderTreeBranch(
                        new RenderRow("rule", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            ["name"] = RenderCell.String(rule.Name),
                            // 🔴 The token, not a paraphrase — and `unknown` for an absent
                            // one rather than a guess at which real class it resembled.
                            ["customization"] = RenderCell.String(
                                RuleCustomizationToken(rule.Customization)),
                            // Carried: a rule disabled on one process and enabled on another
                            // is a real difference and must not diff clean.
                            ["isDisabled"] = RenderCell.Boolean(rule.IsDisabled),
                            // Walked in the assembler's sorted order; no order decided here.
                            ["conditions"] = rule.Conditions.Count == 0
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.String(DescribeRuleConditions(rule.Conditions)),
                            ["actions"] = rule.Actions.Count == 0
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.String(DescribeRuleActions(rule.Actions)),
                        }),
                        []));
                }

                // 🔴 Which backlog levels the type belongs to (AB#238). The reference name is
                // always present; the display name may be empty when the catalogue could not
                // be read, in which case `behaviourCatalogue` is in the type's unfetched list.
                foreach (var behaviour in type.Behaviours)
                {
                    children.Add(new RenderTreeBranch(
                        new RenderRow("behaviour", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            ["referenceName"] = RenderCell.String(behaviour.ReferenceName),
                            ["name"] = RenderCell.String(behaviour.Name),
                            ["rank"] = behaviour.Rank is null
                                ? RenderCell.DisplayOnly(string.Empty)
                                : RenderCell.Integer(behaviour.Rank.Value),
                            ["isDefault"] = RenderCell.Boolean(behaviour.IsDefault),
                        }),
                        []));
                }

                // 🔴 The form layout (AB#238), flattened: one row per PAGE and one per
                // CONTROL, with the page, column and group named on each control row. Flat
                // rather than nested because the render tree carries rows, and because a flat
                // form is what a LINE-oriented diff can actually compare — a nested structure
                // moves every descendant line when one group is inserted. The assembler's
                // order is walked as given: the arrangement IS the content here.
                //
                // 🔴 EVERY member of every layout level reaches a cell, and that is a
                // correctness requirement rather than completeness for its own sake. An
                // earlier draft carried the page flags only on pages that had NO controls and
                // the group flags nowhere at all — so a process that hid a group, or hid a
                // populated page, or marked one inherited-vs-authored differently, produced a
                // byte-identical document. That is precisely the "a difference that exists
                // only in the omitted part diffs clean" failure this feature exists to
                // prevent, arriving in the renderer instead of the assembler.
                if (type.Layout is not null)
                {
                    foreach (var page in type.Layout.Pages)
                    {
                        // 🔴 A page row is emitted UNCONDITIONALLY, not only for pages that
                        // hold no controls. The page's own flags live here, so a page whose
                        // visibility or inheritance changed shows up as one changed line
                        // rather than being invisible; and a process that removed the links
                        // tab differs from one that did not, which dropping empty pages would
                        // diff clean over.
                        children.Add(new RenderTreeBranch(
                            new RenderRow("layoutPage",
                                new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                                {
                                    ["page"] = RenderCell.String(page.Id),
                                    ["pageLabel"] = RenderCell.String(page.Label),
                                    // history / links / attachments pages are server-rendered
                                    // and carry no field controls. They are still part of the
                                    // form a person sees.
                                    ["pageType"] = RenderCell.String(page.PageType),
                                    ["visible"] = RenderCell.Boolean(page.Visible),
                                    ["inherited"] = RenderCell.Boolean(page.Inherited),
                                    ["isContribution"] = RenderCell.Boolean(page.IsContribution),
                                    // 🔴 The server's arrangement key, carried as a fact. Two
                                    // forms whose relative sequence happens to match but whose
                                    // order keys differ are not the same form, and omitting it
                                    // would let that difference diff clean.
                                    ["order"] = OrderCell(page.Order),
                                }),
                            []));

                        foreach (var section in page.Sections)
                        {
                            foreach (var group in section.Groups)
                            {
                                // 🔴 A group row too, unconditionally, for the same reason:
                                // a hidden or newly-inherited group is a real difference, and
                                // a group whose controls were all removed would otherwise
                                // vanish from the document entirely.
                                children.Add(new RenderTreeBranch(
                                    new RenderRow("layoutGroup",
                                        new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                                        {
                                            ["page"] = RenderCell.String(page.Id),
                                            // The COLUMN. Kept rather than collapsed: merging
                                            // columns is a rendering decision a reader can
                                            // make, and a parse that discards them leaves no
                                            // way back.
                                            ["section"] = RenderCell.String(section.Id),
                                            ["group"] = RenderCell.String(group.Id),
                                            ["groupLabel"] = RenderCell.String(group.Label),
                                            ["visible"] = RenderCell.Boolean(group.Visible),
                                            ["inherited"] = RenderCell.Boolean(group.Inherited),
                                            ["isContribution"] = RenderCell.Boolean(group.IsContribution),
                                            ["order"] = OrderCell(group.Order),
                                        }),
                                    []));

                                foreach (var control in group.Controls)
                                {
                                    children.Add(new RenderTreeBranch(
                                        new RenderRow("layoutControl",
                                            new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                                            {
                                                // The control's place in the form, so a reader
                                                // of one row knows where it sits without
                                                // reconstructing the tree.
                                                ["page"] = RenderCell.String(page.Id),
                                                ["section"] = RenderCell.String(section.Id),
                                                ["group"] = RenderCell.String(group.Id),
                                                // For an ordinary field control this is the
                                                // field REFERENCE name, which is what ties the
                                                // layout back to the type's field list.
                                                ["control"] = RenderCell.String(control.Id),
                                                ["controlLabel"] = RenderCell.String(control.Label),
                                                ["controlType"] = RenderCell.String(control.ControlType),
                                                ["readOnly"] = RenderCell.Boolean(control.ReadOnly),
                                                ["visible"] = RenderCell.Boolean(control.Visible),
                                                // The layout's own inherited-vs-authored mark,
                                                // the same distinction rules and types carry.
                                                ["inherited"] = RenderCell.Boolean(control.Inherited),
                                                ["isContribution"] = RenderCell.Boolean(control.IsContribution),
                                                ["order"] = OrderCell(control.Order),
                                            }),
                                        []));
                                }
                            }
                        }
                    }

                    // 🔴 The SYSTEM controls — state, reason, assigned-to, area and iteration
                    // path, tags — which the server returns alongside the pages and places
                    // outside the page structure. Carried for the same reason everything else
                    // is: a process that hid one or made it read-only differs from one that
                    // did not, and an omission with no marker is the failure S3 bans.
                    foreach (var control in type.Layout.SystemControls)
                    {
                        children.Add(new RenderTreeBranch(
                            new RenderRow("layoutSystemControl",
                                new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                                {
                                    ["control"] = RenderCell.String(control.Id),
                                    ["controlLabel"] = RenderCell.String(control.Label),
                                    ["controlType"] = RenderCell.String(control.ControlType),
                                    ["readOnly"] = RenderCell.Boolean(control.ReadOnly),
                                    ["visible"] = RenderCell.Boolean(control.Visible),
                                    ["inherited"] = RenderCell.Boolean(control.Inherited),
                                    ["isContribution"] = RenderCell.Boolean(control.IsContribution),
                                    ["order"] = OrderCell(control.Order),
                                }),
                            []));
                    }
                }
            }

            typeBranches.Add(new RenderTreeBranch(
                new RenderRow("type", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    // 🔴 Reference name first and always: this is what two processes are
                    // matched by. Display names lie.
                    ["referenceName"] = RenderCell.String(type.ReferenceName),
                    ["name"] = RenderCell.String(type.Name),
                    ["description"] = RenderCell.String(type.Description),
                    ["customization"] = RenderCell.String(type.Customization),
                    ["inherits"] = type.Inherits is null
                        ? RenderCell.DisplayOnly(string.Empty)
                        : RenderCell.String(type.Inherits),
                    ["isDisabled"] = RenderCell.Boolean(type.IsDisabled),
                    ["fieldCount"] = RenderCell.Integer(type.Fields.Count),
                    ["stateCount"] = RenderCell.Integer(type.States.Count),
                    ["transitionCount"] = RenderCell.Integer(type.Transitions.Count),
                    // 🔴 A count, and NOT a substitute for the rules themselves — those are
                    // emitted in full above. It is here because the abridged rendering needs
                    // a number, and because a count changing is the fastest way for a reader
                    // scanning a diff to spot that a type's rule set moved at all.
                    ["ruleCount"] = RenderCell.Integer(type.Rules.Count),
                    // 🔴 The AUTHORED count beside the total, because that ratio is the whole
                    // point of the customization tag: ~54 rules of which 1 is authored is the
                    // common shape, and a reader needs the second number without filtering
                    // the document by hand. Derived from the tag, never from a filter applied
                    // to the document — the document still carries all of them.
                    ["authoredRuleCount"] = RenderCell.Integer(
                        type.Rules.Count(static r =>
                            r.Customization.Kind == RuleCustomizationKind.Custom)),
                    ["behaviourCount"] = RenderCell.Integer(type.Behaviours.Count),
                    // 🔴 DisplayOnly-empty when the layout could not be read, rather than 0.
                    // A form with zero controls and a form we failed to fetch are different
                    // facts, and `formLayout` in the unfetched list is the second one.
                    ["layoutControlCount"] = type.Layout is null
                        ? RenderCell.DisplayOnly(string.Empty)
                        : RenderCell.Integer(type.Layout.Pages
                            .SelectMany(static p => p.Sections)
                            .SelectMany(static s => s.Groups)
                            .Sum(static g => g.Controls.Count)),
                    // 🔴 Which parts could not be read. Empty means everything was read. This
                    // is what stops an empty field list reading as "this type has no fields"
                    // when the truth is "the call failed" — indistinguishable otherwise, and
                    // wrong in the silent direction.
                    ["unfetched"] = type.Unfetched.Count == 0
                        ? RenderCell.DisplayOnly(string.Empty)
                        : RenderCell.String(string.Join(",", type.Unfetched)),
                }),
                children));
        }

        return new RenderTreeBranch(
            new RenderRow("process", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["processId"] = RenderCell.String(description.Header.ProcessId),
            }),
            typeBranches);
    }

    /// <summary>
    /// The document's word for one requiredness case.
    /// </summary>
    /// <remarks>
    /// 🔴 Three distinct tokens, and <c>conditional</c> is deliberately NOT a synonym for
    /// either neighbour. Rendering it as <c>false</c> is the AB#236 defect; rendering it as
    /// <c>true</c> would be wrong in the other direction — a caller would supply the field
    /// unconditionally when the process does not ask for it. Written out rather than
    /// switch-expression-defaulted so a new enum member cannot silently render as one of
    /// these.
    /// </remarks>
    private static string RequirednessToken(FieldRequirednessKind kind) => kind switch
    {
        FieldRequirednessKind.Always => "always",
        FieldRequirednessKind.Conditional => "conditional",
        FieldRequirednessKind.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled requiredness kind."),
    };

    /// <summary>
    /// The conditions under which a field becomes required, in one stable line.
    /// </summary>
    /// <remarks>
    /// 🔴 Walks the collections in the order the assembler sorted them and imposes no order
    /// of its own. Re-sorting here would put a second ordering authority in the system and
    /// byte-stability would depend on both agreeing forever.
    /// <para>
    /// Alternatives are joined with <c>OR</c> and clauses within one alternative with
    /// <c>AND</c>, matching the server's semantics: a rule's conditions are conjunctive, and
    /// two rules targeting one field are alternatives.
    /// </para>
    /// </remarks>
    private static string DescribeConditions(FieldRequiredness requiredness)
        => string.Join(" OR ", requiredness.Conditions.Select(static condition =>
            string.Join(" AND ", condition.Clauses.Select(static clause =>
                clause.Value is null
                    ? $"{clause.ConditionType} {clause.Field}"
                    : $"{clause.ConditionType} {clause.Field} = {clause.Value}"))));

    /// <summary>
    /// The document's word for one value-constraint case.
    /// </summary>
    /// <remarks>
    /// 🔴 Four distinct tokens, and none is a synonym for another. <c>unknown</c> is
    /// deliberately NOT <c>unconstrained</c>: rendering an unreadable picklist source as
    /// "unconstrained" would tell a caller the server accepts anything when nobody asked, the
    /// most dangerous of the wrong answers because acting on it fails at the server. And
    /// <c>suggested</c> is deliberately NOT <c>list</c>: a suggested picklist offers values in
    /// the editor while the server enforces nothing, so calling it a constraint overstates what
    /// the process demands. Written out rather than switch-expression-defaulted so a new enum
    /// member cannot silently render as one of these.
    /// </remarks>
    private static string ValueConstraintToken(FieldValueConstraintKind kind) => kind switch
    {
        FieldValueConstraintKind.ListConstrained => "list",
        FieldValueConstraintKind.ListSuggested => "suggested",
        FieldValueConstraintKind.Unconstrained => "unconstrained",
        FieldValueConstraintKind.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled value constraint kind."),
    };

    /// <summary>
    /// A layout element's server-assigned arrangement key.
    /// </summary>
    /// <remarks>
    /// 🔴 An ABSENT order renders as an empty display-only cell rather than as <c>0</c>. Zero is
    /// a real and common order key — it is the FIRST element — so collapsing "the server did not
    /// send one" into it would assert a position nothing supports, and would make an element
    /// whose key was dropped by a version drift indistinguishable from the one that genuinely
    /// leads its group.
    /// </remarks>
    private static RenderCell OrderCell(int? order)
        => order is null ? RenderCell.DisplayOnly(string.Empty) : RenderCell.Integer(order.Value);

    /// <summary>
    /// The document's word for one rule-customization case.
    /// </summary>
    /// <remarks>
    /// 🔴 Four distinct tokens, and <c>unknown</c> is deliberately NOT a synonym for
    /// <c>system</c>. This tag is the reader's FILTER for the ~54 inherited rules a derived
    /// type carries, so rendering an unstated class as <c>system</c> would invite the reader
    /// to discard rules that might be authored — silently undoing the carry-everything ruling
    /// from the far end. Written out rather than switch-expression-defaulted so a new enum
    /// member cannot silently render as one of these.
    /// <para>
    /// An unrecognised server token renders as <c>unknown:&lt;token&gt;</c> rather than as a
    /// bare <c>unknown</c>: Twig does not own this vocabulary, and a class it has not seen is
    /// a fact worth showing rather than an error worth erasing.
    /// </para>
    /// </remarks>
    private static string RuleCustomizationToken(RuleCustomization customization)
        => customization.Kind switch
        {
            RuleCustomizationKind.Custom => "custom",
            RuleCustomizationKind.Inherited => "inherited",
            RuleCustomizationKind.System => "system",
            RuleCustomizationKind.Unknown => customization.Token.Length == 0
                ? "unknown"
                : $"unknown:{customization.Token}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(customization), customization.Kind, "Unhandled rule customization kind."),
        };

    /// <summary>The conditions that gate a rule, in one stable line.</summary>
    /// <remarks>
    /// 🔴 Walks the assembler's sorted order and imposes none of its own. Joined with
    /// <c>AND</c> because a rule's conditions are conjunctive — the server's own semantics.
    /// </remarks>
    private static string DescribeRuleConditions(IReadOnlyList<RuleCondition> conditions)
        => string.Join(" AND ", conditions.Select(static c =>
            c.Value is null
                ? $"{c.ConditionType} {c.Field}"
                : $"{c.ConditionType} {c.Field} = {c.Value}"));

    /// <summary>What a rule does when it fires, in one stable line.</summary>
    /// <remarks>
    /// Joined with <c>;</c> rather than <c>AND</c>: a rule's actions are independent effects
    /// applied together, not a conjunction of conditions, and using the same connective for
    /// both would suggest a relationship the server does not have.
    /// </remarks>
    private static string DescribeRuleActions(IReadOnlyList<RuleAction> actions)
        => string.Join("; ", actions.Select(static a =>
            a.Value is null
                ? $"{a.ActionType} {a.TargetField}"
                : $"{a.ActionType} {a.TargetField} = {a.Value}"));

    /// <remarks>
    /// The abridged shape is deliberately unspecified by the ruling — it is a rendering
    /// concern with no contract weight, since the machine document carries the promise. This
    /// is a build choice: identity, authored-vs-inherited, and counts, one line per type.
    /// <para>
    /// 🔴 The rule count is shown as <c>authored/total</c> (AB#238) rather than as a bare
    /// total. On a derived type the total is ~54 and the authored count is 1, and the second
    /// number is the one a person scanning this actually wants — while the first stops the
    /// summary implying the document only carries the authored ones. Neither number replaces
    /// the rules themselves, which are in <c>-o json</c>; the banner above says so.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RenderNode> BuildAbridgedTypeLines(ProcessDescription description)
    {
        var lines = new List<RenderNode>(description.Types.Count);

        foreach (var type in description.Types)
        {
            // A type with unread parts is called out inline rather than left looking like a
            // type that genuinely has nothing.
            var incomplete = type.Unfetched.Count == 0
                ? string.Empty
                : $"  [COULD NOT READ: {string.Join(", ", type.Unfetched)}]";

            var authored = type.Rules.Count(static r =>
                r.Customization.Kind == RuleCustomizationKind.Custom);

            lines.Add(new RenderNode.Text(
                $"{type.ReferenceName,-46} {type.Customization,-10} "
                + $"{type.Fields.Count,4} fields  {type.States.Count,3} states  "
                + $"{type.Transitions.Count,4} transitions  "
                + $"{authored}/{type.Rules.Count} rules authored/total  "
                + $"{type.Behaviours.Count,2} backlogs{incomplete}"));
        }

        return lines;
    }}
