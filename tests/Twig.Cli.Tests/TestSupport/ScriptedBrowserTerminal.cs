using System.Collections.Concurrent;
using System.Threading.Channels;
using Twig.Browser;

namespace Twig.Cli.Tests.TestSupport;

/// <summary>Queues terminal input and geometry; observes the real application's paint output.</summary>
internal sealed class ScriptedBrowserTerminal : IBrowserTerminal
{
    private readonly ConcurrentQueue<BrowserInput> _input = new();
    private readonly ConcurrentQueue<(int Width, int Height)> _geometry = new();
    private readonly Channel<BrowserPaint> _paints = Channel.CreateUnbounded<BrowserPaint>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private (int Width, int Height) _currentGeometry;
    private long _sequence;

    internal ScriptedBrowserTerminal(int width = 100, int height = 24)
        => _currentGeometry = (width, height);

    internal List<BrowserPaint> ObservedPaints { get; } = [];

    internal void Enqueue(params BrowserInput[] input)
    {
        foreach (var next in input) _input.Enqueue(next);
    }

    internal void Resize(int width, int height) => _geometry.Enqueue((width, height));

    public (int Width, int Height) Geometry()
    {
        if (_geometry.TryDequeue(out var next)) _currentGeometry = next;
        return _currentGeometry;
    }

    public bool TryRead(out BrowserInput input) => _input.TryDequeue(out input);

    public void Paint(IReadOnlyList<string> rows, int top, int width, int height, BrowserChrome chrome)
        => _paints.Writer.TryWrite(new(++_sequence, rows.ToArray(), top, width, height, chrome));

    internal async Task<BrowserPaint> ReadUntilAsync(Func<BrowserPaint, bool> predicate, CancellationToken ct)
    {
        await foreach (var paint in _paints.Reader.ReadAllAsync(ct))
        {
            ObservedPaints.Add(paint);
            if (predicate(paint)) return paint;
        }
        throw new InvalidOperationException("The terminal completed before the expected paint.");
    }

    public void Dispose() => _paints.Writer.TryComplete();
}

internal sealed record BrowserPaint(long Sequence, IReadOnlyList<string> Rows, int Top, int Width, int Height, BrowserChrome Chrome)
{
    internal string Header => Chrome.Header;
    internal string Context => Chrome.Context;
    internal string Status => Chrome.Footer;
    internal string Notice => Chrome.Notice;
    internal string Content => string.Join("\n", Rows);
    internal IReadOnlyList<string> VisibleRows => Rows.Skip(Top).Take(BrowserTerminalFrame.ContentHeight(Height)).ToArray();
    internal bool Working => Header.Contains("◌ working", StringComparison.Ordinal);
}
