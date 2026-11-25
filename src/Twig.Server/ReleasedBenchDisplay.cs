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
internal sealed record QualifiedMember(string Connection, string Item);
internal sealed record BenchMembership(string State, int? ExactTotal, QualifiedMember[] Items);
internal sealed record ReleasedPartitions(Guid[] Partitions, Guid[] LocalOrigins, BenchMembership Membership, Guid Generation,
    long RemainingMilliseconds, DateTimeOffset Expires);
[JsonSerializable(typeof(ReleasedPartitions))]
[JsonSerializable(typeof(DisplayRow))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DisplayJson : JsonSerializerContext;

internal sealed partial class ReleasedBenchDisplay
{
    internal const int MaximumPage = 512 * 1024;
    private readonly ReleasedBenchStore store;
    private readonly UserBenchStore? definitions;
    private readonly object publication = new();
    private readonly object release = new();
    private readonly Dictionary<(Guid Owner, Guid Store, Guid Bench, Guid Partition), Entry> entries = [];
    private readonly Dictionary<(Guid Owner, Guid Store, Guid Bench), Guid> generations = [];
    private readonly Dictionary<DisplayPartition, long> partitionRequests = [];
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim responses = new(16, 16);
    internal Func<CancellationToken, Task>? BeforeEnqueue { get; set; }
    internal Action? AfterStage { get; set; }
    private sealed record Entry(DisplayPartition Scope, Guid Generation, int Pages, GrantWitness? Grant, long RuleRevision);

    internal ReleasedBenchDisplay(string path, TimeProvider? clock = null, ViewSessions? sessions = null, UserBenchStore? definitions = null)
    {
        store = new(path);
        this.definitions = definitions;
        this.clock = clock ?? TimeProvider.System;
        withdrawals = store.Withdrawals();
        watermark = withdrawals.Values.DefaultIfEmpty().Max();
        this.sessions = sessions;
        pending = store.Pending();
        heads = store.AppliedHeads();
    }

    internal void PublishLocal(DisplayPartition scope, NativeSourceObservation observation, ImmutableArray<ItemKey> admitted)
    {
        if (!TryPublishLocal(scope, observation, admitted)) throw Changed();
    }

    private bool TryPublishLocal(DisplayPartition scope, NativeSourceObservation observation, ImmutableArray<ItemKey> admitted)
        => PrepareLocal(scope, observation, admitted)();

    private Func<bool> PrepareLocal(DisplayPartition scope, NativeSourceObservation observation, ImmutableArray<ItemKey> admitted, string? hash = null,
        long ruleRevision = 0)
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
        return () => Publish(scope, pages, request, input: input, ruleRevision: ruleRevision);
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
        (long Version, string Hash)? input = null, long ruleRevision = 0)
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
            var entry = new Entry(scope, Guid.NewGuid(), pages.Count, grant, ruleRevision);
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
        _ = sessions.WithDisplayOwner(view, bearer, owner => owner.CatalogId);
        var catalog = definitions?.InspectRevision();
        var start = sessions.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                var current = Select(owner, bench, partition, catalog);
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
        catalog = definitions?.InspectRevision();
        sessions.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                var current = Select(owner, bench, partition, catalog);
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

    private (Entry Entry, Guid Generation) Select(ViewSession owner, Guid bench, Guid partition, (Guid Instance, long Revision)? catalog)
    {
        RequireOpen();
        var entry = entries.GetValueOrDefault((owner.ProfileId, owner.CatalogId, bench, partition))
            ?? throw new HttpFailure(409, "RefreshRequired", "No eligible release.");
        if (entry.RuleRevision > 0 && catalog != (entry.Scope.Store, entry.RuleRevision)) throw Changed();
        if (Dirty(entry.Scope)) throw new HttpFailure(409, "RefreshRequired", "The Bench has unsettled changes.");
        if (entry.Grant is { } grant && (Withdrawn(grant) || Remaining(grant) <= TimeSpan.Zero)) throw Denied();
        if (entry.Grant?.ScanRequest is { } request && (!scans.TryGetValue((owner.CatalogId, bench, entry.Scope.Connection), out var scan)
            || scan.Request != request || !ScanEligible(scan))) throw Changed();
        return (entry, generations[(owner.ProfileId, owner.CatalogId, bench)]);
    }

    private bool Enqueue<T>(Channel<T> outbox, T value)
    {
        if (!responses.Wait(0)) throw new HttpFailure(503, "Busy", "The bounded display handoff is full.");
        if (outbox.Writer.TryWrite(value)) return true;
        responses.Release();
        throw new HttpFailure(503, "Busy", "The display handoff was not accepted.");
    }

    private ReleasedPartitions Inventory(Guid owner, UserBenchSnapshot catalog, Twig.Core.Bench bench, DateTimeOffset? ownerExpiry = null)
    {
        RequireOpen();
        var storeId = catalog.InstanceId;
        var scoped = entries.Values.Where(e => e.Scope.Owner == owner && e.Scope.Store == storeId
            && e.Scope.Bench == bench.Id && !Dirty(e.Scope)).ToArray();
        var pins = bench.Selectors.Where(s => s.Kind == SelectorKind.Pin && s.Item is { IsLocal: false }
            && s.Connection is not null).DistinctBy(s => Fingerprint(s.Connection!, s.Item))
            .ToDictionary(s => Fingerprint(s.Connection!, s.Item));
        var covered = new HashSet<Connection>();
        var incomplete = false;
        var now = clock.GetUtcNow();
        var remaining = Math.Min(60_000d, ((ownerExpiry ?? now.AddMinutes(1)) - now).TotalMilliseconds);
        var candidate = pins.ToDictionary(p => p.Key, p => (Connection: p.Value.Connection!, Item: p.Value.Item!));
        foreach (var c in bench.Connections)
        {
            if (!scans.TryGetValue((storeId, bench.Id, c), out var scan) || !scan.Owners.Contains(owner)
                || scan.Catalog != catalog.Revision || !ScanEligible(scan)) continue;
            if (scan.State == "Known") covered.Add(c);
            else incomplete = true;
            remaining = Math.Min(remaining, Math.Min((scan.Until - now).TotalMilliseconds,
                (TimeSpan.FromSeconds(60) - clock.GetElapsedTime(scan.Started)).TotalMilliseconds));
            foreach (var key in scan.Keys) candidate[Fingerprint(c, key)] = (c, key);
        }
        var items = new List<QualifiedMember>();
        var partitions = new List<Guid>();
        var denied = bench.Connections.Any(c => withdrawals.ContainsKey(Fingerprint(c, null)) && !covered.Contains(c)
            && bench.Selectors.Any(s => s.Connection == c && s.Kind != SelectorKind.Pin));
        foreach (var (fingerprint, selector) in candidate.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var connection = Fingerprint(selector.Connection!, null);
            var entry = scoped.SingleOrDefault(e => e.Grant?.Item == fingerprint && e.RuleRevision == catalog.Revision);
            if (entry?.Grant is { } grant && !Withdrawn(grant) && Remaining(grant) > TimeSpan.Zero
                && (grant.ScanRequest is null || scans.TryGetValue((storeId, bench.Id, selector.Connection), out var scan)
                    && scan.Request == grant.ScanRequest && scan.Catalog == catalog.Revision && ScanEligible(scan)))
            {
                items.Add(new(connection, selector.Item!.NativeId!));
                remaining = Math.Min(remaining, Remaining(grant).TotalMilliseconds);
                partitions.Add(entry.Scope.Id);
            }
            else if (Withdrawal(connection, fingerprint) != 0 && (entry?.Grant is not { } prior || Withdrawn(prior)))
                denied = true;
        }
        var origins = scoped.Where(e => e.Scope.Origin is not null && e.RuleRevision == catalog.Revision && !denied)
            .Select(e => e.Scope.Id).ToArray();
        partitions.AddRange(origins);
        if (partitions.Count > 1024) throw new HttpFailure(409, "RefreshRequired", "The partition inventory exceeds the bound.");
        var complete = !denied && !incomplete && !pending.Values.Any(d => d.Benches.Contains(bench.Id)) && candidate.Count == items.Count
            && bench.Selectors.All(s => s.Connection is not null && (covered.Contains(s.Connection)
                || s.Kind == SelectorKind.Pin && s.Item is { IsLocal: false }));
        var state = denied ? "Denied" : complete ? "Known" : items.Count > 0 ? "Partial" : "Unknown";
        return new([.. partitions], origins, new(state, complete ? items.Count : null, [.. items]),
            generations.GetValueOrDefault((owner, storeId, bench.Id)), (long)Math.Max(0, remaining), now.AddMilliseconds(remaining));
    }

    internal ReleasedPartitions CliInventory(UserBenchSnapshot catalog, Twig.Core.Bench bench)
    {
        lock (release) return Inventory(Guid.Empty, catalog, bench);
    }

    internal async Task ReadPartitions(ViewSessions views, Guid view, string bearer, UserBenchSnapshot catalog, Guid bench,
        Func<ReleasedPartitions,Task> send, CancellationToken ct)
    {
        var outbox = Channel.CreateBounded<ReleasedPartitions>(1);
        views.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                if (owner.CatalogId != catalog.InstanceId || catalog.Benches.SingleOrDefault(b => b.Id == bench) is not { } definition)
                    throw new HttpFailure(409, "RefreshRequired", "No current Bench definition.");
                return Enqueue(outbox, Inventory(owner.ProfileId, catalog, definition, owner.Expires));
            }
        });
        try { await send(await outbox.Reader.ReadAsync(ct)); }
        finally { responses.Release(); }
    }

    private static HttpFailure Changed() => new(409, "ViewChanged", "Discard the previous pages before reading a new generation.");
}
