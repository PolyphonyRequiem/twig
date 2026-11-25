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
    public Task MatchedCliFreezesInputAndFindsOnlyTheOriginalSubmissionAfterRestart() => RunOwnerScenario(false);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task DisjointProviderAndLocalEditsSettleWhileOriginalAuthorityTransactionIsParked() => RunOwnerScenario(true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task DisjointAdoPatchAndLocalEditSettleBeforeParkedResponse() => RunOwnerScenario(true, remote: "ado");

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task DisjointGitHubMutationAndLocalEditSettleBeforeParkedResponse() => RunOwnerScenario(true, remote: "github");

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task LaterLocalEditAfterDurableAcknowledgementSurvivesRestartAndConflictsWithOriginalDelivery()
        => RunOwnerScenario(false, deliveryConflict: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task ThreeReceiptCasLossesYieldOriginalPendingThenRestartOnlyDelivers() => RunOwnerScenario(false, casLosses: true);

    [SupportedOSPlatform("windows")]
    private static async Task RunOwnerScenario(bool concurrent, bool deliveryConflict = false, string? remote = null, bool casLosses = false)
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
        GitHubCoreSpineTests.Fixture? github = null;
        try
        {
            Directory.CreateDirectory(work);
            using var bindings = new ConnectionBindingService(home);
            var config = new TwigConfiguration { Provider = "twig", NativeDatabase = "qualification" };
            var factory = new CoreWorkspaceFactory(config, TwigPaths.BuildPaths(Path.Combine(work, ".twig"), config, work),
                bindings, Substitute.For<IAuthenticationProvider>());
            if (remote == "ado") await new NativePublicationConsumerTests().InitializeOwnerWorkspace(home, work);
            else if (remote == "github")
            {
                github = new GitHubCoreSpineTests.Fixture(home, root: work);
                await github.Initialize(explicitStore: false);
            }
            else
            {
                InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
                var setup = await new NativeConnectionSetup(home).InitializeFreshAsync(new(work, new NativeTwigSetupTarget("qualification")));
                setup.IsSuccess.ShouldBeTrue(setup.IsSuccess ? "" : setup.Error.Message);
                var inventory = (await NativeCoreExecution.InventoryAsync(bindings.RegistryPath)).Value;
                (await NativeCoreExecution.ActivateAsync(bindings.RegistryPath, inventory, inventory.Digest, true, true)).IsSuccess.ShouldBeTrue();
            }
            using var handle = remote is null ? (await factory.OpenAsync(default)).Value : null;
            for (int i = 0; remote is null && i < (concurrent ? 3 : 1); i++)
            {
                var seed = (await handle!.Workspace.StageSeedAsync(NativeProvider.ItemType, FieldValues.Empty
                    .Set(NativeProvider.Title, FieldValue.String("original")).Set(NativeProvider.StateField, FieldValue.String("Open")), default)).Value;
                var preview = (await handle.Workspace.PreviewDraftAsync(seed.Key, default)).Value;
                (await handle.Workspace.SaveDraftAsync(preview, default)).Value.EffectsVerified.ShouldBeTrue();
            }
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
            var review = Guid.NewGuid().ToString("D");
            await StartOwner();
            if (concurrent)
            {
                var (aId, bId, cId) = remote is null ? ("1", "2", "3") : ("42", "43", "44");
                var title = remote switch { "ado" => "System.Title", "github" => Twig.Providers.GitHub.GitHubProvider.Title, _ => NativeProvider.Title };
                var a = Cli(null, "update", title, "A", "--id", aId, "--review", review);
                var waiting = Stopwatch.StartNew();
                while (true)
                {
                    if (remote is null ? (await Cli(null, "update", "--find", "--id", aId)).Output.Contains("\"Running\"", StringComparison.Ordinal)
                        : File.Exists(Path.Combine(root, "provider-a"))) break;
                    if (a.IsCompleted) throw new InvalidOperationException("Original never parked: " + (await a));
                    if (waiting.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Original never entered dispatch.");
                    await Task.Delay(50);
                }
                var b = await Cli(null, "update", title, "B", "--id", bId, "--review", Guid.NewGuid().ToString());
                b.Code.ShouldBe(0, b.Error + b.Output);
                if (remote is not null) File.Exists(Path.Combine(root, "provider-b")).ShouldBeTrue();
                var c = await Cli(null, "update", title, "C", "--id", cId, "--local");
                c.Code.ShouldBe(0, c.Error + c.Output);
                if (remote is not null)
                {
                    var note = await Cli(null, "note", "--id", bId, "--text", "disjoint note", "--review", Guid.NewGuid().ToString());
                    note.Code.ShouldBe(0, note.Output + note.Error);
                    Match(note.Output).GetProperty("state").GetString().ShouldBe("Complete");
                }
                a.IsCompleted.ShouldBeFalse();
                var aResult = await a;
                aResult.Code.ShouldBe(0, aResult.Error + aResult.Output);
                waiting.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
                await Restart();
                var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(work, ".twig", CoreWorkspaceFactory.FileName)),
                    CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
                var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(document.DatabasePath)).Value;
                var stored = (await store.ReadAsync(document.Connection, default)).Value;
                stored.Items.Single(item => item.Key.NativeId == (remote == "github" ? "I_opaque" : aId)).Fields[title].Text.ShouldBe("A");
                stored.Items.Single(item => item.Key.NativeId == (remote == "github" ? "I_43" : bId)).Fields[title].Text.ShouldBe("B");
                stored.RetainedItems.Single(item => item.Key.NativeId == (remote == "github" ? "I_44" : cId)).Fields[title].Text.ShouldBe("C");
                return;
            }
            if (deliveryConflict)
                (await Cli(null, "update", NativeProvider.Title, "later intent", "--id", "1", "--local")).Code.ShouldBe(0);
            var prepared = await Cli("frozen from stdin", "update", NativeProvider.Title, "--stdin", "--id", "1", "--review", review, "--prepare");
            prepared.Code.ShouldBe(1, prepared.Error);
            var saved = Match(prepared.Output);
            saved.GetProperty("state").GetString().ShouldBe("Queued");
            if (casLosses)
            {
                var pending = await Cli(null, "update", "--submit", review, "--id", "1");
                pending.Code.ShouldBe(1, pending.Output + pending.Error);
                var row = Match(pending.Output);
                row.GetProperty("state").GetString().ShouldBe("Pending");
                row.GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
                var before = (await handle!.Workspace.SnapshotAsync(default)).Value;
                before.RetainedItems.Length.ShouldBe(3);
                var original = before.Journal.Single(j => j.Status == OpStatus.Applying);
                var remoteItem = (await handle.Workspace.ObserveItemsAsync([before.Items.Single().Key], default)).Value.Items.Single();
                remoteItem.Fields[NativeProvider.Title].Text.ShouldBe("frozen from stdin");
                casLosses = false;
                await Restart();
                var resumed = await Cli(null, "update", "--observe", review, "--id", "1");
                resumed.Code.ShouldBe(0, resumed.Error + resumed.Output);
                Match(resumed.Output).GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
                var after = (await handle.Workspace.SnapshotAsync(default)).Value;
                after.Journal.Single(j => j.Attempt == original.Attempt).Status.ShouldBe(OpStatus.Verified);
                after.Items.Single().Revision.ShouldBe(remoteItem.Revision);
                after.RetainedItems.Length.ShouldBe(3);
                return;
            }
            // Reject only the local transaction that would append a new authority acknowledgement.
            await MirrorSql("""
                CREATE TRIGGER reject_update_receipt BEFORE UPDATE ON core_snapshots
                WHEN length(replace(CAST(NEW.document AS TEXT),'authority:durable-sqlite-transaction',''))
                    -length(CAST(NEW.document AS TEXT)) < length(replace(CAST(OLD.document AS TEXT),'authority:durable-sqlite-transaction',''))
                    -length(CAST(OLD.document AS TEXT))
                BEGIN SELECT RAISE(ABORT,'fixture acknowledgement conflict'); END;
                """);
            if (deliveryConflict)
            {
                var interrupted = await Cli(null, "update", "--submit", review, "--id", "1");
                interrupted.Code.ShouldBe(1, interrupted.Error);
                var originalRevision = (await handle!.Workspace.ObserveItemsAsync([ItemKey.Remote(handle.Workspace.Connection.Provider,
                    handle.Workspace.Connection.ResourceScope, "1")], default)).Value.Items.Single();
                originalRevision.Fields[NativeProvider.Title].Text.ShouldBe("frozen from stdin");
                (await Cli(null, "update", NativeProvider.Title, "later intent", "--id", "1", "--local")).Code.ShouldBe(0);
                await Restart();
                await MirrorSql("DROP TRIGGER reject_update_receipt;");
                var conflict = await Cli(null, "update", "--observe", review, "--id", "1");
                Match(conflict.Output).GetProperty("state").GetString().ShouldBe("DeliveryConflict", conflict.Error);
                await Restart();
                var again = await Cli(null, "update", "--submit", review, "--id", "1");
                Match(again.Output).GetProperty("state").GetString().ShouldBe("DeliveryConflict");
                Match(again.Output).GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
                var state = (await handle.Workspace.SnapshotAsync(default)).Value;
                state.Items.Single().Revision.ShouldBe(originalRevision.Revision);
                state.Journal.Count(row => row.Status == OpStatus.Verified).ShouldBe(2);
                state.RetainedItems.Single().Fields[NativeProvider.Title].Text.ShouldBe("later intent");
                return;
            }
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
            var snapshot = (await handle!.Workspace.SnapshotAsync(default)).Value;
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
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
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
                if (concurrent && exe == server && remote is null) start.Environment["TWIG_TEST_PARK_ORIGINAL_REVIEW"] = review;
                if ((remote is not null || casLosses) && exe == server)
                {
                    start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(OwnerUpdateProcessTests).Assembly.Location;
                    start.Environment["DOTNET_ADDITIONAL_DEPS"] = Path.ChangeExtension(typeof(OwnerUpdateProcessTests).Assembly.Location, ".deps.json");
                    start.Environment["TWIG_TEST_REMOTE_PROVIDER"] = remote ?? "cas";
                }
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
            github?.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static JsonElement Match(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.GetProperty("matches")[0].Clone();
    }
}
