using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

public sealed record NativeEditForm(Guid Ticket, ItemKey Item, string Type, string StateField,
    FieldValues Values, EditorEvaluation Evaluation);
public sealed record NativeEditResult(bool Verified, bool DraftRetained, Error? Error);

/// <summary>Human-host editor session. No active-item selection or alternate publisher.</summary>
public sealed class NativeItemEditor : IDisposable
{
    private readonly ServiceProvider runtime;
    private readonly NativeEditor editor;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, NativeEditorItem> items = [];
    private NativeItemEditor(ServiceProvider runtime, NativeEditor editor)
        => (this.runtime, this.editor) = (runtime, editor);
    public bool PlanMode => editor.Plan.PlanMode;
    public Guid PlanId => editor.Plan.Id;
    public long PlanRevision => editor.Plan.Revision;
    public Guid ScopeId => editor.ScopeId;
    public Guid StoreInstance => editor.StoreInstance;
    public void Dispose() { editor.Dispose(); runtime.Dispose(); gate.Dispose(); }

    public static Task<Result<NativeItemEditor>> OpenAsync(string root, Guid benchId,
        NativeSourceStamp expected, CancellationToken ct = default)
        => OpenAsync(root, benchId, expected, null, ct);

    internal static async Task<Result<NativeItemEditor>> OpenAsync(string root, Guid benchId,
        NativeSourceStamp expected, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        if (expected.Connection.Provider.Value is not ("ado" or "github"))
            return Result<NativeItemEditor>.Fail(ErrorCode.Unsupported, "This provider has no qualified native item editor.");
        if (!Path.IsPathFullyQualified(root) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) != root)
            return Result<NativeItemEditor>.Fail(ErrorCode.InvalidInput, "An exact native workspace is required.");
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), new TwigConfiguration(), root);
        var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
        var services = new ServiceCollection();
        configure?.Invoke(services);
        services.AddCoreWorkspaceServices(configuration, paths);
        var runtime = services.BuildServiceProvider();
        try
        {
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            var user = UserBenchStore.ForRegistry(factory.ExecutionRegistryPath);
            var opened = await NativeEditor.OpenAsync(factory, user, benchId, expected, ct).ConfigureAwait(false);
            if (!opened.IsSuccess) return Result<NativeItemEditor>.Fail(opened.Error);
            var result = new NativeItemEditor(runtime, opened.Value);
            runtime = null!;
            return Result<NativeItemEditor>.Ok(result);
        }
        finally { runtime?.Dispose(); }
    }

    public async Task<Result<NativeEditForm>> SelectAsync(ItemKey key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (key.IsLocal && !PlanMode)
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Local seeds remain read-only outside Plan mode.");
            var selected = await editor.SelectAsync(key, ct).ConfigureAwait(false);
            if (!selected.IsSuccess) return Result<NativeEditForm>.Fail(selected.Error);
            var item = selected.Value;
            if (item.Capture is { Ready: false })
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Interrupted native intent requires explicit recovery before editing.");
            if (item.Capture is { } capture && !UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Links.IsEmpty)
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Protected relationship intent must be reviewed in its native workflow; this field Save will not publish it.");
            if (item.Draft is not null && item.Capture is null)
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Existing CLI draft debt must be reviewed in Twig before this editor can save; it was not adopted.");
            if (items.Count >= 64) items.Clear();
            var ticket = Guid.NewGuid();
            items[ticket] = item;
            return Form(ticket, item, FieldValues.Empty);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeEditForm>> EvaluateAsync(Guid ticket, FieldValues changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var valid = await editor.ValidateAsync(ct).ConfigureAwait(false);
            if (!valid.IsSuccess) return Result<NativeEditForm>.Fail(valid.Error);
            return items.TryGetValue(ticket, out var item) ? Form(ticket, item, changes)
                : Result<NativeEditForm>.Fail(ErrorCode.Conflict, "The editor handle expired. Reopen without discarding your edited values.");
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeEditResult>> SaveAsync(Guid ticket, FieldValues changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (PlanMode)
                return Result<NativeEditResult>.Fail(ErrorCode.Conflict, "Plan work stages locally. Review and apply the whole Plan explicitly.");
            if (!items.TryGetValue(ticket, out var item))
                return Result<NativeEditResult>.Fail(ErrorCode.Conflict, "The exact item handle expired; reopen before saving.");
            var evaluated = Form(ticket, item, changes);
            if (!evaluated.IsSuccess) return Result<NativeEditResult>.Fail(evaluated.Error);
            if (!evaluated.Value.Evaluation.Errors.IsEmpty)
                return Result<NativeEditResult>.Fail(evaluated.Value.Evaluation.Errors[0]);
            var staged = await editor.StageAsync(item, changes, null, null, ct).ConfigureAwait(false);
            if (!staged.IsSuccess) return Result<NativeEditResult>.Fail(staged.Error);
            items[ticket] = staged.Value;
            var saved = await editor.SaveAsync(staged.Value, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result<NativeEditResult>.Fail(saved.Error);
            if (saved.Value.EffectsVerified && !saved.Value.DraftRetained) items.Remove(ticket);
            return Result<NativeEditResult>.Ok(new(saved.Value.EffectsVerified, saved.Value.DraftRetained, saved.Value.Error));
        }
        finally { gate.Release(); }
    }

    public Task<Result<EditorChoices>> ChoicesAsync(string type, string field, string query, CancellationToken ct = default)
        => editor.Metadata.Choices(type, field, query, 50, ct);

    public async Task<Result<NativeEditForm>> StageAsync(Guid ticket, FieldValues changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!PlanMode) return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Enter Plan mode explicitly before staging.");
            if (!items.TryGetValue(ticket, out var item))
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Reopen the exact Plan item before staging.");
            var evaluated = Form(ticket, item, changes);
            if (!evaluated.IsSuccess) return evaluated;
            var staged = await editor.StageAsync(item, changes, null, null, ct).ConfigureAwait(false);
            if (!staged.IsSuccess) return Result<NativeEditForm>.Fail(staged.Error);
            items.Clear();
            items[ticket] = staged.Value;
            return Form(ticket, staged.Value, FieldValues.Empty);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeEditForm>> CreateSeedAsync(string type, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!PlanMode) return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Local seed authoring requires explicit Plan mode.");
            var created = await editor.CreateSeedAsync(type, ct).ConfigureAwait(false);
            if (!created.IsSuccess) return Result<NativeEditForm>.Fail(created.Error);
            items.Clear();
            var ticket = Guid.NewGuid();
            items[ticket] = created.Value;
            return Form(ticket, created.Value, FieldValues.Empty);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<ImmutableArray<Draft>>> ReadPlanDraftsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!PlanMode) return Result<ImmutableArray<Draft>>.Fail(ErrorCode.Conflict, "This editor is outside Plan mode.");
            var valid = await editor.ValidateAsync(ct).ConfigureAwait(false);
            if (!valid.IsSuccess) return Result<ImmutableArray<Draft>>.Fail(valid.Error);
            var snapshot = await editor.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
            return snapshot.IsSuccess ? Result<ImmutableArray<Draft>>.Ok(snapshot.Value.Drafts)
                : Result<ImmutableArray<Draft>>.Fail(snapshot.Error);
        }
        finally { gate.Release(); }
    }

    private Result<NativeEditForm> Form(Guid ticket, NativeEditorItem item, FieldValues changes)
    {
        if (changes.Count > 64) return Result<NativeEditForm>.Fail(ErrorCode.InvalidInput, "Too many edited fields.");
        var draft = (item.Draft?.Changes ?? FieldValues.Empty).Overlay(changes);
        var baseline = item.Draft?.Baseline ?? item.Observation;
        var evaluated = Model.EvaluateEditorFields(editor.Metadata.Schema, baseline, draft,
            editor.Workspace.Capabilities.AtomicFieldBatches);
        if (!evaluated.IsSuccess) return Result<NativeEditForm>.Fail(evaluated.Error);
        var gates = baseline.Key.IsLocal ? Result<ImmutableArray<string>>.Ok([])
            : Model.CloseGates(editor.Metadata.Schema, baseline, draft);
        if (!gates.IsSuccess) return Result<NativeEditForm>.Fail(gates.Error);
        var type = editor.Metadata.Schema.Types.Single(value => value.Reference == item.Observation.Type);
        foreach (var field in changes)
        {
            var definition = evaluated.Value.Fields.SingleOrDefault(value => value.Definition.Reference == field.Key);
            if (definition is null || definition.ReadOnly || field.Value.Kind != FieldKind.Null && field.Value.Kind != definition.Definition.Kind)
                return Result<NativeEditForm>.Fail(ErrorCode.InvalidInput, "An edited field is unavailable, read-only or has the wrong native type: " + field.Key);
        }
        var evaluation = evaluated.Value with { Fields = evaluated.Value.Fields.Select(field =>
            field with { Required = field.Required || gates.Value.Contains(field.Definition.Reference) }).ToImmutableArray() };
        return Result<NativeEditForm>.Ok(new(ticket, item.Observation.Key, type.Reference, type.StateField,
            baseline.Fields.Overlay(draft), evaluation));
    }
}
