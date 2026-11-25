using System.Threading.Channels;

namespace Twig.Server;

internal sealed partial class ReleasedBenchDisplay
{
    private sealed class Listener
    {
        internal readonly Channel<long> Queue = Channel.CreateBounded<long>(16);
        internal bool Overrun;
    }
    private readonly HashSet<Listener> listeners = [];
    private long authCursor;

    // Under release: a full channel retires the handoff rather than coalescing a withdrawal.
    private void NotifyAuthorization()
    {
        var cursor = checked(++authCursor);
        foreach (var listener in listeners)
            if (!listener.Overrun && !listener.Queue.Writer.TryWrite(cursor))
            {
                listener.Overrun = true;
                listener.Queue.Writer.TryComplete();
            }
    }

    internal async Task StreamAuthorization(ViewSessions sessions, Guid view, string bearer, HttpContext http, ServerOptions options)
    {
        var listener = new Listener();
        var queue = listener.Queue;
        var baseline = sessions.WithDisplayOwner(view, bearer, owner =>
        {
            lock (release)
            {
                RequireOpen();
                if (listeners.Count >= 16) throw new HttpFailure(503, "Busy", "Authorization subscriptions are full.");
                listeners.Add(listener);
                return (cursor: authCursor, watermark, owner.ProfileId, owner.CatalogId, owner.ProfileGeneration);
            }
        });
        var token = http.RequestAborted;
        var response = http.Response;
        response.ContentType = "text/event-stream";
        async Task Send(string name, long cursor, long mark)
        {
            await response.WriteAsync(FormattableString.Invariant(
                $"event: {name}\ndata: {{\"cursor\":{cursor},\"watermark\":{mark},\"authority\":\"{options.ServerId:D}\",\"process\":\"{options.ProcessInstance:D}\",\"epoch\":\"{baseline.ProfileGeneration:D}\",\"owner\":\"{baseline.ProfileId:D}\",\"store\":\"{baseline.CatalogId:D}\"}}\n\n"), token);
            await response.Body.FlushAsync(token);
        }
        try
        {
            await Send("ready", baseline.cursor, baseline.watermark);
            while (!token.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(2));
                var changed = false;
                try { if (!await queue.Reader.WaitToReadAsync(heartbeat.Token)) break; changed = true; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                var latest = sessions.WithDisplayOwner(view, bearer, owner =>
                {
                    lock (release) return (cursor: authCursor, watermark, valid: !closed && !listener.Overrun && owner.ProfileGeneration == baseline.ProfileGeneration);
                });
                if (!latest.valid) break;
                if (changed && queue.Reader.TryRead(out var next)) await Send("changed", next, latest.watermark);
                else await Send("heartbeat", latest.cursor, latest.watermark);
            }
        }
        catch (HttpFailure) when (response.HasStarted) { /* EOF retires the browser authorization. */ }
        finally { lock (release) listeners.Remove(listener); }
    }
}
