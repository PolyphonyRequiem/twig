using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
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
        var started = BrowserControlProtocol.ProcessBirthId(Environment.ProcessId);
        BrowserControlProtocol.Alive(Environment.ProcessId, started).ShouldBeTrue();
        BrowserControlProtocol.Alive(Environment.ProcessId, started + 1).ShouldBeFalse();
    }

    [LinuxFact]
    public async Task IndependentProcessKernelBirthQualifiesTheSameLiveOwner()
    {
        var pid = Environment.ProcessId;
        var captured = BrowserControlProtocol.ProcessBirthId(pid);
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cat /proc/sys/kernel/random/boot_id; cat /proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        using var peer = Process.Start(start)!;
        var output = await peer.StandardOutput.ReadToEndAsync();
        await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        peer.ExitCode.ShouldBe(0, await peer.StandardError.ReadToEndAsync());
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var boot = Guid.Parse(lines[0]).ToString("N");
        var fields = lines[1][(lines[1].LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var observed = "linux:" + boot + ":" + ulong.Parse(fields[19], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        captured.ShouldBe(observed);
        BrowserControlProtocol.Alive(pid, observed).ShouldBeTrue();
        BrowserControlProtocol.Alive(pid, "linux:" + Guid.NewGuid().ToString("N") + ":" + fields[19]).ShouldBeFalse();
    }

    [Fact]
    public void PreviousProtocolCannotReclaimEvenItsOwnLiveRegistration()
    {
        var root = PrivateRoot();
        try
        {
            var source = Source(); var owner = Owner(source); var path = BrowserControlProtocol.PathFor(root, source);
            var previous = JsonNode.Parse(BrowserControlProtocol.Registration(owner))!.AsObject();
            previous["version"] = 3;
            var bytes = Encoding.UTF8.GetBytes(previous.ToJsonString());
            BrowserControlProtocol.Publish(path, bytes);
            Should.Throw<InvalidDataException>(() => BrowserControlProtocol.RemoveOwned(path, owner));
            File.ReadAllBytes(path).ShouldBe(bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
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
            BrowserControlProtocol.Alive(owner.Pid, owner.BirthId).ShouldBeTrue();
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
                    owner.Token, nonce, owner.Pid, owner.BirthId, owner.HostPane, source, new("status"))), deadline.Token);
                // Let the real server finish its small reply before consuming any buffered bytes.
                await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
                var reply = BrowserControlProtocol.Reply(await BrowserControlProtocol.ReadFrame(pipe, deadline.Token));
                BrowserControlProtocol.EqualToken(reply.Nonce, nonce).ShouldBeTrue();
                reply.Pid.ShouldBe(owner.Pid);
                reply.BirthId.ShouldBe(owner.BirthId);
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

    [Fact]
    public async Task TruncatedClientEofPreservesNextAuthenticatedRequestAndOwner()
    {
        var root = PrivateRoot();
        try
        {
            var source = Source();
            var application = new BrowserApplication(new BrowserRuntimeFactory());
            await using var server = new BrowserControlServer(application, source, "host-pane", BrowserControlProtocol.NewToken(), root);
            var path = BrowserControlProtocol.PathFor(root, source);
            var owner = BrowserControlProtocol.ReadRegistration(path)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (var pipe = new NamedPipeClientStream(".", owner.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(deadline.Token);
                var truncated = new byte[6];
                BinaryPrimitives.WriteInt32LittleEndian(truncated, 10);
                truncated[4] = (byte)'{';
                truncated[5] = (byte)'}';
                await pipe.WriteAsync(truncated, deadline.Token);
                await pipe.FlushAsync(deadline.Token);
            }

            // Availability and ownership are the contract; no browser or tracker operation is needed.
            var reply = await BrowserControlServer.SendAsync(owner, source, new("status"), deadline.Token);
            reply.Pid.ShouldBe(owner.Pid);
            reply.BirthId.ShouldBe(owner.BirthId);
            BrowserControlProtocol.ReadRegistration(path).ShouldBe(owner);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BrowserSource Source() => BrowserSource.Create(Path.Combine(Path.GetTempPath(), "herdr.sock"), "workspace", "tab", "source-pane", Path.GetTempPath());
    private static BrowserHostRegistration Owner(BrowserSource source) => new(BrowserControlProtocol.NewPipeName(),
        BrowserControlProtocol.NewToken(), Environment.ProcessId, BrowserControlProtocol.ProcessBirthId(Environment.ProcessId), "host-pane", source, BrowserControlProtocol.NewToken());
    private static string PrivateRoot() => BrowserPrivateDirectory.Open(Path.Combine(Path.GetTempPath(), "twig-native-control-" + Guid.NewGuid().ToString("N")));

    private sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "This contract uses the Linux kernel's process and boot identities.";
        }
    }
}
