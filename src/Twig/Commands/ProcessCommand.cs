using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig process</c>: exposes process type discovery.
/// <list type="bullet">
///   <item>No args → lists all work item types with state counts.</item>
///   <item>With type arg → shows states, fields, and transitions for that type.</item>
/// </list>
/// Also serves as the implementation for the hidden <c>twig states</c> alias,
/// which scopes to the active work item's type for backward compatibility.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: command builds a <see cref="RenderTree.RenderTree"/> describing the
/// output and dispatches through <see cref="RendererFactory"/>. The
/// <see cref="OutputFormatterFactory"/> dependency remains only for stderr
/// error formatting until error rendering also moves to the seam.
/// </remarks>
public sealed class ProcessCommand(
    ActiveItemResolver? activeItemResolver,
    IProcessTypeStore processTypeStore,
    IFieldDefinitionStore fieldDefinitionStore,
    OutputFormatterFactory formatterFactory,
    RendererFactory rendererFactory,
    TextWriter? stderr = null,
    IIterationService? iterationService = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Executes the <c>twig process [type]</c> command.
    /// When <paramref name="typeName"/> is null, lists all types.
    /// When provided, shows details for that specific type.
    /// </summary>
    /// <param name="includeHidden">
    /// Include types ADO keeps for its own tooling (AB#657). Off by default — see
    /// <see cref="ExecuteListAsync"/> for why the default is exclusion rather than marking.
    /// Ignored when <paramref name="typeName"/> is given: naming a type always describes it.
    /// </param>
    public async Task<int> ExecuteAsync(
        string? typeName = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool includeHidden = false,
        bool refresh = false,
        CancellationToken ct = default)
    {
        return typeName is null
            ? await ExecuteListAsync(outputFormat, includeHidden, refresh, ct)
            : await ExecuteTypeDetailAsync(typeName, outputFormat, refresh, ct);
    }

    /// <summary>
    /// Executes the hidden <c>twig states</c> alias: resolves the active work item's type
    /// and shows its states (backward compat with the old StatesCommand).
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Requires an active-item resolver, which the AB#216 <c>--org</c>/<c>--project</c>
    /// override scope does not have.</b> "Active work item" is a property of a workspace's
    /// context store, and an override invocation has no workspace — so the concept does not
    /// exist there rather than being merely unavailable. The override path never routes here
    /// (<c>states</c> takes no overrides), and the guard makes that a checked fact rather than
    /// a NullReferenceException if a future caller wires one up.
    /// </remarks>
    public async Task<int> ExecuteStatesAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (activeItemResolver is null)
        {
            _stderr.WriteLine(fmt.FormatError(
                "'twig states' resolves the active work item, which requires a workspace. "
                + "Run it from a twig workspace, or use 'twig process <type>' with --org/--project."));
            return 1;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        return await ExecuteTypeDetailAsync(item.Type.Value, outputFormat, refresh: false, ct);
    }

    /// <summary>
    /// Lists process types, excluding ADO's own tooling types unless
    /// <paramref name="includeHidden"/> (AB#657).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Exclusion is the default, and marking alone was not enough.</b> AB#656 added the
    /// <c>[hidden]</c> marker, which stopped a hidden type reading as usable vocabulary. It did
    /// not stop nine of this board's twenty-one types padding the default output — so the list a
    /// caller reads to learn the process was nearly half machinery they must never create.
    /// </para>
    /// <para>
    /// Membership comes from <c>Microsoft.HiddenCategory</c> via
    /// <see cref="ProcessTypeRecord.IsHidden"/>, never from a list of type names. A name list is
    /// a per-board allowlist for a generic property: it rots when a process changes and is wrong
    /// for a customer process we have never seen.
    /// </para>
    /// <para>
    /// The empty-store error is deliberately raised BEFORE filtering, so "run twig sync" still
    /// means an empty cache. A process whose every type is hidden reports an empty list at exit
    /// 0 instead — that is a true answer to "which types can I use", not a failure.
    /// </para>
    /// </remarks>
    private async Task<int> ExecuteListAsync(string outputFormat, bool includeHidden, bool refresh, CancellationToken ct)
    {
        // AB#879 targeted metadata-only recovery. `refresh=true` forces a resync even
        // when the local catalog has rows — that is the "actionable rerun" surface
        // behind the Metadata-not-ready message. Otherwise recovery only fires when
        // the local catalog is empty (list view has no per-name predicate). No
        // work-item enumeration, no pending-write flush. Cancellation propagates.
        var typesCatalog = await MetadataCatalogRecovery.EnsureProcessTypesAsync(
            processTypeStore,
            iterationService,
            defs => !refresh && defs.Count > 0,
            ct);
        switch (typesCatalog.Outcome)
        {
            case MetadataCatalogRecovery.State.NotReady:
                // Unconditional: helper's NotReady already means we could not
                // authoritatively confirm the catalog (either no recovery source or the
                // sync produced zero rows). Callers do NOT re-check catalog count here.
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.ProcessTypesNotReady());
                return 1;
            case MetadataCatalogRecovery.State.RefreshFailed:
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.ProcessTypesRefreshFailed(typesCatalog.RefreshError));
                return 1;
        }

        // AB#879: `twig process --refresh` is advertised as the recovery for
        // field-definition errors too, so an explicit --refresh on the list view must
        // also resync the field-def catalog. A failure here propagates the same as the
        // process-type failure — the operator asked for a metadata refresh, they get
        // one honest verdict per catalog.
        if (refresh)
        {
            var fieldsCatalog = await MetadataCatalogRecovery.EnsureFieldsAsync(
                fieldDefinitionStore,
                iterationService,
                _ => false,
                ct);
            switch (fieldsCatalog.Outcome)
            {
                case MetadataCatalogRecovery.State.NotReady:
                    CommandError.Write(rendererFactory, _stderr, outputFormat,
                        MetadataCatalogRecovery.Messages.FieldsNotReady());
                    return 1;
                case MetadataCatalogRecovery.State.RefreshFailed:
                    CommandError.Write(rendererFactory, _stderr, outputFormat,
                        MetadataCatalogRecovery.Messages.FieldsRefreshFailed(fieldsCatalog.RefreshError));
                    return 1;
            }
        }

        var types = await processTypeStore.GetAllAsync(ct);

        var visible = includeHidden
            ? types
            : types.Where(t => !t.IsHidden).ToList();

        var tree = BuildTypesListTree(visible);
        rendererFactory.GetRenderer(outputFormat).Render(tree);

        return 0;
    }

    private async Task<int> ExecuteTypeDetailAsync(string typeName, string outputFormat, bool refresh, CancellationToken ct)
    {
        // AB#879: the requested type may be absent because (a) the catalog is empty,
        // (b) it lacks THIS type even though populated (a nonempty local catalog cannot
        // prove completeness), or (c) the catalog has the record but its states list is
        // empty. In each case one targeted metadata-only recovery runs before deciding
        // whether "Unknown" is the right answer. The `refresh` flag forces the sync even
        // when the local record looks satisfactory.
        var typesCatalog = await MetadataCatalogRecovery.EnsureProcessTypesAsync(
            processTypeStore,
            iterationService,
            defs =>
            {
                if (refresh) return false;
                var match = defs.FirstOrDefault(d => string.Equals(d.TypeName, typeName, StringComparison.OrdinalIgnoreCase));
                return match is not null && match.States.Count > 0;
            },
            ct);
        switch (typesCatalog.Outcome)
        {
            case MetadataCatalogRecovery.State.NotReady:
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.ProcessTypesNotReady(
                        $"process-type catalog cannot resolve '{typeName}'"));
                return 1;
            case MetadataCatalogRecovery.State.RefreshFailed:
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.ProcessTypesRefreshFailed(typesCatalog.RefreshError));
                return 1;
        }

        var typeRecord = await processTypeStore.GetByNameAsync(typeName, ct);
        if (typeRecord is null || typeRecord.States.Count == 0)
        {
            // Reached only after an authoritative catalog read. If states are still empty
            // the recovery was incomplete for this type — surface as Metadata-not-ready,
            // NOT as Unknown, per AB#879 (the catalog may know the type name but the
            // state definition is still missing).
            if (typeRecord is not null && typeRecord.States.Count == 0)
            {
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.ProcessTypesNotReady(
                        $"process-type record for '{typeName}' has no states after refresh"));
                return 1;
            }

            CommandError.Write(rendererFactory, _stderr, outputFormat,
                $"Unknown work-item type '{typeName}' — not present in the refreshed process-type catalog.");
            return 1;
        }

        // AB#879: the type-detail view renders a fields table. An empty field-definition
        // catalog would previously report `fields=[]` as if the type had zero fields —
        // a false answer to "what fields does this type carry". Attempt one targeted
        // field-definition sync; if it stays empty (or fails), classify as Metadata-not-ready.
        var fieldsCatalog = await MetadataCatalogRecovery.EnsureFieldsAsync(
            fieldDefinitionStore,
            iterationService,
            defs => !refresh && defs.Count > 0,
            ct);
        switch (fieldsCatalog.Outcome)
        {
            case MetadataCatalogRecovery.State.NotReady:
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.FieldsNotReady(
                        $"required to describe fields on '{typeName}'"));
                return 1;
            case MetadataCatalogRecovery.State.RefreshFailed:
                CommandError.Write(rendererFactory, _stderr, outputFormat,
                    MetadataCatalogRecovery.Messages.FieldsRefreshFailed(fieldsCatalog.RefreshError));
                return 1;
        }

        var fields = await fieldDefinitionStore.GetAllAsync(ct);
        var tree = BuildTypeDetailTree(typeRecord, fields);
        rendererFactory.GetRenderer(outputFormat).Render(tree);

        return 0;
    }

    // ─────────────────────────────────────────────────────────────
    //  RenderTree builders
    // ─────────────────────────────────────────────────────────────

    private static RenderTree.RenderTree BuildTypesListTree(IReadOnlyList<ProcessTypeRecord> types)
    {
        var columns = new[]
        {
            new RenderColumn("typeName", "Type"),
            new RenderColumn("stateCount", "States"),
            new RenderColumn("childTypeCount", "Children"),
            new RenderColumn("color", "Color"),
            new RenderColumn("iconId", "Icon ID"),
            // AB#656. isHidden is the question every consumer asks; categories is the
            // underlying fact it derives from. Both ship, because collapsing to the boolean
            // would discard a many-to-many membership twig has already paid to fetch, and
            // shipping only the set would make every consumer re-encode which reference name
            // means "hidden".
            new RenderColumn("isHidden", "Hidden"),
            new RenderColumn("categories", "Categories"),
        };

        var rows = new List<RenderRow>(types.Count);
        var humanLines = new List<RenderNode>(types.Count);

        foreach (var type in types)
        {
            var colorDisplay = type.ColorHex is not null ? $" (#{type.ColorHex})" : string.Empty;
            // The human surface must agree with the machine one (AB#656): an unmarked hidden
            // type reads as ordinary usable vocabulary, which is the defect.
            var hiddenDisplay = type.IsHidden ? "  [hidden — ADO tooling type]" : string.Empty;
            humanLines.Add(new RenderNode.Text(
                $"  {type.TypeName,-20} {type.States.Count} states{colorDisplay}{hiddenDisplay}"));

            var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["typeName"] = RenderCell.String(type.TypeName),
                ["stateCount"] = RenderCell.Integer(type.States.Count),
                ["childTypeCount"] = RenderCell.Integer(type.ValidChildTypes.Count),
                ["color"] = type.ColorHex is not null
                    ? RenderCell.String(type.ColorHex)
                    : new RenderCell("null", new RenderValue.Null()),
                ["iconId"] = type.IconId is not null
                    ? RenderCell.String(type.IconId)
                    : new RenderCell("null", new RenderValue.Null()),
                ["isHidden"] = RenderCell.Boolean(type.IsHidden),
                ["categories"] = new RenderCell(
                    string.Join(", ", type.CategoryReferenceNames),
                    new RenderValue.Array([.. type.CategoryReferenceNames.Select(c => RenderCell.String(c))])),
            };
            rows.Add(new RenderRow(null, cells));
        }

        var doc = new RenderNode.Document(null, [
            new DocumentField(
                Key: "types",
                Node: new RenderNode.Table(null, columns, rows),
                HumanOverride: new RenderNode.Section(null, humanLines)),
            new DocumentField(
                Key: "totalTypes",
                Node: new RenderNode.KeyValue("totalTypes", RenderCell.Integer(types.Count)),
                Audience: RenderAudience.MachineOnly),
        ]);

        return new RenderTree.RenderTree([doc]);
    }

    private static RenderTree.RenderTree BuildTypeDetailTree(
        ProcessTypeRecord type,
        IReadOnlyList<FieldDefinition> fields)
    {
        // Human lines: legacy human output shows ONLY states (not fields or transitions).
        var humanLines = new List<RenderNode>(type.States.Count);
        var stateRows = new List<RenderRow>(type.States.Count);
        foreach (var state in type.States)
        {
            var colorDisplay = state.Color is not null ? $" (#{state.Color})" : string.Empty;
            humanLines.Add(new RenderNode.Text($"  {state.Name,-20} {state.Category}{colorDisplay}"));

            stateRows.Add(new RenderRow(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["name"] = RenderCell.String(state.Name),
                ["category"] = RenderCell.String(state.Category.ToString()),
                ["color"] = state.Color is not null
                    ? RenderCell.String(state.Color)
                    : new RenderCell("null", new RenderValue.Null()),
            }));
        }
        var stateColumns = new[]
        {
            new RenderColumn("name", "Name"),
            new RenderColumn("category", "Category"),
            new RenderColumn("color", "Color"),
        };

        var fieldRows = new List<RenderRow>(fields.Count);
        foreach (var field in fields)
        {
            fieldRows.Add(new RenderRow(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["referenceName"] = RenderCell.String(field.ReferenceName),
                ["displayName"] = RenderCell.String(field.DisplayName),
                ["dataType"] = RenderCell.String(field.DataType),
                ["isReadOnly"] = RenderCell.Boolean(field.IsReadOnly),
            }));
        }
        var fieldColumns = new[]
        {
            new RenderColumn("referenceName", "Reference Name"),
            new RenderColumn("displayName", "Display Name"),
            new RenderColumn("dataType", "Data Type"),
            new RenderColumn("isReadOnly", "Read Only"),
        };

        var transitionRows = new List<RenderRow>(type.States.Count * (type.States.Count - 1));
        for (var i = 0; i < type.States.Count; i++)
        {
            for (var j = 0; j < type.States.Count; j++)
            {
                if (i == j) continue;
                var from = type.States[i];
                var to = type.States[j];
                transitionRows.Add(new RenderRow(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["from"] = RenderCell.String(from.Name),
                    ["to"] = RenderCell.String(to.Name),
                    ["kind"] = RenderCell.String(to.Category == StateCategory.Removed ? "Cut" : "Forward"),
                }));
            }
        }
        var transitionColumns = new[]
        {
            new RenderColumn("from", "From"),
            new RenderColumn("to", "To"),
            new RenderColumn("kind", "Kind"),
        };

        var doc = new RenderNode.Document(null, [
            new DocumentField(
                Key: "type",
                Node: new RenderNode.KeyValue("type", RenderCell.String(type.TypeName)),
                Audience: RenderAudience.MachineOnly),
            // AB#656. The detail view answers the same question as the listing, in the same
            // words — a consumer must not have to ask two commands to learn one fact.
            new DocumentField(
                Key: "isHidden",
                Node: new RenderNode.KeyValue("isHidden", RenderCell.Boolean(type.IsHidden)),
                Audience: RenderAudience.MachineOnly),
            new DocumentField(
                Key: "categories",
                Node: new RenderNode.KeyValue("categories", new RenderCell(
                    string.Join(", ", type.CategoryReferenceNames),
                    new RenderValue.Array([.. type.CategoryReferenceNames.Select(c => RenderCell.String(c))]))),
                Audience: RenderAudience.MachineOnly),
            new DocumentField(
                Key: "states",
                Node: new RenderNode.Table(null, stateColumns, stateRows),
                HumanOverride: new RenderNode.Section(null, humanLines)),
            new DocumentField(
                Key: "fields",
                Node: new RenderNode.Table(null, fieldColumns, fieldRows),
                Audience: RenderAudience.MachineOnly),
            new DocumentField(
                Key: "transitions",
                Node: new RenderNode.Table(null, transitionColumns, transitionRows),
                Audience: RenderAudience.MachineOnly),
        ]);

        return new RenderTree.RenderTree([doc]);
    }
}
