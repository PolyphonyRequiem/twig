using System.Globalization;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Infrastructure.Persistence;

/// <summary>Where the remote half of a <see cref="ComposedWorkItemFields"/> came from.</summary>
internal enum RemoteFieldSource
{
    /// <summary>
    /// No remote values were read: the item is tombstoned, has no ADO identity in the mirror's
    /// resource scope, or the scope could not be resolved.
    /// </summary>
    NotRead,

    /// <summary>The item maps to ADO, but the mirror holds no row for it (evicted or not yet rebuilt).</summary>
    NotMirrored,

    /// <summary>Remote values came from a clean mirror row.</summary>
    Mirrored,

    /// <summary>
    /// Remote values came from a legacy dirty mirror row, so they may include unpublished edits
    /// staged by the pre-#1168 write path. Surfaced rather than hidden until a cutover moves them.
    /// </summary>
    MirroredWithLegacyEdits,
}

/// <summary>
/// A local work item's fields as a reader sees them: remote values from the mirror, overlaid by
/// the durable local values.
/// </summary>
/// <param name="Local">The durable record; its revision is the local compare-and-swap counter.</param>
/// <param name="Fields">The composed values. A local value, including an explicit clear, wins.</param>
/// <param name="Remote">Where the remote values came from.</param>
internal sealed record ComposedWorkItemFields(LocalWorkItemRecord Local, FieldValues Fields, RemoteFieldSource Remote);

/// <summary>
/// Reads a local work item's fields under the split layout (Epic #1168): durable rows hold only
/// local-owned or locally edited values, remote values are read from the rebuildable mirror and
/// mapped to <see cref="FieldValues"/> at read time.
/// </summary>
/// <remarks>
/// <para>
/// Reading never writes. Remote values are never copied into the durable file, so a mirror
/// rebuild cannot strand a stale copy and a sync never moves the local revision.
/// </para>
/// <para>
/// The mirror is keyed by ADO's work-item number alone, so a row is read only for the item's
/// ADO identity whose resource scope equals the mirror's resolved scope. An unresolved scope
/// reads no remote values; a row from another organization is never composed.
/// </para>
/// <para>
/// The mirror keeps a lossy string projection, so remote values are not given a guessed type:
/// each becomes a <see cref="RawValue"/> carrying ADO's declared data type, or
/// <see cref="UndeclaredDataType"/> when no definition is cached. The one exception is the
/// assignee, which becomes an <see cref="IdentityValue"/> when ADO's unique name was observed.
/// </para>
/// </remarks>
internal sealed class DurableFieldReader
{
    /// <summary>Marks a remote value whose ADO data type is not cached locally.</summary>
    public const string UndeclaredDataType = "twig.undeclared";

    private const string AssignedTo = "System.AssignedTo";

    private static readonly string[] CoreFieldRefs =
    [
        "System.WorkItemType", "System.Title", "System.State",
        AssignedTo, "System.IterationPath", "System.AreaPath",
    ];

    private readonly SqliteLocalWorkItemRepository _local;
    private readonly IWorkItemRepository _mirror;
    private readonly IFieldDefinitionStore _definitions;
    private readonly Func<CancellationToken, Task<string?>> _resolveMirrorScope;

    private DurableFieldReader(
        SqliteLocalWorkItemRepository local,
        IWorkItemRepository mirror,
        IFieldDefinitionStore definitions,
        Func<CancellationToken, Task<string?>> resolveMirrorScope)
    {
        _local = local;
        _mirror = mirror;
        _definitions = definitions;
        _resolveMirrorScope = resolveMirrorScope;
    }

    /// <summary>
    /// Returns a reader when the neutral store is opted in and a mirror scope source exists;
    /// otherwise <see langword="null"/>, and callers keep the legacy read path.
    /// </summary>
    public static DurableFieldReader? Create(
        bool neutralStoreOptedIn,
        SqliteLocalWorkItemRepository local,
        IWorkItemRepository mirror,
        IFieldDefinitionStore definitions,
        Func<CancellationToken, Task<string?>>? resolveMirrorScope)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(definitions);
        return neutralStoreOptedIn && resolveMirrorScope is not null
            ? new DurableFieldReader(local, mirror, definitions, resolveMirrorScope)
            : null;
    }

    /// <summary>
    /// Composes the item's fields; <see langword="null"/> when no local item has
    /// <paramref name="id"/>. Throws when the identity map is ambiguous or corrupt.
    /// </summary>
    public async Task<ComposedWorkItemFields?> ReadAsync(LocalWorkItemId id, CancellationToken ct = default)
    {
        var local = await _local.GetAsync(id, ct).ConfigureAwait(false);
        if (local is null || local.IsDeleted)
            return await ComposeAsync(local, scope: null, ct).ConfigureAwait(false);

        var scope = await _resolveMirrorScope(ct).ConfigureAwait(false);
        return await ComposeAsync(local, scope, ct).ConfigureAwait(false);
    }

    internal async Task<ComposedWorkItemFields?> ReadAsync(
        LocalWorkItemId id, string? scope, CancellationToken ct = default)
    {
        var local = await _local.GetAsync(id, ct);
        return await ComposeAsync(local, scope, ct).ConfigureAwait(false);
    }

    private async Task<ComposedWorkItemFields?> ComposeAsync(
        LocalWorkItemRecord? local, string? scope, CancellationToken ct)
    {
        if (local is null)
            return null;
        if (local.IsDeleted)
            return new ComposedWorkItemFields(local, local.Fields, RemoteFieldSource.NotRead);

        var nativeId = string.IsNullOrWhiteSpace(scope)
            ? null
            : await FindMirroredNativeIdAsync(id, scope, ct);
        if (nativeId is null)
            return new ComposedWorkItemFields(local, local.Fields, RemoteFieldSource.NotRead);

        var row = await _mirror.GetByIdAsync(nativeId.Value, ct);
        if (row is null)
            return new ComposedWorkItemFields(local, local.Fields, RemoteFieldSource.NotMirrored);

        var composed = await MapRemoteAsync(row, ct);
        foreach (var (referenceName, value) in local.Fields)
            composed = composed.Set(referenceName, value);

        return new ComposedWorkItemFields(
            local,
            composed,
            row.IsDirty ? RemoteFieldSource.MirroredWithLegacyEdits : RemoteFieldSource.Mirrored);
    }

    private async Task<int?> FindMirroredNativeIdAsync(LocalWorkItemId id, string scope, CancellationToken ct)
    {
        var matches = (await _local.GetExternalIdentitiesAsync(id, ct))
            .Where(identity => identity.ProviderKind == AdoMirrorIdentityProjection.ProviderKind
                && identity.ResourceScope == scope)
            .ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Local work item {id} maps to {matches.Count} ADO identities in one resource scope.");

        var native = matches[0].NativeId;
        if (!int.TryParse(native, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0
            || number.ToString(CultureInfo.InvariantCulture) != native)
            throw new InvalidOperationException(
                $"Local work item {id} maps to ADO native id '{native}', which is not a canonical work-item number.");
        return number;
    }

    private async Task<FieldValues> MapRemoteAsync(WorkItem row, CancellationToken ct)
    {
        var declared = (await _definitions.GetAllAsync(ct))
            .GroupBy(definition => definition.ReferenceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().DataType, StringComparer.OrdinalIgnoreCase);

        FieldValue Raw(string referenceName, string raw) => new RawValue(
            raw,
            declared.TryGetValue(referenceName, out var dataType) && !string.IsNullOrWhiteSpace(dataType)
                ? dataType
                : UndeclaredDataType);

        var values = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        void Put(string referenceName, string? raw)
        {
            if (!string.IsNullOrEmpty(raw))
                values[referenceName] = Raw(referenceName, raw);
        }

        Put("System.WorkItemType", row.Type.Value);
        Put("System.Title", row.Title);
        Put("System.State", row.State);
        Put("System.IterationPath", row.IterationPath.Value);
        Put("System.AreaPath", row.AreaPath.Value);
        if (!string.IsNullOrWhiteSpace(row.AssignedToUniqueName))
            values[AssignedTo] = new IdentityValue(row.AssignedToUniqueName, row.AssignedTo);
        else
            Put(AssignedTo, row.AssignedTo);

        // Extended fields follow the core columns, as the legacy write path does for an authored
        // assignee; a core reference is keyed by its canonical spelling so it cannot appear twice.
        foreach (var (key, raw) in row.Fields)
        {
            var referenceName = CoreFieldRefs.FirstOrDefault(core => string.Equals(core, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (raw is null)
                values[referenceName] = NullValue.Instance;
            else
                values[referenceName] = Raw(referenceName, raw);
        }

        return FieldValues.Create(values);
    }
}
