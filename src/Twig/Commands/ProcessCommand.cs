using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Inspects the admitted workspace's discovered schema, without querying or synchronizing items.</summary>
internal sealed class ProcessCommand(
    RendererFactory rendererFactory,
    CoreWorkspaceFactory? factory = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public async Task<int> ExecuteAsync(
        string? typeName = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool includeHidden = false,
        bool refresh = false,
        CancellationToken ct = default)
    {
        if (OutputFormats.Normalize(outputFormat) == "ids")
            return Error(outputFormat, "Process metadata has no item IDs. Use -o json or -o minimal.", 2);
        var acquired = await AcquireAsync(ct);
        if (!acquired.IsSuccess) return Error(outputFormat, acquired.Error.Message);
        var schema = acquired.Value;
        var type = typeName is null ? null : FindType(schema, typeName);
        if (typeName is not null && type is null)
            return Error(outputFormat, $"Unknown work-item type '{typeName}'. Run 'twig process' to list discovered references.");

        // Discovery is always live; --refresh is accepted without creating a second metadata cache.
        var tree = type is null ? ListTree(schema, includeHidden) : new RenderTree.RenderTree([TypeDocument(type)]);
        rendererFactory.GetRenderer(outputFormat).Render(tree);
        return 0;
    }

    public async Task<int> ExecuteStatesAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (OutputFormats.Normalize(outputFormat) != "human")
            return Error(outputFormat, "Structured states is refused. Use 'twig process <type>' with an explicit type.", 2);
        if (factory is null) return Error(outputFormat, WorkspaceRequired);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(outputFormat, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var context = await workspace.Context.ReadAsync(ct);
        if (!context.IsSuccess) return Error(outputFormat, context.Error.Message);
        if (context.Value.Position < 0)
            return Error(outputFormat, "No active Core item is selected. Use 'twig set <id>' or 'twig process <type>'.");
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(outputFormat, snapshot.Error.Message);
        var key = context.Value.History[context.Value.Position].Resolve(workspace.Connection);
        key = snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
        var type = key.IsLocal
            ? snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == key)?.Type
            : snapshot.Value.Items.SingleOrDefault(item => item.Key == key)?.Type;
        if (type is null)
            return Error(outputFormat, "The active item's type is not cached. Use 'twig show <id> --refresh' or 'twig process <type>'.");
        var discovered = await workspace.SchemaAsync(ct);
        if (!discovered.IsSuccess) return Error(outputFormat, discovered.Error.Message);
        var definition = FindType(discovered.Value, type);
        if (definition is null) return Error(outputFormat, $"Unknown work-item type '{type}' in the discovered schema.");
        rendererFactory.GetRenderer(outputFormat).Render(new([TypeDocument(definition)]));
        return 0;
    }

    internal const string WorkspaceRequired =
        "Process schema inspection requires an explicitly initialized Core workspace. "
        + "Legacy and unpinned override targets are not qualified; no ambient process was substituted. "
        + "Use a fresh isolated worktree and 'twig init <org> <project> --core-store <new-absolute-file>'.";

    internal async Task<Result<ProcessSchema>> AcquireAsync(CancellationToken ct)
    {
        if (factory is null) return Result<ProcessSchema>.Fail(ErrorCode.Unsupported, WorkspaceRequired);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Result<ProcessSchema>.Fail(opened.Error);
        using var handle = opened.Value;
        return await handle.Workspace.SchemaAsync(ct);
    }

    internal static ItemType? FindType(ProcessSchema schema, string reference)
    {
        var matches = schema.Types.Where(type => string.Equals(type.Reference, reference, StringComparison.OrdinalIgnoreCase)
            || type.Rules.SelectMany(rule => rule.Values).Any(value => value.DataType == "ado.native-type-name"
                && string.Equals(value.Text, reference, StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private int Error(string output, string message, int code = 1)
    {
        CommandError.Write(rendererFactory, _stderr, output, message);
        return code;
    }

    private static RenderTree.RenderTree ListTree(ProcessSchema schema, bool includeHidden)
    {
        var types = schema.Types.Where(type => includeHidden || !type.Hidden).OrderBy(type => type.Reference, StringComparer.Ordinal).ToArray();
        return new([new RenderNode.Document(null, [
            new("types", new RenderNode.Table(null, [
                new("typeName", "Type"), new("stateCount", "States"), new("isHidden", "Hidden")],
                types.Select(type => new RenderRow(null, new Dictionary<string, RenderCell>
                {
                    ["typeName"] = RenderCell.String(type.Reference),
                    ["stateCount"] = RenderCell.Integer(type.States.Length),
                    ["isHidden"] = RenderCell.Boolean(type.Hidden),
                })).ToArray())),
            new("totalTypes", new RenderNode.KeyValue("totalTypes", RenderCell.Integer(types.Length))),
            new("schemaFingerprint", new RenderNode.KeyValue("schemaFingerprint", RenderCell.String(schema.Fingerprint))),
            new("knownGaps", new RenderNode.KeyValue("knownGaps", Array(schema.Gaps.Select(value => RenderCell.String(value))))),
        ])]);
    }

    internal static RenderNode.Document TypeDocument(ItemType type, bool fields = true, bool rules = true,
        IReadOnlySet<string>? fieldFilter = null)
    {
        var entries = new List<DocumentField>
        {
            new("type", new RenderNode.KeyValue("type", RenderCell.String(type.Reference))),
            new("isHidden", new RenderNode.KeyValue("isHidden", RenderCell.Boolean(type.Hidden))),
            new("stateField", new RenderNode.KeyValue("stateField", RenderCell.String(type.StateField))),
            new("states", new RenderNode.Table(null, [new("name", "Name"), new("category", "Category")],
                type.States.OrderBy(state => state.Name, StringComparer.Ordinal).Select(state =>
                    new RenderRow(null, new Dictionary<string, RenderCell>
                    {
                        ["name"] = RenderCell.String(state.Name), ["category"] = RenderCell.String(state.Category.ToString()),
                    })).ToArray())),
            new("transitions", new RenderNode.Table(null, [new("from", "From"), new("to", "To")],
                type.Transitions.OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal)
                    .Select(edge => new RenderRow(null, new Dictionary<string, RenderCell>
                    {
                        ["from"] = RenderCell.String(edge.From), ["to"] = RenderCell.String(edge.To),
                    })).ToArray())),
        };
        if (fields)
            entries.Add(new("fields", new RenderNode.Table(null, [
                new("referenceName", "Reference Name"), new("dataType", "Data Type"), new("isReadOnly", "Read Only")],
                type.Fields.Where(field => fieldFilter is null || fieldFilter.Contains(field.Reference))
                    .OrderBy(field => field.Reference, StringComparer.Ordinal)
                    .Select(field => new RenderRow(null, new Dictionary<string, RenderCell>
                    {
                        ["referenceName"] = RenderCell.String(field.Reference),
                        ["dataType"] = RenderCell.String(field.Kind.ToString()),
                        ["isReadOnly"] = RenderCell.Boolean(field.ReadOnly),
                    })).ToArray())));
        if (rules)
            entries.Add(new("rules", new RenderNode.Table(null, [
                new("field", "Field"), new("action", "Action"), new("conditions", "Conditions"),
                new("values", "Values"), new("disabled", "Disabled")],
                type.Rules.Where(rule => fieldFilter is null || fieldFilter.Contains(rule.Field))
                    .Select(rule => new RenderRow(null, new Dictionary<string, RenderCell>
                    {
                        ["field"] = RenderCell.String(rule.Field),
                        ["action"] = RenderCell.String(rule.Action.ToString()),
                        ["disabled"] = RenderCell.Boolean(rule.Disabled),
                        ["conditions"] = Array(rule.Conditions.Select(condition => new RenderCell(
                            $"{condition.Kind} {condition.Field} {Value(condition.Value).DisplayText}", new RenderValue.Object(
                                new Dictionary<string, RenderCell>
                                {
                                    ["kind"] = RenderCell.String(condition.Kind.ToString()),
                                    ["field"] = RenderCell.String(condition.Field),
                                    ["value"] = Value(condition.Value),
                                })))),
                        ["values"] = Array(rule.Values.Select(Value)),
                    })).ToArray())));
        return new(null, entries);
    }

    internal static RenderCell Array(IEnumerable<RenderCell> values)
    {
        var cells = values.ToArray();
        return new(string.Join("; ", cells.Select(cell => cell.DisplayText)), new RenderValue.Array(cells));
    }

    private static RenderCell Value(FieldValue? value)
    {
        if (value is null) return new("absent", new RenderValue.Null());
        var scalar = value.Kind switch
        {
            FieldKind.Null => new RenderCell("null", new RenderValue.Null()),
            FieldKind.Integer => RenderCell.Integer((long)value.Number),
            FieldKind.Decimal => new(value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), new RenderValue.Decimal(value.Number)),
            FieldKind.Boolean => RenderCell.Boolean(value.Boolean),
            FieldKind.Instant => new(value.Instant.ToString("O"), new RenderValue.DateTime(value.Instant)),
            FieldKind.StringList => Array(value.Values.Select(text => RenderCell.String(text))),
            _ => RenderCell.String(value.Text!),
        };
        var cells = new Dictionary<string, RenderCell>
        {
            ["kind"] = RenderCell.String(value.Kind.ToString()), ["value"] = scalar,
        };
        if (value.DataType is not null) cells["dataType"] = RenderCell.String(value.DataType);
        return new(scalar.DisplayText, new RenderValue.Object(cells));
    }
}
