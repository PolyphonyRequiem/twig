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
    Twig.Domain.Interfaces.IConsoleInput consoleInput, Twig.Domain.Interfaces.IEditorLauncher editorLauncher)
{
    private async Task<Result<int>> ResolveIdAsync(int? id, string output, CancellationToken ct)
    {
        if (id is not null) return id != 0 ? Result<int>.Ok(id.Value) : Result<int>.Fail(ErrorCode.InvalidInput, "A nonzero target is required.");
        if (output != "human") return Result<int>.Fail(ErrorCode.InvalidInput, "Structured operations require an explicit target. Pass --id.");
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Result<int>.Fail(opened.Error);
        using var handle = opened.Value;
        var context = await handle.Workspace.Context.ReadAsync(ct);
        if (!context.IsSuccess) return Result<int>.Fail(context.Error);
        if (context.Value.Position < 0) return Result<int>.Fail(ErrorCode.NotFound, "No active Core item is selected. Run `twig set <id>` or pass --id.");
        var key = context.Value.History[context.Value.Position].Resolve(handle.Workspace.Connection);
        var snapshot = await handle.Workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Result<int>.Fail(snapshot.Error);
        if (key.IsLocal)
        {
            if (!KnownLocalKeys(snapshot.Value).Contains(key)
                || KnownLocalKeys(snapshot.Value).Count(local => LocalAlias(local) == LocalAlias(key)) != 1)
                return Result<int>.Fail(ErrorCode.Conflict, "The retained active UUID is absent or ambiguous; it was not replaced.");
            var mapped = snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == key);
            if (mapped is not null) key = mapped.Remote;
            else return Result<int>.Ok(LocalAlias(key));
        }
        return int.TryParse(key.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? Result<int>.Ok(number) : Result<int>.Fail(ErrorCode.Unsupported, "This native identity has no qualified numeric command target.");
    }

    internal async Task<int> SetAsync(string target, string output, CancellationToken ct)
    {
        if (output != "human") return Error(output, "Structured set is refused because it changes interactive context.", 2);
        if (string.IsNullOrWhiteSpace(target)) return Error(output, "Supply a work item ID or cached title pattern.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local aliases are ambiguous; no active identity was guessed.");
        ItemKey? selected = null;
        if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number != 0)
            selected = number < 0 ? KnownLocalKeys(snapshot.Value).SingleOrDefault(key => LocalAlias(key) == number)
                : ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, number.ToString(CultureInfo.InvariantCulture));
        else
        {
            var candidates = snapshot.Value.Items.Concat(snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal)
                .Select(draft => new WorkItem(draft.Key, draft.Type, draft.Changes, null))).Where(item =>
                    item.Fields.TryGetValue("System.Title", out var title) && title.Text is { } text
                    && (target.Contains('*') || target.Contains('?')
                        ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(target, text, ignoreCase: true)
                        : text.Contains(target, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length == 1) selected = candidates[0].Key;
            else if (candidates.Length > 1)
            {
                foreach (var item in candidates)
                    Console.Error.WriteLine($"#{(item.Key.IsLocal ? LocalAlias(item.Key).ToString(CultureInfo.InvariantCulture) : item.Key.NativeId)} {item.Fields["System.Title"].Text}");
                if (!consoleInput.IsOutputRedirected)
                {
                    Console.Write("Choose item ID: ");
                    var chosen = consoleInput.ReadLine();
                    if (int.TryParse(chosen, out var chosenId))
                        selected = candidates.SingleOrDefault(item => item.Key.IsLocal
                            ? LocalAlias(item.Key) == chosenId : item.Key.NativeId == chosenId.ToString(CultureInfo.InvariantCulture))?.Key;
                }
            }
        }
        if (selected is null) return Error(output, "The target is absent, ambiguous or selection was cancelled; active context was preserved.");
        var key = snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == selected)?.Remote ?? selected;
        var shownItem = key.IsLocal
            ? snapshot.Value.Drafts.Where(draft => draft.Key == key).Select(draft => new WorkItem(key, draft.Type, draft.Changes, null)).SingleOrDefault()
            : snapshot.Value.Items.SingleOrDefault(item => item.Key == key);
        if (shownItem is null && !key.IsLocal)
        {
            var fetched = await workspace.ReadAsync(key, ct);
            if (!fetched.IsSuccess) return Error(output, fetched.Error.Message);
            shownItem = fetched.Value;
        }
        if (shownItem is null) return Error(output, "The exact selected item is unavailable; active context was preserved.");
        var context = await workspace.Context.ReadAsync(ct);
        if (!context.IsSuccess) return Error(output, context.Error.Message);
        var history = context.Value.History.Take(context.Value.Position + 1).Append(CoreContextKey.Capture(selected)).ToArray();
        var saved = await workspace.Context.WriteAsync(context.Value, context.Value with { History = history, Position = history.Length - 1 }, ct);
        if (!saved.IsSuccess) return Error(output, saved.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Text(
            $"#{(key.IsLocal ? LocalAlias(key).ToString(CultureInfo.InvariantCulture) : key.NativeId)} {shownItem.Fields.GetValueOrDefault("System.Title")?.Text}", Severity.Success)]));
        return 0;
    }

    internal async Task<int> NavigateAsync(string direction, string? pattern, string output, CancellationToken ct)
    {
        if (output != "human") return Error(output, "Structured navigation is refused because it changes interactive context.", 2);
        var target = await ResolveIdAsync(null, output, ct);
        if (!target.IsSuccess) return Error(output, target.Error.Message);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var current = target.Value < 0 ? KnownLocalKeys(snapshot.Value).Single(key => LocalAlias(key) == target.Value)
            : ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, target.Value.ToString(CultureInfo.InvariantCulture));
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var relations = schema.Value.Relations.Where(relation => relation.Shape == RelationShape.Hierarchy && relation.Directed).ToArray();
        if (relations.Length != 1) return Error(output, "Navigation requires a unique discovered hierarchy relation.");
        var hierarchy = relations[0].Reference;
        if (!current.IsLocal && snapshot.Value.Items.SingleOrDefault(item => item.Key == current)?.RelationshipsComplete != true)
        {
            var observed = await workspace.ReadAsync(current, ct);
            if (!observed.IsSuccess) return Error(output, observed.Error.Message);
            snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        }
        ItemKey Resolve(ItemKey key) => snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
        var edges = snapshot.Value.Relationships.Where(edge => !edge.Deleted && edge.Kind == hierarchy)
            .Select(edge => (From: Resolve(edge.From), To: Resolve(edge.To)))
            .Concat(snapshot.Value.Drafts.Where(draft => draft.Parent is not null && draft.ParentRelation == hierarchy)
                .Select(draft => (From: Resolve(draft.Parent!), To: Resolve(draft.Key)))).Distinct().ToArray();
        var parents = edges.Where(edge => edge.To == current).Select(edge => edge.From).Distinct().ToArray();
        if (parents.Length > 1) return Error(output, "The active item's parent is ambiguous; no navigation target was guessed.");
        ItemKey[] candidates;
        if (direction == "up") candidates = parents;
        else if (direction == "down") candidates = edges.Where(edge => edge.From == current).Select(edge => edge.To).Distinct().ToArray();
        else
        {
            var siblings = parents.Length == 1 ? edges.Where(edge => edge.From == parents[0]).Select(edge => edge.To)
                : snapshot.Value.Items.Select(item => item.Key).Concat(snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal).Select(draft => draft.Key))
                    .Where(key => !edges.Any(edge => edge.To == key));
            var ordered = siblings.Distinct().OrderBy(key => key.IsLocal ? LocalAlias(key) : int.Parse(key.NativeId!, CultureInfo.InvariantCulture)).ToArray();
            var index = Array.IndexOf(ordered, current);
            if (index < 0 || ordered.Length < 2) return Error(output, "No sibling is available for navigation.");
            candidates = [ordered[(index + (direction == "next" ? 1 : ordered.Length - 1)) % ordered.Length]];
        }
        if (pattern is not null)
            candidates = candidates.Where(key => (key.IsLocal ? LocalAlias(key).ToString(CultureInfo.InvariantCulture) : key.NativeId) == pattern
                || snapshot.Value.Items.Where(item => item.Key == key).Select(item => item.Fields.GetValueOrDefault("System.Title")?.Text)
                    .Concat(snapshot.Value.Drafts.Where(draft => draft.Key == key).Select(draft => draft.Changes.GetValueOrDefault("System.Title")?.Text))
                    .Any(title => title is not null && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                        pattern.Contains('*') || pattern.Contains('?') ? pattern : "*" + pattern + "*", title, ignoreCase: true))).ToArray();
        if (candidates.Length != 1)
        {
            foreach (var key in candidates) Console.Error.WriteLine("#" + (key.IsLocal ? LocalAlias(key).ToString(CultureInfo.InvariantCulture) : key.NativeId));
            if (candidates.Length > 1 && !consoleInput.IsOutputRedirected)
            {
                Console.Write("Choose item ID: ");
                var choice = consoleInput.ReadLine();
                candidates = candidates.Where(key => (key.IsLocal ? LocalAlias(key).ToString(CultureInfo.InvariantCulture) : key.NativeId) == choice).ToArray();
            }
        }
        if (candidates.Length != 1) return Error(output, "No unique navigation target was selected; active context was preserved.");
        return await SetAsync(candidates[0].IsLocal ? LocalAlias(candidates[0]).ToString(CultureInfo.InvariantCulture) : candidates[0].NativeId!, output, ct);
    }

    internal async Task<int> MoveHistoryAsync(int direction, string output, CancellationToken ct)
    {
        if (output != "human") return Error(output, "Structured history navigation is refused because it changes interactive context.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var context = await handle.Workspace.Context.ReadAsync(ct);
        if (!context.IsSuccess) return Error(output, context.Error.Message);
        var position = context.Value.Position + direction;
        if (position < 0 || position >= context.Value.History.Length)
            return Error(output, "No further active-item history is available.");
        var selected = context.Value.History[position].Resolve(handle.Workspace.Connection);
        var snapshot = await handle.Workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var key = snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == selected)?.Remote ?? selected;
        if (key.IsLocal && !snapshot.Value.Drafts.Any(draft => draft.Key == key))
            return Error(output, "The historical local draft is no longer available; history was preserved.");
        if (!key.IsLocal && !snapshot.Value.Items.Any(item => item.Key == key))
        {
            var observed = await handle.Workspace.ReadAsync(key, ct);
            if (!observed.IsSuccess) return Error(output, observed.Error.Message);
        }
        var saved = await handle.Workspace.Context.WriteAsync(context.Value, context.Value with { Position = position }, ct);
        if (!saved.IsSuccess) return Error(output, saved.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Text(
            $"#{(key.IsLocal ? LocalAlias(key).ToString(CultureInfo.InvariantCulture) : key.NativeId)} selected from history.", Severity.Success)]));
        return 0;
    }

    internal async Task<int> NavigationHistoryAsync(string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var context = await handle.Workspace.Context.ReadAsync(ct);
        if (!context.IsSuccess) return Error(output, context.Error.Message);
        var rows = context.Value.History.Select((key, index) => new RenderRow("navigationHistory",
            new Dictionary<string, RenderCell>
            {
                ["position"] = RenderCell.Integer(index), ["active"] = RenderCell.Boolean(index == context.Value.Position),
                ["localId"] = key.LocalId is { } local ? RenderCell.String(local.ToString("D")) : new("", new RenderValue.Null()),
                ["nativeId"] = key.NativeId is { } native ? RenderCell.String(native) : new("", new RenderValue.Null()),
            })).ToArray();
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Table(null,
            [new("position", "Position"), new("nativeId", "Native ID"), new("localId", "Local UUID"), new("active", "Active")], rows)]));
        return 0;
    }

    internal async Task<int> SaveAsync(int? id, bool all, string output, CancellationToken ct)
    {
        if (id is null && !all)
        {
            var selected = await ResolveIdAsync(null, output, ct);
            if (!selected.IsSuccess) return Error(output, selected.Error.Message, selected.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
            id = selected.Value;
        }
        if (id < 0 && !all) return await SeedPublishAsync(id, false, false, false, null, null, output, ct);
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
                    op.Concurrency, op.ExpectedLocalVersion, op.Resource, op.Links));
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

    internal async Task<int> SeedViewAsync(string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var snapshot = await handle.Workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var seeds = snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal).ToArray();
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local integer aliases collide; use canonical UUID detail until the aliases are explicitly reconciled.");
        var rows = seeds.Select(draft => new RenderRow(null, new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(LocalAlias(draft.Key)),
            ["localId"] = RenderCell.String(draft.Key.LocalId.ToString("D")),
            ["type"] = RenderCell.String(draft.Type),
            ["title"] = Cell(draft.Changes.TryGetValue("System.Title", out var title) ? title : FieldValue.Null),
            ["isSeed"] = RenderCell.Boolean(true),
            ["fields"] = new("Fields", new RenderValue.Object(draft.Changes.ToDictionary(field => field.Key, field => Cell(field.Value), StringComparer.Ordinal))),
        })).ToArray();
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Table("Local seeds",
            [new("id", "ID"), new("type", "Type"), new("title", "Title")], rows)]));
        return 0;
    }

    internal async Task<int> SeedNewAsync(string? title, string? requestedType, bool editor, int? parent,
        bool noParent, string? description, string[]? fields, string output, CancellationToken ct,
        Action<Draft>? capture = null, ItemKey? predecessor = null)
    {
        if (noParent && parent is not null) return Error(output, "--parent and --no-parent are mutually exclusive.", 2);
        if (!noParent && parent is null)
        {
            var selected = await ResolveIdAsync(null, output, ct);
            if (!selected.IsSuccess) return Error(output, selected.Error.Message, selected.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
            parent = selected.Value;
        }
        if (!editor && string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(requestedType))
            return Error(output, "Unparented seed creation requires --title and --type.", 2);
        var parsed = Twig.Domain.ValueObjects.FieldAssignment.ParseAll(fields, "--field");
        if (!parsed.IsSuccess) return Error(output, parsed.Error, 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        ItemKey? parentKey = null;
        if (parent is not null)
        {
            if (parent == 0) return Error(output, "A nonzero explicit parent target is required.", 2);
            if (parent < 0)
            {
                var current = await workspace.SnapshotAsync(ct);
                if (!current.IsSuccess) return Error(output, current.Error.Message);
                var matches = KnownLocalKeys(current.Value).Where(key => LocalAlias(key) == parent).ToArray();
                if (matches.Length != 1) return Error(output, "The local parent alias is absent or ambiguous.");
                parentKey = matches[0];
                var mapping = current.Value.Mappings.FirstOrDefault(value => value.Local == parentKey);
                if (mapping is not null)
                {
                    var observed = await workspace.ReadAsync(mapping.Remote, ct);
                    if (!observed.IsSuccess) return Error(output, observed.Error.Message);
                }
                else if (!current.Value.Drafts.Any(draft => draft.Key == parentKey))
                    return Error(output, "The local parent has neither a retained draft nor a publication mapping.");
            }
            else
            {
                parentKey = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope,
                    parent.Value.ToString(CultureInfo.InvariantCulture));
                var observed = await workspace.ReadAsync(parentKey, ct);
                if (!observed.IsSuccess) return Error(output, observed.Error.Message);
            }
        }
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var types = schema.Value.Types.Where(type => !type.Hidden && (type.Reference.Equals(requestedType, StringComparison.OrdinalIgnoreCase)
            || type.Rules.SelectMany(rule => rule.Values).Any(value => value.DataType == "ado.native-type-name"
                && string.Equals(value.Text, requestedType, StringComparison.OrdinalIgnoreCase)))).ToArray();
        if (types.Length != 1) return Error(output, "The requested seed type is absent or ambiguous in the actual process.", 2);
        var type = types[0];
        string? parentRelation = null;
        if (parentKey is not null)
        {
            var hierarchy = schema.Value.Relations.Where(relation => relation.Shape == RelationShape.Hierarchy && relation.Directed).ToArray();
            if (hierarchy.Length != 1) return Error(output, "The native process has no unique observed parent/child relation.");
            parentRelation = hierarchy[0].Reference;
        }
        var changes = new Dictionary<string, FieldValue>(StringComparer.Ordinal)
        {
            ["System.Title"] = FieldValue.String(title ?? ""),
        };
        var initial = type.Rules.Where(rule => !rule.Disabled && rule.Action == RuleAction.Allowed
                && rule.Field == type.StateField && rule.Conditions.Length == 1
                && rule.Conditions[0].Kind == ConditionKind.Was && rule.Conditions[0].Field == type.StateField
                && rule.Conditions[0].Value?.Kind == FieldKind.Null)
            .SelectMany(rule => rule.Values).Where(value => value.Kind == FieldKind.Text)
            .Select(value => value.Text!).Distinct(StringComparer.Ordinal).ToArray();
        if (initial.Length == 1) changes[type.StateField] = FieldValue.String(initial[0]);
        var descriptionField = type.Fields.FirstOrDefault(field => field.Reference == "System.Description");
        if (description is not null)
        {
            if (descriptionField is null || !TryParse(descriptionField.Kind, description, null, out var value))
                return Error(output, "The actual seed type cannot accept the supplied description.", 2);
            changes[descriptionField.Reference] = value;
        }
        foreach (var assignment in parsed.Value)
        {
            var definition = type.Fields.FirstOrDefault(field => field.Reference.Equals(assignment.FieldName, StringComparison.OrdinalIgnoreCase));
            if (definition is null || definition.ReadOnly || !TryParse(definition.Kind, assignment.NewValue ?? "", null, out var value))
                return Error(output, "A seed field is absent, read-only or not a valid typed value in the actual process.", 2);
            changes[definition.Reference] = value;
        }
        if (!changes.ContainsKey(type.StateField))
            return Error(output, "The process has no unique observed initial state. Supply its state field explicitly with --field.", 2);
        var assigned = type.Fields.FirstOrDefault(field => field.Reference == "System.AssignedTo" && !field.ReadOnly);
        if (assigned?.Kind == FieldKind.Identity && !changes.ContainsKey(assigned.Reference))
            changes[assigned.Reference] = FieldValue.Identity(workspace.NativeActor);
        if (editor)
        {
            var edited = await EditSeedFieldsAsync(type, new(changes), ct);
            if (!edited.IsSuccess) return Error(output, edited.Error.Message);
            if (edited.Value is null)
            {
                renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedEditCancelled",
                    new Dictionary<string, RenderCell> { ["message"] = RenderCell.String("Editor cancelled; no seed was staged.") })]));
                return 0;
            }
            foreach (var field in edited.Value) changes[field.Key] = field.Value;
        }
        if (!changes.TryGetValue("System.Title", out var seedTitle) || string.IsNullOrWhiteSpace(seedTitle.Text))
            return Error(output, "A seed title is required; no draft was staged.", 2);
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var aliases = KnownLocalKeys(snapshot.Value).Select(LocalAlias).ToHashSet();
        var key = ItemKey.Local(Guid.CreateVersion7());
        if (aliases.Contains(LocalAlias(key)))
            return Error(output, "The allocated seed alias collides with retained local identity; no seed was staged.");
        CreationLink[] links = [];
        if (predecessor is not null)
        {
            var dependency = schema.Value.Relations.Where(relation => relation.Shape == RelationShape.Dependency && relation.Directed).ToArray();
            if (dependency.Length != 1) return Error(output, "The native process has no unique observed dependency relation; no chain seed was staged.");
            links = [new(predecessor, dependency[0].Reference, false)];
        }
        var staged = await workspace.StageSeedAsync(type.Reference, new(changes), ct, key.LocalId,
            parentKey, parentRelation, links: links);
        if (!staged.IsSuccess) return Error(output, staged.Error.Message);
        if (capture is not null) { capture(staged.Value); return 0; }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedCreated",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(LocalAlias(key)), ["localId"] = RenderCell.String(key.LocalId.ToString("D")),
                ["type"] = RenderCell.String(requestedType), ["title"] = RenderCell.String(seedTitle.Text),
                ["isSeed"] = RenderCell.Boolean(true), ["isDirty"] = RenderCell.Boolean(true),
                ["message"] = RenderCell.String("Created a local draft; no native creation was attempted."),
            })]));
        return 0;
    }

    private async Task<Result<FieldValues?>> EditSeedFieldsAsync(ItemType type, FieldValues current, CancellationToken ct, string? selectedField = null)
    {
        selectedField = selectedField?.ToLowerInvariant() switch
        {
            "title" => "System.Title", "description" => "System.Description", "state" => type.StateField,
            "assignedto" => "System.AssignedTo", _ => selectedField,
        };
        static string Display(string reference) => reference switch
        {
            "System.Title" => "Title", "System.Description" => "Description", "System.State" => "State",
            "System.AssignedTo" => "AssignedTo", _ => reference,
        };
        var definitions = type.Fields.Where(field => selectedField is null || field.Reference.Equals(selectedField, StringComparison.OrdinalIgnoreCase))
            .Select(field => new Twig.Domain.ValueObjects.FieldDefinition(field.Reference,
            Display(field.Reference), field.Kind.ToString(), field.ReadOnly)).ToArray();
        if (definitions.Length == 0) return Result<FieldValues?>.Fail(ErrorCode.InvalidInput, "The editor field is absent from the actual native type.");
        var values = type.Fields.ToDictionary(field => field.Reference, field =>
            current.TryGetValue(field.Reference, out var value) && value.Kind != FieldKind.Null ? Cell(value).DisplayText : null, StringComparer.OrdinalIgnoreCase);
        var initial = Twig.Domain.Services.Seed.SeedEditorFormat.Generate(values, definitions);
        var edited = await editorLauncher.LaunchAsync(initial, ct);
        if (edited is null) return Result<FieldValues?>.Ok(null);
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in edited.Split('\n').Where(line => line.StartsWith("# ", StringComparison.Ordinal)))
        {
            var display = line[2..].Trim();
            var definition = definitions.SingleOrDefault(field => field.DisplayName.Equals(display, StringComparison.OrdinalIgnoreCase));
            if (definition is null || definition.IsReadOnly || !sections.Add(definition.ReferenceName))
                return Result<FieldValues?>.Fail(ErrorCode.InvalidInput, "Editor contains an unknown, read-only or duplicate field section.");
        }
        var parsed = Twig.Domain.Services.Seed.SeedEditorFormat.Parse(edited, definitions);
        var changes = FieldValues.Empty;
        foreach (var field in parsed)
        {
            var before = values.GetValueOrDefault(field.Key);
            if (string.Equals(before ?? "", field.Value ?? "", StringComparison.Ordinal)) continue;
            if (field.Key == "System.Title" && string.IsNullOrWhiteSpace(field.Value))
                return Result<FieldValues?>.Fail(ErrorCode.InvalidInput, "A seed title cannot be cleared.");
            var definition = type.Fields.Single(value => value.Reference == field.Key);
            if (field.Value is null) changes = changes.Set(field.Key, FieldValue.Null);
            else if (TryParse(definition.Kind, field.Value, null, out var value)) changes = changes.Set(field.Key, value);
            else return Result<FieldValues?>.Fail(ErrorCode.InvalidInput, "The editor value does not match the actual native field type.");
        }
        return Result<FieldValues?>.Ok(changes);
    }

    internal async Task<int> SeedEditAsync(int id, string output, CancellationToken ct, string? selectedField = null)
    {
        if (id >= 0) return Error(output, "Seed editing requires an unpublished local seed alias.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var matches = KnownLocalKeys(snapshot.Value).Where(key => LocalAlias(key) == id).ToArray();
        var draft = matches.Length == 1 ? snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == matches[0]) : null;
        if (draft is null) return Error(output, "The exact unpublished seed alias is absent or ambiguous.");
        if (snapshot.Value.Mappings.Any(mapping => mapping.Local == draft.Key)
            || snapshot.Value.Journal.Any(row => row.Status != OpStatus.Planned && snapshot.Value.Proposals
                .Single(proposal => proposal.Digest == row.Digest).Operations[row.Operation].Item == draft.Key))
            return Error(output, "The original creation UUID has already been attempted; its captured editor intent remains fenced.");
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var type = schema.Value.Types.SingleOrDefault(type => type.Reference == draft.Type);
        if (type is null) return Error(output, "The seed type is absent from the actual native schema.");
        var edited = await EditSeedFieldsAsync(type, draft.Changes, ct, selectedField);
        if (!edited.IsSuccess) return Error(output, edited.Error.Message);
        if (edited.Value is { Count: > 0 } fields)
        {
            var captured = await workspace.CaptureSeedFieldsAsync(draft, snapshot.Value.Version, fields, ct);
            if (!captured.IsSuccess) return Error(output, captured.Error.Message);
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record(edited.Value is null ? "seedEditCancelled"
            : edited.Value.Count == 0 ? "seedEditNoChanges" : "seedEdited", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id), ["changedFieldCount"] = RenderCell.Integer(edited.Value?.Count ?? 0),
                ["message"] = RenderCell.String(edited.Value is null ? "Seed editor cancelled." : "Retained typed seed edit; no native creation was attempted."),
            })]));
        return 0;
    }

    internal async Task<int> EditAsync(int? id, string? field, string output, CancellationToken ct)
    {
        var target = await ResolveIdAsync(id, output, ct);
        if (!target.IsSuccess) return Error(output, target.Error.Message, target.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        id = target.Value;
        if (id < 0) return await SeedEditAsync(id.Value, output, ct, field);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, id.Value.ToString(CultureInfo.InvariantCulture));
        var observed = await workspace.ReadAsync(key, ct);
        if (!observed.IsSuccess) return Error(output, observed.Error.Message);
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var type = schema.Value.Types.SingleOrDefault(type => type.Reference == observed.Value.Type);
        if (type is null) return Error(output, "The exact native editor type is unavailable.");
        field = field?.ToLowerInvariant() switch
        {
            "title" => "System.Title", "description" => "System.Description", "state" => type.StateField,
            "assignedto" => "System.AssignedTo", _ => field,
        };
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var prior = snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == key);
        var shown = prior is null ? observed.Value.Fields : observed.Value.Fields.Overlay(prior.Changes);
        var names = field is null ? new[] { "System.Title", type.StateField, "System.AssignedTo" }
            : new[] { field };
        var definitions = type.Fields.Where(definition => names.Any(name => definition.Reference.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(definition => !definition.ReadOnly).ToArray();
        if (definitions.Length == 0) return Error(output, "The requested native editor field is unavailable or read-only.", 2);
        var buffer = new System.Text.StringBuilder($"# Editing #{id.Value}. Field values follow their exact native names.\n");
        foreach (var definition in definitions)
            buffer.Append(definition.Reference).Append(": ").AppendLine(shown.TryGetValue(definition.Reference, out var value)
                && value.Kind != FieldKind.Null ? Cell(value).DisplayText : "");
        var edited = await editorLauncher.LaunchAsync(buffer.ToString(), ct);
        if (edited is null)
        {
            renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Text("Edit cancelled.", Severity.Info)]));
            return 0;
        }
        var changes = FieldValues.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in edited.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) return Error(output, "Malformed editor field line; no edit was staged.", 2);
            var name = line[..colon].Trim() switch
            {
                "Title" => "System.Title", "State" => type.StateField, "AssignedTo" => "System.AssignedTo", var exact => exact,
            };
            var definition = type.Fields.SingleOrDefault(definition => definition.Reference.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (definition is null || definition.ReadOnly || !seen.Add(definition.Reference))
                return Error(output, "Unknown, read-only or duplicate editor field; no edit was staged.", 2);
            var text = line[(colon + 1)..].Trim();
            var before = shown.TryGetValue(definition.Reference, out var current) && current.Kind != FieldKind.Null ? Cell(current).DisplayText : "";
            if (text == before) continue;
            if (!TryParse(definition.Kind, text, null, out var value)) return Error(output, "The editor value has an invalid native type.", 2);
            changes = changes.Set(definition.Reference, value);
        }
        if (changes.Count != 0)
        {
            var staged = await workspace.StageEditAsync(key, changes, ct, capturedBaseline: observed.Value, expectedVersion: snapshot.Value.Version);
            if (!staged.IsSuccess) return Error(output, staged.Error.Message);
            var preview = await workspace.PreviewDraftAsync(key, ct);
            if (!preview.IsSuccess) return Error(output, preview.Error.Message);
            var saved = await workspace.SaveDraftAsync(preview.Value, preview.Value.Proposal?.Digest, ct);
            if (!saved.IsSuccess) return Error(output, saved.Error.Message);
            if (saved.Value.Error is { } failure) return Error(output, failure.Message);
            if (!saved.Value.EffectsVerified) return Error(output, "The editor effect remains unresolved; its journal and draft were retained.");
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record(changes.Count == 0 ? "editNoChanges" : "editPushed",
            new Dictionary<string, RenderCell>
            {
                ["itemId"] = RenderCell.Integer(id.Value), ["changeCount"] = RenderCell.Integer(changes.Count),
                ["message"] = RenderCell.String(changes.Count == 0 ? "No changes detected." : "Editor fields applied through one digest-bound proposal and verified journal."),
            })]));
        return 0;
    }

    internal async Task<int> SeedChainAsync(string[]? titles, int? parent, string? type, string output, CancellationToken ct)
    {
        if (parent is null || string.IsNullOrWhiteSpace(type))
            return Error(output, "Core chain authoring requires --parent and --type until active-context selection is connected.", 2);
        var created = new List<Draft>();
        var supplied = titles is { Length: > 0 };
        IEnumerable<string> Source()
        {
            if (supplied)
            {
                foreach (var title in titles!) yield return title;
                yield break;
            }
            while (true)
            {
                if (!consoleInput.IsOutputRedirected) Console.Write("Seed title (empty to finish): ");
                var line = consoleInput.ReadLine();
                if (string.IsNullOrEmpty(line)) yield break;
                yield return line;
            }
        }
        var exit = 0;
        foreach (var title in Source())
        {
            ct.ThrowIfCancellationRequested();
            exit = await SeedNewAsync(title, type, false, parent, false, null, null, output, ct,
                created.Add, created.LastOrDefault()?.Key);
            if (exit != 0) break;
        }
        var rows = created.Select(draft => new RenderRow("seed", new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(LocalAlias(draft.Key)),
            ["localId"] = RenderCell.String(draft.Key.LocalId.ToString("D")),
            ["title"] = Cell(draft.Changes["System.Title"]),
        })).ToArray();
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Document(created.Count == 0 ? "seedChainEmpty" : "seedChainCreated",
            [new("count", new RenderNode.KeyValue("count", RenderCell.Integer(created.Count))),
                new("chain", new RenderNode.KeyValue("chain", RenderCell.String(string.Join(" -> ", created.Select(draft => "#" + LocalAlias(draft.Key)))))),
                new("seeds", new RenderNode.Table(null, [new("id", "ID"), new("title", "Title")], rows))])]));
        return exit;
    }

    internal async Task<int> SeedReconcileAsync(string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var result = await handle.Workspace.ReconcileSeedsAsync(ct);
        if (!result.IsSuccess) return Error(output, result.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedReconcile",
            new Dictionary<string, RenderCell>
            {
                ["seedsSettled"] = RenderCell.Integer(result.Value),
                ["nothingToDo"] = RenderCell.Boolean(result.Value == 0),
                ["message"] = RenderCell.String(result.Value == 0 ? "No verified unchanged seed captures require settlement."
                    : $"Settled {result.Value} unchanged seed capture(s) from original verified receipts; local UUID references remain permanent."),
            })]));
        return 0;
    }

    internal async Task<int> SeedValidateAsync(int? id, string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local seed aliases are ambiguous.");
        var remaining = snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal).ToList();
        if (id is not null && !remaining.Any(draft => LocalAlias(draft.Key) == id))
            return Error(output, "The exact unpublished seed was not found.");
        var rows = new List<RenderRow>();
        var operations = new List<Op>();
        var passed = 0;
        while (remaining.Count != 0)
        {
            var draft = remaining.FirstOrDefault(draft => !remaining.Any(endpoint => endpoint.Key == draft.Parent
                || draft.Links.Any(link => link.Other == endpoint.Key)));
            if (draft is null) return Error(output, "Local creation links contain a cycle; no success was inferred.");
            remaining.Remove(draft);
            var preview = await workspace.PreviewDraftAsync(draft.Key, ct, operations);
            if (preview.IsSuccess)
            {
                var op = preview.Value.Proposal!.Operations.Last();
                operations.Add(new($"validate-{operations.Count}", op.Kind, op.Item, fields: op.Fields,
                    type: op.Type, other: op.Other, relation: op.Relation, resource: op.Resource,
                    concurrency: op.Concurrency, links: op.Links));
            }
            if (id is not null && LocalAlias(draft.Key) != id) continue;
            if (preview.IsSuccess) passed++;
            rows.Add(new("seedValidation", new Dictionary<string, RenderCell>
            {
                ["seedId"] = RenderCell.Integer(LocalAlias(draft.Key)), ["title"] = Cell(draft.Changes["System.Title"]),
                ["passed"] = RenderCell.Boolean(preview.IsSuccess),
                ["failures"] = RenderCell.String(preview.IsSuccess ? "" : preview.Error.Message),
                ["warnings"] = RenderCell.String("Native schema and explicitly declared Twig policy scope; validation is not a publication acknowledgement."),
            }));
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Document("seedValidation",
            [new("results", new RenderNode.Table(null, [new("seedId", "Seed"), new("title", "Title"), new("passed", "Passed"), new("failures", "Failures")], rows)),
                new("passed", new RenderNode.KeyValue("passed", RenderCell.Integer(passed))),
                new("total", new RenderNode.KeyValue("total", RenderCell.Integer(rows.Count)))])]));
        return passed == rows.Count ? 0 : 1;
    }

    internal async Task<int> SeedPublishAsync(int? id, bool all, bool force, bool dryRun,
        string? branch, string? repository, string output, CancellationToken ct)
    {
        if (output != "human") return Error(output, "Structured seed publication is refused because it changes local publication context. Use an explicit reviewed proposal apply.", 2);
        if (id is not null && (id >= 0 || all) || id is null && !all)
            return Error(output, "Specify one negative seed ID or --all.", 2);
        if (repository is not null && branch is null)
            return Error(output, "--repo requires --link-branch.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local integer aliases collide; no seed publication target was guessed.");
        var drafts = snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal
            && (all || LocalAlias(draft.Key) == id)).ToArray();
        if (drafts.Length == 0) return Error(output, "No unpublished seed matches the explicit publication scope.");
        if (branch is not null)
        {
            var resource = await factory.ResolveBranchAsync(branch, repository, ct);
            if (!resource.IsSuccess) return Error(output, resource.Error.Message);
            var captured = await workspace.CaptureSeedResourceAsync(drafts, snapshot.Value.Version, resource.Value, ct);
            if (!captured.IsSuccess) return Error(output, captured.Error.Message);
            snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
            var selected = drafts.Select(draft => draft.Key).ToHashSet();
            drafts = snapshot.Value.Drafts.Where(draft => selected.Contains(draft.Key)).ToArray();
        }
        var keys = drafts.Select(draft => draft.Key).ToHashSet();
        if (snapshot.Value.Relationships.Any(edge => !edge.Deleted && (keys.Contains(edge.From) || keys.Contains(edge.To))))
            return Error(output, "This seed has retained relationships requiring creation-bound publication effects; no link was dropped or seed published.");
        var captures = new List<CoreDraftPreview>();
        var operations = new List<Op>();
        var remaining = drafts.ToList();
        var ordered = new List<Draft>();
        while (remaining.Count != 0)
        {
            var next = remaining.FirstOrDefault(draft => !remaining.Any(other => other.Key == draft.Parent
                || draft.Links.Any(link => link.Other == other.Key)));
            if (next is null) return Error(output, "Local publication parents contain a cycle; no creation was attempted.");
            ordered.Add(next);
            remaining.Remove(next);
        }
        foreach (var draft in ordered)
        {
            var preview = await workspace.PreviewDraftAsync(draft.Key, ct, operations);
            if (!preview.IsSuccess) return Error(output, preview.Error.Message);
            captures.Add(preview.Value);
            var op = preview.Value.Proposal!.Operations.Last();
            operations.Add(new($"create-{operations.Count}", op.Kind, op.Item, fields: op.Fields,
                type: op.Type, other: op.Other, relation: op.Relation, resource: op.Resource, concurrency: op.Concurrency, links: op.Links));
        }
        var prepared = await workspace.PrepareAsync(operations, ct);
        if (!prepared.IsSuccess) return Error(output, prepared.Error.Message);
        var proposal = prepared.Value;
        if (dryRun)
        {
            var retained = await workspace.RetainReviewAsync(proposal, ct);
            if (!retained.IsSuccess) return Error(output, retained.Error.Message);
            renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedPublicationPreview",
                new Dictionary<string, RenderCell>
                {
                    ["digest"] = RenderCell.String(proposal.Digest), ["seedCount"] = RenderCell.Integer(drafts.Length),
                    ["message"] = RenderCell.String("Retained exact unattempted creation intent; no seed was published."),
                })]));
            return 0;
        }
        var published = await workspace.SaveDraftSetAsync(captures, proposal, proposal.Digest, ct);
        if (!published.IsSuccess) return Error(output, published.Error.Message);
        if (published.Value.Error is { } error) return Error(output, error.Message);
        if (!published.Value.EffectsVerified) return Error(output, "Creation is unresolved; its UUID fence, drafts and receipts remain. No replay was performed.");
        var final = await workspace.SnapshotAsync(ct);
        if (!final.IsSuccess) return Error(output, final.Error.Message);
        var rows = final.Value.Mappings.Where(mapping => keys.Contains(mapping.Local)).Select(mapping =>
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["oldId"] = RenderCell.Integer(LocalAlias(mapping.Local)),
                ["newId"] = RenderCell.String(mapping.Remote.NativeId!),
                ["localId"] = RenderCell.String(mapping.Local.LocalId.ToString("D")),
                ["status"] = RenderCell.String("Created"), ["digest"] = RenderCell.String(mapping.ProposalDigest),
            })).ToArray();
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Table("Published seeds",
            [new("oldId", "Seed"), new("newId", "Native ID"), new("status", "Status")], rows)]));
        return 0;
    }

    internal async Task<int> SeedDiscardAsync(int id, bool yes, string output, CancellationToken ct)
    {
        if (id >= 0) return Error(output, "Seed discard requires an explicit negative seed ID.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var matches = KnownLocalKeys(snapshot.Value).Where(key => LocalAlias(key) == id).ToArray();
        if (matches.Length != 1) return Error(output, "The seed alias is absent or ambiguous; nothing was discarded.");
        var draft = snapshot.Value.Drafts.FirstOrDefault(draft => draft.Key == matches[0]);
        if (draft is null) return Error(output, "The explicit seed no longer has an unpublished draft.");
        if (!yes)
        {
            if (output != "human" || consoleInput.IsOutputRedirected)
                return Error(output, "Seed discard requires --yes when interactive confirmation is unavailable.", 2);
            Console.Write($"Discard local seed {id}? [y/N] ");
            if (!string.Equals(consoleInput.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Text("Seed discard cancelled.")]));
                return 0;
            }
        }
        var discarded = await workspace.DiscardSeedAsync(draft, snapshot.Value.Version, ct);
        if (!discarded.IsSuccess) return Error(output, discarded.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedDiscarded",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id), ["localId"] = RenderCell.String(draft.Key.LocalId.ToString("D")),
                ["message"] = RenderCell.String("Discarded the unattempted local draft; journals and identity evidence were preserved."),
            })]));
        return 0;
    }

    private static IEnumerable<ItemKey> KnownLocalKeys(StoreSnapshot snapshot)
        => snapshot.Drafts.Select(draft => draft.Key).Concat(snapshot.Mappings.Select(mapping => mapping.Local))
            .Concat(snapshot.Drafts.SelectMany(draft => draft.Links.Select(link => link.Other)
                .Concat(draft.Parent is null ? [] : new[] { draft.Parent })))
            .Concat(snapshot.Proposals.SelectMany(proposal => proposal.Operations)
                .SelectMany(op => op.Links.Select(link => link.Other).Prepend(op.Item)
                    .Concat(op.Other is null ? [] : new[] { op.Other })))
            .Concat(snapshot.Relationships.SelectMany(edge => new[] { edge.From, edge.To }))
            .Concat(snapshot.Benches.SelectMany(bench => bench.Selectors).Where(selector => selector.Item is not null)
                .Select(selector => selector.Item!))
            .Where(key => key.IsLocal).Distinct();

    internal async Task<int> SeedLinkAsync(int sourceId, int targetId, string? type, bool remove, string output, CancellationToken ct)
    {
        if (sourceId == 0 || targetId == 0 || sourceId == targetId || sourceId > 0 && targetId > 0)
            return Error(output, "Two distinct nonzero targets, including a local seed, are required.", 2);
        var shape = (type ?? "related").ToLowerInvariant() switch
        {
            "related" => RelationShape.Association,
            "parent-child" => RelationShape.Hierarchy,
            "blocks" or "blocked-by" or "depends-on" or "depended-on-by" or "successor" or "predecessor" => RelationShape.Dependency,
            _ => (RelationShape?)null,
        };
        if (shape is null) return Error(output, "Unknown seed link type.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        ItemKey? Key(int id)
        {
            if (id > 0) return ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, id.ToString(CultureInfo.InvariantCulture));
            var matches = KnownLocalKeys(snapshot.Value).Where(key => LocalAlias(key) == id).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        var source = Key(sourceId);
        var target = Key(targetId);
        if (source is null || target is null) return Error(output, "A local target alias is absent or ambiguous.");
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var kinds = schema.Value.Relations.Where(kind => kind.Shape == shape
            && (shape != RelationShape.Association || !kind.Directed)).ToArray();
        if (kinds.Length != 1) return Error(output, "The actual native schema has no unique relation for this seed link shape.");
        var kind = kinds[0];
        if (shape == RelationShape.Hierarchy && !source.IsLocal)
            return Error(output, "Parenting an existing native child to an unpublished parent requires a later reviewed native effect; no link was dropped.");
        var holder = shape == RelationShape.Hierarchy || !target.IsLocal ? source : target;
        var endpoint = holder == source ? target : source;
        var draft = snapshot.Value.Drafts.SingleOrDefault(value => value.Key == holder);
        if (draft is null) return Error(output, "The creation-bound link owner is not an unpublished local seed.");
        var mapped = snapshot.Value.Mappings.FirstOrDefault(value => value.Local == endpoint)?.Remote ?? endpoint;
        if (!mapped.IsLocal)
        {
            var observed = await workspace.ReadAsync(mapped, ct);
            if (!observed.IsSuccess) return Error(output, observed.Error.Message);
        }
        else if (!snapshot.Value.Drafts.Any(value => value.Key == endpoint))
            return Error(output, "The exact local link endpoint is missing.");
        var forward = type?.ToLowerInvariant() is not ("blocked-by" or "depends-on" or "predecessor");
        var link = new CreationLink(endpoint, kind.Reference, !kind.Directed || (holder == source ? forward : !forward));
        var parent = draft.Parent;
        var parentRelation = draft.ParentRelation;
        var links = draft.Links;
        if (shape == RelationShape.Hierarchy)
        {
            if (remove && (parent != endpoint || parentRelation != kind.Reference))
                return Error(output, "The exact captured parent link is absent.");
            if (!remove && parent == endpoint && parentRelation == kind.Reference)
                return Error(output, "The parent link already exists.");
            parent = remove ? null : endpoint;
            parentRelation = remove ? null : kind.Reference;
        }
        else
        {
            if (remove && !links.Contains(link)) return Error(output, "The exact captured creation link is absent.");
            if (!remove && links.Contains(link)) return Error(output, "The creation link already exists.");
            links = remove ? links.Remove(link) : links.Add(link);
        }
        var fresh = await workspace.SnapshotAsync(ct);
        if (!fresh.IsSuccess) return Error(output, fresh.Error.Message);
        var captured = await workspace.CaptureSeedLinksAsync(draft, fresh.Value.Version, parent, parentRelation, links, ct);
        if (!captured.IsSuccess) return Error(output, captured.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record(remove ? "seedUnlinked" : "seedLinked",
            new Dictionary<string, RenderCell>
            {
                ["sourceId"] = RenderCell.Integer(sourceId), ["targetId"] = RenderCell.Integer(targetId),
                ["linkType"] = RenderCell.String(type ?? "related"),
                ["message"] = RenderCell.String(remove ? "Removed exact local creation-link intent; no native effect." : "Captured creation-link intent; publication requires its exact reviewed proposal."),
            })]));
        return 0;
    }

    internal async Task<int> SeedLinksAsync(int? id, string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var snapshot = await handle.Workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local identity aliases are ambiguous; no link identity was guessed.");
        int Id(ItemKey key) => key.IsLocal ? LocalAlias(key) : int.Parse(key.NativeId!, CultureInfo.InvariantCulture);
        var rows = new List<RenderRow>();
        foreach (var draft in snapshot.Value.Drafts.Where(draft => draft.Key.IsLocal))
        {
            var links = draft.Links.Select(link => (From: link.Outgoing ? draft.Key : link.Other,
                To: link.Outgoing ? link.Other : draft.Key, Kind: link.Relation))
                .Concat(draft.Parent is null ? [] : new[] { (From: draft.Key, To: draft.Parent, Kind: draft.ParentRelation!) });
            foreach (var link in links.Where(link => id is null || Id(link.From) == id || Id(link.To) == id))
                rows.Add(new("seedLink", new Dictionary<string, RenderCell>
                {
                    ["sourceId"] = RenderCell.Integer(Id(link.From)), ["targetId"] = RenderCell.Integer(Id(link.To)),
                    ["linkType"] = RenderCell.String(link.Kind),
                    ["message"] = RenderCell.String("Captured local creation intent; not a native relationship acknowledgement."),
                }));
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Document("seedLinks",
            [new("links", new RenderNode.Table(null, [new("sourceId", "Source"), new("targetId", "Target"), new("linkType", "Type")], rows)),
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(rows.Count)))])]));
        return 0;
    }

    private static int LocalAlias(ItemKey key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(key.LocalId.ToByteArray());
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) & int.MaxValue;
        return value == 0 ? int.MinValue : -value;
    }

    internal async Task<int> ShowBatchAsync(string batch, string output, string? fields, string? sections,
        bool includeFields, CancellationToken ct)
    {
        if (includeFields && (output != "json" || fields is not null || sections is not null))
            return Error(output, "--include-fields requires -o json without field/section projection.", 2);
        if (sections is not null)
            return Error(output, "Section projection requires the retained hierarchy presentation integration; no partial section was reported as complete.");
        var segments = batch.Split(',', StringSplitOptions.TrimEntries);
        var ids = new List<int>();
        foreach (var segment in segments)
        {
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return Error(output, "Batch detail requires a nonempty comma-separated list of positive native IDs.", 2);
            if (ids.Contains(id))
            {
                if (includeFields) return Error(output, "--include-fields requires distinct native IDs.", 2);
                continue;
            }
            ids.Add(id);
        }
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var selected = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rows = new List<RenderRow>();
        var missing = new List<int>();
        foreach (var id in ids)
        {
            var key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope,
                id.ToString(CultureInfo.InvariantCulture));
            var item = snapshot.Value.Items.FirstOrDefault(item => item.Key == key);
            if (item is null) { missing.Add(id); continue; }
            rows.Add(new(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id),
                ["type"] = Cell(item.Fields.TryGetValue("System.WorkItemType", out var type) ? type : FieldValue.String(item.Type)),
                ["title"] = Cell(item.Fields.TryGetValue("System.Title", out var title) ? title : FieldValue.Null),
                ["state"] = Cell(item.Fields.TryGetValue("System.State", out var state) ? state : FieldValue.Null),
                ["revision"] = item.Revision is null ? new("", new RenderValue.Null()) : RenderCell.String(item.Revision.Token),
                ["cached"] = RenderCell.Boolean(true),
                ["isDirty"] = RenderCell.Boolean(snapshot.Value.Drafts.Any(draft => draft.Key == key)),
                ["fields"] = new("Fields", new RenderValue.Object(item.Fields.Where(field => selected is null || selected.Contains(field.Key))
                    .ToDictionary(field => field.Key, field => Cell(field.Value), StringComparer.Ordinal))),
            }));
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Document("batchDetail", [
            new("count", new RenderNode.KeyValue("count", RenderCell.Integer(rows.Count))),
            new("missingIds", new RenderNode.KeyValue("missingIds", new("", new RenderValue.Array(missing.Select(id => RenderCell.Integer(id)).ToArray())))),
            new("items", new RenderNode.Table(null,
                [new("id", "ID"), new("type", "Type"), new("state", "State"), new("title", "Title")], rows)),
        ])]));
        return missing.Count == 0 ? 0 : Error(output, $"Uncached target IDs: {string.Join(", ", missing)}. Use explicit show --refresh for each missing item.");
    }

    internal async Task<int> ShowAsync(int? id, string output, bool tree, bool refresh,
        string? fields, string? sections, string? localId, CancellationToken ct)
    {
        if (id is null && localId is null)
        {
            var activeTarget = await ResolveIdAsync(null, output, ct);
            if (!activeTarget.IsSuccess) return Error(output, activeTarget.Error.Message, activeTarget.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
            id = activeTarget.Value;
        }
        if (id is null && localId is null || id == 0)
            return Error(output, "No Core active target is selected. Pass an explicit work item ID or --local-id.", 2);
        if (tree || sections is not null)
            return Error(output, "Hierarchy/section detail routing is not yet connected in this in-progress Core cutover.", 1);
        if (localId is not null && (id is not null || refresh || fields is not null || output is not ("json" or "json-full" or "json-compact")))
            return Error(output, "--local-id requires JSON snapshot output without another target, refresh or projection.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var original = await workspace.SnapshotAsync(ct);
        if (!original.IsSuccess) return Error(output, original.Error.Message);
        ItemKey key;
        if (localId is not null)
        {
            if (!Guid.TryParseExact(localId, "D", out var uuid) || uuid.Version != 7 || uuid.ToString("D") != localId)
                return Error(output, "A canonical lowercase UUIDv7 local identity is required.", 2);
            key = ItemKey.Local(uuid);
        }
        else if (id < 0)
        {
            var matches = KnownLocalKeys(original.Value).Where(local => LocalAlias(local) == id).ToArray();
            if (matches.Length != 1) return Error(output, "The local alias is absent or ambiguous; no target was guessed.");
            key = matches[0];
        }
        else key = ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope,
            id!.Value.ToString(CultureInfo.InvariantCulture));
        var localKey = key.IsLocal ? key : original.Value.Mappings.FirstOrDefault(mapping => mapping.Remote == key)?.Local;
        key = original.Value.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
        WorkItem item;
        if (key.IsLocal)
        {
            var draft = original.Value.Drafts.FirstOrDefault(draft => draft.Key == key);
            if (draft is null) return Error(output, "No retained local draft matches this identity.");
            if (refresh) return Error(output, "An unpublished seed has no native observation to refresh.", 2);
            item = new(key, draft.Type, draft.Changes, null);
        }
        else if (refresh)
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
            if (found is null) return Error(output, $"Work item #{key.NativeId} is not cached. Use show {key.NativeId} --refresh.");
            item = found;
        }
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var selected = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = key.IsLocal ? RenderCell.Integer(LocalAlias(key))
                : RenderCell.Integer(long.Parse(key.NativeId!, CultureInfo.InvariantCulture)),
            ["localId"] = localKey is null ? new("", new RenderValue.Null()) : RenderCell.String(localKey.LocalId.ToString("D")),
            ["isSeed"] = RenderCell.Boolean(key.IsLocal),
            ["type"] = Cell(item.Fields.TryGetValue("System.WorkItemType", out var nativeType)
                ? nativeType : FieldValue.String(item.Type)),
            ["title"] = Cell(item.Fields.TryGetValue("System.Title", out var title) ? title : FieldValue.Null),
            ["state"] = Cell(item.Fields.TryGetValue("System.State", out var state) ? state : FieldValue.Null),
            ["revision"] = item.Revision is null ? new("", new RenderValue.Null()) : RenderCell.String(item.Revision.Token),
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
        var target = await ResolveIdAsync(id, output, ct);
        if (!target.IsSuccess) return Error(output, target.Error.Message, target.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        id = target.Value;
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
        Draft? seed = null;
        StoreSnapshot? captured = null;
        if (id < 0)
        {
            var snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
            var matches = KnownLocalKeys(snapshot.Value).Where(local => LocalAlias(local) == id).ToArray();
            if (matches.Length != 1) return Error(output, "The local target alias is absent or ambiguous.");
            var mapping = snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == matches[0]);
            seed = snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == matches[0]);
            if (mapping is not null) key = mapping.Remote;
            else if (seed is null) return Error(output, "The local target has no retained draft or native mapping.");
            if (mapping is not null) seed = null;
            captured = snapshot.Value;
        }
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var observed = seed is null ? await workspace.ReadAsync(key, ct) : Result<WorkItem>.Ok(new(seed.Key, seed.Type, seed.Changes, null));
        if (!observed.IsSuccess) return Error(output, observed.Error.Message);
        var name = field.ToLowerInvariant() switch
        {
            "title" => "System.Title", "state" => "System.State", "description" => "System.Description", _ => field,
        };
        var definitions = schema.Value.Types.Where(type => type.Reference == observed.Value.Type).SelectMany(t => t.Fields)
            .Where(f => f.Reference.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (definitions.Length != 1) return Error(output, "The field is absent or ambiguous in the actual process schema.", 2);
        var definition = definitions[0];
        if (definition.ReadOnly) return Error(output, "The actual native field is read-only.", 2);
        name = definition.Reference;
        var text = body.Value!;
        if (append)
        {
            if (!observed.Value.Fields.TryGetValue(name, out var current)
                || current.Kind is not (FieldKind.Text or FieldKind.Html or FieldKind.Markdown))
                return Error(output, "Append requires an actually observed text field.", 2);
            text = current.Text + text;
        }
        if (!TryParse(definition.Kind, text, format, out var parsed))
            return Error(output, "This typed field value is invalid or its CLI conversion is not yet connected.", 2);
        if (seed is not null)
        {
            var changed = await workspace.CaptureSeedFieldsAsync(seed, captured!.Version, new([KeyValuePair.Create(name, parsed)]), ct);
            if (!changed.IsSuccess) return Error(output, changed.Error.Message);
            renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("fieldUpdated",
                new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.Integer(id.Value), ["field"] = RenderCell.String(name),
                    ["valueDisplay"] = RenderCell.String(text), ["wasSeed"] = RenderCell.Boolean(true),
                    ["append"] = RenderCell.Boolean(append), ["journalStatus"] = RenderCell.String("Draft"),
                    ["message"] = RenderCell.String("Retained typed local seed edit; no native mutation was attempted."),
                })]));
            return 0;
        }
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
            && (proposal.Operations[0].Kind != OpKind.Create || d.Parent == proposal.Operations[0].Other
                && d.ParentRelation == proposal.Operations[0].Relation && d.Resource == proposal.Operations[0].Resource
                && d.Links.SequenceEqual(proposal.Operations[0].Links))
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
        var selected = await ResolveIdAsync(id, output, ct);
        if (!selected.IsSuccess) return Error(output, selected.Error.Message, selected.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        id = selected.Value;
        if (id <= 0) return Error(output, "A note requires a published Core target; local note publication is not yet connected.");
        if (text is null && file is null && !stdin && output == "human")
        {
            text = await editorLauncher.LaunchAsync("", ct);
            if (text is null)
            {
                renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Text("Note editor cancelled.", Severity.Info)]));
                return 0;
            }
        }
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
        var selectedId = await ResolveIdAsync(id, output, ct);
        if (!selectedId.IsSuccess) return Error(output, selectedId.Error.Message, selectedId.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        id = selectedId.Value;
        if (id <= 0) return await UpdateAsync("System.State", requested, output, null, null, false, id, false, ct);
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
