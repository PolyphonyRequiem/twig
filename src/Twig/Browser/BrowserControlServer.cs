using System.IO.Pipes;

namespace Twig.Browser;

internal sealed class BrowserControlServer : IAsyncDisposable
{
    private static readonly byte[] ReplyAcknowledgement = [0x06];
    private static readonly TimeSpan TransportTimeout = TimeSpan.FromSeconds(5);
    private static TimeSpan DispatchTimeout(BrowserIntent intent)
        => intent.Command == "reconnect" ? TimeSpan.FromSeconds(15) : TransportTimeout;
    private readonly BrowserApplication _application;
    private readonly BrowserHostRegistration _owned;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private TaskCompletionSource? _closeReply;

    internal BrowserControlServer(BrowserApplication application, BrowserSource source, string hostPane, string launchNonce, string root)
    {
        _application = application;
        _path = BrowserControlProtocol.PathFor(root, source);
        _owned = new(BrowserControlProtocol.NewPipeName(), BrowserControlProtocol.NewToken(),
            Environment.ProcessId, BrowserControlProtocol.ProcessBirthId(Environment.ProcessId), hostPane, source, launchNonce);
        // Bind before exposing the exclusive registration; a pipe collision never owns that file.
        var listener = CreateListener();
        try { BrowserControlProtocol.Publish(_path, BrowserControlProtocol.Registration(_owned)); }
        catch { listener.Dispose(); _stop.Dispose(); throw; }
        _worker = ServeAsync(listener);
    }

    // Keep OS buffer defaults: Unix applies explicit sizes after accept, when an EOF peer may already be gone.
    private NamedPipeServerStream CreateListener() => new(_owned.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream listener)
    {
        var acknowledgement = new byte[1];
        using (listener)
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await listener.WaitForConnectionAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TransportTimeout);
                TaskCompletionSource? close = null;
                try
                {
                    var request = BrowserControlProtocol.Request(await BrowserControlProtocol.ReadFrame(listener, deadline.Token));
                    if (!BrowserControlProtocol.EqualToken(request.Token, _owned.Token) || request.Pid != _owned.Pid || request.BirthId != _owned.BirthId ||
                        request.HostPane != _owned.HostPane || !request.Source.Matches(_owned.Source))
                        throw new InvalidDataException("Control authentication/context mismatch.");
                    if (request.Intent.Command == "close")
                    { close = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref _closeReply, close); }
                    // Authentication, cold runtime rebuild and reply delivery have separate budgets.
                    // A legitimate reconnect must not inherit time already spent reading its request.
                    deadline.CancelAfter(DispatchTimeout(request.Intent));
                    var result = await _application.DispatchAsync(request.Intent, deadline.Token);
                    deadline.CancelAfter(TransportTimeout);
                    var reply = new BrowserControlReply(request.Nonce, _owned.Pid, _owned.BirthId, _owned.HostPane, _owned.Source, result);
                    await BrowserControlProtocol.WriteFrame(listener, BrowserControlProtocol.Reply(reply), deadline.Token);
                    await listener.ReadExactlyAsync(acknowledgement, deadline.Token);
                    if (acknowledgement[0] != ReplyAcknowledgement[0]) throw new InvalidDataException("Invalid control reply acknowledgement.");
                    // Delivery is qualified only after the client validates the complete reply.
                    close?.TrySetResult();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or
                    ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or OperationCanceledException)
                {
                    // Bad/slow/disconnected clients neither retarget nor stop the terminal owner.
                }
                finally
                {
                    close?.TrySetCanceled();
                    // EOF can make IsConnected false; a Broken accepted pipe still needs disconnecting.
                    listener.Disconnect();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var close = Volatile.Read(ref _closeReply);
            if (close is not null)
            {
                try { await close.Task.WaitAsync(TimeSpan.FromSeconds(6)); }
                catch (TimeoutException) { /* Deadline expiry cannot delay application shutdown indefinitely. */ }
                catch (OperationCanceledException) when (close.Task.IsCanceled) { /* The request ended without acknowledged delivery. */ }
            }
        }
        finally
        {
            _stop.Cancel();
            try { await _worker; }
            finally
            {
                try
                {
                    using var routeLock = await BrowserRouteLock.AcquireAsync(_path, CancellationToken.None);
                    BrowserControlProtocol.RemoveOwned(_path, _owned);
                }
                finally { _stop.Dispose(); }
            }
        }
    }

    internal static async Task<BrowserControlReply> SendAsync(BrowserHostRegistration record, BrowserSource source, BrowserIntent intent, CancellationToken ct)
    {
        BrowserControlProtocol.ValidateIntent(intent);
        if (!record.Source.Matches(source)) throw new InvalidOperationException("This tab belongs to a different captured source; refusing implicit retarget.");
        if (!BrowserControlProtocol.Alive(record.Pid, record.BirthId)) throw new InvalidOperationException("Native host is closed/replaced; no control was dispatched.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TransportTimeout);
        using var pipe = new NamedPipeClientStream(".", record.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var nonce = BrowserControlProtocol.NewToken();
        await pipe.ConnectAsync(deadline.Token);
        await BrowserControlProtocol.WriteFrame(pipe, BrowserControlProtocol.Request(new BrowserControlRequest(record.Token, nonce, record.Pid, record.BirthId, record.HostPane, source, intent)), deadline.Token);
        // A buffered write may finish before server authentication; include that phase too.
        deadline.CancelAfter(TransportTimeout + DispatchTimeout(intent) + TransportTimeout);
        var reply = BrowserControlProtocol.Reply(await BrowserControlProtocol.ReadFrame(pipe, deadline.Token));
        if (!BrowserControlProtocol.EqualToken(reply.Nonce, nonce) || reply.Pid != record.Pid || reply.BirthId != record.BirthId ||
            reply.HostPane != record.HostPane || !reply.Source.Matches(source)) throw new InvalidDataException("Control reply failed nonce/owner/source checks.");
        deadline.CancelAfter(TransportTimeout);
        await pipe.WriteAsync(ReplyAcknowledgement, deadline.Token);
        await pipe.FlushAsync(deadline.Token);
        return reply;
    }
}

internal static class BrowserRouteLock
{
    internal static async Task<FileStream> AcquireAsync(string registrationPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                var path = registrationPath + ".lock";
                FileStream stream;
                try { stream = BrowserPrivateDirectory.CreateExclusive(path); }
                catch (IOException) when (File.Exists(path))
                {
                    stream = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                    });
                }
                try { BrowserPrivateDirectory.ValidateFile(path); return stream; }
                catch { stream.Dispose(); throw; }
            }
            catch (IOException) { await Task.Delay(25, timeout.Token); }
        }
    }
}
