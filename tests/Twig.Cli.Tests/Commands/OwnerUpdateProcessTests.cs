using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Providers.Native;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class OwnerUpdateProcessTests(Xunit.Abstractions.ITestOutputHelper log)
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
    public Task AcknowledgedSeedReservesItsNewItemButLetsADisjointUpdateFinishBeforeReadback()
        => RunOwnerScenario(false, create: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task NativeCreateOwnerExitBeforeMirrorAcknowledgementRecoversTheOriginalAuthorityEffect()
        => RunOwnerScenario(false, create: true, reboot: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task AdoCreateKeepsItsParentQueuedWhileDisjointWorkCompletesBeforeReadback()
        => RunOwnerScenario(false, create: true, remote: "ado");

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task GitHubCreateIsPublishedOnceThroughTheMatchedDaemon()
        => RunOwnerScenario(false, create: true, remote: "github");

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task LaterLocalEditAfterDurableAcknowledgementSurvivesRestartAndConflictsWithOriginalDelivery()
        => RunOwnerScenario(false, deliveryConflict: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task ThreeReceiptCasLossesYieldOriginalPendingThenRestartOnlyDelivers() => RunOwnerScenario(false, casLosses: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task PendingReceiptSettlesOnTheSameBootWithoutAnotherSend() => RunOwnerScenario(false, casLosses: true, sameBootReceipt: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task ThreeBeginCasLossesResumeOnlyTheLocalReducerOnTheSameBoot() => RunOwnerScenario(false, casLosses: true, begin: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task OwnerChangeNeverResumesTheParkedPreDispatchReducer() => RunOwnerScenario(false, casLosses: true, begin: true, reboot: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task ChangedCaptureDuringRescheduledBeginRequiresReviewAndSendsNothing() => RunOwnerScenario(false, casLosses: true, begin: true, editPending: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task PlanCliAfterOwnerLossNeverConsumesAnUnadmittedCapture() => RunOwnerScenario(false, interruptedPlan: true);

    [QualificationFact, SupportedOSPlatform("windows")]
    public Task PlanCliWithholdsScopedSettlementWithoutARecoverableBenchPublication()
        => RunOwnerScenario(false, interruptedPlan: true, admittedPlan: true);

    [SupportedOSPlatform("windows")]
    private async Task RunOwnerScenario(bool concurrent, bool deliveryConflict = false, string? remote = null, bool casLosses = false,
        bool begin = false, bool reboot = false, bool editPending = false, bool sameBootReceipt = false, bool create = false,
        bool interruptedPlan = false, bool admittedPlan = false)
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
            if (interruptedPlan)
            {
                var user = UserBenchStore.ForRegistry(bindings.RegistryPath);
                var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Plan fixture", []), 0)).Value;
                var status = (await user.ReadPlanStatusAsync(bench.Id)).Value;
                (await user.ChangePlanModeAsync(status.UserInstance, bench.Id, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
                void Configure(IServiceCollection services) => services.AddSingleton<IConnectionBindingService>(bindings);
                using var editor = (await NativeItemEditor.OpenAsync(work, bench.Id, handle!.Source, Configure, default)).Value;
                var form = (await editor.SelectAsync((await handle.Workspace.SnapshotAsync(default)).Value.Items.Single().Key)).Value;
                (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set(NativeProvider.Title, admittedPlan
                    ? form.Values[NativeProvider.Title] : FieldValue.String("never published")))).IsSuccess.ShouldBeTrue();
                var staged = (await user.ReadPlanStatusAsync(bench.Id)).Value;
                var plan = (await NativePlanReview.ReviewAsync(work, bench.Id, staged.Sources, Configure, default)).Value;
                var retained = (await user.RetainPlanReviewAsync(plan.Document, default)).Value;
                if (admittedPlan)
                {
                    plan.Operations.ShouldBeEmpty();
                    (await user.AdvancePlanReviewAsync(retained, true, 0, 1, default)).IsSuccess.ShouldBeTrue();
                    (await user.AdmitPlanCaptureAsync(retained, 0, plan.Document.Scopes.Single().Captures.Single().Id, default)).IsSuccess.ShouldBeTrue();
                }
                await StartOwner();
                foreach (var (verb, state) in new[] { ("observe", "Unadmitted"), ("continue", "Unadmitted"),
                    ("abandon-unadmitted", "Abandoned"), ("observe", "Abandoned") })
                {
                    var reply = await Cli(null, "plan", verb, plan.Digest, "--bench", bench.Id.ToString("D"), "-o", "json");
                    reply.Code.ShouldBe(0, reply.Error);
                    using var json = JsonDocument.Parse(reply.Output);
                    json.RootElement.GetProperty("units")[0].GetProperty("state").GetString().ShouldBe(admittedPlan ? "Admitted" : state);
                    if (admittedPlan && verb == "continue") json.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("withheld");
                }
                (await user.ReadEditorCapturesAsync(default)).Value.Single().Settled.ShouldBeFalse();
                (await user.ReadAsync()).Value.Benches.Single().Selectors.ShouldBeEmpty();
                (await handle.Workspace.ObserveItemsAsync([form.Item], default)).Value.Items.Single().Fields[NativeProvider.Title].Text.ShouldBe("original");
                return;
            }
            var displayBench = Guid.NewGuid();
            CoreWorkspaceDocument? projection = null;
            if (remote == "ado")
            {
                var marker = await File.ReadAllBytesAsync(Path.Combine(work, ".twig", CoreWorkspaceFactory.FileName));
                projection = JsonSerializer.Deserialize(marker, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
                var pin = ItemKey.Remote(projection.Connection.Provider, projection.Connection.ResourceScope, "44");
                var pinned = await new UserBenchStore(Path.Combine(home, "benches.db")).SaveAsync(new(displayBench, "Unrelated pin",
                    [new(SelectorKind.Pin, pin, connection: projection.Connection)]), 0);
                pinned.IsSuccess.ShouldBeTrue();
            }
            await StartOwner();
            if (create && remote is not null)
            {
                var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(work, ".twig", CoreWorkspaceFactory.FileName)),
                    CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
                var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(document.DatabasePath)).Value;
                var current = (await store.ReadAsync(document.Connection, default)).Value;
                var ado = remote == "ado";
                var title = ado ? "System.Title" : Twig.Providers.GitHub.GitHubProvider.Title;
                var fields = FieldValues.Empty.Set(title, FieldValue.String("reviewed seed")).Set(
                    ado ? "System.State" : Twig.Providers.GitHub.GitHubProvider.StateField, FieldValue.String(ado ? "To Do" : "OPEN"));
                var seed = new Seed(ItemKey.Local(Guid.NewGuid()), ado ? "Fixture.Task" : Twig.Providers.GitHub.GitHubProvider.Untyped, fields,
                    parent: ado ? ItemKey.Remote(document.Connection.Provider, document.Connection.ResourceScope, "42") : null,
                    parentRelation: ado ? "Native.ParentForward" : null);
                (await store.CommitAsync(document.Connection, current.Version, new(current.Version + 1, current.Items, current.Relationships,
                    current.RetainedItems.Add(seed), current.Plans, current.Journal, current.Mappings, current.Benches), default)).Value.ShouldBe(current.Version + 1);
                var seedId = seed.Key.LocalId.ToString("D");
                var submit = Cli(null, "seed", "publish", "--local-id", seedId, "--review", review);
                if (ado)
                {
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(root, "parked-remote-readback")))
                    {
                        if (submit.IsCompleted) throw new InvalidOperationException("ADO did not reach readback: " + await submit);
                        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
                        await Task.Delay(50);
                    }
                    var parent = await Cli(null, "update", title, "parent", "--id", "42", "--review", Guid.NewGuid().ToString());
                    Match(parent.Output).GetProperty("state").GetString().ShouldBe("Queued");
                    var unrelated = await Cli(null, "update", title, "disjoint", "--id", "43", "--review", Guid.NewGuid().ToString());
                    unrelated.Code.ShouldBe(0, unrelated.Error + unrelated.Output);
                    await File.WriteAllTextAsync(Path.Combine(root, "release-remote-readback"), "");
                }
                var published = await submit;
                published.Code.ShouldBe(0, published.Output + published.Error);
                var final = (await store.ReadAsync(document.Connection, default)).Value;
                final.VerifiedSeedTarget(document.Connection, seed.Key)!.NativeId.ShouldBe(ado ? "45" : "I_opaque");
                await Restart();
                var duplicate = await Cli(null, "seed", "publish", "--local-id", seedId, "--review", review);
                Match(duplicate.Output).GetProperty("submission").GetGuid().ShouldBe(Match(published.Output).GetProperty("submission").GetGuid());
                File.ReadAllLines(Path.Combine(root, "remote-creates")).Length.ShouldBe(1);
                return;
            }
            if (create)
            {
                var seed = (await handle!.Workspace.StageSeedAsync(NativeProvider.ItemType, FieldValues.Empty
                    .Set(NativeProvider.Title, FieldValue.String("reviewed seed")).Set(NativeProvider.StateField, FieldValue.String("Open")), default)).Value;
                var seedId = seed.Key.LocalId.ToString("D");
                var publication = Cli(null, "seed", "publish", "--local-id", seedId, "--review", review);
                var timer = Stopwatch.StartNew();
                async Task Parked(string phase)
                {
                    while (!File.Exists(Path.Combine(root, "parked-" + phase)))
                    {
                        if (publication.IsCompleted) throw new InvalidOperationException("Create did not park: " + await publication);
                        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));
                        await Task.Delay(50);
                    }
                }
                await Parked("receipt");
                if (reboot)
                {
                    var first = Match((await Cli(null, "seed", "--find", "--local-id", seedId)).Output);
                    owner!.Kill(true);
                    await owner.WaitForExitAsync();
                    owner.Dispose();
                    owner = null;
                    (await publication).Code.ShouldNotBe(0);
                    await File.WriteAllTextAsync(Path.Combine(root, "release-receipt"), "");
                    await File.WriteAllTextAsync(Path.Combine(root, "release-readback"), "");
                    await StartOwner();
                    var waitingForProof = await Cli(null, "update", NativeProvider.Title, "blocked after restart", "--id", "1", "--review", Guid.NewGuid().ToString());
                    Match(waitingForProof.Output).GetProperty("state").GetString().ShouldBe("Queued");
                    var observedCreate = await Cli(null, "seed", "--observe", review, "--local-id", seedId);
                    observedCreate.Code.ShouldBe(0, observedCreate.Output + observedCreate.Error);
                    Match(observedCreate.Output).GetProperty("submission").GetGuid().ShouldBe(first.GetProperty("submission").GetGuid());
                    var originalCreate = (await handle.Workspace.SnapshotAsync(default)).Value;
                    originalCreate.VerifiedSeedTarget(handle.Workspace.Connection, seed.Key)!.NativeId.ShouldBe("2");
                    originalCreate.Plans.SelectMany(p => p.Operations).Count(op => op.Item == seed.Key).ShouldBe(1);
                    return;
                }
                var waiting = await Cli(null, "update", NativeProvider.Title, "disjoint", "--id", "1", "--review", Guid.NewGuid().ToString());
                Match(waiting.Output).GetProperty("state").GetString().ShouldBe("Queued");
                await File.WriteAllTextAsync(Path.Combine(root, "release-receipt"), "");
                await Parked("readback");
                var before = (await handle.Workspace.SnapshotAsync(default)).Value;
                before.Mappings.Single(m => m.Local == seed.Key).Remote.NativeId.ShouldBe("2");
                before.VerifiedSeedTarget(handle.Workspace.Connection, seed.Key).ShouldBeNull();
                (await handle.Workspace.ResolveTargetAsync(CoreHostContext.LocalAlias(seed.Key), false, default)).Value.ShouldBe(seed.Key);
                while (!(await Cli(null, "update", "--find", "--id", "1")).Output.Contains("\"Complete\"", StringComparison.Ordinal))
                    timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));
                var blocked = await Cli(null, "update", NativeProvider.Title, "same item", "--id", "2", "--review", Guid.NewGuid().ToString());
                Match(blocked.Output).GetProperty("state").GetString().ShouldBe("Queued");
                publication.IsCompleted.ShouldBeFalse();
                await File.WriteAllTextAsync(Path.Combine(root, "release-readback"), "");
                var published = await publication;
                published.Code.ShouldBe(0, published.Error + published.Output);
                await Restart();
                var recovered = await Cli(null, "seed", "--observe", review, "--local-id", seedId);
                Match(recovered.Output).GetProperty("submission").GetGuid().ShouldBe(Match(published.Output).GetProperty("submission").GetGuid());
                var final = (await handle.Workspace.SnapshotAsync(default)).Value;
                final.VerifiedSeedTarget(handle.Workspace.Connection, seed.Key)!.NativeId.ShouldBe("2");
                return;
            }
            if (concurrent)
            {
                var (aId, bId, cId) = remote is null ? ("1", "2", "3") : ("42", "43", "44");
                var title = remote switch { "ado" => "System.Title", "github" => Twig.Providers.GitHub.GitHubProvider.Title, _ => NativeProvider.Title };
                using var http = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true, UseProxy = false })
                    { BaseAddress = new($"https://localhost:{port}") };
                string? displayPath = null;
                if (projection is not null)
                {
                    using var status = JsonDocument.Parse(await http.GetStringAsync("/api/v1/status"));
                    http.DefaultRequestHeaders.Add("X-Twig-Execution", status.RootElement.GetProperty("executionSession").GetString());
                    http.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
                    using var enrolled = await http.PostAsync("/api/v1/windows-enroll", new StringContent(
                        $$"""{"viewId":null,"profileId":"{{Guid.NewGuid():D}}"}""", System.Text.Encoding.UTF8, "application/json"));
                    var enrollment = await enrolled.Content.ReadAsStringAsync();
                    enrolled.IsSuccessStatusCode.ShouldBeTrue(enrollment);
                    using var pair = JsonDocument.Parse(enrollment);
                    http.DefaultRequestHeaders.Authorization = new("Bearer", pair.RootElement.GetProperty("contentCapability").GetString());
                    var view = pair.RootElement.GetProperty("viewId").GetGuid();
                    displayPath = $"/api/v2/views/{view:D}/benches/{displayBench:D}/partitions/{projection.DatabaseInstance:D}/page";
                    var staged = await Cli(null, "update", title, "parked-read-witness", "--id", cId, "--local");
                    staged.Code.ShouldBe(0, staged.Error + staged.Output);
                    // Grants come from an exact provider read, never from that local edit.
                    var authorized = await Cli(null, "update", "--find", "--id", cId);
                    authorized.Output.ShouldContain("NotFound");
                    var directory = displayPath[..displayPath.LastIndexOf("/partitions/", StringComparison.Ordinal)] + "/partitions";
                    using var inventory = JsonDocument.Parse(await http.GetStringAsync(directory));
                    var provider = inventory.RootElement.GetProperty("partitions").EnumerateArray()
                        .Select(x => x.GetGuid()).Single(id => id != projection.DatabaseInstance);
                    var fresh = await http.GetStringAsync($"{directory}/{provider:D}/page");
                    fresh.ShouldContain("\"kind\":\"provider\"");
                    fresh.ShouldNotContain("parked-read-witness");
                }
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
                if (displayPath is not null)
                {
                    var successes = 0;
                    var timer = Stopwatch.StartNew();
                    for (var i = 0; i < 100; i++)
                    {
                        using var page = await http.GetAsync(displayPath);
                        var content = await page.Content.ReadAsStringAsync();
                        if (page.IsSuccessStatusCode)
                        {
                            content.ShouldContain("parked-read-witness");
                            successes++;
                        }
                        else
                        {
                            using var refused = JsonDocument.Parse(content);
                            refused.RootElement.GetProperty("code").GetString().ShouldBeOneOf("RefreshRequired", "Busy", "ViewChanged");
                        }
                    }
                    timer.Stop();
                    log.WriteLine($"100 daemon page reads while ADO response was parked: {successes} content, {100-successes} typed refusals, {timer.ElapsedMilliseconds} ms.");
                    timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
                    successes.ShouldBeGreaterThan(0);
                    a.IsCompleted.ShouldBeFalse();
                }
                log.WriteLine($"Parked {remote ?? "Twig"} provider after {waiting.ElapsedMilliseconds} ms.");
                var parked = Stopwatch.StartNew();
                var b = await Cli(null, "update", title, "B", "--id", bId, "--review", Guid.NewGuid().ToString());
                b.Code.ShouldBe(0, b.Error + b.Output);
                log.WriteLine($"Disjoint update settled at {parked.ElapsedMilliseconds} ms.");
                if (remote is not null) File.Exists(Path.Combine(root, "provider-b")).ShouldBeTrue();
                var c = await Cli(null, "update", title, "C", "--id", cId, "--local");
                c.Code.ShouldBe(0, c.Error + c.Output);
                log.WriteLine($"Local edit settled at {parked.ElapsedMilliseconds} ms.");
                if (remote is not null)
                {
                    var note = await Cli(null, "note", "--id", bId, "--text", "disjoint note", "--review", Guid.NewGuid().ToString());
                    note.Code.ShouldBe(0, note.Output + note.Error);
                    Match(note.Output).GetProperty("state").GetString().ShouldBe("Complete");
                    log.WriteLine($"Disjoint note settled at {parked.ElapsedMilliseconds} ms.");
                }
                a.IsCompleted.ShouldBeFalse();
                var queuedReview = Guid.NewGuid().ToString("D");
                if (remote is null)
                {
                    var queued = await Cli(null, "update", title, "stale", "--id", aId, "--review", queuedReview);
                    Match(queued.Output).GetProperty("state").GetString().ShouldBe("Queued");
                }
                var aResult = await a;
                aResult.Code.ShouldBe(0, aResult.Error + aResult.Output);
                waiting.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
                if (remote is null)
                {
                    var deadline = Stopwatch.StartNew();
                    while (!(await Cli(null, "update", "--find", "--id", aId)).Output.Contains("\"ReviewRequired\"", StringComparison.Ordinal))
                    {
                        deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the earlier submission must advance without another submit");
                        await Task.Delay(50);
                    }
                }
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
                if (begin)
                {
                    if (reboot)
                    {
                        casLosses = false;
                        await Restart();
                        var stopped = await Cli(null, "update", "--submit", review, "--id", "1");
                        Match(stopped.Output).GetProperty("state").GetString().ShouldBe("NotStarted", stopped.Output + stopped.Error);
                        Match(stopped.Output).GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
                    }
                    if (editPending)
                        (await Cli(null, "update", NativeProvider.Title, "later edit", "--id", "1", "--local")).Code.ShouldBe(0);
                    await File.WriteAllTextAsync(Path.Combine(root, "continue-local"), "");
                    var expected = reboot ? "NotStarted" : editPending ? "ReviewRequired" : "Complete";
                    var watch = Stopwatch.StartNew();
                    while (!(await Cli(null, "update", "--find", "--id", "1")).Output.Contains($"\"{expected}\"", StringComparison.Ordinal))
                    {
                        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6), "the owner must resume the admitted reducer");
                        await Task.Delay(50);
                    }
                    var completed = (await handle.Workspace.SnapshotAsync(default)).Value;
                    if (reboot || editPending)
                    {
                        var authority = (await handle.Workspace.ObserveItemsAsync([completed.Items.Single().Key], default)).Value.Items.Single();
                        authority.Fields[NativeProvider.Title].Text.ShouldBe("original");
                        completed.Journal.Count(j => j.Status == OpStatus.Verified).ShouldBe(1);
                        if (editPending) completed.RetainedItems.OfType<LocalEdits>().Single().Changes[NativeProvider.Title].Text.ShouldBe("later edit");
                        if (editPending)
                        {
                            var replacement = Guid.NewGuid().ToString("D");
                            var refreshed = await Cli(null, "update", "--re-review", review, "--review", replacement, "--id", "1");
                            refreshed.Code.ShouldBe(1, refreshed.Output + refreshed.Error);
                            Match(refreshed.Output).GetProperty("state").GetString().ShouldBe("Queued", refreshed.Error);
                            using var details = JsonDocument.Parse(refreshed.Output);
                            var comparison = details.RootElement.GetProperty("replacement");
                            comparison.GetProperty("previousDigest").GetString().ShouldNotBe(comparison.GetProperty("digest").GetString());
                            comparison.GetProperty("previousCapture").GetString().ShouldNotBe(comparison.GetProperty("capture").GetString());
                            comparison.GetProperty("previousReview").GetGuid().ShouldBe(Guid.Parse(review));
                            var published = await Cli(null, "update", "--submit", replacement, "--id", "1");
                            var deadline = Stopwatch.StartNew();
                            while (Match(published.Output).GetProperty("state").GetString() is "Pending" or "Claimed" or "Running" or "Settling")
                            {
                                deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
                                await Task.Delay(50);
                                published = await Cli(null, "update", "--observe", replacement, "--id", "1");
                            }
                            published.Code.ShouldBe(0, published.Output + published.Error);
                            var duplicate = await Cli(null, "update", "--submit", replacement, "--id", "1");
                            Match(duplicate.Output).GetProperty("submission").GetGuid().ShouldBe(Match(published.Output).GetProperty("submission").GetGuid());
                            (await Cli(null, "update", "--re-review", review, "--review", Guid.NewGuid().ToString("D"), "--id", "1")).Code.ShouldBe(2);
                            Match((await Cli(null, "update", "--observe", review, "--id", "1")).Output).GetProperty("state").GetString().ShouldBe("ReviewRequired");
                            var afterReplacement = (await handle.Workspace.SnapshotAsync(default)).Value;
                            afterReplacement.Journal.Count(j => j.Status == OpStatus.Verified).ShouldBe(2);
                            afterReplacement.Items.Single().Fields[NativeProvider.Title].Text.ShouldBe("frozen from stdin");
                        }
                        return;
                    }
                    completed.Items.Single().Fields[NativeProvider.Title].Text.ShouldBe("frozen from stdin");
                    completed.RetainedItems.Length.ShouldBe(3);
                    completed.Journal.Count(j => j.Status == OpStatus.Verified).ShouldBe(2);
                    return;
                }
                var original = before.Journal.Single(j => j.Status == OpStatus.Applying);
                var remoteItem = (await handle.Workspace.ObserveItemsAsync([before.Items.Single().Key], default)).Value.Items.Single();
                remoteItem.Fields[NativeProvider.Title].Text.ShouldBe("frozen from stdin");
                casLosses = false;
                if (sameBootReceipt)
                {
                    await File.WriteAllTextAsync(Path.Combine(root, "continue-local"), "");
                    var watch = Stopwatch.StartNew();
                    while (!(await Cli(null, "update", "--find", "--id", "1")).Output.Contains("\"Complete\"", StringComparison.Ordinal))
                    {
                        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
                        await Task.Delay(50);
                    }
                }
                else
                {
                    await Restart();
                    var resumed = await Cli(null, "update", "--observe", review, "--id", "1");
                    resumed.Code.ShouldBe(0, resumed.Error + resumed.Output);
                    Match(resumed.Output).GetProperty("submission").GetGuid().ShouldBe(saved.GetProperty("submission").GetGuid());
                }
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
                (await Cli(null, "update", "--re-review", review, "--review", Guid.NewGuid().ToString("D"), "--id", "1")).Code.ShouldBe(2);
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
                if ((remote is not null || casLosses || create) && exe == server)
                {
                    start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(OwnerUpdateProcessTests).Assembly.Location;
                    start.Environment["DOTNET_ADDITIONAL_DEPS"] = Path.ChangeExtension(typeof(OwnerUpdateProcessTests).Assembly.Location, ".deps.json");
                    start.Environment["TWIG_TEST_REMOTE_PROVIDER"] = remote ?? (create ? "create" : "cas");
                    if (remote is not null && create) start.Environment["TWIG_TEST_REMOTE_CREATE"] = "1";
                    if (begin) start.Environment["TWIG_TEST_RACE_BEGIN"] = "1";
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
