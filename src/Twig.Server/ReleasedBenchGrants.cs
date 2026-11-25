using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Services;

namespace Twig.Server;

internal sealed record GrantWitness(string Connection, string Item, long Watermark, long Request, long Started,
    string? Revision = null, NativeSourceStamp? Source = null, long? ScanRequest = null);

internal sealed partial class ReleasedBenchDisplay
{
    private readonly TimeProvider clock;
    private readonly Dictionary<string, long> withdrawals;
    private readonly Dictionary<string, long> latest = [];
    private long sequence, watermark;
    private bool closed;

    // Only an owner-side, already bound provider can provide evidence. In
    // particular, ValidateReadAccessAsync/project discovery is not evidence.
    internal async Task<Result<bool>> AcquireAsync(DisplayPartition scope, ItemKey key, IProvider provider, CancellationToken ct,
        Twig.Infrastructure.Services.NativeSourceStamp? source = null)
    {
        if (scope.Origin is not null || !key.BelongsTo(scope.Connection) || source is not null && source.Connection != scope.Connection)
            throw new ArgumentException("Expected one exact item of a provider partition.");
        var observed = await ObserveExact(scope.Connection, key, [scope],
            () => provider.ReadAsync(scope.Connection, [key], ct, captureOnly: true), source: source);
        return observed.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(observed.Error);
    }

    public async Task<Result<QueryPage>> Observe(string root, NativeSourceStamp source, UserBenchStore definitions,
        ItemKey key, Func<Task<Result<QueryPage>>> read, bool publish, CancellationToken ct)
    {
        var scopes = new List<DisplayPartition>();
        var revisions = new Dictionary<Guid, long>();
        Func<Func<bool>, Task<bool>>? commit = null;
        if (publish && sessions is not null)
        {
            var catalog = await definitions.ReadAsync(ct);
            var exclusions = await definitions.ReadViewRulesAsync(root, source.Connection, ct);
            if (!catalog.IsSuccess || !exclusions.IsSuccess)
                return Result<QueryPage>.Fail(ErrorCode.StorageUnavailable, "Bench projection inputs unavailable.");
            commit = apply => definitions.PublishIfCurrent(root, source.Connection, catalog.Value, exclusions.Value.Revision, apply, ct);
            var owners = sessions.StandaloneDisplayOwners(catalog.Value.InstanceId);
            var partition = new Guid(Convert.FromHexString(Fingerprint(source.Connection, key)).AsSpan(0, 16));
            if (exclusions.Value.Exclusions.IsEmpty)
                foreach (var bench in catalog.Value.Benches.Where(b => b.Selectors.Any(s => s.Connection == source.Connection
                    && s.Kind == SelectorKind.Pin && s.Item == key)))
                {
                    revisions.Add(bench.Id, catalog.Value.Revision);
                    foreach (var owner in owners.Append(Guid.Empty))
                        scopes.Add(new(owner, catalog.Value.InstanceId, bench.Id, partition, null, source.Connection));
                }
        }
        var observed = await ObserveExact(source.Connection, key, scopes, read, commit, source, revisions);
        if (publish && observed.IsSuccess) await ProjectWorkspace(root, source, definitions);
        return observed;
    }

    private async Task<Result<QueryPage>> ObserveExact(Connection origin, ItemKey key, IReadOnlyList<DisplayPartition> scopes,
        Func<Task<Result<QueryPage>>> read, Func<Func<bool>,Task<bool>>? commit = null, NativeSourceStamp? source = null,
        Dictionary<Guid, long>? revisions = null)
    {
        var connection = Fingerprint(origin, null);
        var item = Fingerprint(origin, key);
        GrantWitness witness;
        lock (release)
        {
            RequireOpen();
            if (scopes.Any(Dirty)) return Result<QueryPage>.Fail(ErrorCode.Conflict, "The selected Bench has unsettled inputs.");
            ChangeObservations(origin);
            latest[item] = checked(++sequence);
            foreach (var scope in scopes) partitionRequests[scope] = sequence;
            witness = new(connection, item, Withdrawal(connection, item), sequence, clock.GetTimestamp());
        }
        var result = await read();
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error.Evidence.Any(e => e is "http:401" or "http:403" or "http:404"))
                Withdraw(origin, error.Evidence.Contains("item-unavailable:" + key.NativeId) ? key : null);
            else if (error.Code == ErrorCode.Conflict) Withdraw(origin, null);
            else if (error.Code is ErrorCode.NotFound or ErrorCode.PolicyViolation) Withdraw(origin, key);
            return result;
        }
        var page = result.Value;
        if (!page.Complete || page.Continuation is not null || page.Items is not [var observed] || observed.Key != key)
        {
            Withdraw(origin, key);
            return Result<QueryPage>.Fail(ErrorCode.Partial, "The exact item was not observed; no grant was issued.");
        }
        if (scopes.Count > 0)
        {
            witness = witness with { Revision = observed.Revision?.Token, Source = source };
            var pages = Pages(Rows("provider", key.NativeId!, observed.Type, observed.Fields));
            foreach (var scope in scopes)
            {
                bool Apply() => Publish(scope, pages, witness.Request, witness, ruleRevision: revisions?.GetValueOrDefault(scope.Bench) ?? 0);
                if (!(commit is null ? Apply() : await commit(Apply)))
                    return Result<QueryPage>.Fail(ErrorCode.Conflict, "Observation superseded or expired.");
            }
        }
        return result;
    }

    // Called before Save changes the observation against which an acquisition
    // started. This is ordering, not a new observation or a renewable grant.
    public void InvalidateItem(Connection connection, ItemKey key)
    {
        if (!key.BelongsTo(connection)) throw new ArgumentException("Invalid acquisition scope.");
        var item = Fingerprint(connection, key);
        lock (release)
        {
            ChangeObservations(connection);
            latest[item] = checked(++sequence);
            foreach (var entry in entries.Values.Where(e => e.Grant?.Item == item).ToArray())
                Withhold(entry.Scope);
        }
    }

    internal void Withdraw(Connection connection, ItemKey? key)
    {
        if (key is not null && !key.BelongsTo(connection)) throw new ArgumentException("Invalid withdrawal scope.");
        var scope = Fingerprint(connection, key);
        long stamp;
        lock (release)
        {
            ChangeObservations(connection);
            withdrawals[scope] = stamp = checked(++watermark);
            NotifyAuthorization();
        }
        try { lock (publication) store.Withdraw(scope, stamp); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            lock (release) closed = true;
            throw new HttpFailure(503, "AuthorityUnavailable", "Withdrawal persistence failed; this owner stopped releasing content.");
        }
    }

    internal static string Fingerprint(Connection c, ItemKey? key) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { c.Provider.Value, c.ResourceScope, c.Destination, c.IdentityRef,
            key?.NativeId ?? "" }, MappingJson.Default.StringArray)));

    private long Withdrawal(string connection, string item) =>
        Math.Max(withdrawals.GetValueOrDefault(connection), withdrawals.GetValueOrDefault(item));
    private bool Withdrawn(GrantWitness g) => Withdrawal(g.Connection, g.Item) != g.Watermark;
    private TimeSpan Remaining(GrantWitness g) => TimeSpan.FromSeconds(60) - clock.GetElapsedTime(g.Started);
    private bool Current(GrantWitness g) => !Withdrawn(g) && latest[g.Item] == g.Request && Remaining(g) > TimeSpan.Zero;
    private void RequireOpen()
    {
        if (closed) throw new HttpFailure(503, "AuthorityUnavailable", "This authority cannot release content.");
    }
    private static HttpFailure Denied() => new(403, "GrantRequired", "Fresh exact-scope authorization is required.");
}
