using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Globalization;
using System.Text;
using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Twig.Store.Sqlite;

namespace Twig.Server;

internal sealed record DisplayPartition(Guid Owner, Guid Store, Guid Bench, Guid Id, NativeSourceStamp? Origin, Connection Connection);
internal sealed record DisplayRow(string Kind, string Item, string? Type, string? Field, FieldValueDto? Value);
internal sealed record ReleasedPartitions(Guid[] Partitions);
[JsonSerializable(typeof(ReleasedPartitions))]
[JsonSerializable(typeof(DisplayRow))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DisplayJson : JsonSerializerContext;

internal sealed partial class ReleasedBenchDisplay
{
    internal const int MaximumPage = 512 * 1024;
    private readonly ReleasedBenchStore store;
    private readonly object publication = new();
    private readonly object release = new();
    private readonly Dictionary<(Guid Owner, Guid Store, Guid Bench, Guid Partition), Entry> entries = [];
    private readonly Dictionary<(Guid Owner, Guid Store, Guid Bench), Guid> generations = [];
    private readonly Dictionary<DisplayPartition, long> partitionRequests = [];
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim responses = new(16, 16);
    internal Func<CancellationToken, Task>? BeforeEnqueue { get; set; }
    internal Action? AfterStage { get; set; }
    private sealed record Entry(DisplayPartition Scope, Guid Generation, int Pages, GrantWitness? Grant);

    internal ReleasedBenchDisplay(string path, TimeProvider? clock = null, ViewSessions? sessions = null)
    {
        store = new(path);
        this.clock = clock ?? TimeProvider.System;
        withdrawals = store.Withdrawals();
        watermark = withdrawals.Values.DefaultIfEmpty().Max();
        this.sessions = sessions;
        pending = store.Pending();
        planPending = store.PendingPlans();
        heads = store.AppliedHeads();
    }

    internal void PublishLocal(DisplayPartition scope, NativeSourceObservation observation, ImmutableArray<ItemKey> admitted)
    {
        if (!TryPublishLocal(scope, observation, admitted)) throw Changed();
    }

    private bool TryPublishLocal(DisplayPartition scope, NativeSourceObservation observation, ImmutableArray<ItemKey> admitted)
        => PrepareLocal(scope, observation, admitted)();

    private Func<bool> PrepareLocal(DisplayPartition scope, NativeSourceObservation observation, ImmutableArray<ItemKey> admitted, string? hash = null)
    {
        if (scope.Origin != observation.Source || scope.Connection != observation.Source.Connection)
            throw new InvalidDataException("The local observation is not from the authorized origin.");
        var input = (observation.Snapshot.Version, hash ?? WorkspacePublication.Hash(Persistence.Serialize(observation.Snapshot)));
        long request;
        lock (release)
        {
            if (Dirty(scope) || heads.TryGetValue(observation.Source.StoreInstance, out var head)
                && head != input) return () => false;
            request = Begin(scope);
        }
        var members = admitted.ToHashSet();
        var intent = observation.Snapshot.RetainedItems.Where(item => members.Contains(item.Key)).ToArray();
        if (intent.Length != members.Count) throw new InvalidDataException("The local projection is incomplete.");
        var rows = intent.SelectMany(item => item switch
        {
            LocalEdits edit when edit.Key.BelongsTo(scope.Connection)
                => Rows("edit", edit.Key.NativeId!, null, edit.Changes),
            Seed seed => Rows("seed", seed.Key.LocalId.ToString("D"), seed.Type, seed.Fields),
            _ => throw new InvalidDataException("Local intent does not belong to the released partition.")
        });
        var pages = Pages(rows);
        return () => Publish(scope, pages, request, input: input);
    }

    private static IEnumerable<DisplayRow> Rows(string kind, string item, string? type, FieldValues fields)
    {
        if (fields.Count == 0) yield return new(kind, item, type, null, null);
        foreach (var (name, value) in fields)
            yield return new(kind, item, type, name, ViewMapping.Value(value));
    }

    private static List<byte[]> Pages(IEnumerable<DisplayRow> rows)
    {
        var pages = new List<byte[]>();
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)'[');
        foreach (var row in rows)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(row, DisplayJson.Default.DisplayRow);
            if (bytes.Length > MaximumPage - 4098) throw new InvalidDataException("A released field exceeds the page bound.");
            if (buffer.Length + bytes.Length + 2 > MaximumPage - 4096 && buffer.Length > 1)
            {
                buffer.WriteByte((byte)']');
                pages.Add(buffer.ToArray());
                buffer.SetLength(0);
                buffer.WriteByte((byte)'[');
            }
            if (buffer.Length > 1) buffer.WriteByte((byte)',');
            buffer.Write(bytes);
        }
        buffer.WriteByte((byte)']');
        pages.Add(buffer.ToArray());
        return pages;
    }

    private long Begin(DisplayPartition scope)
    {
        RequireOpen();
        return partitionRequests[scope] = checked(++sequence);
    }

    internal void Withhold(DisplayPartition scope)
    {
        lock (release)
        {
            _ = Begin(scope);
            var key = (scope.Owner, scope.Store, scope.Bench, scope.Id);
            if (entries.TryGetValue(key, out var entry) && entry.Scope == scope)
            {
                entries.Remove(key);
                generations[(scope.Owner, scope.Store, scope.Bench)] = Guid.NewGuid();
                NotifyAuthorization();
            }
        }
    }

    private bool Publish(DisplayPartition scope, List<byte[]> pages, long request, GrantWitness? grant = null,
        (long Version, string Hash)? input = null)
    {
        var key = (scope.Owner, scope.Store, scope.Bench, scope.Id);
        bool Eligible() => !Dirty(scope) && partitionRequests[scope] == request && (grant is null || Current(grant))
            && (scope.Origin is null || !heads.TryGetValue(scope.Origin.StoreInstance, out var head) || head == input);
        lock (publication)
        {
            Entry? old;
            lock (release)
            {
                RequireOpen();
                if (!Eligible()) return false;
                entries.TryGetValue(key, out old);
                if (old is not null && (old.Scope != scope || old.Grant?.Item != grant?.Item))
                    throw new InvalidDataException("A partition cannot change origin, grant scope or content kind.");
            }
            var entry = new Entry(scope, Guid.NewGuid(), pages.Count, grant);
            if (!store.Stage($"{scope.Owner:N}/{scope.Store:N}/{scope.Bench:N}/{scope.Id:N}",
                    old?.Generation, entry.Generation, pages, grant)) return false;
            AfterStage?.Invoke();
            lock (release)
            {
                RequireOpen();
                if (!Eligible()) return false;
                entries[key] = entry;
                generations[(scope.Owner, scope.Store, scope.Bench)] = Guid.NewGuid();
                NotifyAuthorization();
                return true;
            }
        }
    }

    internal sealed record Frame(byte[] Body, Guid Generation, DisplayPartition Scope, string? Next, Guid AuthEpoch,
        DateTimeOffset Expires, long RemainingMilliseconds, long Watermark, long Cursor, string? ItemRevision = null,
        NativeSourceStamp? InputSource = null);

    internal async Task Read(ViewSessions sessions, Guid view, string bearer, Guid bench, Guid partition, string? cursor,
        string? expectedGeneration, Func<Frame, Task> send, CancellationToken ct)
    {
        var start = sessions.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                var current = Select(owner, bench, partition);
                return (current.Entry, current.Generation, owner.ProfileGeneration);
            }
        });
        if (expectedGeneration is not null && expectedGeneration != start.Generation.ToString("D")) throw Changed();
        var ordinal = 0;
        string Token(int index) => $"{index.ToString(CultureInfo.InvariantCulture)}." + Convert.ToHexString(
            HMACSHA256.HashData(cursorKey, Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"{view:N}/{start.ProfileGeneration:N}/{start.Generation:N}/{start.Entry.Generation:N}/{index}"))));
        if (cursor is not null)
        {
            if (cursor.Length > 80 || cursor.Split('.') is not [var number, _]
                || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
                || ordinal <= 0 || ordinal >= start.Entry.Pages || cursor != Token(ordinal)) throw Changed();
        }
        var next = ordinal + 1 < start.Entry.Pages ? Token(ordinal + 1) : null;
        byte[] body;
        try { body = store.Read(start.Entry.Generation, ordinal) ?? throw Changed(); }
        catch (SqliteException error)
        {
            throw new HttpFailure(503, error.SqliteErrorCode is 5 or 6 ? "Busy" : "RefreshRequired",
                "The released page is unavailable; no substitute data was returned.");
        }
        if (BeforeEnqueue is { } barrier) await barrier(ct);
        ct.ThrowIfCancellationRequested();
        var outbox = Channel.CreateBounded<Frame>(1);
        sessions.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                var current = Select(owner, bench, partition);
                if (current.Entry != start.Entry || current.Generation != start.Generation
                    || owner.ProfileGeneration != start.ProfileGeneration) throw Changed();
                var now = clock.GetUtcNow();
                var remaining = owner.Expires - now;
                if (current.Entry.Grant is { } grant) remaining = TimeSpan.FromTicks(Math.Min(remaining.Ticks, Remaining(grant).Ticks));
                if (remaining <= TimeSpan.Zero) throw Denied();
                return Enqueue(outbox, new(body, start.Generation, start.Entry.Scope, next,
                    owner.ProfileGeneration, now + remaining, (long)remaining.TotalMilliseconds, watermark, authCursor,
                    current.Entry.Grant?.Revision, current.Entry.Grant?.Source));
            }
        });
        try { await send(await outbox.Reader.ReadAsync(ct)); }
        finally { responses.Release(); }
    }

    private (Entry Entry, Guid Generation) Select(ViewSession owner, Guid bench, Guid partition)
    {
        RequireOpen();
        var entry = entries.GetValueOrDefault((owner.ProfileId, owner.CatalogId, bench, partition))
            ?? throw new HttpFailure(409, "RefreshRequired", "No eligible release.");
        if (Dirty(entry.Scope)) throw new HttpFailure(409, "RefreshRequired", "The Bench has unsettled changes.");
        if (entry.Grant is { } grant && (Withdrawn(grant) || Remaining(grant) <= TimeSpan.Zero)) throw Denied();
        return (entry, generations[(owner.ProfileId, owner.CatalogId, bench)]);
    }

    private bool Enqueue<T>(Channel<T> outbox, T value)
    {
        if (!responses.Wait(0)) throw new HttpFailure(503, "Busy", "The bounded display handoff is full.");
        if (outbox.Writer.TryWrite(value)) return true;
        responses.Release();
        throw new HttpFailure(503, "Busy", "The display handoff was not accepted.");
    }

    internal async Task ReadPartitions(ViewSessions views, Guid view, string bearer, Guid bench,
        Func<ReleasedPartitions,Task> send, CancellationToken ct)
    {
        var outbox = Channel.CreateBounded<ReleasedPartitions>(1);
        views.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                RequireOpen();
                var ids = entries.Values.Where(e => e.Scope.Owner == owner.ProfileId && e.Scope.Store == owner.CatalogId
                    && e.Scope.Bench == bench && !Dirty(e.Scope)
                    && (e.Grant is null || !Withdrawn(e.Grant) && Remaining(e.Grant) > TimeSpan.Zero))
                    .Select(e => e.Scope.Id).Take(1025).ToArray();
                if (ids.Length is 0 or > 1024)
                    throw new HttpFailure(409, "RefreshRequired", "No bounded eligible partition inventory.");
                return Enqueue(outbox, new(ids));
            }
        });
        try { await send(await outbox.Reader.ReadAsync(ct)); }
        finally { responses.Release(); }
    }

    private static HttpFailure Changed() => new(409, "ViewChanged", "Discard the previous pages before reading a new generation.");
}
