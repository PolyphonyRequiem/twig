using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Providers.GitHub;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

public sealed record NativeEditForm(Guid Ticket, ItemKey Item, string Type, string StateField,
    FieldValues Values, EditorEvaluation Evaluation)
{
    public bool RelationshipsReadOnly { get; init; }
    public ImmutableArray<CreationLink> RelationshipIntent { get; init; } = [];
}
public sealed record NativeEditResult(bool Verified, bool DraftRetained, Error? Error)
{
    public NativeSeedPublication? SeedPublication { get; init; }
}

/// <summary>Human-host editor session. No active-item selection or alternate publisher.</summary>
public sealed partial class NativeItemEditor : IDisposable
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
    private bool GitHub => editor.Origin.Connection.Provider.Value == "github";
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
            if (key.IsLocal && GitHub)
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "GitHub authoring here supports existing issues only; local creation intent remains protected.");
            var selected = await editor.SelectAsync(key, ct).ConfigureAwait(false);
            if (!selected.IsSuccess) return Result<NativeEditForm>.Fail(selected.Error);
            var item = selected.Value;
            if (key.IsLocal && !PlanMode && item.Capture is { SeedProposal: not null })
                return Result<NativeEditForm>.Fail(ErrorCode.Indeterminate, "The original seed publication is protected. Observe or continue its exact delivery reference; never edit or create it again.");
            if (item.Capture is { Ready: false })
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Interrupted native intent requires explicit recovery before editing.");
            var supported = SupportedItem(item);
            if (!supported.IsSuccess) return Result<NativeEditForm>.Fail(supported.Error);
            var relationshipIntent = item.Capture is { } capture && !UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Links.IsEmpty;
            if (relationshipIntent && (GitHub || PlanMode || key.IsLocal))
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "This provider/mode has no qualified existing-item relationship editor.");
            if (items.Count + localPairs.Count >= 64) { items.Clear(); relationshipEndpoints.Clear(); localPairs.Clear(); }
            var ticket = Guid.NewGuid();
            items[ticket] = item;
            var form = Form(ticket, item, FieldValues.Empty);
            return form.IsSuccess && relationshipIntent ? Result<NativeEditForm>.Ok(form.Value with
            {
                RelationshipsReadOnly = true,
                RelationshipIntent = UserBenchStore.ParseIntent(item.Capture!.IntentJson).Operations.Single().Links,
                Evaluation = form.Value.Evaluation with { Fields = form.Value.Evaluation.Fields.Select(field => field with { ReadOnly = true }).ToImmutableArray() }
            }) : form;
        }
        finally { gate.Release(); }
    }

    private Result<bool> SupportedItem(NativeEditorItem item)
    {
        var intent = item.Capture is { } retained ? UserBenchStore.ParseIntent(retained.IntentJson).Operations.Single() : null;
        if (item.Draft is { } draft && (draft.Type != item.Observation.Type || draft.Parent is not null
            || draft.ParentRelation is not null || draft.Resource is not null || !draft.Links.IsEmpty)
            || intent is not null && (intent.Type is not null && intent.Type != item.Observation.Type
                || intent.Other is not null || intent.Relation is not null || intent.Resource is not null || intent.Body is not null)
            || GitHub && ((item.Draft?.Changes ?? FieldValues.Empty).Any(field => !GitHubScalar(field.Key))
                || intent is not null && intent.Fields.Any(field => !GitHubScalar(field.Key))))
            return Result<bool>.Fail(ErrorCode.Unsupported, "Protected type, collection, creation or resource intent is unsupported; nothing was adopted or dropped.");
        if (item.Draft is not null && item.Capture is null)
            return Result<bool>.Fail(ErrorCode.Conflict, "Existing CLI draft debt must be reviewed in Twig before this editor can save; it was not adopted.");
        return Result<bool>.Ok(true);
    }

    public async Task<Result<NativeEditForm>> EvaluateAsync(Guid ticket, FieldValues changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (localPairs.ContainsKey(ticket))
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "LOCAL pair fields are read-only; scalar evaluation does not adopt local relationship tickets.");
            var valid = await editor.ValidateAsync(ct).ConfigureAwait(false);
            if (!valid.IsSuccess) return Result<NativeEditForm>.Fail(valid.Error);
            if (!items.TryGetValue(ticket, out var item))
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "The editor handle expired. Reopen without discarding your edited values.");
            var available = await editor.LocalEndpointAvailableAsync(item.Observation.Key, ct).ConfigureAwait(false);
            if (!available.IsSuccess) return Result<NativeEditForm>.Fail(available.Error);
            if (relationshipEndpoints.TryGetValue(ticket, out var endpoint))
            {
                var current = await RelationshipFormAsync(ticket, item, endpoint.Item, ct).ConfigureAwait(false);
                if (!current.IsSuccess) return Result<NativeEditForm>.Fail(current.Error);
                var observed = await editor.Metadata.Read([item.Observation.Key, endpoint.Item.Key], ct).ConfigureAwait(false);
                if (!observed.IsSuccess) return Result<NativeEditForm>.Fail(observed.Error);
                if (!observed.Value.Complete || observed.Value.Items.Length != 2
                    || new[] { item.Observation, endpoint.Item }.Any(expected => !observed.Value.Items.Any(value =>
                        value.Key == expected.Key && value.Revision == expected.Revision && value.RelationshipsComplete))
                    || !observed.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Provider && !edge.Deleted)
                        .ToHashSet().SetEquals(current.Value.Incident))
                    return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "The displayed native pair evidence changed; prospective fields were not staged.");
                valid = await editor.ValidateAsync(ct).ConfigureAwait(false);
                if (!valid.IsSuccess) return Result<NativeEditForm>.Fail(valid.Error);
                var coherent = await RelationshipFormAsync(ticket, item, endpoint.Item, ct).ConfigureAwait(false);
                if (!coherent.IsSuccess) return Result<NativeEditForm>.Fail(coherent.Error);
            }
            else if (HasRelationships(item))
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Retained relationships are read-only here; select both explicit endpoints in the qualified editor.");
            return Form(ticket, item, changes);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeEditResult>> SaveAsync(Guid ticket, FieldValues changes, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (localPairs.ContainsKey(ticket))
                return Result<NativeEditResult>.Fail(ErrorCode.Unsupported, "Scalar Save cannot apply LOCAL relationship intent; use its separate exact digest.");
            if (PlanMode)
                return Result<NativeEditResult>.Fail(ErrorCode.Conflict, "Plan work stages locally. Review and apply the whole Plan explicitly.");
            if (!items.TryGetValue(ticket, out var item))
                return Result<NativeEditResult>.Fail(ErrorCode.Conflict, "The exact item handle expired; reopen before saving.");
            var available = await editor.LocalEndpointAvailableAsync(item.Observation.Key, ct).ConfigureAwait(false);
            if (!available.IsSuccess) return Result<NativeEditResult>.Fail(available.Error);
            if (HasRelationships(item) || relationshipEndpoints.ContainsKey(ticket))
                return Result<NativeEditResult>.Fail(ErrorCode.Unsupported, "Scalar Save cannot publish retained relationship intent; use its explicit qualified ticket.");
            var evaluated = Form(ticket, item, changes);
            if (!evaluated.IsSuccess) return Result<NativeEditResult>.Fail(evaluated.Error);
            if (!evaluated.Value.Evaluation.Errors.IsEmpty)
                return Result<NativeEditResult>.Fail(evaluated.Value.Evaluation.Errors[0]);
            var staged = await editor.StageAsync(item, changes, null, null, ct).ConfigureAwait(false);
            if (!staged.IsSuccess) return Result<NativeEditResult>.Fail(staged.Error);
            items[ticket] = staged.Value;
            var saved = await editor.SaveAsync(staged.Value, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result<NativeEditResult>.Fail(saved.Error);
            NativeSeedPublication? publication = null;
            if (staged.Value.Observation.Key.IsLocal)
            {
                var observed = await editor.ObserveSeedAsync(staged.Value.Capture!.Id, ct).ConfigureAwait(false);
                if (!observed.IsSuccess) return Result<NativeEditResult>.Fail(observed.Error);
                publication = observed.Value with { Error = saved.Value.Error ?? observed.Value.Error };
            }
            if (saved.Value.EffectsVerified && !saved.Value.DraftRetained) items.Remove(ticket);
            return Result<NativeEditResult>.Ok(new(saved.Value.EffectsVerified, saved.Value.DraftRetained, saved.Value.Error)
                { SeedPublication = publication });
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
            if (localPairs.ContainsKey(ticket))
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Scalar staging cannot adopt LOCAL relationship tickets.");
            if (!items.TryGetValue(ticket, out var item))
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Reopen the exact native item before staging.");
            var available = await editor.LocalEndpointAvailableAsync(item.Observation.Key, ct).ConfigureAwait(false);
            if (!available.IsSuccess) return Result<NativeEditForm>.Fail(available.Error);
            if (HasRelationships(item) || relationshipEndpoints.ContainsKey(ticket))
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Scalar staging cannot replace retained relationship intent.");
            if (!PlanMode && (!item.Observation.Key.IsLocal || GitHub))
                return Result<NativeEditForm>.Fail(ErrorCode.Conflict, "Normal staging supports owned ADO seeds only; existing scalar edits use Save.");
            var evaluated = Form(ticket, item, changes);
            if (!evaluated.IsSuccess) return evaluated;
            var staged = await editor.StageAsync(item, changes, null, null, ct).ConfigureAwait(false);
            if (!staged.IsSuccess) return Result<NativeEditForm>.Fail(staged.Error);
            items.Clear();
            relationshipEndpoints.Clear();
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
            if (GitHub)
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "GitHub authoring here supports existing issues only; creation remains in its native workflow.");
            var created = await editor.CreateSeedAsync(type, ct).ConfigureAwait(false);
            if (!created.IsSuccess) return Result<NativeEditForm>.Fail(created.Error);
            items.Clear();
            relationshipEndpoints.Clear();
            var ticket = Guid.NewGuid();
            items[ticket] = created.Value;
            return Form(ticket, created.Value, FieldValues.Empty);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<ImmutableArray<NativeOwnedSeed>>> ReadOwnedSeedsAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await editor.ReadOwnedSeedsAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeSeedPublication>> ObserveSeedAsync(Guid captureId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await editor.ObserveSeedAsync(captureId, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeSeedPublication>> ContinueSeedDeliveryAsync(Guid captureId, string exactDigest, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await editor.ContinueSeedDeliveryAsync(captureId, exactDigest, ct).ConfigureAwait(false); }
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
            if (GitHub && !GitHubScalar(field.Key))
                return Result<NativeEditForm>.Fail(ErrorCode.Unsupported, "Only existing GitHub title, Markdown body and discovered state are editable here: " + field.Key);
            if (definition is null || definition.ReadOnly || GitHub && !editor.Metadata.EditableFields.Contains(field.Key)
                || field.Value.Kind != FieldKind.Null && field.Value.Kind != definition.Definition.Kind
                || GitHub && field.Value.Kind == FieldKind.Null)
                return Result<NativeEditForm>.Fail(ErrorCode.InvalidInput, "An edited field is unavailable, read-only or has the wrong native type: " + field.Key);
        }
        var evaluation = evaluated.Value with { Fields = evaluated.Value.Fields.Select(field =>
            field with { Required = field.Required || gates.Value.Contains(field.Definition.Reference),
                ReadOnly = field.ReadOnly || GitHub && (!GitHubScalar(field.Definition.Reference)
                    || !editor.Metadata.EditableFields.Contains(field.Definition.Reference)) }).ToImmutableArray() };
        return Result<NativeEditForm>.Ok(new(ticket, item.Observation.Key, type.Reference, type.StateField,
            baseline.Fields.Overlay(draft), evaluation));
    }

    private static bool GitHubScalar(string reference)
        => reference is GitHubProvider.Title or GitHubProvider.Body or GitHubProvider.StateField;

    private static bool HasRelationships(NativeEditorItem item)
        => item.Capture is { } capture && !UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Links.IsEmpty;
}
