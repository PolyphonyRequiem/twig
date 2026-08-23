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
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Executes the <c>twig process [type]</c> command.
    /// When <paramref name="typeName"/> is null, lists all types.
    /// When provided, shows details for that specific type.
    /// </summary>
    public async Task<int> ExecuteAsync(
        string? typeName = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        return typeName is null
            ? await ExecuteListAsync(outputFormat, ct)
            : await ExecuteTypeDetailAsync(typeName, outputFormat, ct);
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

        return await ExecuteTypeDetailAsync(item.Type.Value, outputFormat, ct);
    }

    private async Task<int> ExecuteListAsync(string outputFormat, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var types = await processTypeStore.GetAllAsync(ct);

        if (types.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError("No process types found. Run 'twig sync' to refresh process data."));
            return 1;
        }

        var tree = BuildTypesListTree(types);
        rendererFactory.GetRenderer(outputFormat).Render(tree);

        // Human output is a sequence of unterminated lines from the renderer;
        // the legacy formatter emitted them via Console.WriteLine which adds a
        // trailing newline. SpectreNodeRenderer writes through MarkupLine so
        // each line is already terminated — no extra newline needed.
        return 0;
    }

    private async Task<int> ExecuteTypeDetailAsync(string typeName, string outputFormat, CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var typeRecord = await processTypeStore.GetByNameAsync(typeName, ct);

        if (typeRecord is null || typeRecord.States.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError($"No states found for type '{typeName}'. Run 'twig sync' to refresh process data."));
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
