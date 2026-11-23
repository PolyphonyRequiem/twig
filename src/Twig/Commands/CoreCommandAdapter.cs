using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Services;
using Twig.Providers.Ado;
using Twig.Providers.GitHub;
using Twig.RenderTree;
using Twig.Rendering;
using static Twig.Infrastructure.Services.CoreHostContext;

namespace Twig.Commands;

internal sealed class CoreCommandAdapter(CoreWorkspaceFactory factory, RendererFactory renderer,
    Twig.Commands.IConsoleInput consoleInput, Twig.Commands.IEditorLauncher editorLauncher,
    Twig.Infrastructure.Telemetry.ITelemetryClient? telemetry = null, NativeHistoricalEvidence? historicalEvidence = null)
{
    private string TitleField => factory.IsTwig ? Twig.Providers.Native.NativeProvider.Title : factory.IsGitHub ? GitHubProvider.Title : "System.Title";
    private string BodyField => factory.IsTwig ? Twig.Providers.Native.NativeProvider.Description : factory.IsGitHub ? GitHubProvider.Body : "System.Description";
    private string AssigneeField => factory.IsGitHub ? GitHubProvider.Assignees : "System.AssignedTo";
    private string StateField => factory.IsTwig ? Twig.Providers.Native.NativeProvider.StateField : factory.IsGitHub ? GitHubProvider.StateField : "System.State";
    private NativeHistoricalEvidence History => historicalEvidence ?? NativeHistoricalEvidence.ForLocalUser();

    private async Task<Result<int>> ResolveIdAsync(int? id, CancellationToken ct, bool allowNativeLookup = false)
    {
        if (id is null or 0) return Result<int>.Fail(ErrorCode.InvalidInput, "Pass an explicit nonzero work item ID.");
        if (id > 0) return Result<int>.Ok(id.Value);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Result<int>.Fail(opened.Error);
        using var handle = opened.Value;
        var key = await handle.Workspace.ResolveTargetAsync(id.Value, allowNativeLookup, ct);
        if (!key.IsSuccess) return Result<int>.Fail(key.Error);
        var snapshot = await handle.Workspace.SnapshotAsync(ct);
        return snapshot.IsSuccess ? CoreWorkspace.DisplayId(handle.Workspace.Connection, snapshot.Value, key.Value)
            : Result<int>.Fail(snapshot.Error);
    }

    internal async Task<int> TreeAsync(int? id, bool all, int? depth, bool refresh, string output, CancellationToken ct)
    {
        using var observation = refresh ? Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true) : null;
        if (factory.IsGitHub) return Error(output, "GitHub tree presentation is not qualified; use show or query.");
        if (depth is < 0) return Error(output, "Tree depth cannot be negative.", 2);
        if (all && id is not null) return Error(output, "--all cannot be combined with an explicit tree target.", 2);
        if (!all)
        {
            var selected = await ResolveIdAsync(id, ct);
            if (!selected.IsSuccess) return Error(output, selected.Error.Message, selected.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
            id = selected.Value;
        }
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        ItemKey? requested = all ? null : id < 0
            ? KnownLocalKeys(snapshot.Value).SingleOrDefault(key => LocalAlias(key) == id)
            : ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope, id!.Value.ToString(CultureInfo.InvariantCulture));
        if (!all && requested is null) return Error(output, "The local tree target is absent or ambiguous.");
        ItemKey Resolve(ItemKey key) => snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
        if (requested is not null) requested = Resolve(requested);
        if (refresh && all) return Error(output, "Refresh-all requires the retained Bench/query scope; that route is not yet connected.");
        if (refresh && requested is { IsLocal: false })
        {
            var observed = await workspace.ReadAsync(requested, ct);
            if (!observed.IsSuccess) return Error(output, observed.Error.Message);
            var restored = History.RestoreRetainedItem(handle.Source, observed.Value);
            if (!restored.IsSuccess) return Error(output, restored.Error.Message);
            snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        }
        var evidence = History.Read(handle.Source);
        if (!evidence.IsSuccess) return Error(output, evidence.Error.Message);
        var history = new NativeHistoricalProjection(snapshot.Value, evidence.Value);
        if (requested is not null && history.Withholds(requested))
            return Error(output, "Historical output is withheld for this item.");
        snapshot = Result<StoreSnapshot>.Ok(history.Snapshot());
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var releaseEvidence = History.Read(handle.Source);
        if (!releaseEvidence.IsSuccess) return Error(output, releaseEvidence.Error.Message);
        if (!releaseEvidence.Value.IsSubsetOf(evidence.Value))
            return Error(output, "Historical evidence changed while loading the tree; read it again.");
        var kinds = schema.Value.Relations.Where(relation => relation.Shape == RelationShape.Hierarchy && relation.Directed).ToArray();
        if (kinds.Length != 1) return Error(output, "Tree display requires a unique discovered hierarchy relation.");
        var hierarchy = kinds[0].Reference;
        var items = snapshot.Value.Items.ToDictionary(item => item.Key);
        foreach (var draft in snapshot.Value.RetainedItems)
        {
            if (draft.Key.IsLocal)
            {
                if (!snapshot.Value.Mappings.Any(mapping => mapping.Local == draft.Key))
                    items[draft.Key] = new(draft.Key, draft.Type, draft.Fields, null);
            }
            else if (items.TryGetValue(draft.Key, out var item))
                items[draft.Key] = new(item.Key, draft.Type, item.Fields.Overlay(draft.Fields), item.Revision,
                    item.RelationshipsComplete, item.Resources, item.ResourcesComplete);
        }
        if (requested is not null && !items.ContainsKey(requested))
            return Error(output, "The explicit tree target is not cached. Use --refresh to observe it.");
        var edges = snapshot.Value.Relationships.Where(edge => !edge.Deleted && edge.Kind == hierarchy)
            .Select(edge => (From: Resolve(edge.From), To: Resolve(edge.To), edge.Origin))
            .Concat(snapshot.Value.RetainedItems.OfType<Seed>().Where(draft => draft.Parent is not null && draft.ParentRelation == hierarchy)
                .Select(draft => (From: Resolve(draft.Parent!), To: Resolve(draft.Key), Origin: RelationshipOrigin.Local))).Distinct().ToArray();
        var roots = requested is null ? items.Keys.Where(key => !edges.Any(edge => edge.To == key && items.ContainsKey(edge.From))).ToArray() : [requested];
        if (roots.Length == 0 && items.Count != 0) return Error(output, "The cached hierarchy has no root; a cycle prevents a trustworthy tree.");
        var ancestors = new List<ItemKey>();
        if (requested is not null)
        {
            var current = requested;
            var seen = new HashSet<ItemKey> { current };
            while (true)
            {
                var parents = edges.Where(edge => edge.To == current).Select(edge => edge.From).Distinct().ToArray();
                if (parents.Length > 1) return Error(output, "The requested item's ancestor chain is ambiguous.");
                if (parents.Length == 0) break;
                current = parents[0];
                if (!seen.Add(current)) return Error(output, "A cycle prevents the requested ancestor chain.");
                ancestors.Add(current);
                if (!items.ContainsKey(current)) break;
            }
            if (ancestors.Count != 0) roots = [ancestors[^1]];
        }
        var path = requested is null ? [] : ancestors.AsEnumerable().Reverse().Append(requested).ToArray();
        var visited = new HashSet<ItemKey>();
        Error? error = null;
        RenderTreeBranch Build(ItemKey key, int level)
        {
            if (!visited.Add(key))
            {
                error = new(ErrorCode.Conflict, "A repeated descendant or hierarchy cycle prevents a trustworthy tree.");
                return new(new("unavailable", new Dictionary<string, RenderCell> { ["message"] = RenderCell.String(error.Message) }), []);
            }
            items.TryGetValue(key, out var item);
            var cells = new Dictionary<string, RenderCell>
            {
                ["id"] = key.IsLocal ? RenderCell.Integer(LocalAlias(key)) : RenderCell.Integer(int.Parse(key.NativeId!, CultureInfo.InvariantCulture)),
                ["title"] = item is null ? RenderCell.String("(not cached)") : Cell(item.Fields.GetValueOrDefault(TitleField) ?? FieldValue.Null),
                ["type"] = item is null ? new("", new RenderValue.Null()) : RenderCell.String(item.Type),
                ["cached"] = RenderCell.Boolean(item is not null), ["isSeed"] = RenderCell.Boolean(key.IsLocal),
                ["relationshipsComplete"] = RenderCell.Boolean(!key.IsLocal && item?.RelationshipsComplete == true),
            };
            var incoming = edges.Where(edge => edge.To == key).Select(edge => edge.Origin).Distinct().ToArray();
            cells["relationshipOrigin"] = incoming.Length == 1 ? RenderCell.String(incoming[0].ToString().ToLowerInvariant())
                : new("", new RenderValue.Null());
            if (item is not null && schema.Value.Types.SingleOrDefault(type => type.Reference == item.Type) is { } actualType)
                cells["state"] = Cell(item.Fields.GetValueOrDefault(actualType.StateField) ?? FieldValue.Null);
            var pathIndex = Array.IndexOf(path, key);
            var children = pathIndex >= 0 && pathIndex < path.Length - 1 ? new[] { Build(path[pathIndex + 1], level + 1) }
                : level - ancestors.Count >= (depth ?? int.MaxValue) ? [] : edges.Where(edge => edge.From == key)
                    .Select(edge => edge.To).Distinct().Select(child => Build(child, level + 1)).ToArray();
            return new(new(item is null ? "missingItem" : "workItem", cells), children);
        }
        var branches = roots.Select(key => Build(key, 0)).ToArray();
        if (error is not null) return Error(output, error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree(branches.Select(branch => (RenderNode)new RenderNode.TreeView(branch)).ToArray()));
        return 0;
    }

    internal async Task<int> QueryAsync(string? search, string? title, string? description, string? requestedType,
        string? state, string? assignedTo, string? area, string? iteration, string? createdSince, string? changedSince,
        int top, string output, CancellationToken ct)
    {
        if (top < 1) return Error(output, "Query --top must be positive.", 2);
        if ((factory.IsGitHub || factory.IsTwig) && new[] { requestedType, assignedTo, area, iteration, createdSince, changedSince }.Any(value => value is not null))
            return Error(output, "This provider has no qualified tracker type, assignee, path or date query spellings; use title, description or state.", 2);
        if (new[] { search, title, description, requestedType, state, assignedTo, area, iteration, createdSince, changedSince }
            .All(string.IsNullOrWhiteSpace))
        {
            renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("querySummary",
                new Dictionary<string, RenderCell>
                {
                    ["message"] = RenderCell.String("Supply a search term or explicit title, description, type, state, assignee, path or time filter. No native query was sent."),
                })]));
            return 0;
        }
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var terms = new List<Query>();
        Error? failure = null;
        Query? Operand(QueryKind kind, string name, string text)
        {
            var fields = schema.Value.Types.SelectMany(type => type.Fields).Where(field => field.Reference == name).Distinct().ToArray();
            if (fields.Length != 1 || !TryParse(fields[0].Kind, text, null, out var value))
            {
                failure = new(ErrorCode.Unsupported, $"The actual native schema has no unique typed query field '{name}'.");
                return null;
            }
            if (kind is QueryKind.Contains or QueryKind.Under && value.Kind is not (FieldKind.Text or FieldKind.Html or FieldKind.Markdown)
                || kind == QueryKind.GreaterOrEqual && value.Kind is not (FieldKind.Integer or FieldKind.Decimal or FieldKind.Instant))
            {
                failure = new(ErrorCode.Unsupported, "The native field type does not support the requested typed query operator.");
                return null;
            }
            return new(kind, name, value);
        }
        void Add(QueryKind kind, string name, string? text)
        {
            if (text is not null && Operand(kind, name, text) is { } operand) terms.Add(operand);
        }
        Add(QueryKind.Contains, TitleField, title);
        Add(QueryKind.Contains, BodyField, description);
        Add(QueryKind.Equal, StateField, state);
        Add(QueryKind.Equal, AssigneeField, assignedTo);
        Add(QueryKind.Under, "System.AreaPath", area);
        Add(QueryKind.Under, "System.IterationPath", iteration);
        if (!factory.IsGitHub && !factory.IsTwig && area is null && factory.QueryAreaDefaults.Count != 0)
        {
            var scopes = factory.QueryAreaDefaults.Select(scope => Operand(scope.IncludeChildren ? QueryKind.Under : QueryKind.Equal,
                "System.AreaPath", scope.Path)).Where(scope => scope is not null).Cast<Query>().ToArray();
            if (scopes.Length != 0) terms.Add(scopes.Length == 1 ? scopes[0] : new(QueryKind.Or, children: scopes));
        }
        if (requestedType is not null)
        {
            var types = schema.Value.Types.Where(type => type.Reference.Equals(requestedType, StringComparison.OrdinalIgnoreCase)
                || type.Rules.SelectMany(rule => rule.Values).Any(value => value.DataType == "ado.native-type-name"
                    && value.Text!.Equals(requestedType, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (types.Length != 1) return Error(output, "The requested native query type is absent or ambiguous.", 2);
            var native = types[0].Rules.SelectMany(rule => rule.Values).SingleOrDefault(value => value.DataType == "ado.native-type-name")?.Text;
            Add(QueryKind.Equal, "System.WorkItemType", native ?? types[0].Reference);
        }
        if (search is not null)
        {
            var titleTerm = Operand(QueryKind.Contains, TitleField, search);
            var descriptionTerm = schema.Value.Types.Any(type => type.Fields.Any(field => field.Reference == BodyField))
                ? Operand(QueryKind.Contains, BodyField, search) : null;
            if (titleTerm is not null) terms.Add(descriptionTerm is null ? titleTerm : new(QueryKind.Or, children: [titleTerm, descriptionTerm]));
        }
        foreach (var time in new[] { (Field: "System.CreatedDate", Text: createdSince), (Field: "System.ChangedDate", Text: changedSince) })
        {
            if (time.Text is null) continue;
            var raw = time.Text.Trim();
            if (DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absolute))
            {
                Add(QueryKind.GreaterOrEqual, time.Field, absolute.ToString("O", CultureInfo.InvariantCulture));
                continue;
            }
            raw = raw.ToLowerInvariant();
            var multiplier = raw.EndsWith('w') ? 7 : 1;
            var digits = raw.EndsWith('d') || raw.EndsWith('w') ? raw[..^1] : raw;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 0 || days > 36500 / multiplier)
                return Error(output, "Time filters require a nonnegative day count or d/w duration.", 2);
            Add(QueryKind.GreaterOrEqual, time.Field, DateTimeOffset.UtcNow.AddDays(-days * multiplier).ToString("O", CultureInfo.InvariantCulture));
        }
        if (failure is not null) return Error(output, failure.Message);
        if (terms.Count == 0) return Error(output, "No native query operand could be constructed.", 2);
        var query = terms.Count == 1 ? terms[0] : new Query(QueryKind.And, children: terms);
        var result = await workspace.QueryAsync(query, ct, top);
        if (!result.IsSuccess) return Error(output, result.Error.Message);
        var items = result.Value.Items.Take(top).ToArray();
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var display = items.Select(item => CoreWorkspace.DisplayId(workspace.Connection, snapshot.Value, item.Key)).ToArray();
        if (display.Any(value => !value.IsSuccess)) return Error(output, "Query result has no unique observed numeric identity.");
        var rows = items.Select(item => new RenderRow("workItem", new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(CoreWorkspace.DisplayId(workspace.Connection, snapshot.Value, item.Key).Value),
            ["title"] = Cell(item.Fields.GetValueOrDefault(TitleField) ?? FieldValue.Null),
            ["type"] = RenderCell.String(item.Type),
            ["state"] = Cell(item.Fields.GetValueOrDefault(schema.Value.Types.Single(type => type.Reference == item.Type).StateField) ?? FieldValue.Null),
        })).ToArray();
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Document("queryResults",
            [new("count", new RenderNode.KeyValue("count", RenderCell.Integer(items.Length))),
                new("truncated", new RenderNode.KeyValue("truncated", RenderCell.Boolean(result.Value.Items.Length > top || !result.Value.Complete))),
                new("query", new RenderNode.KeyValue("query", RenderCell.String("Exact typed native API query; explicitly declared Twig scope."))),
                new("items", new RenderNode.Table(null, [new("id", "ID"), new("title", "Title"), new("state", "State"), new("type", "Type")], rows))])]));
        return 0;
    }

    internal async Task<int> SaveAsync(int? id, bool all, string output, CancellationToken ct)
    {
        if (id < 0 && !all) return await SeedPublishAsync(id, false, false, false, null, null, output, ct);
        if (id is not null && (id <= 0 || all)) return Error(output, "Specify one positive --id or --all, not both.", 2);
        if (id is null && !all) return Error(output, "Specify --id or --all.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var saved = await SaveRemoteDraftsAsync(workspace, id, all, ct);
        return saved.IsSuccess ? SaveResult(output, saved.Value.Count, saved.Value.Digest) : Error(output, saved.Error.Message);
    }

    private static async Task<Result<(int Count, string? Digest)>> SaveRemoteDraftsAsync(
        CoreWorkspace workspace, int? id, bool all, CancellationToken ct)
    {
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Result<(int, string?)>.Fail(snapshot.Error);
        var target = all ? null : await workspace.ResolveTargetAsync(id!.Value, true, ct);
        if (target is { IsSuccess: false }) return Result<(int, string?)>.Fail(target.Error);
        var drafts = snapshot.Value.RetainedItems.Where(draft => !draft.Key.IsLocal
            && (all || draft.Key == target!.Value)).ToArray();
        if (drafts.Length == 0) return Result<(int, string?)>.Ok((0, null));
        var captures = new List<CoreDraftPreview>();
        var operations = new List<Op>();
        foreach (var draft in drafts)
        {
            var preview = await workspace.PreviewDraftAsync(draft.Key, ct);
            if (!preview.IsSuccess) return Result<(int, string?)>.Fail(preview.Error);
            captures.Add(preview.Value);
            if (preview.Value.Plan is { } intent)
            {
                var op = intent.Operations.Single();
                operations.Add(new($"save-{operations.Count}", op.Kind, op.Item, op.ExpectedRevision,
                    op.Fields, op.Type, op.Other, op.Relation, op.Origin, op.Body,
                    op.Concurrency, op.ExpectedLocalVersion, op.Resource, op.Links, op.CreateEvidence));
            }
        }
        Plan? plan = null;
        if (operations.Count != 0)
        {
            var prepared = await workspace.PrepareAsync(operations, ct);
            if (!prepared.IsSuccess) return Result<(int, string?)>.Fail(prepared.Error);
            plan = prepared.Value;
        }
        var saved = await workspace.SaveDraftSetAsync(captures, plan, ct);
        if (!saved.IsSuccess) return Result<(int, string?)>.Fail(saved.Error);
        if (saved.Value.Error is { } error) return Result<(int, string?)>.Fail(error);
        if (!saved.Value.EffectsVerified)
            return Result<(int, string?)>.Fail(ErrorCode.Indeterminate, "Save has unresolved journal effects; local edits were retained without replay.");
        return Result<(int, string?)>.Ok((drafts.Length, plan?.Digest));
    }

    private int SaveResult(string output, int count, string? digest)
    {
        var message = count == 0 ? "Nothing to save." : $"Saved local edits for {count} item(s); all effects verified.";
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([output == "human"
            ? new RenderNode.Text(message, Severity.Success)
            : new RenderNode.Record("draftsSaved", new Dictionary<string, RenderCell>
            {
                ["message"] = RenderCell.String(count == 0 ? "Nothing to save." : $"Saved {count} draft(s); all effects verified."),
                ["itemsFlushed"] = RenderCell.Integer(count),
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
        var target = all ? null : await workspace.ResolveTargetAsync(id!.Value, false, ct);
        if (target is { IsSuccess: false }) return Error(output, target.Error.Message);
        var drafts = snapshot.Value.RetainedItems.Where(draft => !draft.Key.IsLocal
            && (all || draft.Key == target!.Value)).ToArray();
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
        var seeds = snapshot.Value.RetainedItems.OfType<Seed>().ToArray();
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local integer aliases collide; use canonical UUID detail until the aliases are explicitly reconciled.");
        var rows = seeds.Select(draft => new RenderRow(null, new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(LocalAlias(draft.Key)),
            ["localId"] = RenderCell.String(draft.Key.LocalId.ToString("D")),
            ["type"] = RenderCell.String(draft.Type),
            ["title"] = Cell(draft.Fields.TryGetValue(TitleField, out var title) ? title : FieldValue.Null),
            ["isSeed"] = RenderCell.Boolean(true),
            ["fields"] = new("Fields", new RenderValue.Object(draft.Fields.ToDictionary(field => field.Key, field => Cell(field.Value), StringComparer.Ordinal))),
        })).ToArray();
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Table("Local seeds",
            [new("id", "ID"), new("type", "Type"), new("title", "Title")], rows)]));
        return 0;
    }

    internal async Task<int> SeedNewAsync(string? title, string? requestedType, bool editor, int? parent,
        bool noParent, string? description, string[]? fields, string output, CancellationToken ct,
        Action<RetainedItem>? capture = null, ItemKey? predecessor = null)
    {
        if (noParent && parent is not null) return Error(output, "--parent and --no-parent are mutually exclusive.", 2);
        if (factory.IsTwig && (!noParent || predecessor is not null))
            return Error(output, "The Twig provider requires --no-parent; this source has no relationship schema.", 2);
        if (factory.IsGitHub && (!noParent || editor || predecessor is not null))
            return Error(output, "GitHub seed creation requires --no-parent and explicit native type/fields; editor and creation links are not qualified.", 2);
        if (!noParent && parent is null) return Error(output, "Specify --parent <id> or --no-parent.", 2);
        if (!editor && string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(requestedType))
            return Error(output, "Unparented seed creation requires --title and --type.", 2);
        var parsed = FieldAssignment.ParseAll(fields, "--field");
        if (!parsed.IsSuccess) return Error(output, parsed.Error.Message, 2);
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
                else if (!current.Value.RetainedItems.Any(draft => draft.Key == parentKey))
                    return Error(output, "The local parent has neither a retained seed nor a publication mapping.");
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
        var types = schema.Value.Types.Where(type => !type.Hidden && (type.Reference.Equals(requestedType,
                factory.IsGitHub ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            || !factory.IsGitHub && type.Rules.SelectMany(rule => rule.Values).Any(value => value.DataType == "ado.native-type-name"
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
            [TitleField] = FieldValue.String(title ?? ""),
        };
        var initial = type.Rules.Where(rule => !rule.Disabled && rule.Action == RuleAction.Allowed
                && rule.Field == type.StateField && rule.Conditions.Length == 1
                && rule.Conditions[0].Kind == ConditionKind.Was && rule.Conditions[0].Field == type.StateField
                && rule.Conditions[0].Value?.Kind == FieldKind.Null)
            .SelectMany(rule => rule.Values).Where(value => value.Kind == FieldKind.Text)
            .Select(value => value.Text!).Distinct(StringComparer.Ordinal).ToArray();
        if (initial.Length == 1) changes[type.StateField] = FieldValue.String(initial[0]);
        var defaults = type.Rules.Where(rule => !rule.Disabled && rule.Action == RuleAction.Default
            && rule.Field == type.StateField && rule.Conditions.IsEmpty).SelectMany(rule => rule.Values).Distinct().ToArray();
        if (initial.Length == 0 && defaults.Length == 1) changes[type.StateField] = defaults[0];
        var descriptionField = type.Fields.FirstOrDefault(field => field.Reference == BodyField);
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
        var assigned = type.Fields.FirstOrDefault(field => field.Reference == AssigneeField && !field.ReadOnly);
        if (assigned?.Kind == FieldKind.Identity && !changes.ContainsKey(assigned.Reference))
            changes[assigned.Reference] = FieldValue.Identity(workspace.NativeActor);
        else if (factory.IsGitHub && assigned?.Kind == FieldKind.StringList && !changes.ContainsKey(assigned.Reference))
            changes[assigned.Reference] = FieldValue.Strings([workspace.NativeActor]);
        if (editor)
        {
            var edited = await EditSeedFieldsAsync(type, new(changes), ct);
            if (!edited.IsSuccess) return Error(output, edited.Error.Message);
            if (edited.Value.Cancelled)
            {
                renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedEditCancelled",
                    new Dictionary<string, RenderCell> { ["message"] = RenderCell.String("Editor cancelled; no seed was staged.") })]));
                return 0;
            }
            foreach (var field in edited.Value.Fields) changes[field.Key] = field.Value;
        }
        if (!changes.TryGetValue(TitleField, out var seedTitle) || string.IsNullOrWhiteSpace(seedTitle.Text))
            return Error(output, "A seed title is required; no seed was created.", 2);
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
                ["message"] = RenderCell.String(output == "human" ? "Created a seed; no provider creation was attempted."
                    : "Created a local draft; no native creation was attempted."),
            })]));
        return 0;
    }

    private async Task<Result<(bool Cancelled, FieldValues Fields)>> EditSeedFieldsAsync(ItemType type, FieldValues current, CancellationToken ct, string? selectedField = null)
    {
        selectedField = selectedField?.ToLowerInvariant() switch
        {
            "title" => TitleField, "description" => BodyField, "state" => type.StateField,
            "assignedto" => "System.AssignedTo", _ => selectedField,
        };
        static string Display(string reference) => reference switch
        {
            "System.Title" => "Title", "System.Description" => "Description", "System.State" => "State",
            "System.AssignedTo" => "AssignedTo", _ => reference,
        };
        var definitions = type.Fields.Where(field => selectedField is null || field.Reference.Equals(selectedField, StringComparison.OrdinalIgnoreCase))
            .Select(field => (ReferenceName: field.Reference, DisplayName: Display(field.Reference),
                IsReadOnly: field.ReadOnly || SeedFieldEditor.IsSystemManaged(field.Reference))).ToArray();
        if (definitions.Length == 0) return Result<(bool, FieldValues)>.Fail(ErrorCode.InvalidInput, "The editor field is absent from the actual native type.");
        var values = type.Fields.ToDictionary(field => field.Reference, field =>
            current.TryGetValue(field.Reference, out var value) && value.Kind != FieldKind.Null ? Cell(value).DisplayText : null, StringComparer.OrdinalIgnoreCase);
        var initial = SeedFieldEditor.Generate(values, definitions);
        var edited = await editorLauncher.LaunchAsync(initial, ct);
        if (edited is null) return Result<(bool, FieldValues)>.Ok((true, FieldValues.Empty));
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in edited.Split('\n').Where(line => line.StartsWith("# ", StringComparison.Ordinal)))
        {
            var display = line[2..].Trim();
            var definition = definitions.SingleOrDefault(field => field.DisplayName.Equals(display, StringComparison.OrdinalIgnoreCase));
            if (definition.ReferenceName is null || definition.IsReadOnly || !sections.Add(definition.ReferenceName))
                return Result<(bool, FieldValues)>.Fail(ErrorCode.InvalidInput, "Editor contains an unknown, read-only or duplicate field section.");
        }
        var parsed = SeedFieldEditor.Parse(edited, definitions);
        var changes = FieldValues.Empty;
        foreach (var field in parsed)
        {
            var before = values.GetValueOrDefault(field.Key);
            if (string.Equals(before ?? "", field.Value ?? "", StringComparison.Ordinal)) continue;
            if (field.Key == TitleField && string.IsNullOrWhiteSpace(field.Value))
                return Result<(bool, FieldValues)>.Fail(ErrorCode.InvalidInput, "A seed title cannot be cleared.");
            var definition = type.Fields.Single(value => value.Reference == field.Key);
            if (field.Value is null) changes = changes.Set(field.Key, FieldValue.Null);
            else if (TryParse(definition.Kind, field.Value, null, out var value)) changes = changes.Set(field.Key, value);
            else return Result<(bool, FieldValues)>.Fail(ErrorCode.InvalidInput, "The editor value does not match the actual native field type.");
        }
        return Result<(bool, FieldValues)>.Ok((false, changes));
    }

    internal async Task<int> SeedEditAsync(int id, string output, CancellationToken ct, string? selectedField = null)
    {
        if (factory.IsGitHub) return Error(output, "GitHub seed editor is not qualified; use explicit typed update.");
        if (id >= 0) return Error(output, "Seed editing requires an unpublished local seed alias.", 2);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var matches = KnownLocalKeys(snapshot.Value).Where(key => LocalAlias(key) == id).ToArray();
        var draft = matches.Length == 1 ? snapshot.Value.RetainedItems.OfType<Seed>().SingleOrDefault(draft => draft.Key == matches[0]) : null;
        if (draft is null) return Error(output, "The exact unpublished seed alias is absent or ambiguous.");
        if (snapshot.Value.Mappings.Any(mapping => mapping.Local == draft.Key)
            || snapshot.Value.Journal.Any(row => row.Status != OpStatus.Planned && snapshot.Value.Plans
                .Single(plan => plan.Digest == row.Digest).Operations[row.Operation].Item == draft.Key))
            return Error(output, "The original creation UUID has already been attempted; its captured editor intent remains fenced.");
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var type = schema.Value.Types.SingleOrDefault(type => type.Reference == draft.Type);
        if (type is null) return Error(output, "The seed type is absent from the actual native schema.");
        var edited = await EditSeedFieldsAsync(type, draft.Fields, ct, selectedField);
        if (!edited.IsSuccess) return Error(output, edited.Error.Message);
        var fields = edited.Value.Fields;
        if (!edited.Value.Cancelled && fields.Count > 0)
        {
            var captured = await workspace.CaptureSeedFieldsAsync(draft, snapshot.Value.Version, fields, ct);
            if (!captured.IsSuccess) return Error(output, captured.Error.Message);
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record(edited.Value.Cancelled ? "seedEditCancelled"
            : fields.Count == 0 ? "seedEditNoChanges" : "seedEdited", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id), ["changedFieldCount"] = RenderCell.Integer(fields.Count),
                ["message"] = RenderCell.String(edited.Value.Cancelled ? "Seed editor cancelled." : "Retained typed seed edit; no native creation was attempted."),
            })]));
        return 0;
    }

    internal async Task<int> EditAsync(int? id, string? field, string output, CancellationToken ct)
    {
        if (factory.IsGitHub) return Error(output, "GitHub editor is not qualified; use explicit update or patch.");
        var target = await ResolveIdAsync(id, ct);
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
            "title" => TitleField, "description" => BodyField, "state" => type.StateField,
            "assignedto" => "System.AssignedTo", _ => field,
        };
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var prior = snapshot.Value.RetainedItems.SingleOrDefault(draft => draft.Key == key);
        var shown = prior is null ? observed.Value.Fields : observed.Value.Fields.Overlay(prior.Fields);
        var names = field is null ? new[] { TitleField, type.StateField, AssigneeField }
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
                "Title" => TitleField, "State" => type.StateField, "AssignedTo" => AssigneeField, var exact => exact,
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
            var saved = await workspace.SaveDraftAsync(preview.Value, ct);
            if (!saved.IsSuccess) return Error(output, saved.Error.Message);
            if (saved.Value.Error is { } failure) return Error(output, failure.Message);
            if (!saved.Value.EffectsVerified) return Error(output, "The editor effect remains unresolved; its journal and draft were retained.");
        }
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record(changes.Count == 0 ? "editNoChanges" : "editPushed",
            new Dictionary<string, RenderCell>
            {
                ["itemId"] = RenderCell.Integer(id.Value), ["changeCount"] = RenderCell.Integer(changes.Count),
                ["message"] = RenderCell.String(changes.Count == 0 ? "No changes detected." : "Editor fields applied through one digest-bound plan and verified journal."),
            })]));
        return 0;
    }

    internal async Task<int> SeedChainAsync(string[]? titles, int? parent, string? type, string output, CancellationToken ct)
    {
        if (parent is null || string.IsNullOrWhiteSpace(type))
            return Error(output, "Core chain authoring requires --parent and --type.", 2);
        var created = new List<RetainedItem>();
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
            ["title"] = Cell(draft.Fields[TitleField]),
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
        var remaining = snapshot.Value.RetainedItems.OfType<Seed>().ToList();
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
                var op = preview.Value.Plan!.Operations.Last();
                operations.Add(op.WithId($"validate-{operations.Count}"));
            }
            if (id is not null && LocalAlias(draft.Key) != id) continue;
            if (preview.IsSuccess) passed++;
            rows.Add(new("seedValidation", new Dictionary<string, RenderCell>
            {
                ["seedId"] = RenderCell.Integer(LocalAlias(draft.Key)), ["title"] = Cell(draft.Fields[TitleField]),
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
        if (factory.IsGitHub && branch is not null) return Error(output, "GitHub creation-bound resources are not qualified.");
        if (output != "human" && !dryRun)
            return Error(output, "Structured seed publication is refused. Export the plan with --dry-run -o json, then use plan apply with an explicit file.", 2);
        if (id is not null && (id >= 0 || all) || id is null && !all)
            return Error(output, "Specify one negative seed ID or --all.", 2);
        if (repository is not null && branch is null)
            return Error(output, "--repo requires --link-branch.", 2);
        return await WithWorkspaceAdmissionAsync(output, dryRun ? "Publication preview" : "Publication",
            () => PublishSeedsUnderLeaseAsync(id, all, dryRun, branch, repository, output, ct), ct);
    }

    private async Task<int> PublishSeedsUnderLeaseAsync(int? id, bool all, bool dryRun,
        string? branch, string? repository, string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local integer aliases collide; no seed publication target was guessed.");
        var drafts = snapshot.Value.RetainedItems.OfType<Seed>().Where(draft => (all || LocalAlias(draft.Key) == id)).ToArray();
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
            drafts = snapshot.Value.RetainedItems.OfType<Seed>().Where(draft => selected.Contains(draft.Key)).ToArray();
        }
        var keys = drafts.Select(draft => draft.Key).ToHashSet();
        if (snapshot.Value.Relationships.Any(edge => edge.Origin == RelationshipOrigin.Provider
            && !edge.Deleted && (keys.Contains(edge.From) || keys.Contains(edge.To))))
            return Error(output, "This seed has retained relationships requiring creation-bound publication effects; no link was dropped or seed published.");
        var captures = new List<CoreDraftPreview>();
        var operations = new List<Op>();
        var remaining = drafts.ToList();
        var ordered = new List<RetainedItem>();
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
            var preview = await workspace.PreviewDraftAsync(draft.Key, ct, operations, seedPublication: true);
            if (!preview.IsSuccess) return Error(output, preview.Error.Message);
            captures.Add(preview.Value);
            var op = preview.Value.Plan!.Operations.Last();
            operations.Add(op.WithId($"create-{operations.Count}"));
        }
        var prepared = await workspace.PrepareSeedPublicationAsync(operations, dryRun, ct);
        if (!prepared.IsSuccess) return Error(output, prepared.Error.Message);
        var (plan, reviewedStoreVersion) = prepared.Value;
        if (dryRun)
        {
            var cells = new Dictionary<string, RenderCell>
            {
                ["digest"] = RenderCell.String(plan.Digest), ["seedCount"] = RenderCell.Integer(drafts.Length),
                ["message"] = RenderCell.String("Retained exact unattempted creation intent; no seed was published."),
            };
            if (plan.Connection.Provider.Value == "ado")
                cells["notice"] = RenderCell.String(AdoProvider.CreateConcurrencyNotice);
            if (output != "human")
                cells["canonicalPlan"] = RenderCell.String(System.Text.Encoding.UTF8.GetString(global::Twig.Core.Core.PlanBytes(plan)));
            renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("seedPublicationPreview", cells)]));
            return 0;
        }
        var published = await workspace.SaveDraftSetAsync(captures, plan, ct, reviewedStoreVersion);
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
                ["status"] = RenderCell.String("Created"), ["digest"] = RenderCell.String(mapping.PlanDigest),
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
        var draft = snapshot.Value.RetainedItems.OfType<Seed>().FirstOrDefault(draft => draft.Key == matches[0]);
        if (draft is null) return Error(output, "The unpublished seed no longer exists.");
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
                ["message"] = RenderCell.String(output == "human" ? "Discarded the unattempted seed; journals and identity evidence were preserved."
                    : "Discarded the unattempted local draft; journals and identity evidence were preserved."),
            })]));
        return 0;
    }

    internal async Task<int> SeedLinkAsync(int sourceId, int targetId, string? type, bool remove, string output, CancellationToken ct)
    {
        if (factory.IsGitHub) return Error(output, "GitHub creation links are not qualified.");
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
        var draft = snapshot.Value.RetainedItems.OfType<Seed>().SingleOrDefault(value => value.Key == holder);
        if (draft is null) return Error(output, "The creation-bound link owner is not an unpublished local seed.");
        var mapped = snapshot.Value.Mappings.FirstOrDefault(value => value.Local == endpoint)?.Remote ?? endpoint;
        if (!mapped.IsLocal)
        {
            var observed = await workspace.ReadAsync(mapped, ct);
            if (!observed.IsSuccess) return Error(output, observed.Error.Message);
        }
        else if (!snapshot.Value.RetainedItems.Any(value => value.Key == endpoint))
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
                ["message"] = RenderCell.String(remove ? "Removed exact local creation-link intent; no native effect." : "Captured creation-link intent; publication requires its exact reviewed plan."),
            })]));
        return 0;
    }

    internal async Task<int> SeedLinksAsync(int? id, string output, CancellationToken ct)
    {
        if (factory.IsGitHub) return Error(output, "GitHub creation links are not qualified.");
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var snapshot = await handle.Workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        if (KnownLocalKeys(snapshot.Value).GroupBy(LocalAlias).Any(group => group.Count() != 1))
            return Error(output, "Local identity aliases are ambiguous; no link identity was guessed.");
        int Id(ItemKey key) => key.IsLocal ? LocalAlias(key) : int.Parse(key.NativeId!, CultureInfo.InvariantCulture);
        var rows = new List<RenderRow>();
        foreach (var draft in snapshot.Value.RetainedItems.OfType<Seed>())
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
        var evidence = History.Read(handle.Source);
        if (!evidence.IsSuccess) return Error(output, evidence.Error.Message);
        var read = await workspace.SnapshotAsync(ct);
        if (!read.IsSuccess) return Error(output, read.Error.Message);
        var snapshot = new NativeHistoricalProjection(read.Value, evidence.Value).Snapshot();
        var selected = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var rows = new List<RenderRow>();
        var missing = new List<int>();
        foreach (var id in ids)
        {
            var resolved = await workspace.ResolveTargetAsync(id, false, ct);
            if (!resolved.IsSuccess)
            {
                if (resolved.Error.Code != ErrorCode.NotFound) return Error(output, resolved.Error.Message);
                missing.Add(id); continue;
            }
            var key = resolved.Value;
            var item = snapshot.Items.FirstOrDefault(item => item.Key == key);
            if (item is null) { missing.Add(id); continue; }
            rows.Add(new(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id),
                ["type"] = Cell(item.Fields.TryGetValue("System.WorkItemType", out var type) ? type : FieldValue.String(item.Type)),
                ["title"] = Cell(item.Fields.TryGetValue(TitleField, out var title) ? title : FieldValue.Null),
                ["state"] = Cell(item.Fields.TryGetValue(StateField, out var state) ? state : FieldValue.Null),
                ["revision"] = item.Revision is null ? new("", new RenderValue.Null()) : RenderCell.String(item.Revision.Token),
                ["cached"] = RenderCell.Boolean(true),
                ["isDirty"] = RenderCell.Boolean(snapshot.RetainedItems.Any(draft => draft.Key == key)),
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
        if (tree && localId is null && fields is null && sections is null)
            return await TreeAsync(id, false, null, refresh, output, ct);
        if (id is null && localId is null || id == 0)
            return Error(output, "Pass an explicit work item ID or --local-id.", 2);
        if (tree || sections is not null)
            return Error(output, "Hierarchy/section detail routing is not yet connected in this in-progress Core cutover.", 1);
        if (localId is not null && (id is not null || refresh || fields is not null || output is not ("json" or "json-full" or "json-compact")))
            return Error(output, "--local-id requires JSON snapshot output without another target, refresh or projection.", 2);
        using var observation = refresh ? Twig.Infrastructure.Auth.ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true) : null;
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var evidence = refresh ? null : History.Read(handle.Source);
        if (evidence is { IsSuccess: false }) return Error(output, evidence.Error.Message);
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
        else
        {
            var resolved = await workspace.ResolveTargetAsync(id!.Value, refresh, ct);
            if (!resolved.IsSuccess) return Error(output, resolved.Error.Message);
            key = resolved.Value;
        }
        var localKey = key.IsLocal ? key : original.Value.Mappings.FirstOrDefault(mapping => mapping.Remote == key)?.Local;
        key = original.Value.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
        if (evidence is { IsSuccess: true })
        {
            var history = new NativeHistoricalProjection(original.Value, evidence.Value);
            if (history.Withholds(key)) return Error(output, "Historical output is withheld for this item.");
            original = Result<StoreSnapshot>.Ok(history.Snapshot());
        }
        WorkItem item;
        if (key.IsLocal)
        {
            var draft = original.Value.RetainedItems.FirstOrDefault(draft => draft.Key == key);
            if (draft is null) return Error(output, "No retained local draft matches this identity.");
            if (refresh) return Error(output, "An unpublished seed has no native observation to refresh.", 2);
            item = new(key, draft.Type, draft.Fields, null);
        }
        else if (refresh)
        {
            var read = await workspace.ReadAsync(key, ct);
            if (!read.IsSuccess) return Error(output, read.Error.Message);
            var restored = History.RestoreRetainedItem(handle.Source, read.Value);
            if (!restored.IsSuccess) return Error(output, restored.Error.Message);
            item = read.Value;
        }
        else
        {
            var found = original.Value.Items.FirstOrDefault(value => value.Key == key);
            if (found is null) return Error(output, $"Work item #{key.NativeId} is not cached. Use show {key.NativeId} --refresh.");
            item = found;
        }
        var snapshot = refresh ? await workspace.SnapshotAsync(ct) : original;
        if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
        var selected = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var displayId = CoreWorkspace.DisplayId(workspace.Connection, snapshot.Value, key);
        if (!displayId.IsSuccess) return Error(output, displayId.Error.Message);
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(displayId.Value),
            ["localId"] = localKey is null ? new("", new RenderValue.Null()) : RenderCell.String(localKey.LocalId.ToString("D")),
            ["isSeed"] = RenderCell.Boolean(key.IsLocal),
            ["type"] = Cell(item.Fields.TryGetValue("System.WorkItemType", out var nativeType)
                ? nativeType : FieldValue.String(item.Type)),
            ["title"] = Cell(item.Fields.TryGetValue(TitleField, out var title) ? title : FieldValue.Null),
            ["state"] = Cell(item.Fields.TryGetValue(StateField, out var state) ? state : FieldValue.Null),
            ["revision"] = item.Revision is null ? new("", new RenderValue.Null()) : RenderCell.String(item.Revision.Token),
            ["isDirty"] = RenderCell.Boolean(snapshot.Value.RetainedItems.Any(d => d.Key == key)),
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
        var target = await ResolveIdAsync(id, ct, allowNativeLookup: true);
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
        var resolved = await workspace.ResolveTargetAsync(id.Value, true, ct);
        if (!resolved.IsSuccess) return Error(output, resolved.Error.Message);
        var key = resolved.Value;
        Seed? seed = null;
        StoreSnapshot? captured = null;
        if (id < 0)
        {
            var snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(output, snapshot.Error.Message);
            var matches = KnownLocalKeys(snapshot.Value).Where(local => LocalAlias(local) == id).ToArray();
            if (matches.Length != 1) return Error(output, "The local target alias is absent or ambiguous.");
            var mapping = snapshot.Value.Mappings.FirstOrDefault(mapping => mapping.Local == matches[0]);
            seed = snapshot.Value.RetainedItems.OfType<Seed>().SingleOrDefault(draft => draft.Key == matches[0]);
            if (mapping is not null) key = mapping.Remote;
            else if (seed is null) return Error(output, "The local target has no retained draft or native mapping.");
            if (mapping is not null) seed = null;
            captured = snapshot.Value;
        }
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var observed = seed is null ? await workspace.ReadAsync(key, ct) : Result<WorkItem>.Ok(new(seed.Key, seed.Type, seed.Fields, null));
        if (!observed.IsSuccess) return Error(output, observed.Error.Message);
        var nativeType = schema.Value.Types.SingleOrDefault(type => type.Reference == observed.Value.Type);
        if (nativeType is null) return Error(output, "The observed item's native type is unavailable.");
        var name = field.ToLowerInvariant() switch
        {
            "title" => TitleField, "state" => nativeType.StateField, "description" => BodyField, "assignedto" => AssigneeField, _ => field,
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
        var saved = await workspace.SaveDraftAsync(preview.Value, ct);
        if (!saved.IsSuccess) return Error(output, saved.Error.Message);
        if (saved.Value.Error is { } failure) return Error(output, failure.Message);
        if (!saved.Value.EffectsVerified) return Error(output, "The requested effect did not verify; retained journal evidence requires attention.");
        var message = $"#{id.Value} {name} updated.";
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(id.Value),
            ["title"] = RenderCell.String(name == TitleField ? text : ""),
            ["field"] = RenderCell.String(name), ["valueDisplay"] = RenderCell.String(text),
            ["valueSource"] = RenderCell.String(body.Origin.ToString().ToLowerInvariant()),
            ["append"] = RenderCell.Boolean(append), ["wasSeed"] = RenderCell.Boolean(false),
            ["message"] = RenderCell.String(message), ["digest"] = preview.Value.Plan is { } plan
                ? RenderCell.String(plan.Digest) : new("", new RenderValue.Null()),
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

    internal Task<int> ValidatePlanAsync(string? file, string output, CancellationToken ct)
        => InspectAsync("plan validate", output, async format =>
        {
            if (string.IsNullOrWhiteSpace(file)) return Error(format, "An explicit Core plan file is required.", 2);
            var parsed = await CorePlanFiles.ReadAsync(factory.WorkspaceRoot, file, ct);
            if (!parsed.IsSuccess) return Error(format, parsed.Error.Message, parsed.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
            renderer.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Record("planValidation",
                new Dictionary<string, RenderCell>
                {
                    ["isValid"] = RenderCell.Boolean(true),
                    ["digest"] = RenderCell.String(parsed.Value.Digest),
                    ["formatVersion"] = RenderCell.Integer(Plan.FormatVersion),
                    ["operationCount"] = RenderCell.Integer(parsed.Value.Operations.Length),
                    ["validationScope"] = RenderCell.String("canonical-format"),
                    ["applicationEvidenceChecked"] = RenderCell.Boolean(false),
                    ["message"] = RenderCell.String("Canonical format is valid. Preview must separately acquire current process, policy and application evidence."),
                })]));
            return 0;
        });

    internal Task<int> PlanStatusAsync(string? file, string output, CancellationToken ct)
        => InspectAsync("plan status", output, async format =>
        {
            if (string.IsNullOrWhiteSpace(file)) return Error(format, "An explicit Core plan file is required.", 2);
            var parsed = await CorePlanFiles.ReadAsync(factory.WorkspaceRoot, file, ct);
            if (!parsed.IsSuccess) return Error(format, parsed.Error.Message, parsed.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
            return await WithWorkspaceAdmissionAsync(format, "Journal inspection",
                () => ReadPlanStatusUnderLeaseAsync(parsed.Value, format, ct), ct);
        });

    private async Task<int> ReadPlanStatusUnderLeaseAsync(Plan planFile, string format, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(format, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        if (planFile.Connection != workspace.Connection)
            return Error(format, "The plan belongs to another immutable workspace connection.", 2);
        var snapshot = await workspace.SnapshotAsync(ct);
        if (!snapshot.IsSuccess) return Error(format, snapshot.Error.Message);
        var plan = snapshot.Value.Plans.SingleOrDefault(value => value.Digest == planFile.Digest);
        var rows = snapshot.Value.Journal.Where(row => row.Digest == planFile.Digest).OrderBy(row => row.Operation).ToArray();
        var found = plan is not null;
        if (plan is not null && (rows.Length != plan.Operations.Length
            || rows.Where((row, ordinal) => row.Operation != ordinal).Any()))
            return Error(format, "The retained plan has incomplete journal ordinals; no execution outcome was inferred.");
        var operations = rows.Select(row => new RenderCell("", new RenderValue.Object(new Dictionary<string, RenderCell>
        {
            ["ordinal"] = RenderCell.Integer(row.Operation),
            ["id"] = RenderCell.String(planFile.Operations[row.Operation].Id),
            ["kind"] = RenderCell.String(planFile.Operations[row.Operation].Kind.ToString()),
            ["item"] = InspectionKey(planFile.Operations[row.Operation].Item),
            ["status"] = RenderCell.String(row.Status.ToString()),
            ["attempt"] = InspectionText(row.Attempt == Guid.Empty ? null : row.Attempt.ToString("D")),
            ["receipts"] = new("Receipts", new RenderValue.Array(row.Receipts.Select(receipt =>
                new RenderCell("", new RenderValue.Object(new Dictionary<string, RenderCell>
                {
                    ["status"] = RenderCell.String(receipt.Status.ToString()),
                    ["attempt"] = RenderCell.String(receipt.Attempt.ToString("D")),
                    ["evidence"] = new("Evidence", new RenderValue.Array(receipt.Evidence.Select(value => RenderCell.String(value)).ToArray())),
                    ["publishedItem"] = InspectionKey(receipt.PublishedKey),
                    ["revision"] = InspectionText(receipt.Revision?.Token),
                    ["error"] = receipt.Error is { } error
                        ? new("", new RenderValue.Object(new Dictionary<string, RenderCell>
                        {
                            ["code"] = RenderCell.String(error.Code.ToString()),
                            ["message"] = RenderCell.String(error.Message),
                            ["evidence"] = new("Evidence", new RenderValue.Array(error.Evidence.Select(value => RenderCell.String(value)).ToArray())),
                        })) : new("", new RenderValue.Null()),
                }))).ToArray())),
        }))).ToArray();
        renderer.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Record("planStatus",
                new Dictionary<string, RenderCell>
                {
                    ["digest"] = RenderCell.String(planFile.Digest),
                    ["found"] = RenderCell.Boolean(found),
                    ["allVerified"] = RenderCell.Boolean(found && rows.All(row => row.Status == OpStatus.Verified)),
                    ["operationCount"] = RenderCell.Integer(rows.Length),
                    ["operations"] = new("Operations", new RenderValue.Array(operations)),
                    ["message"] = RenderCell.String(found
                        ? "Journal inspected without applying, settling or replaying any operation."
                        : "No journal is retained for this exact canonical digest."),
                })]));
        return found ? 0 : 1;
    }

    private async Task<int> WithWorkspaceAdmissionAsync(string output, string operation, Func<Task<int>> run,
        CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(3));
        IDisposable? lease = null;
        try
        {
            // Acquire and run in the same async flow so nested operations inherit the exclusive lease.
            while (lease is null)
            {
                wait.Token.ThrowIfCancellationRequested();
                try { lease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true); }
                catch (ConnectionBindingResolutionException ex) when (ex.FailureCode == ConnectionOperationGate.ActiveFailureCode)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), wait.Token);
                }
            }
            wait.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            lease?.Dispose();
            return Error(output, ct.IsCancellationRequested
                ? $"{operation} did not start: cancelled before acquiring the workspace lease."
                : $"{operation} did not start: binding-operation-active: timed out waiting for the workspace lease.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(output, $"{operation} did not start: unable to open the workspace lease: {ex.Message}");
        }

        using (lease)
            return await run();
    }

    internal Task<int> PendingAsync(string output, CancellationToken ct)
        => InspectAsync("pending", output, async format =>
        {
            var opened = await factory.OpenAsync(ct);
            if (!opened.IsSuccess) return Error(format, opened.Error.Message);
            using var handle = opened.Value;
            var snapshot = await handle.Workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(format, snapshot.Error.Message);
            var drafts = snapshot.Value.RetainedItems.Select(draft => new RenderCell("", new RenderValue.Object(
                new Dictionary<string, RenderCell>
                {
                    ["item"] = InspectionKey(draft.Key),
                    ["isSeed"] = RenderCell.Boolean(draft.Key.IsLocal),
                    ["type"] = RenderCell.String(draft.Type),
                    ["baselineRevision"] = InspectionText((draft as LocalEdits)?.EditBaseline?.Revision?.Token),
                    ["fields"] = new("Fields", new RenderValue.Object(draft.Fields.ToDictionary(field => field.Key, field =>
                    {
                        var baseline = (draft as LocalEdits)?.EditBaseline;
                        var present = baseline?.Fields.ContainsKey(field.Key) == true;
                        return new RenderCell(field.Key, new RenderValue.Object(new Dictionary<string, RenderCell>
                        {
                            ["oldPresent"] = RenderCell.Boolean(present),
                            ["oldValue"] = present ? Cell(baseline!.Fields[field.Key]) : new("", new RenderValue.Null()),
                            ["newValue"] = Cell(field.Value),
                        }));
                    }, StringComparer.Ordinal))),
                    ["parent"] = InspectionKey((draft as Seed)?.Parent),
                    ["parentRelation"] = InspectionText((draft as Seed)?.ParentRelation),
                    ["resource"] = (draft as Seed)?.Resource is { } resource
                        ? new("", new RenderValue.Object(new Dictionary<string, RenderCell>
                        {
                            ["relation"] = RenderCell.String(resource.Relation),
                            ["target"] = RenderCell.String(resource.Target),
                            ["name"] = InspectionText(resource.Name),
                            ["comment"] = InspectionText(resource.Comment),
                        })) : new("", new RenderValue.Null()),
                    ["links"] = new("Links", new RenderValue.Array(((draft as Seed)?.Links ?? []).Select(link =>
                        new RenderCell("", new RenderValue.Object(new Dictionary<string, RenderCell>
                        {
                            ["other"] = InspectionKey(link.Other),
                            ["relation"] = RenderCell.String(link.Relation),
                            ["outgoing"] = RenderCell.Boolean(link.Outgoing),
                        }))).ToArray())),
                }))).ToArray();
            renderer.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Record("pending",
                new Dictionary<string, RenderCell>
                {
                    ["storeVersion"] = RenderCell.Integer(snapshot.Value.Version),
                    ["count"] = RenderCell.Integer(drafts.Length),
                    ["fieldCount"] = RenderCell.Integer(snapshot.Value.RetainedItems.Sum(draft => draft.Fields.Count)),
                    ["drafts"] = new(format == "human" ? "Local edits and seeds" : "Drafts", new RenderValue.Array(drafts)),
                    ["message"] = RenderCell.String(format == "human" ? "Captured local edits and seeds in retained order; no journal or local intent was changed."
                        : "Captured typed drafts, in retained draft order; no journal or draft was changed."),
                })]));
            return 0;
        });

    private async Task<int> InspectAsync(string command, string output, Func<string, Task<int>> inspect)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(output) ?? string.Empty;
        var exit = 1;
        try
        {
            if (format.Length == 0 || format == "ids")
                return exit = Error(format.Length == 0 ? OutputFormats.Default : format,
                    "Inspection supports human, json, json-full, json-compact and minimal, not bare IDs.", 2);
            return exit = await inspect(format);
        }
        catch (OperationCanceledException)
        {
            return exit = Error(format, "Inspection was cancelled; drafts and journal receipts were not changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return exit = Error(format, "Inspection did not complete: " + ex.Message);
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, command, format, exit, started); }
            catch (Exception) { Debug.WriteLine("Optional Core inspection telemetry failed; command outcome was preserved."); }
        }
    }

    private static RenderCell InspectionText(string? value)
        => value is null ? new("", new RenderValue.Null()) : RenderCell.String(value);

    private static RenderCell InspectionKey(ItemKey? key) => key is null ? new("", new RenderValue.Null())
        : new("", new RenderValue.Object(new Dictionary<string, RenderCell>
        {
            ["provider"] = InspectionText(key.Provider?.Value),
            ["scope"] = InspectionText(key.ResourceScope),
            ["nativeId"] = InspectionText(key.NativeId),
            ["localId"] = InspectionText(key.IsLocal ? key.LocalId.ToString("D") : null),
        }));

    internal async Task<int> ApplyPlanAsync(string? file, string output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file))
            return Error(output, "An explicit Core plan file is required.", 2);
        var parsed = await CorePlanFiles.ReadAsync(factory.WorkspaceRoot, file, ct);
        if (!parsed.IsSuccess) return Error(output, parsed.Error.Message, parsed.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        var plan = parsed.Value;
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var applied = await handle.Workspace.ApplyFilePlanAsync(plan, ct);
        if (!applied.IsSuccess) return Error(output, applied.Error.Message);
        if (applied.Value.Error is { } failure) return Error(output, failure.Message);
        if (!applied.Value.EffectsVerified) return Error(output, "The plan has an unresolved effect; no blind replay was performed.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("planApplied",
            new Dictionary<string, RenderCell>
            {
                ["digest"] = RenderCell.String(plan.Digest),
                ["verified"] = RenderCell.Boolean(true),
                ["operationCount"] = RenderCell.Integer(plan.Operations.Length),
                ["alreadyVerified"] = RenderCell.Boolean(applied.Value.AlreadyVerified),
            })]));
        return 0;
    }

    internal async Task<int> PreviewPlanAsync(string? file, string output, CancellationToken ct, string? expectedDigest = null)
    {
        if (string.IsNullOrWhiteSpace(file)) return Error(output, "An explicit Core plan file is required.", 2);
        var parsed = await CorePlanFiles.ReadAsync(factory.WorkspaceRoot, file, ct);
        if (!parsed.IsSuccess) return Error(output, parsed.Error.Message, 2);
        var plan = parsed.Value;
        if (expectedDigest is not null && expectedDigest != plan.Digest)
            return Error(output, "The plan differs from the exact expected review digest.");
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        if (plan.Connection != workspace.Connection) return Error(output, "The plan belongs to another immutable workspace connection.");
        var checkedIntent = await workspace.CheckPlanAsync(plan, ct);
        if (!checkedIntent.IsSuccess) return Error(output, checkedIntent.Error.Message);
        var blocked = checkedIntent.Value.FirstOrDefault();
        var retained = await workspace.RetainReviewAsync(plan, ct);
        if (!retained.IsSuccess) return Error(output, retained.Error.Message);
        var cells = new Dictionary<string, RenderCell>
        {
            ["digest"] = RenderCell.String(plan.Digest),
            ["canApply"] = RenderCell.Boolean(blocked is null),
            ["operations"] = new("Operations", new RenderValue.Array(plan.Operations.Select(op =>
                new RenderCell(op.Id, new RenderValue.Object(new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.String(op.Id), ["kind"] = RenderCell.String(op.Kind.ToString()),
                    ["concurrency"] = RenderCell.String(op.Concurrency.ToString()),
                }))).ToArray())),
        };
        if (plan.Connection.Provider.Value == "ado" && plan.Operations.Any(op => op.Kind == OpKind.Create))
            cells["notice"] = RenderCell.String(AdoProvider.CreateConcurrencyNotice);
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
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("planPreview", cells)]));
        return 0;
    }

    internal async Task<int> NoteAsync(string? text, string? file, bool stdin, int? id,
        string output, string? format, CancellationToken ct)
    {
        var selected = await ResolveIdAsync(id, ct, allowNativeLookup: true);
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
        var value = factory.IsGitHub ? body.Value! : HtmlFieldFormatter.ResolveComment(body.Value!, format).EffectiveValue;
        var executed = await ExecuteEffectAsync(id.Value,
            item => new("note", OpKind.Comment, item.Key, item.Revision,
                body: FieldValue.RichText(value, factory.IsGitHub ? FieldKind.Markdown : FieldKind.Html)),
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
        var selectedId = await ResolveIdAsync(id, ct, allowNativeLookup: true);
        if (!selectedId.IsSuccess) return Error(output, selectedId.Error.Message, selectedId.Error.Code == ErrorCode.InvalidInput ? 2 : 1);
        id = selectedId.Value;
        if (id <= 0) return await UpdateAsync(StateField, requested, output, null, null, false, id, false, ct);
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Error(output, opened.Error.Message);
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var resolved = await workspace.ResolveTargetAsync(id.Value, true, ct);
        if (!resolved.IsSuccess) return Error(output, resolved.Error.Message);
        var key = resolved.Value;
        var read = await workspace.ReadAsync(key, ct);
        if (!read.IsSuccess) return Error(output, read.Error.Message);
        var schema = await workspace.SchemaAsync(ct);
        if (!schema.IsSuccess) return Error(output, schema.Error.Message);
        var type = schema.Value.Types.Single(t => t.Reference == read.Value.Type);
        if (type.Fields.SingleOrDefault(field => field.Reference == type.StateField)?.Kind != FieldKind.Text)
            return Error(output, "The native state role requires its discovered text field.");
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
        var plan = await workspace.PrepareAsync([new("state", OpKind.Batch, key, read.Value.Revision,
            new([KeyValuePair.Create(type.StateField, FieldValue.String(target))]), concurrency: workspace.Concurrency)], ct);
        if (!plan.IsSuccess) return Error(output, plan.Error.Message);
        var applied = await workspace.ApplyAsync(plan.Value, ct);
        if (!applied.IsSuccess) return Error(output, applied.Error.Message);
        if (applied.Value.Error is { } failure) return Error(output, failure.Message);
        if (!applied.Value.EffectsVerified) return Error(output, "The transition has unresolved journal evidence.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("stateChanged",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id.Value), ["title"] = Cell(read.Value.Fields[TitleField]),
                ["fromState"] = RenderCell.String(from), ["toState"] = RenderCell.String(target),
                ["requestedState"] = RenderCell.String(requested), ["transitionCount"] = RenderCell.Integer(1),
                ["isCategoryResolution"] = RenderCell.Boolean(category),
                ["message"] = RenderCell.String($"#{id.Value} moved to '{target}'."),
                ["digest"] = RenderCell.String(plan.Value.Digest),
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
        var parsed = FieldAssignment.ParseAll(set, "--set");
        if (!parsed.IsSuccess) return Error(output, parsed.Error.Message, 2);
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
            var resolved = await workspace.ResolveTargetAsync(target, true, ct);
            if (!resolved.IsSuccess) return Error(output, resolved.Error.Message);
            var key = resolved.Value;
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
            var body = note is null ? null : FieldValue.RichText(factory.IsGitHub ? note : HtmlFieldFormatter.ResolveComment(note, format).EffectiveValue,
                factory.IsGitHub ? FieldKind.Markdown : FieldKind.Html);
            operations.Add(fields.Count == 0
                ? new("note-" + target, OpKind.Comment, key, read.Value.Revision, body: body, concurrency: workspace.Concurrency)
                : new("batch-" + target, OpKind.Batch, key, read.Value.Revision, fields, body: body, concurrency: workspace.Concurrency));
        }
        var plan = await workspace.PrepareAsync(operations, ct);
        if (!plan.IsSuccess) return Error(output, plan.Error.Message);
        var executed = await workspace.ApplyAsync(plan.Value, ct);
        if (!executed.IsSuccess) return Error(output, executed.Error.Message);
        if (executed.Value.Error is { } failure) return Error(output, failure.Message);
        if (!executed.Value.EffectsVerified) return Error(output, "The ordered batch has unresolved effects; no remaining operation was replayed.");
        var final = await workspace.SnapshotAsync(ct);
        if (!final.IsSuccess) return Error(output, final.Error.Message);
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("batchCompleted",
            new Dictionary<string, RenderCell>
            {
                ["digest"] = RenderCell.String(plan.Value.Digest), ["successCount"] = RenderCell.Integer(operations.Count),
                ["failureCount"] = RenderCell.Integer(0),
                ["results"] = new("Results", new RenderValue.Array(operations.Select(op => new RenderCell(op.Item.NativeId!,
                    new RenderValue.Object(new Dictionary<string, RenderCell>
                    {
                        ["itemId"] = CoreWorkspace.DisplayId(workspace.Connection, final.Value, op.Item) is { IsSuccess: true } number
                            ? RenderCell.Integer(number.Value) : RenderCell.String("(number unavailable)"),
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
        else if (kind == FieldKind.StringList)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(text, CoreWorkspaceJsonContext.Default.JsonElement);
                if (parsed.ValueKind != JsonValueKind.Array || parsed.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                    return false;
                value = FieldValue.Strings(parsed.EnumerateArray().Select(item => item.GetString()!));
            }
            catch (JsonException) { return false; }
        }
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
        var resolved = await workspace.ResolveTargetAsync(id.Value, true, ct);
        if (!resolved.IsSuccess) return Error(output, resolved.Error.Message);
        var key = resolved.Value;
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
            else if (definition.Kind == FieldKind.StringList && property.Value.ValueKind == JsonValueKind.Array
                && TryParse(definition.Kind, property.Value.GetRawText(), format, out var list)) value = list;
            else return Error(output, "A patch value has an invalid native type.", 2);
            fields = fields.Set(definition.Reference, value);
        }
        if (fields.Count == 0) return Error(output, "At least one explicit patch field is required.", 2);
        var plan = await workspace.PrepareAsync([new("patch", OpKind.Batch, key, read.Value.Revision, fields, concurrency: workspace.Concurrency)], ct);
        if (!plan.IsSuccess) return Error(output, plan.Error.Message);
        var applied = await workspace.ApplyAsync(plan.Value, ct);
        if (!applied.IsSuccess) return Error(output, applied.Error.Message);
        if (applied.Value.Error is { } failure) return Error(output, failure.Message);
        if (!applied.Value.EffectsVerified) return Error(output, "The patch did not verify; its journal remains authoritative.");
        renderer.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("fieldsPatched",
            new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(id.Value), ["title"] = Cell(read.Value.Fields[TitleField]),
                ["fieldCount"] = RenderCell.Integer(fields.Count), ["wasSeed"] = RenderCell.Boolean(false),
                ["message"] = RenderCell.String($"Patched {fields.Count} field(s) on #{id.Value}."),
                ["digest"] = RenderCell.String(plan.Value.Digest),
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

    private async Task<Plan?> ExecuteEffectAsync(int id, Func<WorkItem, Op> build, string output, CancellationToken ct)
    {
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) { Error(output, opened.Error.Message); return null; }
        using var handle = opened.Value;
        var workspace = handle.Workspace;
        var key = await workspace.ResolveTargetAsync(id, true, ct);
        if (!key.IsSuccess) { Error(output, key.Error.Message); return null; }
        var read = await workspace.ReadAsync(key.Value, ct);
        if (!read.IsSuccess) { Error(output, read.Error.Message); return null; }
        var op = build(read.Value);
        op = new(op.Id, op.Kind, op.Item, op.ExpectedRevision, op.Fields, op.Type, op.Other, op.Relation,
            op.Origin, op.Body, workspace.Concurrency, op.ExpectedLocalVersion, op.Resource, op.Links, op.CreateEvidence);
        var plan = await workspace.PrepareAsync([op], ct);
        if (!plan.IsSuccess) { Error(output, plan.Error.Message); return null; }
        var applied = await workspace.ApplyAsync(plan.Value, ct);
        if (!applied.IsSuccess) { Error(output, applied.Error.Message); return null; }
        if (applied.Value.Error is { } error) { Error(output, error.Message); return null; }
        if (!applied.Value.EffectsVerified) { Error(output, "The effect is unresolved; no automatic retry was performed."); return null; }
        return plan.Value;
    }

    internal static RenderCell Cell(FieldValue value) => value.Kind switch
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
