using System.ComponentModel;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class CommandPipeClientTests
{
    [Fact]
    public async Task SameAccountPeerIsVerifiedBeforeRealInvocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new PipeFixture("same-account");
        var result = await fixture.RunAsync(expectInvocation: true,
            () => DaemonCommandClient.ExecuteAsync(["connection", "list", "-o", "json"]));

        result.Exit.ShouldBe(0);
        result.Received.ShouldBeTrue();
        fixture.Error.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("different-user")]
    [InlineData("missing-user")]
    [InlineData("native-query-failure")]
    [InlineData("token-access-failure")]
    public async Task UnverifiedPeerClosesPipeWithoutAnyInvocationBytes(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var other = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        (other != identity.User).ShouldBeTrue("fixture must not resolve the actual server user");
        using var fixture = new PipeFixture(mode);
        var queries = 0;

        var result = await fixture.RunAsync(expectInvocation: false,
            () => DaemonCommandClient.ExecuteAsync(["connection", "list", "-o", "json"], _ =>
        {
            queries++;
            return mode switch
            {
                "different-user" => other,
                "missing-user" => null,
                "native-query-failure" => throw new Win32Exception(5, "Synthetic peer token query refused."),
                "token-access-failure" => throw new SecurityException("Synthetic token access refused."),
                _ => throw new InvalidOperationException("Unknown test mode."),
            };
        }));

        result.Received.ShouldBeFalse("even the first Invocation byte must be withheld");
        result.Exit.ShouldBe(1);
        queries.ShouldBe(1);
        using var error = JsonDocument.Parse(fixture.Error.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("No command was dispatched.");
    }

    private sealed class PipeFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "twig-command-peer-test-" + Guid.NewGuid().ToString("N"));
        private readonly string? previousRoot = Environment.GetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT");
        private readonly TextWriter previousError = Console.Error;
        private readonly NamedPipeServerStream server;
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        private readonly string caseName;
        internal StringWriter Error { get; } = new();

        internal PipeFixture(string caseName)
        {
            this.caseName = caseName;
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            var id = Guid.NewGuid();
            File.WriteAllText(Path.Combine(data, "daemon.id"), id.ToString("D"));
            using (var file = File.Create(Path.Combine(root, "current.json")))
            using (var writer = new Utf8JsonWriter(file))
            {
                writer.WriteStartObject();
                writer.WriteString("privateDataDir", data);
                writer.WriteEndObject();
            }
            server = new(CommandEndpoint.PipeName(data, id), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            Environment.SetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT", root);
            Console.SetError(Error);
            Stage("ready");
        }

        internal async Task<(int Exit, bool Received)> RunAsync(bool expectInvocation, Func<Task<int>> execute)
        {
            using var interrupt = deadline.Token.Register(server.Dispose);
            var received = ReceiveAsync(expectInvocation);
            Stage("client-start");
            var client = execute();
            try
            {
                var pending = new List<Task> { received, client };
                while (pending.Count != 0)
                {
                    var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token);
                    await completed;
                    pending.Remove(completed);
                }
                Stage("client-and-server-completed");
                return (await client, await received);
            }
            finally
            {
                server.Dispose();
                var drained = Task.WhenAll(client, received);
                await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(2)));
                if (!drained.IsCompleted)
                    throw new TimeoutException("Command-pipe fixture tasks did not stop after closing the server.");
                // Preserve the first failure while observing both completed tasks.
                await drained.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Stage("tasks-drained");
            }
        }

        private async Task<bool> ReceiveAsync(bool expectInvocation)
        {
            Stage("server-wait");
            await server.WaitForConnectionAsync(deadline.Token);
            Stage("server-connected");
            if (!expectInvocation)
            {
                var first = new byte[1];
                var received = await server.ReadAsync(first, deadline.Token) != 0;
                Stage(received ? "unexpected-first-byte" : "zero-invocation-bytes");
                server.Dispose();
                return received;
            }
            Stage("read-invocation");
            var (kind, _) = await CommandProtocol.ReadAsync(server, deadline.Token);
            kind.ShouldBe(FrameKind.Invocation);
            Stage("invocation-drained");
            await CommandProtocol.WriteAsync(server, FrameKind.Exit, "0", deadline.Token);
            Stage("exit-written");
            return true;
        }

        private void Stage(string stage)
        {
            var trace = Environment.GetEnvironmentVariable("TWIG_TEST_TRACE");
            if (string.IsNullOrWhiteSpace(trace)) return;
            Directory.CreateDirectory(trace);
            File.AppendAllText(Path.Combine(trace, "command-pipe-fixture.tsv"),
                $"{DateTimeOffset.UtcNow:O}\t{caseName}\t{stage}{Environment.NewLine}");
        }

        public void Dispose()
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT", previousRoot);
            server.Dispose();
            deadline.Dispose();
            Error.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
