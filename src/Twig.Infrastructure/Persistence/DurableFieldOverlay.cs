using System.Globalization;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Composes durable local fields over a mirrored work item for display without mutating either
/// the disposable mirror or the durable record.
/// </summary>
internal sealed class DurableFieldOverlay
{
    private static readonly DurableFieldOverlay Disabled = new(null, null, null);

    private readonly SqliteLocalWorkItemRepository? _local;
    private readonly DurableFieldReader? _reader;
    private readonly Func<CancellationToken, Task<string?>>? _resolveScope;

    private DurableFieldOverlay(
        SqliteLocalWorkItemRepository? local,
        DurableFieldReader? reader,
        Func<CancellationToken, Task<string?>>? resolveScope)
    {
        _local = local;
        _reader = reader;
        _resolveScope = resolveScope;
    }

    /// <summary>Builds a display overlay only when the mirror has an ADO identity projection.</summary>
    public static DurableFieldOverlay Create(
        IWorkItemRepository mirror,
        SqliteCacheStore store,
        IFieldDefinitionStore definitions)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(definitions);
        if (mirror is not SqliteWorkItemRepository { DurableScope: { } scope })
            return Disabled;

        var local = new SqliteLocalWorkItemRepository(store);
        var reader = DurableFieldReader.Create(neutralStoreOptedIn: true, local, mirror, definitions, scope)!;
        return new DurableFieldOverlay(local, reader, scope);
    }

    /// <summary>Applies durable fields to one mirrored item for display.</summary>
    public async Task<WorkItem> ApplyAsync(WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var results = await ApplyAsync([item], ct).ConfigureAwait(false);
        return results[0];
    }

    /// <summary>Applies durable fields while resolving the ADO scope at most once for the batch.</summary>
    public async Task<IReadOnlyList<WorkItem>> ApplyAsync(
        IReadOnlyList<WorkItem> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_reader is null || !items.Any(item => item.Id > 0))
            return items;

        var scope = await _resolveScope!(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(scope))
            return items;

        var composed = new WorkItem[items.Count];
        for (var i = 0; i < items.Count; i++)
            composed[i] = await ApplyAsync(items[i], scope, ct).ConfigureAwait(false);
        return composed;
    }

    private async Task<WorkItem> ApplyAsync(WorkItem item, string scope, CancellationToken ct)
    {
        if (item.Id <= 0)
            return item;

        var localId = await _local!.FindAsync(
            new ExternalWorkItemIdentity(
                AdoMirrorIdentityProjection.ProviderKind,
                scope,
                item.Id.ToString(CultureInfo.InvariantCulture)),
            ct).ConfigureAwait(false);
        if (localId is null)
            return item;

        var composed = await _reader!.ReadAsync(localId.Value, scope, ct).ConfigureAwait(false);
        if (composed is null || composed.Local.IsDeleted || composed.Local.Fields.Count == 0)
            return item;

        return Compose(item, composed.Local.Fields.Keys.Select(key => (key, composed.Fields[key])));
    }

    private static WorkItem Compose(WorkItem source, IEnumerable<(string ReferenceName, FieldValue Value)> overrides)
    {
        var title = source.Title;
        var state = source.State;
        var type = source.Type;
        var assignedTo = source.AssignedTo;
        var assignedToUniqueName = source.AssignedToUniqueName;
        var iterationPath = source.IterationPath;
        var areaPath = source.AreaPath;
        var fields = new Dictionary<string, string?>(source.Fields, StringComparer.OrdinalIgnoreCase);

        foreach (var (referenceName, value) in overrides)
        {
            var text = Render(value);
            var shownAsCore = true;
            switch (referenceName.ToUpperInvariant())
            {
                case "SYSTEM.TITLE":
                    title = text ?? string.Empty;
                    break;
                case "SYSTEM.STATE":
                    state = text ?? string.Empty;
                    break;
                case "SYSTEM.ASSIGNEDTO":
                    (assignedTo, assignedToUniqueName) = value.Value is IdentityValue identity
                        ? (identity.DisplayName ?? identity.NativeId, identity.NativeId)
                        : (text, null);
                    break;
                case "SYSTEM.WORKITEMTYPE":
                    shownAsCore = TryParse(text, WorkItemType.Parse, ref type);
                    break;
                case "SYSTEM.ITERATIONPATH":
                    shownAsCore = TryParse(text, IterationPath.Parse, ref iterationPath);
                    break;
                case "SYSTEM.AREAPATH":
                    shownAsCore = TryParse(text, AreaPath.Parse, ref areaPath);
                    break;
                default:
                    shownAsCore = false;
                    break;
            }

            if (!shownAsCore || fields.ContainsKey(referenceName))
                fields[referenceName] = text;
        }

        var copy = new WorkItem
        {
            Id = source.Id,
            Type = type,
            Title = title,
            State = state,
            AssignedTo = assignedTo,
            AssignedToUniqueName = assignedToUniqueName,
            IterationPath = iterationPath,
            AreaPath = areaPath,
            ParentId = source.ParentId,
            IsSeed = source.IsSeed,
            SeedCreatedAt = source.SeedCreatedAt,
            StagedIdentity = source.StagedIdentity,
            LastSyncedAt = source.LastSyncedAt,
        };
        if (source.Revision > 0)
            copy.MarkSynced(source.Revision);
        copy.ImportFields(fields);
        foreach (var note in source.PendingNotes)
            copy.AddPendingNote(note);
        if (source.IsDirty)
            copy.SetDirty();
        return copy;
    }

    private static bool TryParse<T>(string? text, Func<string, Result<T>> parse, ref T target)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            target = default!;
            return true;
        }

        var parsed = parse(text);
        if (!parsed.IsSuccess)
            return false;
        target = parsed.Value;
        return true;
    }

    internal static string? Render(FieldValue value) => value.Value switch
    {
        TextValue text => text.Value,
        RichTextValue rich => rich.Content,
        IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        BooleanValue flag => flag.Value ? "True" : "False",
        DateTimeValue instant => instant.Value.ToString("O", CultureInfo.InvariantCulture),
        IdentityValue identity => identity.DisplayName ?? identity.NativeId,
        StringListValue list => string.Join("; ", list.Values),
        RawValue raw => raw.Raw,
        NullValue => null,
        _ => throw new InvalidOperationException("A field value carries no case."),
    };
}
