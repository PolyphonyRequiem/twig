using System.Globalization;
using System.Text.Json;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

internal sealed class CoreCommandAdapter(CoreWorkspaceFactory factory, RendererFactory renderer,
    Twig.Domain.Interfaces.IConsoleInput consoleInput)
{
    internal async Task<int> SaveAsync(int? id, bool all, string output, CancellationToken ct)
    {
        if (id is not null && (id <= 0 || all)) return Error(output, "Specify one positive --id or --all, not both.", 2);
        if (id is null && !all) return Error(output, "No Core active scope is selected. Specify --id or --all.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var drafts = snapshot.Value.Drafts.Where(draft => !draft.Key.IsLocal
            && (all || draft.Key.NativeId == id!.Value.ToString(CultureInfo.InvariantCulture))).ToArray();
        if (drafts.Length == 0) return SaveResult(output, 0, null);
        var captures = new List<CoreDraftPreview>();
        var operations = new List<Op>();
        foreach (var draft in drafts)
        {
            var preview = await workspace.PreviewDraftAsync(draft.Key, ct);
            if (!preview.IsSuccess) return Error(output, preview.Error.Message);
            captures.Add(preview.Value);
            if (preview.Value.Proposal is { } intent)
            {
                var op = intent.Operations.Single();
                operations.Add(new($"save-{operations.Count}", op.Kind, op.Item, op.ExpectedRevision,
                    op.Fields, op.Type, op.Other, op.Relation, op.Origin, op.Body,
                    op.Concurrency, op.ExpectedLocalVersion, op.Resource));
            }
        }
        Proposal? proposal = null;
        if (operations.Count != 0)
        {
            var prepared = await workspace.PrepareAsync(operations, ct);
            if (!prepared.IsSuccess) return Error(output, prepared.Error.Message);
            proposal = prepared.Value;
        }
        var saved = await workspace.SaveDraftSetAsync(captures, proposal, proposal?.Digest, ct);
        if (!saved.IsSuccess) return Error(output, saved.Error.Message);
        if (saved.Value.Error is { } error) return Error(output, error.Message);
        if (!saved.Value.EffectsVerified) return Error(output, "Save has unresolved journal effects; drafts were retained without replay.");
        return SaveResult(output, drafts.Length, proposal?.Digest);
    }

    private int SaveResult(string output, int count, string? digest)
    {
        var message = count == 0 ? "Nothing to save." : $"Saved {count} draft(s); all effects verified.";
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([output == "human"
            ? new RenderNode.Text(message, Severity.Success)
            : new RenderNode.Record("draftsSaved", new Dictionary<string, RenderCell>
            {
                ["message"] = RenderCell.String(message), ["itemsFlushed"] = RenderCell.Integer(count),
                ["digest"] = digest is null ? new("", new RenderValue.Null()) : RenderCell.String(digest),
            })]));
        return 0;
    }

    internal async Task<int> DiscardAsync(int? id, bool all, bool yes, string output, CancellationToken ct)
    {
        if (id is not null && (id <= 0 || all) || id is null && !all)
            return Error(output, "Specify one positive --id or --all. Seeds require seed discard.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var drafts = snapshot.Value.Drafts.Where(draft => !draft.Key.IsLocal
            && (all || draft.Key.NativeId == id!.Value.ToString(CultureInfo.InvariantCulture))).ToArray();
        if (!yes && drafts.Length != 0)
        {
            if (output != "human" || consoleInput.IsOutputRedirected)
                return Error(output, "Discard requires explicit --yes when interactive confirmation is unavailable.", 2);
            Console.Write($"Discard local field edits for {drafts.Length} item(s)? [y/N] ");
            if (!string.Equals(consoleInput.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Text("Discard cancelled.")]));
                return 0;
            }
        }
        var removed = drafts.Length == 0 ? Result<int>.Ok(0)
            : await workspace.DiscardDraftsAsync(drafts, snapshot.Value.Version, ct);
        if (!removed.IsSuccess) return Error(output, removed.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("draftsDiscarded",
            new Dictionary<string, RenderCell>
            {
                ["itemCount"] = RenderCell.Integer(removed.Value),
                ["message"] = RenderCell.String($"Discarded local edits for {removed.Value} item(s); no journal or seed was removed."),
            })]));
        return 0;
    }

    internal int Delete(string output) => Error(output,
        "Delete is unavailable: the provider cannot prove the required revision-bound deletion precondition. No legacy writer or unqualified delete was used.");

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
        WorkItem item;
        if (refresh)
        {
            var read = await workspace.ReadAsync(key, ct);
            if (!read.IsSuccess) return Error(output, read.Error.Message);
            item = read.Value;
        }
        else
        {
            var cached = await workspace.SnapshotAsync(ct);
            if (!cached.IsSuccess) return Error(output, cached.Error.Message);
            var found = cached.Value.Items.FirstOrDefault(value => value.Key == key);
            if (found is null) return Error(output, $"Work item #{id.Value} is not cached. Use show {id.Value} --refresh.");
            item = found;
        }
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var selected = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(id.Value),
            ["type"] = Cell(item.Fields.TryGetValue("System.WorkItemType", out var nativeType)
                ? nativeType : FieldValue.String(item.Type)),
            ["title"] = Cell(item.Fields.TryGetValue("System.Title", out var title) ? title : FieldValue.Null),
            ["state"] = Cell(item.Fields.TryGetValue("System.State", out var state) ? state : FieldValue.Null),
            ["revision"] = RenderCell.String(item.Revision!.Token),
            ["isDirty"] = RenderCell.Boolean(snapshot.Value.Drafts.Any(d => d.Key == key)),
            ["cached"] = RenderCell.Boolean(!refresh),
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
        if (!TryParse(definition.Kind, text, format, out var parsed))
            return Error(output, "This typed field value is invalid or its CLI conversion is not yet connected.", 2);
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

    internal async Task<int> PreviewProposalAsync(string? file, string output, CancellationToken ct, string? expectedDigest = null)
    {
        if (string.IsNullOrWhiteSpace(file)) return Error(output, "An explicit Core proposal file is required.", 2);
        var parsed = await CoreProposalFiles.ReadAsync(factory.WorkspaceRoot, file, ct);
        if (!parsed.IsSuccess) return Error(output, parsed.Error.Message, 2);
        var proposal = parsed.Value;
        if (expectedDigest is not null && expectedDigest != proposal.Digest)
            return Error(output, "The proposal differs from the exact expected review digest.");
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        if (proposal.Connection != workspace.Connection) return Error(output, "The proposal belongs to another immutable workspace connection.");
        var checkedIntent = await workspace.CheckProposalAsync(proposal, ct);
        if (!checkedIntent.IsSuccess) return Error(output, checkedIntent.Error.Message);
        var blocked = checkedIntent.Value.FirstOrDefault();
        var retained = await workspace.RetainReviewAsync(proposal, ct);
        if (!retained.IsSuccess) return Error(output, retained.Error.Message);
        var cells = new Dictionary<string, RenderCell>
        {
            ["digest"] = RenderCell.String(proposal.Digest),
            ["canApply"] = RenderCell.Boolean(blocked is null),
            ["operations"] = new("Operations", new RenderValue.Array(proposal.Operations.Select(op =>
                new RenderCell(op.Id, new RenderValue.Object(new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.String(op.Id), ["kind"] = RenderCell.String(op.Kind.ToString()),
                }))).ToArray())),
        };
        cells["reviewModel"] = new("Review", new RenderValue.Object(new Dictionary<string, RenderCell>
        {
            ["policyDecisions"] = new("Decisions", new RenderValue.Array(blocked is null ? [] : [
                new RenderCell(blocked.Message, new RenderValue.Object(new Dictionary<string, RenderCell>
                {
                    ["kind"] = RenderCell.String("process-required-fields"),
                    ["outcome"] = RenderCell.String("unavailable"),
                    ["blocking"] = RenderCell.Boolean(true),
                    ["reason"] = RenderCell.String(blocked.Message),
                }))
            ])),
        }));
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("proposalPreview", cells)]));
        return 0;
    }

    internal async Task<int> NoteAsync(string? text, string? file, bool stdin, int? id,
        string output, string? format, CancellationToken ct)
    {
        if (id is null || id <= 0) return Error(output, "An explicit Core note target is required. Pass --id.", 2);
        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null) return Error(output, formatError, 2);
        var body = await TextBodySource.ResolveAsync(text, file, stdin, Console.In, "note text",
            "--file", "--stdin", trimTrailingNewline: true, ct);
        if (body.Outcome != TextBodySource.Outcome.Resolved || string.IsNullOrWhiteSpace(body.Value))
            return Error(output, body.Error ?? "A nonempty note body is required.", 2);
        var value = HtmlFieldFormatter.ResolveComment(body.Value!, format).EffectiveValue;
        var executed = await ExecuteEffectAsync(id.Value,
            item => new("note", OpKind.Comment, item.Key, item.Revision, body: FieldValue.RichText(value, FieldKind.Html)),
            output, ct);
        if (executed is null) return 1;
        var message = $"Note added to #{id.Value}.";
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([output == "human"
            ? new RenderNode.Text(message, Severity.Success)
            : new RenderNode.Record("noteAdded", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id.Value), ["isPending"] = RenderCell.Boolean(false),
                ["message"] = RenderCell.String(message), ["digest"] = RenderCell.String(executed.Digest),
            })]));
        return 0;
    }

    internal async Task<int> StateAsync(string requested, int? id, string output, CancellationToken ct)
    {
        if (id is null || id <= 0) return Error(output, "An explicit Core state target is required. Pass --id.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, id.Value.ToString(CultureInfo.InvariantCulture));
        var read = await workspace.ReadAsync(key, ct);
        if (!read.IsSuccess) return Error(output, read.Error.Message);
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var type = schema.Value.Types.Single(t => t.Reference == read.Value.Type);
        var states = type.States.Where(s => s.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToArray();
        var category = false;
        if (states.Length == 0) states = type.States.Where(s => s.Name.StartsWith(requested, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (states.Length == 0 && Enum.TryParse<StateCategory>(requested, true, out var selected))
        {
            states = type.States.Where(s => s.Category == selected).ToArray();
            category = true;
        }
        if (states.Length != 1) return Error(output, "The requested state is absent or ambiguous in the actual process.");
        var from = read.Value.Fields[type.StateField].Text!;
        var target = states[0].Name;
        if (from == target)
        {
            renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("alreadyInState",
                new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.Integer(id.Value), ["state"] = RenderCell.String(from),
                    ["requestedState"] = RenderCell.String(requested), ["isCategoryResolution"] = RenderCell.Boolean(category),
                    ["message"] = RenderCell.String($"Already in state '{from}'."),
                })]));
            return 0;
        }
        var proposal = await workspace.PrepareAsync([new("state", OpKind.Batch, key, read.Value.Revision,
            new([KeyValuePair.Create(type.StateField, FieldValue.String(target))]))], ct);
        if (!proposal.IsSuccess) return Error(output, proposal.Error.Message);
        var applied = await workspace.ApplyAsync(proposal.Value, proposal.Value.Digest, ct);
        if (!applied.IsSuccess) return Error(output, applied.Error.Message);
        if (applied.Value.Error is { } failure) return Error(output, failure.Message);
        if (!applied.Value.EffectsVerified) return Error(output, "The transition has unresolved journal evidence.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("stateChanged",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id.Value), ["title"] = Cell(read.Value.Fields["System.Title"]),
                ["fromState"] = RenderCell.String(from), ["toState"] = RenderCell.String(target),
                ["requestedState"] = RenderCell.String(requested), ["transitionCount"] = RenderCell.Integer(1),
                ["isCategoryResolution"] = RenderCell.Boolean(category),
                ["message"] = RenderCell.String($"#{id.Value} moved to '{target}'."),
                ["digest"] = RenderCell.String(proposal.Value.Digest),
            })]));
        return 0;
    }

    internal async Task<int> BatchAsync(string? state, string[]? set, string? note, int? id, string? ids,
        string output, string? format, CancellationToken ct)
    {
        if (id is not null && ids is not null) return Error(output, "--id and --ids are mutually exclusive.", 2);
        var targets = ids is null ? (id is { } single ? [single] : Array.Empty<int>()) : ids.Split(',')
            .Select(value => int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0).ToArray();
        if (targets.Length == 0 || targets.Any(value => value <= 0)) return Error(output, "Explicit valid batch targets are required.", 2);
        var parsed = Twig.Domain.ValueObjects.FieldAssignment.ParseAll(set, "--set");
        if (!parsed.IsSuccess) return Error(output, parsed.Error, 2);
        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null) return Error(output, formatError, 2);
        if (parsed.Value.Count == 0 && state is null && note is null) return Error(output, "At least one batch effect is required.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var operations = new List<Op>();
        foreach (var target in targets.Distinct())
        {
            var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, target.ToString(CultureInfo.InvariantCulture));
            var read = await workspace.ReadAsync(key, ct);
            if (!read.IsSuccess) return Error(output, read.Error.Message);
            var type = schema.Value.Types.Single(t => t.Reference == read.Value.Type);
            var fields = FieldValues.Empty;
            foreach (var assignment in parsed.Value)
            {
                var definition = type.Fields.FirstOrDefault(f => f.Reference.Equals(assignment.FieldName, StringComparison.OrdinalIgnoreCase));
                if (definition is null || !TryParse(definition.Kind, assignment.NewValue ?? "", format, out var value))
                    return Error(output, "A batch field is unavailable or its typed value is invalid.", 2);
                fields = fields.Set(definition.Reference, value);
            }
            if (state is not null)
            {
                var selected = type.States.Where(value => value.Name.Equals(state, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (selected.Length != 1) return Error(output, "The batch state must name an actual unambiguous native state.", 2);
                fields = fields.Set(type.StateField, FieldValue.String(selected[0].Name));
            }
            var body = note is null ? null : FieldValue.RichText(HtmlFieldFormatter.ResolveComment(note, format).EffectiveValue, FieldKind.Html);
            operations.Add(fields.Count == 0
                ? new("note-" + target, OpKind.Comment, key, read.Value.Revision, body: body)
                : new("batch-" + target, OpKind.Batch, key, read.Value.Revision, fields, body: body));
        }
        var proposal = await workspace.PrepareAsync(operations, ct);
        if (!proposal.IsSuccess) return Error(output, proposal.Error.Message);
        var executed = await workspace.ApplyAsync(proposal.Value, proposal.Value.Digest, ct);
        if (!executed.IsSuccess) return Error(output, executed.Error.Message);
        if (executed.Value.Error is { } failure) return Error(output, failure.Message);
        if (!executed.Value.EffectsVerified) return Error(output, "The ordered batch has unresolved effects; no remaining operation was replayed.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("batchCompleted",
            new Dictionary<string, RenderCell>
            {
                ["digest"] = RenderCell.String(proposal.Value.Digest), ["successCount"] = RenderCell.Integer(operations.Count),
                ["failureCount"] = RenderCell.Integer(0),
                ["results"] = new("Results", new RenderValue.Array(operations.Select(op => new RenderCell(op.Item.NativeId!,
                    new RenderValue.Object(new Dictionary<string, RenderCell>
                    {
                        ["itemId"] = RenderCell.Integer(long.Parse(op.Item.NativeId!, CultureInfo.InvariantCulture)),
                        ["success"] = RenderCell.Boolean(true),
                    }))).ToArray())),
            })]));
        return 0;
    }

    private static bool TryParse(FieldKind kind, string text, string? format, out FieldValue value)
    {
        value = FieldValue.Null;
        if (kind == FieldKind.Integer && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            value = FieldValue.Integer(integer);
        else if (kind == FieldKind.Decimal && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            value = FieldValue.Decimal(number);
        else if (kind == FieldKind.Boolean && bool.TryParse(text, out var boolean)) value = FieldValue.Bool(boolean);
        else if (kind == FieldKind.Text) value = FieldValue.String(text);
        else if (kind == FieldKind.Html)
            value = FieldValue.RichText(format == "raw" ? text : HtmlFieldFormatter.ResolveComment(text, format).EffectiveValue, FieldKind.Html);
        else if (kind == FieldKind.Markdown) value = FieldValue.RichText(text, FieldKind.Markdown);
        else return false;
        return true;
    }

    internal async Task<int> PatchAsync(int? id, string? json, bool stdin, string? format, string output, CancellationToken ct)
    {
        if (id is null || id <= 0) return Error(output, "An explicit Core patch target is required. Pass --id.", 2);
        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null) return Error(output, formatError, 2);
        var body = await TextBodySource.ResolveAsync(json, null, stdin, Console.In, "--json",
            "--file", "--stdin", trimTrailingNewline: false, ct);
        if (body.Outcome != TextBodySource.Outcome.Resolved) return Error(output, body.Error ?? "A JSON field object is required.", 2);
        JsonElement payload;
        try { payload = JsonSerializer.Deserialize(body.Value!, CoreWorkspaceJsonContext.Default.JsonElement); }
        catch (JsonException ex) { return Error(output, ex.Message, 2); }
        if (payload.ValueKind != JsonValueKind.Object) return Error(output, "Patch input must be a field object.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, id.Value.ToString(CultureInfo.InvariantCulture));
        var read = await workspace.ReadAsync(key, ct);
        if (!read.IsSuccess) return Error(output, read.Error.Message);
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var type = schema.Value.Types.Single(t => t.Reference == read.Value.Type);
        var fields = FieldValues.Empty;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.EnumerateObject())
        {
            if (!names.Add(property.Name)) return Error(output, "Duplicate patch field identity.", 2);
            var definition = type.Fields.FirstOrDefault(f => f.Reference.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (definition is null) return Error(output, "A patch field is absent from the actual native schema.", 2);
            FieldValue value;
            if (property.Value.ValueKind == JsonValueKind.Null) value = FieldValue.Null;
            else if (property.Value.ValueKind == JsonValueKind.String && TryParse(definition.Kind, property.Value.GetString()!, format, out var text))
                value = text;
            else if (definition.Kind == FieldKind.Integer && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt64(out var integer)) value = FieldValue.Integer(integer);
            else if (definition.Kind == FieldKind.Decimal && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDecimal(out var number)) value = FieldValue.Decimal(number);
            else if (definition.Kind == FieldKind.Boolean && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                value = FieldValue.Bool(property.Value.GetBoolean());
            else return Error(output, "A patch value has an invalid native type.", 2);
            fields = fields.Set(definition.Reference, value);
        }
        if (fields.Count == 0) return Error(output, "At least one explicit patch field is required.", 2);
        var proposal = await workspace.PrepareAsync([new("patch", OpKind.Batch, key, read.Value.Revision, fields)], ct);
        if (!proposal.IsSuccess) return Error(output, proposal.Error.Message);
        var applied = await workspace.ApplyAsync(proposal.Value, proposal.Value.Digest, ct);
        if (!applied.IsSuccess) return Error(output, applied.Error.Message);
        if (applied.Value.Error is { } failure) return Error(output, failure.Message);
        if (!applied.Value.EffectsVerified) return Error(output, "The patch did not verify; its journal remains authoritative.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("fieldsPatched",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id.Value), ["title"] = Cell(read.Value.Fields["System.Title"]),
                ["fieldCount"] = RenderCell.Integer(fields.Count), ["wasSeed"] = RenderCell.Boolean(false),
                ["message"] = RenderCell.String($"Patched {fields.Count} field(s) on #{id.Value}."),
                ["digest"] = RenderCell.String(proposal.Value.Digest),
            })]));
        return 0;
    }

    internal async Task<int> LinkResourceAsync(string url, string? name, int? id, string output, CancellationToken ct)
    {
        if (id is null || id <= 0) return Error(output, "An explicit Core artifact target is required. Pass --id.", 2);
        ResourceLink resource;
        try
        {
            resource = url.StartsWith("vstfs:///", StringComparison.OrdinalIgnoreCase)
                ? new("ArtifactLink", url, name ?? "Artifact")
                : new("Hyperlink", url, comment: name);
        }
        catch (ArgumentException ex) { return Error(output, ex.Message, 2); }
        var executed = await ExecuteEffectAsync(id.Value,
            item => new("resource", OpKind.LinkResource, item.Key, item.Revision, resource: resource), output, ct);
        if (executed is null) return 1;
        var message = $"Linked {url} to #{id.Value}.";
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([output == "human"
            ? new RenderNode.Text(message, Severity.Success)
            : new RenderNode.Record("artifactLinked", new Dictionary<string, RenderCell>
            {
                ["itemId"] = RenderCell.Integer(id.Value), ["url"] = RenderCell.String(url),
                ["alreadyLinked"] = RenderCell.Boolean(false), ["message"] = RenderCell.String(message),
                ["digest"] = RenderCell.String(executed.Digest),
                ["name"] = name is null ? new("", new RenderValue.Null()) : RenderCell.String(name),
            })]));
        return 0;
    }

    private async Task<Proposal?> ExecuteEffectAsync(int id, Func<WorkItem, Op> build, string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) { Error(output, opened.Error.Message); return null; }
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var read = await workspace.ReadAsync(ItemKey.Remote(workspace.Connection.Provider,
            workspace.Connection.ResourceScope, id.ToString(CultureInfo.InvariantCulture)), ct);
        if (!read.IsSuccess) { Error(output, read.Error.Message); return null; }
        var proposal = await workspace.PrepareAsync([build(read.Value)], ct);
        if (!proposal.IsSuccess) { Error(output, proposal.Error.Message); return null; }
        var applied = await workspace.ApplyAsync(proposal.Value, proposal.Value.Digest, ct);
        if (!applied.IsSuccess) { Error(output, applied.Error.Message); return null; }
        if (applied.Value.Error is { } error) { Error(output, error.Message); return null; }
        if (!applied.Value.EffectsVerified) { Error(output, "The effect is unresolved; no automatic retry was performed."); return null; }
        return proposal.Value;
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
