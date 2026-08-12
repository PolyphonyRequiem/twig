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
/// 🔴 <b>KNOWN INCOMPLETE at descriptor version 0.1.</b> The document is not yet trustworthy
/// about conditional requiredness (AB#236) or picklist values (AB#237). It says so on its
/// face, in its own header, rather than presenting a partial truth as a whole one — see
/// <see cref="ProcessDescriptionAssembler.KnownGaps"/>.
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
            new DocumentField(
                "knownGapsHuman",
                new RenderNode.Section("KNOWN INCOMPLETE — do not treat this document as authoritative about:",
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
                            // 🔴 Named for what it actually reports. This route carries
                            // UNCONDITIONAL requiredness only; a field made mandatory by a
                            // rule reads false here. The document's knownGaps declares it.
                            ["requiredUnconditionally"] = RenderCell.Boolean(field.RequiredUnconditionally),
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

    /// <remarks>
    /// The abridged shape is deliberately unspecified by the ruling — it is a rendering
    /// concern with no contract weight, since the machine document carries the promise. This
    /// is a build choice: identity, authored-vs-inherited, and counts, one line per type.
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

            lines.Add(new RenderNode.Text(
                $"{type.ReferenceName,-46} {type.Customization,-10} "
                + $"{type.Fields.Count,4} fields  {type.States.Count,3} states  "
                + $"{type.Transitions.Count,4} transitions{incomplete}"));
        }

        return lines;
    }
}
