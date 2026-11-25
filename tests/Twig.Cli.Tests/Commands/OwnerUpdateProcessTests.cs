using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Providers.Native;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class OwnerUpdateProcessTests
{
    private sealed class QualificationFact : FactAttribute
    {
        public QualificationFact()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("TWIG_TEST_OWNER_CERT") is null)
                Skip = "Set TWIG_TEST_OWNER_CERT to an already trusted localhost certificate for real owner qualification.";
        }
    }

    [QualificationFact, SupportedOSPlatform("windows")]
    public async Task MatchedCliFreezesInputAndFindsOnlyTheOriginalSubmissionAfterRestart()
    {
        var id = Guid.NewGuid().ToString("D");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig.OwnerFixtures", id);
        var home = Path.Combine(root, "home");
        var work = Path.Combine(root, "work");
        var security = new DirectorySecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(security);
        Process? owner = null;
        try
        {
            Directory.CreateDirectory(work);
            InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
            var setup = await new NativeConnectionSetup(home).InitializeFreshAsync(new(work, new NativeTwigSetupTarget("qualification")));
            setup.IsSuccess.ShouldBeTrue(setup.IsSuccess ? "" : setup.Error.Message);
            using var bindings = new ConnectionBindingService(home);
            var inventory = (await NativeCoreExecution.InventoryAsync(bindings.RegistryPath)).Value;
            (await NativeCoreExecution.ActivateAsync(bindings.RegistryPath, inventory, inventory.Digest, true, true)).IsSuccess.ShouldBeTrue();
            var config = new TwigConfiguration { Provider = "twig", NativeDatabase = "qualification" };
            var factory = new CoreWorkspaceFactory(config, TwigPaths.BuildPaths(Path.Combine(work, ".twig"), config, work),
                bindings, Substitute.For<IAuthenticationProvider>());
            using var handle = (await factory.OpenAsync(default)).Value;
            var seed = (await handle.Workspace.StageSeedAsync(NativeProvider.ItemType, FieldValues.Empty
                .Set(NativeProvider.Title, FieldValue.String("original")).Set(NativeProvider.StateField, FieldValue.String("Open")), default)).Value;
            var preview = (await handle.Workspace.PreviewDraftAsync(seed.Key, default)).Value;
            (await handle.Workspace.SaveDraftAsync(preview, default)).Value.EffectsVerified.ShouldBeTrue();
            var cli = Path.Combine(AppContext.BaseDirectory, "twig.exe");
            var server = Path.Combine(AppContext.BaseDirectory, "Twig.Server.exe");
            var certificate = Environment.GetEnvironmentVariable("TWIG_TEST_OWNER_CERT")!;
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port.ToString();
            socket.Stop();
            await File.WriteAllLinesAsync(Path.Combine(root, "owner.fixture"), [port, certificate, server]);
            await File.WriteAllTextAsync(Path.Combine(root, "current.json"),
                $$"""{"privateDataDir":{{JsonSerializer.Serialize(home)}}}""");
            await StartOwner();
            var review = Guid.NewGuid().ToString("D");
            var prepared = await Cli("frozen from stdin", "update", NativeProvider.Title, "--stdin", "--id", "1", "--review", review, "--prepare");
            prepared.Code.ShouldBe(1, prepared.Error);
            var saved = Match(prepared.Output);
            saved.GetProperty("state").GetString().ShouldBe("Queued");
            // Reject only the local transaction that would append a new authority acknowledgement.
            await MirrorSql("""
                CREATE TRIGGER reject_update_receipt BEFORE UPDATE ON core_snapshots
                WHEN length(replace(CAST(NEW.document AS TEXT),'authority:durable-sqlite-transaction',''))
                    -length(CAST(NEW.document AS TEXT)) < length(replace(CAST(OLD.document AS TEXT),'authority:durable-sqlite-transaction',''))
                    -length(CAST(OLD.document AS TEXT))
                BEGIN SELECT RAISE(ABORT,'fixture acknowledgement conflict'); END;
                """);
            // Lose the submitter's final reply immediately after the owner proves its binding.
            var run = await File.ReadAllLinesAsync(Path.Combine(home, "owner.run"));
            using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", CommandEndpoint.ExecutionPipe(Guid.Parse(id), Guid.Parse(run[0])),
                System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(2000);
                await CommandProtocol.WriteAsync(pipe, FrameKind.Negotiate, OwnerFixtureSelection.Protocol);
                (await CommandProtocol.ReadAsync(pipe)).Kind.ShouldBe(FrameKind.Negotiate);
                var request = new CommandInvocation(2, ["update", "--submit", review, "--id", "1"], work, home, true);
                await CommandProtocol.WriteAsync(pipe, FrameKind.Invocation, JsonSerializer.Serialize(request, CommandWireJson.Default.CommandInvocation));
                (await CommandProtocol.ReadAsync(pipe)).Kind.ShouldBe(FrameKind.Bound);
            }
            await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (_, _) =>
            {
                var duplicate = await Cli(null, "update", NativeProvider.Title, "frozen from stdin", "--id", "1", "--review", review);
                duplicate.Code.ShouldBe(1, duplicate.Error);
                Match(duplicate.Output).GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
                Match(duplicate.Output).GetProperty("action").GetGuid().ShouldBe(saved.GetProperty("action").GetGuid());
            });
            var replay = await Cli(null, "update", NativeProvider.Title, "changed input", "--id", "1", "--review", review);
            replay.Code.ShouldBe(2);
            replay.Error.ShouldContain("different intent");
            var lateCancel = await Cli(null, "update", "--cancel", review, "--id", "1");
            lateCancel.Code.ShouldBe(1);
            Match(lateCancel.Output).GetProperty("state").GetString().ShouldNotBe("Cancelled");
            await Restart();
            var found = await Cli(null, "update", "--find", "--id", "1");
            found.Code.ShouldBe(1, found.Error);
            Match(found.Output).GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
            await MirrorSql("DROP TRIGGER reject_update_receipt;");
            var settled = await Cli(null, "update", "--observe", review, "--id", "1");
            settled.Code.ShouldBe(0, settled.Error);
            Match(settled.Output).GetProperty("state").GetString().ShouldBe("Complete");
            var snapshot = (await handle.Workspace.SnapshotAsync(default)).Value;
            snapshot.Items.Single().Fields[NativeProvider.Title].Text.ShouldBe("frozen from stdin");
            snapshot.Journal.Count(j => j.Status == OpStatus.Verified).ShouldBe(2); // Seed plus the single update.
            var cancelled = Guid.NewGuid().ToString("D");
            await Cli(null, "update", NativeProvider.Title, "never sent", "--id", "1", "--review", cancelled, "--prepare");
            await Restart();
            var paused = await Cli(null, "update", "--submit", cancelled, "--id", "1");
            paused.Code.ShouldBe(1);
            Match(paused.Output).GetProperty("state").GetString().ShouldBe("Queued");
            Match((await Cli(null, "update", "--cancel", cancelled, "--id", "1")).Output).GetProperty("state").GetString().ShouldBe("Cancelled");
            var ambiguous = await Cli(null, "update", "--find", "--id", "1");
            ambiguous.Code.ShouldBe(1);
            ambiguous.Output.ShouldContain("\"ambiguous\":true");
            ambiguous.Output.ShouldNotContain("never sent");
            ambiguous.Output.ShouldNotContain("frozen from stdin");

            async Task MirrorSql(string sql)
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(work, ".twig", "core.db")};Pooling=False");
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }

            async Task Restart()
            {
                (await Cli(null, "server", "stop", "-o", "json")).Code.ShouldBe(0);
                await owner!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                owner.Dispose();
                owner = null;
                await StartOwner();
            }
            async Task StartOwner()
            {
                owner = Run(server, false, ["--data-dir", home, "--port", port, "--https-certificate", certificate]);
                for (var i = 0; i < 60; i++)
                {
                    if (owner.HasExited) throw new InvalidOperationException("Qualification owner exited: " + await owner.StandardError.ReadToEndAsync());
                    if (File.Exists(Path.Combine(home, "owner.run")) && (await Cli(null, "bench", "list", "-o", "json")).Code == 0) return;
                    await Task.Delay(100);
                }
                throw new TimeoutException("Owner pipe did not become ready.");
            }
            async Task<(int Code, string Output, string Error)> Cli(string? stdin, params string[] arguments)
            {
                using var process = Run(cli, true, arguments);
                try
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    return (process.ExitCode, await output, await error);
                }
                finally { if (!process.HasExited) process.Kill(true); }
            }
            Process Run(string exe, bool input, string[] args)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = work,
                    RedirectStandardInput = input, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                start.Environment["TWIG_USER_HOME"] = home;
                start.Environment["TWIG_SERVER_INSTALL_ROOT"] = root;
                start.Environment["TWIG_OWNER_FIXTURE_ID"] = id;
                return Process.Start(start)!;
            }
        }
        finally
        {
            if (owner is not null)
            {
                if (!owner.HasExited) owner.Kill(true);
                await owner.WaitForExitAsync();
                owner.Dispose();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static JsonElement Match(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("matches")[0].Clone();
    }
}
