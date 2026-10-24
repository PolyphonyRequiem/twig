using System.Globalization;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Shows a mirrored work item as its durable local record composes it (Epic #1168): the values
/// the durable file holds for the item replace the mirror's, through <see cref="DurableFieldReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// Display only. The returned item is a copy that is never saved: the mirror is shared with write
/// paths, so composing there would copy local values into the rebuildable mirror. Reading never
/// writes either store, and the copy keeps the source's revision, dirty flag and pending notes.
/// </para>
/// <para>
/// Not opted in, the overlay returns its input unchanged and never resolves the provider. Opted
/// in, it shares the mirror projection's cached scope, so a process resolves the scope once.
/// An unresolved scope, an item with no durable identity in that scope, or a tombstoned record
/// shows the mirror unchanged; scope, transport, configuration and identity-map failures surface.
/// </para>
/// </remarks>
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

    /// <summary>True when items are composed from the durable store.</summary>
    internal bool IsEnabled => _reader is not null;

    /// <summary>
    /// Builds the overlay for <paramref name="mirror"/>; it composes only when the mirror projects
    /// durable identities, and then reuses the mirror's scope resolution.
    /// </summary>
    public static DurableFieldOverlay Create(IWorkItemRepository mirror, SqliteCacheStore store, IFieldDefinitionStore definitions)
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

    /// <summary>Returns <paramref name="item"/> as the durable store composes it.</summary>
    public async Task<WorkItem> ApplyAsync(WorkItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_reader is null || item.Id <= 0)
            return item;

        var scope = await _resolveScope!(ct);
        if (string.IsNullOrWhiteSpace(scope))
            return item;

        var localId = await _local!.FindAsync(
            new ExternalWorkItemIdentity(
                AdoMirrorIdentityProjection.ProviderKind, scope, item.Id.ToString(CultureInfo.InvariantCulture)),
            ct);
        if (localId is null)
            return item;

        var composed = await _reader.ReadAsync(localId.Value, ct);
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

            // A core value also replaces any copy the mirror keeps among the extended fields; a
            // value the core property cannot hold stays visible there rather than being dropped.
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

    /// <summary>The string a mirrored item would carry for <paramref name="value"/>.</summary>
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
