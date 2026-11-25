using System.Text.Json;
using Twig.Core;
using Twig.Infrastructure.Services;

namespace Twig.Server;

internal sealed partial class ReleasedBenchDisplay
{
    private sealed record Scan(long Request, long Catalog, long Started, DateTimeOffset Until, Guid[] Owners,
        ItemKey[] Keys, string State);
    private readonly Dictionary<(Guid Store, Guid Bench, Connection Connection), Scan> scans = [];
    private readonly Dictionary<Connection, long> connectionRequests = [];

    private void ChangeObservations(Connection connection)
    {
        connectionRequests[connection] = checked(++sequence);
        foreach (var key in scans.Keys.Where(k => k.Connection == connection).ToArray())
        {
            foreach (var owner in scans[key].Owners) generations[(owner, key.Store, key.Bench)] = Guid.NewGuid();
            scans.Remove(key);
            NotifyAuthorization();
        }
    }

    private bool ScanEligible(Scan scan) => clock.GetElapsedTime(scan.Started) < TimeSpan.FromSeconds(60)
        && clock.GetUtcNow() < scan.Until;

    public async Task<int> RefreshAsync(CoreWorkspace workspace, NativeSourceStamp source, UserBenchStore definitions,
        string name, TextWriter output, CancellationToken ct)
    {
        var connection = source.Connection;
        static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidDataException(result.Error.Message);
        var catalog = Require(await definitions.InspectAsync(ct));
        var matches = catalog.Benches.Where(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || b.Id.ToString("D").Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches is not [var bench]) throw new InvalidDataException("Select one existing Bench by exact name or ID.");
        if (!bench.Connections.Contains(connection)) throw new InvalidDataException("No selector uses this exact connection.");
        var rules = Require(await definitions.ReadViewRulesAsync(workspace.WorktreeRoot, connection, ct));
        var audiences = sessions!.StandaloneDisplayOwners(catalog.InstanceId).Append(Guid.Empty).ToArray();
        var slot = (catalog.InstanceId, bench.Id, connection);
        long request, started, mark;
        lock (release)
        {
            RequireOpen();
            ChangeObservations(connection);
            request = connectionRequests[connection]; started = clock.GetTimestamp(); mark = watermark;
            NotifyAuthorization();
        }
        var until = clock.GetUtcNow().AddSeconds(60);
        var members = new Dictionary<ItemKey, WorkItem>();
        var excluded = new HashSet<ItemKey>();
        var absent = new HashSet<ItemKey>();
        var state = "Known";
        var calendars = Require(await definitions.ReadPlanningCalendarsAsync(connection, ct));
        foreach (var (selector, excluding) in bench.Selectors.Where(s => s.Connection == connection).Select(s => (s, false))
                     .Concat(rules.Exclusions.Select(s => (s, true))))
        {
            if (excluding && selector.Kind == SelectorKind.Pin)
            {
                excluded.Add(selector.Item!);
                continue;
            }
            bool exhausted = true;
            string? cursor = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<ItemKey>();
            Query? query = null;
            if (selector.Query is { } expression)
            {
                var instant = clock.GetUtcNow();
                var resolved = Twig.Core.Core.ResolvePlanningQuery(expression, null, instant, calendars);
                if (!resolved.IsSuccess)
                {
                    state = "Unknown";
                    if (excluding) { members.Clear(); break; }
                    continue;
                }
                query = resolved.Value;
                foreach (var relative in RelativeRules(expression))
                {
                    var calendar = calendars.Single(c => c.Team == relative.PlanningTeam);
                    until = new[] { until, calendar.ObservedAt.AddHours(24),
                        new DateTimeOffset(instant.UtcDateTime.Date.AddDays(1), TimeSpan.Zero) }.Min();
                }
            }
            do
            {
                Result<QueryPage> result;
                if (selector.Kind == SelectorKind.Pin && selector.Item is { IsLocal: false } pin)
                    result = await workspace.ObserveItemsAsync([pin], ct);
                else if (selector.Kind == SelectorKind.Subtree && selector.Item is { IsLocal: false } root)
                    result = await workspace.ObserveSubtreeAsync(root, selector.HierarchyRelation!, ct);
                else if (query?.Kind is QueryKind.SavedQuery or QueryKind.Backlog)
                {
                    var page = await workspace.ObservePlanningBrowseAsync(query.PlanningTeam,
                        query.Kind == QueryKind.Backlog ? PlanningSourceKind.Backlog : PlanningSourceKind.SavedQuery,
                        query.Value!.Text!, query.PlanningTeam, ct);
                    result = page.IsSuccess ? Result<QueryPage>.Ok(page.Value.Page) : Result<QueryPage>.Fail(page.Error);
                }
                else if (query is not null)
                    result = await workspace.ObserveQueryPageAsync(query, cursor, ct);
                else { exhausted = false; break; }
                if (!result.IsSuccess)
                {
                    if (result.Error.Code == ErrorCode.NotFound && selector.Item is { IsLocal: false } rootKey
                        && result.Error.Evidence.Contains($"authority-absent:{rootKey.NativeId}"))
                    {
                        absent.Add(rootKey);
                        break;
                    }
                    if (result.Error.Code is ErrorCode.PolicyViolation or ErrorCode.NotFound
                        || result.Error.Evidence.Any(e => e is "http:401" or "http:403" or "http:404"))
                    {
                        Withdraw(connection, selector.Kind == SelectorKind.Pin && selector.Item is { IsLocal: false } ? selector.Item : null);
                        state = "Denied";
                    }
                    else exhausted = false;
                    break;
                }
                var observed = result.Value;
                if (observed.Complete == (observed.Continuation is not null)
                    || observed.Continuation is { } next && (!seen.Add(next) || seen.Count > 128)
                    || selector.Kind == SelectorKind.Pin && (observed.Items is not [var only] || only.Key != selector.Item)
                    || observed.Items.Any(i => !i.Key.BelongsTo(connection) || i.Revision is null || !keys.Add(i.Key)))
                { exhausted = false; break; }
                foreach (var item in observed.Items)
                {
                    if (members.TryGetValue(item.Key, out var prior) && prior.Revision != item.Revision)
                        throw new InvalidDataException("Selectors observed different revisions; refresh the Bench again.");
                    if (excluding) { excluded.Add(item.Key); continue; }
                    members[item.Key] = item;
                    if (members.Count > 1024) throw new InvalidDataException("Membership exceeds the bounded release.");
                }
                cursor = observed.Continuation;
            } while (cursor is not null);
            if (state == "Denied") break;
            if (!exhausted)
            {
                state = "Unknown";
                if (excluding) { members.Clear(); break; }
            }
        }
        if (absent.Overlaps(members.Keys)) throw new InvalidDataException("Selectors disagree on authoritative item presence.");
        foreach (var key in excluded) members.Remove(key);
        if (state != "Denied")
        {
            bool Commit()
            {
                lock (publication)
                {
                    lock (release)
                    {
                        if (connectionRequests[connection] != request || mark != watermark
                            || pending.Values.Any(d => d.Benches.Contains(bench.Id))) return false;
                        foreach (var key in absent)
                        {
                            var id = Fingerprint(connection, key);
                            latest[id] = request;
                            foreach (var entry in entries.Values.Where(e => e.Grant?.Item == id).ToArray()) Withhold(entry.Scope);
                        }
                        foreach (var key in members.Keys) latest[Fingerprint(connection, key)] = request;
                    }
                    foreach (var item in members.Values)
                    {
                        var id = Fingerprint(connection, item.Key);
                        var grant = new GrantWitness(Fingerprint(connection, null), id, WithdrawalAtStart(id), request,
                            started, item.Revision!.Token, source, request);
                        var pages = Pages(Rows("provider", item.Key.NativeId!, item.Type, item.Fields));
                        foreach (var owner in audiences)
                        {
                            var scope = new DisplayPartition(owner, catalog.InstanceId, bench.Id,
                                new Guid(Convert.FromHexString(id).AsSpan(0, 16)), null, connection);
                            lock (release)
                            {
                                if (connectionRequests[connection] != request || watermark != mark) return false;
                                partitionRequests[scope] = request;
                            }
                            if (!Publish(scope, pages, request, grant, ruleRevision: catalog.Revision)) return false;
                        }
                    }
                    lock (release)
                    {
                        if (connectionRequests[connection] != request || watermark != mark) return false;
                        scans[slot] = new(request, catalog.Revision, started, until, audiences, [.. members.Keys], state);
                        foreach (var owner in audiences) generations[(owner, catalog.InstanceId, bench.Id)] = Guid.NewGuid();
                        NotifyAuthorization();
                        return true;
                    }
                }
            }
            long WithdrawalAtStart(string id) { lock (release) return Withdrawal(Fingerprint(connection, null), id); }
            if (!await definitions.PublishIfCurrent(workspace.WorktreeRoot, connection, catalog, rules.Revision, Commit, ct))
                throw new InvalidDataException("Membership acquisition was superseded.");
        }
        var inventory = CliInventory(catalog, bench);
        output.WriteLine(JsonSerializer.Serialize(inventory, DisplayJson.Default.ReleasedPartitions));
        return inventory.Membership.State == "Known" ? 0 : 1;
    }

    private static IEnumerable<Query> RelativeRules(Query query)
    {
        if (query.Kind == QueryKind.Iteration && query.Value!.Values.Any(v => new IterationExpression(v).IsRelative))
            yield return query;
        foreach (var child in query.Children)
            foreach (var relative in RelativeRules(child)) yield return relative;
    }
}
