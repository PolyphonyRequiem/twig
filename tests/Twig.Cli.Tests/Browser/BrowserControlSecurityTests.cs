using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Shouldly;
using Twig.Browser;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class BrowserControlSecurityTests
{
    [Fact]
    public async Task OversizedFrameRefusesBeforeReadingBody()
    {
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, 65537);
        using var input = new MemoryStream(prefix);
        await Should.ThrowAsync<InvalidDataException>(() => BrowserControlProtocol.ReadFrame(input, CancellationToken.None));
        input.Position.ShouldBe(4);
    }

    [Fact]
    public void DuplicateOrPublicationCommandCannotEnterTheControlProtocol()
    {
        Should.Throw<InvalidDataException>(() => BrowserControlProtocol.Request(Encoding.UTF8.GetBytes("{\"command\":\"status\",\"command\":\"close\"}")));
        Should.Throw<InvalidDataException>(() => BrowserControlProtocol.ValidateIntent(new("apply")));
        Should.Throw<InvalidDataException>(() => BrowserControlProtocol.ValidateIntent(new("review", "table")));
        Should.Throw<InvalidDataException>(() => BrowserControlProtocol.ValidateIntent(new("close", ProposalFile: "proposal.json")));
    }

    [Fact]
    public async Task DifferentSourceRefusesBeforeContactingTheLivePipe()
    {
        var source = Source();
        var owner = Owner(source);
        var other = source with { Pane = "different-opaque-pane" };
        await Should.ThrowAsync<InvalidOperationException>(() => BrowserControlServer.SendAsync(owner, other, new("close"), CancellationToken.None));
    }

    [Fact]
    public void PidReuseCannotQualifyARegistrationAsLive()
    {
        var started = BrowserControlProtocol.ProcessStarted(Environment.ProcessId);
        BrowserControlProtocol.Alive(Environment.ProcessId, started).ShouldBeTrue();
        BrowserControlProtocol.Alive(Environment.ProcessId, started + 1).ShouldBeFalse();
    }

    [Fact]
    public void CleanupCannotDeleteAReplacementRegistration()
    {
        var root = PrivateRoot();
        try
        {
            var source = Source(); var path = BrowserControlProtocol.PathFor(root, source);
            var previous = Owner(source); var replacement = Owner(source);
            BrowserControlProtocol.Publish(path, BrowserControlProtocol.Registration(replacement));
            BrowserControlProtocol.RemoveOwned(path, previous);
            BrowserControlProtocol.ReadRegistration(path)!.Token.ShouldBe(replacement.Token);
            BrowserControlProtocol.RemoveOwned(path, replacement);
            File.Exists(path).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SlowLivePeerTimesOutWithoutStealingRegistration()
    {
        var root = PrivateRoot();
        try
        {
            var source = Source(); var owner = Owner(source); var path = BrowserControlProtocol.PathFor(root, source);
            BrowserControlProtocol.Publish(path, BrowserControlProtocol.Registration(owner));
            using var pipe = new NamedPipeServerStream(owner.Pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var acceptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepted = pipe.WaitForConnectionAsync(acceptDeadline.Token);
            using var deadline = new CancellationTokenSource();
            var pending = BrowserControlServer.SendAsync(owner, source, new("status"), deadline.Token);
            await accepted;
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Should.ThrowAsync<OperationCanceledException>(() => pending);
            BrowserControlProtocol.ReadRegistration(path)!.Token.ShouldBe(owner.Token);
            BrowserControlProtocol.Alive(owner.Pid, owner.Started).ShouldBeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DelayedReaderReceivesQualifiedReplyBeforeAcknowledgedDisconnectAndNextConnection()
    {
        var root = PrivateRoot();
        try
        {
            var source = Source();
            var application = new BrowserApplication(new BrowserRuntimeFactory());
            await using var server = new BrowserControlServer(application, source, "host-pane", BrowserControlProtocol.NewToken(), root);
            var owner = BrowserControlProtocol.ReadRegistration(BrowserControlProtocol.PathFor(root, source))!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using (var pipe = new NamedPipeClientStream(".", owner.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                var nonce = BrowserControlProtocol.NewToken();
                await pipe.ConnectAsync(deadline.Token);
                await BrowserControlProtocol.WriteFrame(pipe, BrowserControlProtocol.Request(new BrowserControlRequest(
                    owner.Token, nonce, owner.Pid, owner.Started, owner.HostPane, source, new("status"))), deadline.Token);
                // Let the real server finish its small reply before consuming any buffered bytes.
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
                var reply = BrowserControlProtocol.Reply(await BrowserControlProtocol.ReadFrame(pipe, deadline.Token));
                BrowserControlProtocol.EqualToken(reply.Nonce, nonce).ShouldBeTrue();
                reply.Pid.ShouldBe(owner.Pid);
                reply.Started.ShouldBe(owner.Started);
                reply.HostPane.ShouldBe(owner.HostPane);
                reply.Source.Matches(source).ShouldBeTrue();
                reply.Result.Ok.ShouldBeFalse();

                // A delivered result is not permission to discard the connection before its ACK.
                var disconnected = BrowserControlProtocol.ReadFrame(pipe, deadline.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                disconnected.IsCompleted.ShouldBeFalse();
                await pipe.WriteAsync(new byte[] { 0x06 }, deadline.Token);
                await pipe.FlushAsync(deadline.Token);
                await Should.ThrowAsync<EndOfStreamException>(() => disconnected);
            }

            var next = await BrowserControlServer.SendAsync(owner, source, new("close"), deadline.Token);
            next.Result.Ok.ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BrowserSource Source() => BrowserSource.Create(Path.Combine(Path.GetTempPath(), "herdr.sock"), "workspace", "tab", "source-pane", Path.GetTempPath());
    private static BrowserHostRegistration Owner(BrowserSource source) => new("twig-browser-aot-" + BrowserControlProtocol.NewToken(),
        BrowserControlProtocol.NewToken(), Environment.ProcessId, BrowserControlProtocol.ProcessStarted(Environment.ProcessId), "host-pane", source, BrowserControlProtocol.NewToken());
    private static string PrivateRoot() => BrowserPrivateDirectory.Open(Path.Combine(Path.GetTempPath(), "twig-native-control-" + Guid.NewGuid().ToString("N")));
}
