using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;

[assembly: InternalsVisibleTo("Bench.Infrastructure")]

namespace Twig.Infrastructure.Services;

/// <summary>Negative evidence for historical display, not admission for a provider operation.</summary>
internal sealed class NativeHistoricalEvidence(string directory)
{
    private static readonly AsyncLocal<ReadScope?> ActiveRead = new();

    internal static IDisposable ObserveFailures(Connection connection, Func<Error, Error> record)
    {
        var scope = new ReadScope(connection, record, ActiveRead.Value);
        ActiveRead.Value = scope;
        return scope;
    }

    internal static Error ReportReadFailure(Connection connection, Error error, Func<Error, Error>? fallback)
        => ActiveRead.Value is { Active: true } scope && scope.Connection == connection
            ? scope.Record(error) : fallback?.Invoke(error) ?? error;

    private sealed class ReadScope(Connection connection, Func<Error, Error> record, ReadScope? previous) : IDisposable
    {
        internal bool Active { get; private set; } = true;
        internal Connection Connection => connection;
        internal Error Record(Error error) => record(error);
        public void Dispose()
        {
            Active = false;
            ActiveRead.Value = previous;
        }
    }

    internal static IEnumerable<string> UnavailableIds(Error error)
    {
        const string prefix = "item-unavailable:";
        return error.Evidence.Where(entry => entry.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => entry[prefix.Length..])
            .Where(id => id.Length is > 0 and <= 256 && id == id.Trim() && !id.Any(char.IsControl));
    }

    internal static NativeHistoricalEvidence ForLocalUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig", "DaemonData", "native-evidence"));

    internal static string NativeStamp(NativeSourceStamp source)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { source.PrincipalId, source.AuthorityStamp }, NativeHistoricalJson.Default.StringArray)));

    internal static string BindingKey(Connection connection, Guid storeInstance, string nativeStamp)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(connection.Provider.Value);
            writer.WriteStringValue(connection.ResourceScope);
            writer.WriteStringValue(connection.Destination);
            writer.WriteStringValue(connection.IdentityRef);
            writer.WriteStringValue(storeInstance);
            writer.WriteStringValue(nativeStamp);
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    internal Result<ImmutableHashSet<ItemKey>> Read(NativeSourceStamp source)
    {
        try
        {
            var path = PathFor(source);
            using var denial = Open(path + ".denied");
            if (denial is not null)
                return Result<ImmutableHashSet<ItemKey>>.Fail(ErrorCode.PolicyViolation, "Historical output is withheld for this source.");
            using var legacy = Open(Path.ChangeExtension(path, ".check"));
            if (legacy is not null)
                return Result<ImmutableHashSet<ItemKey>>.Fail(ErrorCode.StorageUnavailable,
                    "Legacy native cache cannot be reused. Initialize a fresh Twig workspace.");
            return Result<ImmutableHashSet<ItemKey>>.Ok(ReadWithdrawals(path).Select(id =>
                ItemKey.Remote(source.Connection.Provider, source.Connection.ResourceScope, id)).ToImmutableHashSet());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Result<ImmutableHashSet<ItemKey>>.Fail(ErrorCode.StorageUnavailable, "Historical source evidence is unavailable; no cached content was released.");
        }
    }

    private string PathFor(NativeSourceStamp source)
        => Path.Combine(directory, BindingKey(source.Connection, source.StoreInstance, NativeStamp(source)) + ".json");

    internal static ImmutableHashSet<string> ReadWithdrawals(string path)
    {
        using var file = Open(path + ".withdrawn");
        if (file is null) return ImmutableHashSet<string>.Empty;
        if (file.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("Invalid native withdrawal evidence.");
        var ids = JsonSerializer.Deserialize(file, NativeHistoricalJson.Default.StringArray);
        if (ids is null || ids.Any(id => string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Any(char.IsControl)))
            throw new InvalidDataException("Invalid native withdrawal identity.");
        return ids.ToImmutableHashSet(StringComparer.Ordinal);
    }

    internal static void SaveWithdrawals(string path, ImmutableHashSet<string> ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var target = path + ".withdrawn";
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, ids.Order(StringComparer.Ordinal).ToArray(), NativeHistoricalJson.Default.StringArray);
                if (file.Length > 1024 * 1024) throw new IOException("Native withdrawal evidence exceeds its bound.");
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void SaveSourceDenial(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var denial = new FileStream(path + ".denied", FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
        denial.WriteByte(1);
        denial.Flush(flushToDisk: true);
    }

    internal Error RecordReadFailure(NativeSourceStamp source, Error error)
    {
        var path = PathFor(source);
        var ids = UnavailableIds(error).ToArray();
        try
        {
            if (error.Evidence.Contains("http:401")) SaveSourceDenial(path);
            else if (ids.Length != 0) RecordWithdrawals(path, ids);
            return error;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return new(ErrorCode.StorageUnavailable, "Could not retain the provider refusal; no work-item data was released.");
        }
    }

    internal Result<bool> RestoreRetainedItem(NativeSourceStamp source, WorkItem item)
    {
        if (item.Revision is null || !item.Key.BelongsTo(source.Connection))
            return Result<bool>.Ok(false);
        try
        {
            var path = PathFor(source);
            var before = ReadWithdrawals(path);
            var after = before.Remove(item.Key.NativeId!);
            if (before.Count == after.Count) return Result<bool>.Ok(false);
            SaveWithdrawals(path, after);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Could not restore the retained item after refreshing it.");
        }
    }

    internal static bool RecordWithdrawals(string path, IEnumerable<string> ids)
    {
        try
        {
            var before = ReadWithdrawals(path);
            var after = before.Union(ids);
            if (before.Count == after.Count) return false;
            SaveWithdrawals(path, after);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            SaveSourceDenial(path);
            throw;
        }
    }

    private static FileStream? Open(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

internal sealed class NativeHistoricalProjection(StoreSnapshot snapshot, ImmutableHashSet<ItemKey> withdrawn)
{
    private readonly ImmutableHashSet<ItemKey> withheld = withdrawn.Union(snapshot.Mappings
        .Where(mapping => withdrawn.Contains(mapping.Remote)).Select(mapping => mapping.Local));

    internal bool Withholds(ItemKey key) => withheld.Contains(key);
    internal bool AllowsSelector(Selector selector) => selector.Item is null || !Withholds(selector.Item);

    internal StoreSnapshot Snapshot()
    {
        if (withheld.IsEmpty) return snapshot;
        bool WithheldEdge(Relationship edge) => Withholds(edge.From) || Withholds(edge.To);
        var incomplete = snapshot.Relationships.Where(edge => !edge.Deleted && WithheldEdge(edge))
            .SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet();
        foreach (var mapping in snapshot.Mappings)
            if (incomplete.Contains(mapping.Local)) incomplete.Add(mapping.Remote);
        return new(snapshot.Version,
            snapshot.Items.Where(item => !Withholds(item.Key)).Select(item => incomplete.Contains(item.Key) && item.RelationshipsComplete
                ? new WorkItem(item.Key, item.Type, item.Fields, item.Revision, false, item.Resources, item.ResourcesComplete)
                : item),
            snapshot.Relationships.Where(edge => !WithheldEdge(edge)),
            snapshot.Drafts.Where(draft => !Withholds(draft.Key) && (draft.Parent is null || !Withholds(draft.Parent))
                && draft.Links.All(link => !Withholds(link.Other))),
            mappings: snapshot.Mappings.Where(mapping => !Withholds(mapping.Remote)),
            benches: snapshot.Benches);
    }

    internal ImmutableArray<PlanningMembership> Memberships(ImmutableArray<PlanningMembership> memberships)
        => withheld.IsEmpty ? memberships : memberships.Select(membership =>
            membership with { Items = membership.Items.Where(key => !Withholds(key)).ToImmutableArray() }).ToImmutableArray();
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class NativeHistoricalJson : JsonSerializerContext;
