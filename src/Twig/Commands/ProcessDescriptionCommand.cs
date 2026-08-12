using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig process description [&lt;type&gt;]</c>: writes a byte-stable structural
/// description of an ADO process, so a caller can point an ordinary diff tool at two of them.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter, deliberately. It resolves the type argument and the output target and
/// decides NOTHING about the document — that all lives in
/// <see cref="ProcessDescriptionAssembler"/>. The placement is what makes "the agent surface
/// returns the same document with fewer types" a structural fact rather than a convention
/// two surfaces would drift apart on.
/// </para>
/// <para>
/// Switches mirror the shipped <c>twig process layout</c> — <c>--out</c>, <c>-o</c>, stdout
/// when <c>--out</c> is omitted, confirmation on the error stream so <c>--out</c> composes
/// in scripts — so a caller does not learn a second convention inside one command family.
/// </para>
/// <para>
/// 🔴 <b>Descriptor version 0.1 declares NO known gaps, and as of AB#238 that claim is
/// checked rather than assumed.</b> Every content item Implementation Decision 4 enumerates is
/// now carried: conditional requiredness by AB#236's rules merge, picklist values by AB#237's
/// constraint merge, and rules, behaviour membership and form layout by AB#238. The mechanism
/// is kept — see <see cref="ProcessDescriptionAssembler.KnownGaps"/> — and the human rendering
/// states the absence positively rather than printing a warning heading with nothing under it,
/// because "this document makes no reservations" is itself the claim a reader needs in order
/// to read a future non-empty list as meaningful.
/// </para>
/// <para>
/// 🔴 <b>The descriptor version stays 0.1 despite this ticket adding three content items.</b>
/// 0.1 is explicitly "still under design" and the form layout's shape was named on the record
/// as the reason it is not 1.0 — so shipping the layout does not settle it, and bumping now
/// would announce a stability this document does not yet have. Raised rather than decided
/// silently: it is a contract question.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> Implementation Decisions
/// 1, 2, 3, 4, 8, 9, 11.
/// </para>
/// </remarks>
internal sealed class ProcessDescriptionCommand(
    ProcessDescriptionAssembler assembler,
    OutputFormatterFactory formatterFactory,
    RendererFactory rendererFactory,
    TimeProvider? timeProvider = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 🔴 The <c>-o</c> value that produces the COMPLETE document, and the one the abridged
    /// rendering's banner names.
    /// </summary>
    /// <remarks>
    /// Named here once and read by both the completeness test below and the banner, so the
    /// banner cannot come to name a format that does not produce the complete document. A
    /// banner pointing at a nonexistent format would satisfy a bare string-presence test
    /// while telling the reader a lie — this constant is what makes the test assert against
    /// the real value instead.
    /// </remarks>
    internal const string CompleteFormat = "json";

    /// <summary>
    /// Every <c>-o</c> value that yields the complete document.
    /// </summary>
    /// <remarks>
    /// 🔴 There is more than one, and treating <see cref="CompleteFormat"/> as the only one is
    /// a live defect rather than a tidiness point: <c>json-full</c> and <c>json-compact</c>
    /// are on the accept-list and resolve to the SAME JSON renderer. Labelling their output
    /// "abridged" would stamp a machine-complete document with a banner saying it omits
    /// detail — a false warning is as much a lie as a missing one, and it would send a reader
    /// looking for content that is already in front of them.
    /// <para>
    /// Derived from the format's normalized name so this cannot drift from the renderer's own
    /// aliasing.
    /// </para>
    /// </remarks>
    internal static bool IsCompleteFormat(string? outputFormat)
        => OutputFormats.Normalize(outputFormat) is "json" or "json-full" or "json-compact";

    /// <summary>
    /// Executes <c>twig process description [&lt;type&gt;] [--out path] [-o format]</c>.
    /// </summary>
    /// <param name="typeName">
    /// A type's REFERENCE name to describe just that type, or <c>null</c> for every type in
    /// the process. Naming one is the cheap path.
    /// </param>
    /// <param name="outPath">Optional file to write the rendered document to.</param>
    /// <param name="outputFormat">The rendering. <c>json</c> is complete; others are abridged.</param>
    public async Task<int> ExecuteAsync(
        string? typeName = null,
        string? outPath = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 🔴 `-o ids` is REFUSED, not served badly. That renderer emits only cells keyed "id"
        // whose value is an integer, and a process description has no numeric ids at all — so
        // it would produce an EMPTY file, silently, with a zero exit code. Worse, it is the
        // one format that structurally cannot carry the abridged self-declaration, so the
        // reader would get nothing and no notice that anything was dropped. An explicit error
        // naming the format that works is the honest outcome.
        if (string.Equals(OutputFormats.Normalize(outputFormat), "ids", StringComparison.Ordinal))
        {
            _stderr.WriteLine(fmt.FormatError(
                "'-o ids' cannot render a process description: the document contains no "
                + $"numeric ids. Use '-o {CompleteFormat}' for the complete document."));
            return 1;
        }

        ProcessDescription? description;
        try
        {
            description = await assembler.AssembleAsync(
                typeName is null ? null : [typeName],
                // Injected rather than read inside the assembler so a test can hold it fixed
                // and assert everything else is byte-identical.
                _time.GetUtcNow(),
                ct);
        }
        catch (ProcessDescriptionTypeNotFoundException ex)
        {
            // 🔴 A hard error, and NO partial file. A script that banked a document saying
            // "this process has nothing" when the truth is "you asked for something that is
            // not here" would be worse than a failure.
            _stderr.WriteLine(fmt.FormatError(
                $"Work item type '{ex.TypeReferenceName}' does not exist in this process. " +
                "Run 'twig process' to list types."));
            return 1;
        }

        if (description is null)
        {
            _stderr.WriteLine(fmt.FormatError(
                "Could not describe this project's process. The process may not be reachable, " +
                "or this project does not resolve to one."));
            return 1;
        }

        var tree = BuildTree(description, outputFormat);

        if (outPath is null)
        {
            rendererFactory.GetRenderer(outputFormat).Render(tree);
            return 0;
        }

        // 🔴 Rendered to a TEMPORARY file and moved into place. Writing straight to outPath
        // means a renderer that throws mid-render leaves a TRUNCATED document on disk — which
        // is worse than no file at all, because a truncated document is silently missing
        // types and a reader diffing it sees differences that are not real. The unknown-type
        // path already promises "no partial file"; this makes the promise hold for write
        // failures too.
        var tempPath = outPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using (var writer = new StreamWriter(tempPath, append: false))
            {
                rendererFactory.GetRenderer(outputFormat, writer).Render(tree);
            }

            File.Move(tempPath, outPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(tempPath);
            _stderr.WriteLine(fmt.FormatError($"Could not write '{outPath}': {ex.Message}"));
            return 1;
        }
        catch
        {
            // Any other failure mid-render must not leave the scratch file behind either.
            TryDelete(tempPath);
            throw;
        }

        // Confirmation on the error stream so `--out` stays silent on stdout and composes in
        // scripts; the file is the output. Same contract the layout command ships.
        _stderr.WriteLine(
            $"Wrote process description ({description.Types.Count} type(s), descriptor " +
            $"{description.Header.DescriptorVersion}) to {outPath}");
        return 0;
    }

    /// <remarks>
    /// Best-effort scratch cleanup. A failure to delete the temp file must never mask the
    /// real error the caller is being told about.
    /// </remarks>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // ─────────────────────────────────────────────────────────────
    //  RenderTree builder
    // ─────────────────────────────────────────────────────────────

    /// <remarks>
    /// 🔴 <b>Ordering is NOT decided here.</b> Every collection arrives from the assembler
    /// already sorted and this method walks it in the order given. Re-sorting, grouping, or
    /// projecting through a dictionary at this layer would put a second ordering authority
    /// in the system and byte-stability would depend on both agreeing forever.
    /// </remarks>
    private static RenderTree.RenderTree BuildTree(ProcessDescription description, string outputFormat)
    {
        var isComplete = IsCompleteFormat(outputFormat);

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
    }
}
