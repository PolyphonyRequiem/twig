using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;

namespace Twig.Server;

internal sealed record GrantWitness(string Connection, string Item, long Watermark, long Request, long Started);

internal sealed partial class ReleasedBenchDisplay
{
    private readonly TimeProvider clock;
    private readonly Dictionary<string, long> withdrawals;
    private readonly Dictionary<string, long> latest = [];
    private long sequence, watermark;
    private bool closed;

    // Only an owner-side, already bound provider can provide evidence. In
    // particular, ValidateReadAccessAsync/project discovery is not evidence.
    internal async Task<Result<bool>> AcquireAsync(DisplayPartition scope, ItemKey key, IProvider provider, CancellationToken ct)
    {
        if (scope.Origin is not null || !key.BelongsTo(scope.Connection))
            throw new ArgumentException("Expected one exact item of a provider partition.");
        var connection = Fingerprint(scope.Connection, null);
        var item = Fingerprint(scope.Connection, key);
        GrantWitness witness;
        lock (release)
        {
            latest[item] = Begin(scope);
            witness = new(connection, item, Withdrawal(connection, item), sequence, clock.GetTimestamp());
        }
        var result = await provider.ReadAsync(scope.Connection, [key], ct, captureOnly: true);
        if (!result.IsSuccess)
        {
            var error = result.Error;
            if (error.Evidence.Any(e => e is "http:401" or "http:403" or "http:404"))
                Withdraw(scope.Connection, error.Evidence.Contains("item-unavailable:" + key.NativeId) ? key : null);
            else if (error.Code == ErrorCode.Conflict) Withdraw(scope.Connection, null);
            else if (error.Code is ErrorCode.NotFound or ErrorCode.PolicyViolation) Withdraw(scope.Connection, key);
            return Result<bool>.Fail(error);
        }
        var page = result.Value;
        if (!page.Complete || page.Continuation is not null || page.Items is not [var observed] || observed.Key != key)
        {
            Withdraw(scope.Connection, key);
            return Result<bool>.Fail(ErrorCode.Partial, "The exact item was not observed; no grant was issued.");
        }
        var published = Publish(scope, Pages(Rows("provider", key.NativeId!, observed.Type, observed.Fields)), witness.Request, witness);
        return published ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.Conflict, "Observation superseded or expired.");
    }

    // Called before Save changes the observation against which an acquisition
    // started. This is ordering, not a new observation or a renewable grant.
    internal void InvalidateItem(Connection connection, ItemKey key)
    {
        if (!key.BelongsTo(connection)) throw new ArgumentException("Invalid acquisition scope.");
        var item = Fingerprint(connection, key);
        lock (release) latest[item] = checked(++sequence);
    }

    internal void Withdraw(Connection connection, ItemKey? key)
    {
        if (key is not null && !key.BelongsTo(connection)) throw new ArgumentException("Invalid withdrawal scope.");
        var scope = Fingerprint(connection, key);
        long stamp;
        lock (release) withdrawals[scope] = stamp = checked(++watermark);
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
