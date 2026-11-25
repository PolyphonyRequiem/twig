using System.Text.Json.Serialization;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Commands;
using static Twig.Server.BenchHost;

namespace Twig.Server;

internal sealed record PrivateInputRequest(string Item, string Field, Guid? Write = null, long? Expected = null, string? Text = null,
    string? Baseline = null, bool Rereview = false);
[JsonSerializable(typeof(PrivateInputRequest))]
[JsonSerializable(typeof(PrivateInput))]
[JsonSerializable(typeof(CanvasActionRequest))]
[JsonSerializable(typeof(UpdateReply))]
[JsonSerializable(typeof(CanvasActionReply))]
internal sealed partial class TransportJson;
internal sealed record CanvasActionRequest(string Item, string Field, string Mode, Guid? Write = null);
internal sealed record CanvasActionReply(string Status, InputReview[] Matches, bool Ambiguous);

internal sealed partial class ReleasedBenchDisplay
{
    internal static string RevisionTag(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(value)));

    internal Frame AuthorizeItem(ViewSessions sessions, Guid view, string bearer, Guid bench, Guid partition, string item)
        => WithItem(sessions, view, bearer, bench, partition, item, frame => frame);

    private T WithItem<T>(ViewSessions sessions, Guid view, string bearer, Guid bench, Guid partition, string item, Func<Frame,T> use)
    {
        var catalog = definitions?.InspectRevision();
        return sessions.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                var (entry, generation) = Select(owner, bench, partition, catalog);
                var scope = entry.Scope;
                if (entry.Grant is not { Revision: not null, Source: not null } grant || grant.Source.Connection != scope.Connection || grant.Item != Fingerprint(scope.Connection,
                    ItemKey.Remote(scope.Connection.Provider, scope.Connection.ResourceScope, item))) throw Denied();
                var now = clock.GetUtcNow();
                var left = TimeSpan.FromTicks(Math.Min((owner.Expires - now).Ticks, Remaining(grant).Ticks));
                if (left.TotalMilliseconds < 1) throw Denied();
                return use(new([], generation, scope, null, owner.ProfileGeneration, now + left, (long)left.TotalMilliseconds,
                    watermark, authCursor, grant.Revision, grant.Source));
            }
        });
    }

    internal async Task ReleaseItem(ViewSessions sessions, Guid view, string bearer, Frame expected, string item, byte[] body,
        Func<Frame,Task> send, CancellationToken ct)
    {
        if (BeforeEnqueue is { } barrier) await barrier(ct);
        var handoff = System.Threading.Channels.Channel.CreateBounded<Frame>(1);
        WithItem(sessions, view, bearer, expected.Scope.Bench, expected.Scope.Id, item, frame =>
        {
            if (frame.Generation != expected.Generation || frame.AuthEpoch != expected.AuthEpoch || frame.Cursor != expected.Cursor)
                throw Changed();
            if (!responses.Wait(0)) throw new HttpFailure(503, "Busy", "No release capacity.");
            if (handoff.Writer.TryWrite(frame with { Body = body })) return true;
            responses.Release();
            throw new HttpFailure(503, "Busy", "Release handoff failed.");
        });
        try { await send(await handoff.Reader.ReadAsync(ct)); }
        finally { responses.Release(); }
    }
}

public static partial class NativeBenchHost
{
    private const string ReleasedPartition = "/api/v2/views/{view:guid}/benches/{bench:guid}/partitions/{partition:guid}";
    private static void MapReleasedInputs(WebApplication app, ViewSessions sessions, ServerOptions options,
        ReleasedBenchDisplay display, UserBenchStore store)
    {
        async Task Input(HttpContext http, Guid view, Guid bench, Guid partition)
        {
            var b = await Body<PrivateInputRequest>(http, 256 * 1024);
            if (b.Item is not { Length: > 0 and <= 256 } || b.Field is not { Length: > 0 and <= 256 }
                || b.Write == Guid.Empty || b.Text is { Length: > 65536 })
                throw new HttpFailure(400, "InvalidInput", "Use an exact item, field and bounded private input.");
            var bearer = Bearer(http);
            var witness = display.AuthorizeItem(sessions, view, bearer, bench, partition, b.Item);
            var put = HttpMethods.IsPut(http.Request.Method);
            if (put && (b.Write is null || b.Expected is null or < 0 || b.Baseline is null))
                throw new HttpFailure(400, "InvalidInput", "An exact input version, write identity and observed baseline are required.");
            if (put && b.Baseline != ReleasedBenchDisplay.RevisionTag(witness.ItemRevision!))
                throw new HttpFailure(409, "ReviewRequired", "The observed provider revision changed.");
            if (!put && (b.Expected.HasValue || b.Text is not null || b.Baseline is not null || b.Rereview))
                throw new HttpFailure(400, "InvalidInput", "Inspection cannot write private input.");
            var value = Require(await store.AccessInputAsync(InputScope(witness), b.Item, b.Field, b.Write, b.Expected,
                b.Text, b.Baseline, http.RequestAborted, b.Rereview,
                new(witness.InputSource!.StoreInstance, witness.Scope.Connection, witness.InputSource.PrincipalId, b.Item)));
            await display.ReleaseItem(sessions, view, bearer, witness, b.Item,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, TransportJson.Default.PrivateInput),
                frame => WriteReleasedFrame(http, options, frame), http.RequestAborted);
        }
        app.MapPut(ReleasedPartition + "/input", Input);
        app.MapPost(ReleasedPartition + "/input/inspect", Input);
        app.MapPost(ReleasedPartition + "/action", async (HttpContext http, Guid view, Guid bench, Guid partition) =>
        {
            var b = await Body<CanvasActionRequest>(http);
            if (b.Item is not { Length: > 0 and <= 256 } || b.Field is not { Length: > 0 and <= 256 }
                || b.Mode is not ("prepare" or "submit" or "observe" or "find") || b.Mode != "find" && (b.Write is null || b.Write == Guid.Empty))
                throw new HttpFailure(400, "InvalidInput", "Use one exact private input and original action command.");
            var bearer = Bearer(http);
            var witness = display.AuthorizeItem(sessions, view, bearer, bench, partition, b.Item);
            ReleasedBenchDisplay.Frame Recheck()
            {
                var final = display.AuthorizeItem(sessions, view, bearer, bench, partition, b.Item);
                if (final.AuthEpoch != witness.AuthEpoch || final.Scope != witness.Scope || final.InputSource != witness.InputSource)
                    throw new HttpFailure(403, "PolicyViolation", "The authorized input scope changed.");
                return final;
            }
            var owner = app.Services.GetRequiredService<OwnerCommandEndpoint>();
            var scope = InputScope(witness);
            var origin = new UpdateOrigin(witness.InputSource!.StoreInstance, witness.Scope.Connection, witness.InputSource.PrincipalId, b.Item);
            var receipts = Require(await store.InputReviewsAsync(scope, origin, b.Field, b.Mode == "find" ? null : b.Write, http.RequestAborted));
            CanvasActionReply reply;
            if (b.Mode == "find") reply = new(receipts.Length switch { 0 => "NotFound", 1 => receipts[0].Original!.State, _ => "Ambiguous" },
                receipts, receipts.Length > 1);
            else
            {
                var receipt = receipts.SingleOrDefault(r => r.Write == b.Write)
                    ?? throw new HttpFailure(409, "InputRevisionConflict", "Inspect the original input write before creating an action.");
                var review = receipt.Review.ToString("D");
                UpdateReply executed;
                if (b.Mode == "prepare" && receipt.Original is { } existing) executed = new(existing.State, [existing]);
                else if (b.Mode == "prepare")
                {
                    var saved = Require(await store.AccessInputAsync(scope, b.Item, b.Field, b.Write, null, null, null, http.RequestAborted));
                    if (saved.Acknowledged != saved.Version || saved.Write != b.Write || saved.Text is null
                        || saved.Baseline != ReleasedBenchDisplay.RevisionTag(witness.ItemRevision!))
                        throw new HttpFailure(409, "ReviewRequired", "The acknowledged input changed; private text was retained.");
                    executed = await owner.CanvasUpdate(["update", b.Field, "--stdin", "--id", b.Item, "--review", review, "--prepare"],
                        witness.InputSource, witness.ItemRevision, () => Recheck() is not null, saved.Text, http.RequestAborted);
                }
                else executed = await owner.CanvasUpdate(["update", "--" + b.Mode, review, "--id", b.Item], witness.InputSource,
                    null, () => Recheck() is not null, null, http.RequestAborted);
                reply = new(executed.Status, executed.Matches.Select(m => receipt with { Original = m }).ToArray(), executed.Ambiguous);
            }
            await display.ReleaseItem(sessions, view, bearer, witness, b.Item,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(reply, TransportJson.Default.CanvasActionReply),
                frame => WriteReleasedFrame(http, options, frame), http.RequestAborted);
        });
    }

    private static string InputScope(ReleasedBenchDisplay.Frame f) => $"{f.Scope.Store:N}/{f.Scope.Owner:N}/"
        + $"{ReleasedBenchDisplay.Fingerprint(f.Scope.Connection, null)}/{f.InputSource!.StoreInstance:N}/"
        + $"{ReleasedBenchDisplay.RevisionTag(f.InputSource.PrincipalId)}/{ReleasedBenchDisplay.RevisionTag(f.InputSource.AuthorityStamp)}/{f.Scope.Bench:N}/{f.Scope.Id:N}";

    private static Task WriteReleasedFrame(HttpContext http, ServerOptions options, ReleasedBenchDisplay.Frame f)
    {
        var h = http.Response.Headers;
        h["X-Twig-Authority"] = options.ServerId.ToString("D");
        h["X-Twig-Process"] = options.ProcessInstance.ToString("D");
        h["X-Twig-Owner"] = f.Scope.Owner.ToString("D");
        h["X-Twig-Store"] = f.Scope.Store.ToString("D");
        h["X-Twig-Generation"] = f.Generation.ToString("D");
        h["X-Twig-Partition"] = f.Scope.Id.ToString("D");
        h["X-Twig-Connection"] = ReleasedBenchDisplay.Fingerprint(f.Scope.Connection, null);
        h["X-Twig-Auth-Epoch"] = f.AuthEpoch.ToString("D");
        h["X-Twig-Auth-Cursor"] = f.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        h["X-Twig-Watermark"] = f.Watermark.ToString(System.Globalization.CultureInfo.InvariantCulture);
        h["X-Twig-Expires"] = f.Expires.ToString("O");
        h["X-Twig-Remaining-Ms"] = f.RemainingMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (f.Scope.Origin is { } origin) h["X-Twig-Origin"] = origin.StoreInstance.ToString("D");
        if (f.InputSource is not null && f.ItemRevision is { } itemRevision) h["X-Twig-Item-Version"] = ReleasedBenchDisplay.RevisionTag(itemRevision);
        if (f.Next is not null) h["X-Twig-Next-Cursor"] = f.Next;
        http.Response.ContentType = "application/json";
        return http.Response.Body.WriteAsync(f.Body.AsMemory(), http.RequestAborted).AsTask();
    }
}
