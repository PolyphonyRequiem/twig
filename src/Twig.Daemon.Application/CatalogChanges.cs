using System.Threading.Channels;

namespace Bench.Application;

// Advisory local invalidations only. Successful catalog commit remains the authority.
public sealed class CatalogChanges
{
    public sealed record Change(long Revision, bool PresentationOnly = false, Guid? AgentViewId = null, bool Resync = false);
    private readonly object gate = new();
    private readonly HashSet<Channel<Change>> subscriptions = [];
    public IDisposable Subscribe(out ChannelReader<Change> reader)
    {
        var channel = Channel.CreateBounded<Change>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        lock (gate) subscriptions.Add(channel);
        reader = channel.Reader;
        return new Subscription(() =>
        {
            lock (gate) subscriptions.Remove(channel);
            channel.Writer.TryComplete();
        });
    }
    public void Publish(long revision) => PublishChange(new(revision));
    public void PublishPresentation() => PublishChange(new(0, true));
    public void PublishAgent(Guid viewId) => PublishChange(new(0, AgentViewId: viewId));
    private void PublishChange(Change change)
    {
        lock (gate)
            foreach (var subscriber in subscriptions)
            {
                if (subscriber.Writer.TryWrite(change)) continue;
                // A full queue may contain a different resource or view. Never replace it with a narrower signal.
                subscriber.Reader.TryRead(out _);
                subscriber.Writer.TryWrite(new(0, Resync: true));
            }
    }
    private sealed class Subscription(Action close) : IDisposable
    {
        private Action? dispose = close;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
