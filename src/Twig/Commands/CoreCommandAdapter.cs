using System.Globalization;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

internal sealed class CoreCommandAdapter(CoreWorkspaceFactory factory, RendererFactory renderer)
{
    internal async Task<int> ShowAsync(int? id, string output, bool tree, bool refresh,
        string? fields, string? sections, string? localId, CancellationToken ct)
    {
        if (id is null || id <= 0) return Error(output, "No Core active target is selected. Pass an explicit work item ID.", 2);
        if (tree || sections is not null || localId is not null)
            return Error(output, "Tree/local-alias detail routing is not yet connected in this in-progress Core cutover.", 1);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope,
            id.Value.ToString(CultureInfo.InvariantCulture));
        var read = await workspace.ReadAsync(key, ct);
        if (!read.IsSuccess) return Error(output, read.Error.Message);
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var item = read.Value;
        var type = schema.Value.Types.Single(t => t.Reference == item.Type);
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var selected = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(id.Value),
            ["type"] = RenderCell.String(type.Rules.SelectMany(r => r.Values)
                .FirstOrDefault(v => v.DataType == "ado.native-type-name")?.Text ?? item.Type),
            ["title"] = Cell(item.Fields.TryGetValue("System.Title", out var title) ? title : FieldValue.Null),
            ["state"] = Cell(item.Fields.TryGetValue(type.StateField, out var state) ? state : FieldValue.Null),
            ["revision"] = RenderCell.String(item.Revision!.Token),
            ["isDirty"] = RenderCell.Boolean(snapshot.Value.Drafts.Any(d => d.Key == key)),
            ["fields"] = new("Fields", new RenderValue.Object(item.Fields.Where(f => selected is null || selected.Contains(f.Key))
                .ToDictionary(f => f.Key, f => Cell(f.Value), StringComparer.Ordinal))),
        };
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("workItem", cells)]));
        return 0;
    }

    internal async Task<int> UpdateAsync(string field, string? value, string output, string? format, string? file,
        bool stdin, int? id, bool append, CancellationToken ct)
    {
        if (id is null || id <= 0) return Error(output, "An explicit Core target is required. Pass --id.", 2);
        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null) return Error(output, formatError, 2);
        var body = await TextBodySource.ResolveAsync(value, file, stdin, Console.In, "inline value",
            "--file", "--stdin", trimTrailingNewline: true, ct);
        if (body.Outcome != TextBodySource.Outcome.Resolved) return Error(output, body.Error ?? "A field value is required.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope,
            id.Value.ToString(CultureInfo.InvariantCulture));
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var name = field.ToLowerInvariant() switch
        {
            "title" => "System.Title", "state" => "System.State", "description" => "System.Description", _ => field,
        };
        var definitions = schema.Value.Types.SelectMany(t => t.Fields)
            .Where(f => f.Reference.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (definitions.Length != 1) return Error(output, "The field is absent or ambiguous in the actual process schema.", 2);
        var definition = definitions[0];
        name = definition.Reference;
        var text = body.Value!;
        if (append)
        {
            var current = await workspace.ReadAsync(key, ct);
            if (!current.IsSuccess) return Error(output, current.Error.Message);
            if (!current.Value.Fields.TryGetValue(name, out var observed)
                || observed.Kind is not (FieldKind.Text or FieldKind.Html or FieldKind.Markdown))
                return Error(output, "Append requires an actually observed text field.", 2);
            text = observed.Text + text;
        }
        FieldValue parsed;
        if (definition.Kind == FieldKind.Integer && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            parsed = FieldValue.Integer(integer);
        else if (definition.Kind == FieldKind.Decimal && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            parsed = FieldValue.Decimal(number);
        else if (definition.Kind == FieldKind.Boolean && bool.TryParse(text, out var boolean)) parsed = FieldValue.Bool(boolean);
        else if (definition.Kind == FieldKind.Text) parsed = FieldValue.String(text);
        else if (definition.Kind == FieldKind.Html)
            parsed = FieldValue.RichText(format == "raw" ? text : HtmlFieldFormatter.ResolveComment(text, format).EffectiveValue, FieldKind.Html);
        else if (definition.Kind == FieldKind.Markdown) parsed = FieldValue.RichText(text, FieldKind.Markdown);
        else return Error(output, "This typed field value is invalid or its CLI conversion is not yet connected.", 2);
        var staged = await workspace.StageEditAsync(key, new([KeyValuePair.Create(name, parsed)]), ct);
        if (!staged.IsSuccess) return Error(output, staged.Error.Message);
        var preview = await workspace.PreviewDraftAsync(key, ct);
        if (!preview.IsSuccess) return Error(output, preview.Error.Message);
        var saved = await workspace.SaveDraftAsync(preview.Value, preview.Value.Proposal?.Digest, ct);
        if (!saved.IsSuccess) return Error(output, saved.Error.Message);
        if (saved.Value.Error is { } failure) return Error(output, failure.Message);
        if (!saved.Value.EffectsVerified) return Error(output, "The requested effect did not verify; retained journal evidence requires attention.");
        var message = $"#{id.Value} {name} updated.";
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(id.Value),
            ["title"] = RenderCell.String(name == "System.Title" ? text : ""),
            ["field"] = RenderCell.String(name), ["valueDisplay"] = RenderCell.String(text),
            ["valueSource"] = RenderCell.String(body.Origin.ToString().ToLowerInvariant()),
            ["append"] = RenderCell.Boolean(append), ["wasSeed"] = RenderCell.Boolean(false),
            ["message"] = RenderCell.String(message), ["digest"] = preview.Value.Proposal is { } proposal
                ? RenderCell.String(proposal.Digest) : new("", new RenderValue.Null()),
            ["journalStatus"] = RenderCell.String("Verified"),
        };
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([output == "human"
            ? new RenderNode.Text(message, Severity.Success) : new RenderNode.Record("fieldUpdated", cells)]));
        return 0;
    }

    private int Error(string output, string message, int code = 1)
    {
        CommandError.Write(renderer, Console.Error, output, message);
        return code;
    }

    internal async Task<int> ApplyProposalAsync(string? file, string? confirmation, string output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(confirmation))
            return Error(output, "A Core proposal file and its exact --confirm digest are required.", 2);
        var parsed = await CoreProposalFiles.ReadAsync(factory.WorkspaceRoot, file, ct);
        if (!parsed.IsSuccess) return Error(output, parsed.Error.Message, parsed.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        var proposal = parsed.Value;
        if (proposal.Digest != confirmation) return Error(output, "Confirmation differs from the exact canonical proposal digest.");
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var draft = proposal.Operations.Length == 1 ? snapshot.Value.Drafts.FirstOrDefault(d =>
            d.Key == proposal.Operations[0].Item && d.Changes.Equals(proposal.Operations[0].Fields)
            && (d.Key.IsLocal || d.Baseline?.Revision == proposal.Operations[0].ExpectedRevision)) : null;
        var applied = draft is null ? await workspace.ApplyAsync(proposal, confirmation, ct)
            : await workspace.SaveDraftAsync(new(draft, proposal), confirmation, ct);
        if (!applied.IsSuccess) return Error(output, applied.Error.Message);
        if (applied.Value.Error is { } failure) return Error(output, failure.Message);
        if (!applied.Value.EffectsVerified) return Error(output, "The proposal has an unresolved effect; no blind replay was performed.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("proposalApplied",
            new Dictionary<string, RenderCell>
            {
                ["digest"] = RenderCell.String(proposal.Digest),
                ["verified"] = RenderCell.Boolean(true),
                ["operationCount"] = RenderCell.Integer(proposal.Operations.Length),
                ["alreadyVerified"] = RenderCell.Boolean(applied.Value.AlreadyVerified),
            })]));
        return 0;
    }

    private static RenderCell Cell(FieldValue value) => value.Kind switch
    {
        FieldKind.Null => new("<unset>", new RenderValue.Null()),
        FieldKind.Integer => RenderCell.Integer(decimal.ToInt64(value.Number)),
        FieldKind.Decimal => new(value.Number.ToString(CultureInfo.InvariantCulture), new RenderValue.Decimal(value.Number)),
        FieldKind.Boolean => RenderCell.Boolean(value.Boolean),
        FieldKind.Instant => new(value.Instant.ToString("O", CultureInfo.InvariantCulture), new RenderValue.DateTime(value.Instant)),
        FieldKind.StringList => new(string.Join(", ", value.Values), new RenderValue.Array(value.Values.Select(v => RenderCell.String(v)).ToArray())),
        FieldKind.Raw => new(value.Text!, new RenderValue.Object(new Dictionary<string, RenderCell>
        {
            ["kind"] = RenderCell.String("raw"), ["type"] = RenderCell.String(value.DataType!), ["content"] = RenderCell.String(value.Text!),
        })),
        _ => RenderCell.String(value.Text!),
    };
}
