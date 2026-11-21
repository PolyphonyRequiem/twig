using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Daemon.Tests.Sessions;

public sealed class SessionInvariantTests
{
    [Theory]
    [InlineData("repository-selection")]
    [InlineData("bare-repository-selection")]
    [InlineData("initial-view")]
    [InlineData("owner-transport")]
    [InlineData("owner-lifetime")]
    [InlineData("owner-session")]
    [InlineData("default-display")]
    public async Task PersistentOwnerAndProfileInvariants(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var root = Path.Combine(Path.GetTempPath(), "twig-session-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            switch (scenario)
            {
                case "repository-selection": await SessionTests.RepositorySelection(root, deadline.Token); break;
                case "bare-repository-selection": await SessionTests.RepositorySelection(root, deadline.Token, bare: true); break;
                case "initial-view": await SessionTests.InitialView(root, deadline.Token); break;
                case "owner-transport": await SessionTests.OwnerTransport(root, deadline.Token); break;
                case "owner-lifetime": SessionTests.OwnerLifetime(root); break;
                case "owner-session": await SessionTests.Run(root, deadline.Token); break;
                case "default-display": await SessionTests.DefaultDisplay(root, deadline.Token); break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }
}

internal static class SessionTests
{
    internal static int AssertionsPassed { get; private set; }

    internal static async Task RepositorySelection(string root, CancellationToken ct, bool bare = false)
    {
        async Task Git(string directory, params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            Check(process.ExitCode == 0, $"Git fixture failed: {await output} {await error}");
        }
        var repository = Path.Combine(root, "repository");
        var linked = Path.Combine(root, "linked");
        var unrelated = Path.Combine(root, "unrelated");
        var seed = bare ? Path.Combine(root, "seed") : repository;
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(unrelated);
        await Git(seed, "init", "--quiet");
        await Git(seed, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "--quiet", "--allow-empty", "-m", "Fixture");
        string? common = null;
        if (bare)
        {
            common = Path.Combine(root, "bare common.git");
            await Git(root, "clone", "--quiet", "--bare", seed, common);
            await Git(root, $"--git-dir={common}", "worktree", "add", "--quiet", "-b", "first", repository);
            // Add the second checkout from its sibling, just as a normal checkout adds a worktree.
        }
        await Git(repository, "worktree", "add", "--quiet", "-b", "linked", linked);
        await Git(unrelated, "init", "--quiet");
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var storeId = Required(await store.ReadAsync(ct)).InstanceId;
        var fallback = Guid.NewGuid();
        var selected = Guid.NewGuid();
        _ = Required(await store.SaveAsync(new(fallback, "Default", []), 0, ct));
        _ = Required(await store.SaveAsync(new(selected, "Previously selected", []), 0, ct));
        var bindings = Required(await store.SaveRepositoryAsync(repository, 0, [], fallback, ct));
        _ = Required(await store.SaveRepositoryAsync(unrelated, 0, [], fallback, ct));
        Check(Required(await store.ReadRepositoryAsync(linked, ct)).RepositoryId == bindings.RepositoryId,
            "The linked worktree must resolve to the same verified repository identity.");
        if (common is not null)
            Check((await store.ReadRepositoryAsync(common, ct)).Error.Code == ErrorCode.InvalidInput,
                "The bare directory itself is not a checkout and must not be accepted as a binding context.");
        var expectedLinkedRoot = bare ? linked : repository;
        var gateway = new Gateway([]);
        var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, new()));
        var profiles = Path.Combine(root, "profiles");
        var sessions = new ViewSessions(profiles);
        var presentations = new Dictionary<Guid, string>();
        PairResult Open(ViewSessions owner, string sessionId, string directory)
        {
            var canvas = owner.RegisterNativeCanvas(storeId, new(sessionId, directory));
            presentations[canvas.ViewId] = canvas.PresentationCapability;
            var profile = owner.ResolveOwnerProfile(storeId, new(null, null, canvas.ViewId));
            return owner.ConnectOwner(storeId, canvas.ViewId, profile.ProfileId);
        }
        async Task<Guid?> Initial(Host host, PairResult owner)
        {
            var response = await host.Send("POST", $"/api/v1/views/{owner.ViewId:D}/initial-view",
                new EmptyBody(), owner.ContentCapability, null, ct);
            Check(response.Status == 200, $"Initial loading must succeed: {response.Body}");
            return response.Body!["viewState"]!["currentBenchId"]?.GetValue<Guid>();
        }
        var first = Open(sessions, "first", repository);
        await using (var host = await Host.StartProduction(panel, sessions, root, ct))
        {
            Check(await Initial(host, first) == fallback, "An untouched repository uses its configured default.");
            var response = await host.Send("PUT", $"/api/v1/views/{first.ViewId:D}/selection",
                new SelectionBody(fallback, selected), first.ContentCapability, null, ct);
            Check(response.Status == 200, "The real selection endpoint must accept the explicit Bench.");
            var state = sessions.ReadState(sessions.Content(first.ViewId, first.ContentCapability, true));
            response = await host.Send("PUT", $"/api/v1/views/{first.ViewId:D}/view-state",
                new NativeViewStateBody(state.Revision, state.Choices with { BenchId = selected }),
                first.ContentCapability, null, ct);
            Check(response.Status == 200, "The original session must also persist its own Bench choice.");
        }
        var restarted = new ViewSessions(profiles);
        var next = Open(restarted, "next", linked);
        Check(restarted.ReadState(restarted.Content(next.ViewId, next.ContentCapability, true)).Choices.BenchId is null,
            "A new session must start with its own empty profile, not reuse another session's state.");
        await using (var host = await Host.StartProduction(panel, restarted, root, ct))
        {
            Check(await Initial(host, next) == selected,
                "A new session in a linked worktree must auto-load the repository's last-selected Bench after restart.");
            var linkedState = restarted.ReadState(restarted.Content(next.ViewId, next.ContentCapability, true));
            Check(linkedState.Choices.RepositoryRoot == expectedLinkedRoot,
                "The runtime context must use the main checkout when present, or retain the requesting checkout.");
            var linkedRoute = $"/api/v1/views/{next.ViewId:D}/repository-bindings";
            var linkedBindings = await host.Send("POST", linkedRoute,
                new ReadRepositoryBindingsBody(linked), next.ContentCapability, null, ct);
            Check(linkedBindings.Status == 200
                && linkedBindings.Body!["repositoryRoot"]?.GetValue<string>() == expectedLinkedRoot
                && linkedBindings.Body["revision"]?.GetValue<long>() == bindings.Revision,
                $"Observing bindings through a worktree must return its context root and shared revision: {linkedBindings.Status} {linkedBindings.Body}");
            var legacyBindings = await host.Send("GET", linkedRoute + "?root=" + Uri.EscapeDataString(linked),
                null, next.ContentCapability, null, ct);
            Check(legacyBindings.Status == 403
                && legacyBindings.Body!["code"]?.GetValue<string>() == "InvalidTransport",
                "The retained GET must still refuse query-string transport; POST must not weaken that boundary.");
            var deniedBindings = await host.Send("POST", linkedRoute,
                new ReadRepositoryBindingsBody(linked), presentations[next.ViewId], null, ct);
            Check(deniedBindings.Status == 403, "SDK presentation authority cannot read owner repository bindings.");
            deniedBindings = await host.Send("POST", linkedRoute,
                new ReadRepositoryBindingsBody(linked), next.ContentCapability, null, ct, csrf: false);
            Check(deniedBindings.Status == 403, "Repository binding POST reads require the owner CSRF header.");
            Check(await Initial(host, Open(restarted, "other-repo", unrelated)) == fallback,
                "A different repository must not inherit that selection.");
            Check(Required(await store.ReadRepositoryAsync(repository, ct)).DefaultBench == fallback,
                "Remembering a selection must not overwrite the configured repository default.");
            var response = await host.Send("PUT", $"/api/v1/views/{next.ViewId:D}/selection",
                new SelectionBody(selected, fallback), next.ContentCapability, null, ct);
            Check(response.Status == 200, "Another session can choose a different Bench.");
            Check(await Initial(host, Open(restarted, "first", repository)) == selected,
                "An existing session's explicit saved selection takes precedence over repository memory.");
            var fresh = Open(restarted, "latest", repository);
            Check(await Initial(host, fresh) == fallback,
                "A later new session must use the latest explicit repository selection.");
            response = await host.Send("PUT", $"/api/v1/views/{fresh.ViewId:D}/selection",
                new SelectionBody(fallback, selected), fresh.ContentCapability, null, ct);
            Check(response.Status == 200, "The remembered Bench can be changed again.");
            var revision = Required(await store.ReadAsync(ct)).Benches.Single(bench => bench.Id == selected).Revision;
            _ = Required(await store.DeleteAsync(selected, revision, ct));
            Check(await Initial(host, Open(restarted, "deleted", linked)) == fallback,
                "A deleted remembered Bench must fall back to the repository default.");
            var explicitContext = restarted.ConnectOwner(storeId, null, Guid.NewGuid());
            response = await host.Send("PUT", $"/api/v1/views/{explicitContext.ViewId:D}/repository-context",
                new RepositoryContextBody(repository), explicitContext.ContentCapability, null, ct);
            Check(response.Status == 200 && await Initial(host, explicitContext) == fallback,
                "Explicit repository selection without SDK context must use the same validated fallback.");
            response = await host.Send("PUT", $"/api/v1/views/{explicitContext.ViewId:D}/repository-context",
                new RepositoryContextBody(linked), explicitContext.ContentCapability, null, ct);
            Check(response.Status == 200
                && restarted.ReadState(restarted.Content(explicitContext.ViewId, explicitContext.ContentCapability, true))
                    .Choices.RepositoryRoot == expectedLinkedRoot,
                "Explicit worktree selection must use the same verified context as automatic selection.");
            var separate = Path.Combine(root, "separate repository");
            var separateLinked = Path.Combine(root, "separate linked");
            Directory.CreateDirectory(separate);
            await Git(separate, "init", "--quiet");
            await Git(separate, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--quiet", "--allow-empty", "-m", "Fixture");
            await Git(separate, "worktree", "add", "--quiet", "-b", "separate-linked", separateLinked);
            _ = Required(await store.SaveRepositoryAsync(separate, 0, [], fallback, ct));
            var nested = Path.Combine(separateLinked, "nested");
            Directory.CreateDirectory(nested);
            Check(Required(await store.ReadRepositoryAsync(nested, ct)).RepositoryRoot == separate,
                "A nested worktree directory must resolve to its own main repository, including paths with spaces.");
            var separateOwner = Open(restarted, "separate", nested);
            Check(await Initial(host, separateOwner) == fallback
                && restarted.ReadState(restarted.Content(separateOwner.ViewId, separateOwner.ContentCapability, true))
                    .Choices.RepositoryRoot == separate,
                "Repository resolution must use Git's main checkout, not infer a root from the nested worktree path.");
            Check(gateway.Reads.Count == 0 && gateway.SelectCalls == 0,
                "Remembering a Bench must not acquire connections or write CLI selection.");
        }
    }

    internal static async Task InitialView(string root, CancellationToken ct)
    {
        var source = Path.Combine(root, "configured");
        Directory.CreateDirectory(source);
        var gateway = new Gateway([source]);
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var storeId = Required(await store.ReadAsync(ct)).InstanceId;
        var evidence = Path.Combine(root, "evidence");
        var panel = new NativePanel(store, gateway, new NativeSourceAdapter(evidence, gateway, new()));
        var sessions = new ViewSessions(Path.Combine(root, "profiles"));
        var profile = Guid.NewGuid();
        var first = sessions.ConnectOwner(storeId, null, profile);
        var firstSession = sessions.Content(first.ViewId, first.ContentCapability, true);
        var admitted = Required(await panel.OpenAsync(source, ct));
        firstSession.NativeContext = admitted;
        firstSession.DisplayContexts[admitted.Stamp.Connection] = admitted;
        var bench = Guid.NewGuid();
        var otherBench = Guid.NewGuid();
        _ = Required(await panel.SaveAsync(new(bench, "Configured", [], connections: [admitted.Stamp.Connection]), 0, ct));
        _ = Required(await panel.SaveAsync(new(otherBench, "Changed selection", [], connections: [admitted.Stamp.Connection]), 0, ct));
        gateway.Selection[source] = bench;
        var saved = sessions.SaveState(firstSession, new(0, NativeViewChoices.Empty with
        { ViewingRoot = source, DisplayRoots = [source], BenchId = bench }));
        var canvas = sessions.RegisterNativeCanvas(storeId);
        var owner = sessions.ConnectOwner(storeId, canvas.ViewId, profile);
        await using var host = await Host.Start(panel, sessions, ct);
        var route = $"/api/v1/views/{owner.ViewId:D}/initial-view";
        Check((await host.Send("POST", route, new EmptyBody(), null, null, ct)).Status == 403,
            "Anonymous initial content is refused.");
        Check((await host.Send("POST", route, new EmptyBody(), canvas.PresentationCapability, null, ct)).Status == 403,
            "SDK presentation authority cannot acquire initial content.");
        var reads = gateway.Reads.Count; var schema = gateway.SchemaReads; var online = gateway.OnlineReads;
        var result = await host.Send("POST", route, new EmptyBody(), owner.ContentCapability, null, ct);
        Check(result.Status == 200 && result.Body!["initialView"]!["view"]!["id"]!.GetValue<Guid>() == bench
            && gateway.Reads.Count == reads + 1 && gateway.SchemaReads == schema && gateway.OnlineReads == online,
            "Real initial HTTP composes the configured selected Bench with exactly one full local observation.");
        Check(gateway.SelectCalls == 0, "Restoration never writes native selection.");
        gateway.OnNextRead = () => gateway.Selection[source] = otherBench;
        result = await host.Send("POST", route, new EmptyBody(), owner.ContentCapability, null, ct);
        Check(result.Status == 200 && result.Body!["initialView"]!["view"]!["id"]!.GetValue<Guid>() == bench
            && result.Body!["viewState"]!["choices"]!["benchId"]!.GetValue<Guid>() == bench,
            "Initial targeting retains its selected user Bench; a source's CLI selection cannot retarget this view.");
        gateway.OnNextRead = () =>
        {
            var state = sessions.ReadState(firstSession);
            sessions.SaveState(firstSession, new(state.Revision, state.Choices with { Mode = "queries" }));
        };
        result = await host.Send("POST", route, new EmptyBody(), owner.ContentCapability, null, ct);
        Check(result.Status == 409 && result.Body!["initialView"] is null,
            "An overlapping profile revision during native acquisition cannot release an initial payload.");
        var binding = sessions.StateReferences(firstSession).Single().Binding;
        var path = Path.Combine(evidence, binding + ".json");
        var original = await File.ReadAllTextAsync(path, ct);
        foreach (var mutation in new Action<JsonNode>[] {
            json => json["blocked"] = true,
            json => json["schema"]!["fingerprint"] = new string('0', 64),
            json => json["stamp"]!["principalId"] = "changed-principal",
        })
        {
            var observations = new Dictionary<NativePanelContext, SourceObservation>();
            _ = Required(await panel.OpenAsync(source, ct, binding, observations));
            var changed = JsonNode.Parse(original)!; mutation(changed);
            await File.WriteAllTextAsync(path, changed.ToJsonString(), ct);
            Check(!(await panel.CheckInitialAsync(observations, ct)).IsSuccess,
                "A denied or schema-replaced admission after capture must fail the final request fence.");
            await File.WriteAllTextAsync(path, original, ct);
        }
        {
            var observations = new Dictionary<NativePanelContext, SourceObservation>();
            _ = Required(await panel.OpenAsync(source, ct, binding, observations));
            var cell = Path.Combine(evidence, binding + ".check");
            var allowed = await File.ReadAllTextAsync(cell, ct);
            await File.WriteAllTextAsync(cell, $"twig-native-check-v1\npending\n{Guid.NewGuid():N}", ct);
            Check(!(await panel.CheckInitialAsync(observations, ct)).IsSuccess,
                "A newly pending durable operation after capture cannot release initial content.");
            await File.WriteAllTextAsync(cell, allowed, ct);
        }
        gateway.OnNextRead = () => sessions.Revoke(firstSession);
        result = await host.Send("POST", route, new EmptyBody(), owner.ContentCapability, null, ct);
        Check(result.Status == 403 && result.Body!["initialView"] is null,
            "A profile generation revoked during native acquisition cannot release the old request payload.");
        owner = sessions.ConnectOwner(storeId, canvas.ViewId, profile);
        gateway.ReplaceIdentity(source);
        result = await host.Send("POST", route, new EmptyBody(), owner.ContentCapability, null, ct);
        Check(result.Status == 200 && result.Body!["initialView"]!["view"]!["sources"]![0]!["errorCode"] is not null
            && result.Body["initialView"]!["view"]!["sources"]![0]!["items"]!.AsArray().Count == 0
            && result.Body!["viewState"]!["sources"]![0]!["status"]!.GetValue<string>() == "unavailable",
            "Changed source identity remains visibly degraded; no old snapshot is released.");
        var empty = sessions.ConnectOwner(storeId, null, Guid.NewGuid());
        result = await host.Send("POST", $"/api/v1/views/{empty.ViewId:D}/initial-view", new EmptyBody(),
            empty.ContentCapability, null, ct);
        Check(result.Status == 200 && result.Body!["initialView"]!["view"] is null,
            "Genuine first use without selected Bench returns no invented membership.");
        sessions.Revoke(firstSession);
        Check((await host.Send("POST", route, new EmptyBody(), owner.ContentCapability, null, ct)).Status == 403,
            "Revoked profile cannot release initial content.");
    }

    internal static async Task OwnerTransport(string root, CancellationToken ct)
    {
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var storeId = Required(await store.ReadAsync(ct)).InstanceId;
        var gateway = new Gateway([]);
        var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, new()));
        var sessions = new ViewSessions(Path.Combine(root, "profiles"));
        var profile = Guid.NewGuid();
        var owner = sessions.ConnectOwner(storeId, null, profile);
        var body = new OwnerRenewBody(owner.ViewId, profile, storeId);
        await using var host = await Host.Start(panel, sessions, ct);
        var mixed = await host.Send("POST", "/api/v1/windows-renew", body, owner.ContentCapability, "same-owner", ct, legacyBearer: true);
        Check(mixed.Status == 401, "A content Bearer must not occupy the Windows authentication transport.");
        var missingProof = await host.Send("POST", "/api/v1/windows-renew", body, owner.ContentCapability, null, ct);
        Check(missingProof.Status == 401, "The separate content header cannot prove the OS owner.");
        var renewed = await host.Send("POST", "/api/v1/windows-renew", body, owner.ContentCapability, "same-owner", ct);
        Check(renewed.Status == 200 && renewed.Body!["contentCapability"]!.GetValue<string>() == owner.ContentCapability,
            "Windows-authenticated renewal must preserve the existing capability through its separate header, not rotate it.");
        Check(gateway.SchemaReads == 0 && gateway.OnlineReads == 0, "Active owner renewal does not re-admit sources.");
        var resolveBody = new OwnerProfileBody(storeId, profile);
        Check((await host.Send("POST", "/api/v1/windows-profile", resolveBody, owner.ContentCapability, null, ct)).Status == 401,
            "An agent content capability cannot resolve the human's default profile.");
        var resolved = await host.Send("POST", "/api/v1/windows-profile", resolveBody, null, "same-owner", ct);
        Check(resolved.Status == 200 && resolved.Body!["profileId"]!.GetValue<Guid>() == profile
            && resolved.Body.AsObject().Count == 2, "Owner proof resolves only store/profile identities, not capabilities or content.");
        Check((await host.Send("POST", "/api/v1/windows-profile", new OwnerProfileBody(Guid.NewGuid(), profile),
            null, "same-owner", ct)).Status == 409, "A remembered foreign store cannot be silently replaced.");
        sessions.Revoke(sessions.Content(owner.ViewId, owner.ContentCapability, true));
        var restarted = new ViewSessions(Path.Combine(root, "profiles"));
        Check(restarted.ResolveOwnerProfile(storeId, new(null, null)).ProfileId == profile,
            "A fresh origin retains a signed-out default instead of inventing another identity.");
        Expect("SessionRevoked", () => restarted.RenewOwner(storeId, new(null, profile, storeId), null));
        var pointer = Path.Combine(root, "profiles", $"default-profile-{storeId:D}");
        var signedOutPath = Path.Combine(root, "profiles", $"native-{storeId:D}-{profile:D}.json");
        var signedOutBytes = File.ReadAllBytes(signedOutPath);
        var recovery = new OwnerProfileBody(storeId, null, RecoverMissingDefault: true);
        Expect("ProfileRecoveryNotRequired", () => restarted.ResolveOwnerProfile(storeId, recovery));
        Check(File.ReadAllText(pointer) == profile.ToString("D") && File.ReadAllBytes(signedOutPath).SequenceEqual(signedOutBytes),
            "Recovery must not replace a signed-out default or change its content.");
        File.WriteAllText(pointer, "invalid");
        Expect("DefaultProfileUnavailable", () => restarted.ResolveOwnerProfile(storeId, new(null, null)));
        const string profileRoute = "/api/v1/windows-profile";
        Check((await host.Send("GET", profileRoute, null, owner.ContentCapability, null, ct)).Status == 401
            && (await host.Send("GET", profileRoute, null, null, "different-owner", ct)).Status == 403,
            "Store discovery requires the Windows owner, not a content capability or a different Windows user.");
        var discovery = await host.Send("GET", profileRoute, null, null, "same-owner", ct);
        Check(discovery.Status == 200 && discovery.Body!["storeId"]!.GetValue<Guid>() == storeId
            && discovery.Body.AsObject().Count == 1, "Store discovery returns only the current store identity.");
        Check((await host.Send("POST", profileRoute, recovery with { StoreId = null }, null, "same-owner", ct)).Status == 400
            && (await host.Send("POST", profileRoute, recovery with { StoreId = Guid.Empty }, null, "same-owner", ct)).Status == 400
            && (await host.Send("POST", profileRoute, recovery with { StoreId = Guid.NewGuid() }, null, "same-owner", ct)).Status == 409
            && File.ReadAllText(pointer) == "invalid",
            "Discovery, or recovery with a missing, empty or stale store ID, must not mutate the broken pointer.");
        Check((await host.Send("POST", profileRoute, recovery, owner.ContentCapability, null, ct)).Status == 401,
            "A content capability cannot replace the owner default.");
        var recovered = await host.Send("POST", profileRoute,
            recovery with { StoreId = discovery.Body!["storeId"]!.GetValue<Guid>() }, null, "same-owner", ct);
        Check(recovered.Status == 200 && recovered.Body!["profileId"]!.GetValue<Guid>() != profile,
            "Explicit owner recovery creates a fresh profile instead of adopting old state.");
        Check(restarted.ResolveOwnerProfile(storeId, new(null, null)).ProfileId == recovered.Body!["profileId"]!.GetValue<Guid>(),
            "The replacement pointer remains valid for subsequent passive resolution.");
        Expect("ProfileRecoveryNotRequired", () => restarted.ResolveOwnerProfile(storeId, recovery));
        var orphan = Guid.NewGuid();
        File.WriteAllText(pointer, orphan.ToString("D"));
        var orphanPath = Path.Combine(root, "profiles", $"native-{storeId:D}-{orphan:D}.json");
        var renewal = await host.Send("POST", "/api/v1/windows-renew", new OwnerRenewBody(null, orphan, storeId),
            null, "same-owner", ct);
        Check(renewal.Status == 409 && renewal.Body!["code"]!.GetValue<string>() == "ProfileUnavailable"
            && !File.Exists(orphanPath) && File.ReadAllText(pointer) == orphan.ToString("D"),
            "Passive renewal cannot recreate a missing profile or recover its default pointer.");
        Expect("DefaultProfileUnavailable", () => restarted.ResolveOwnerProfile(storeId, new(null, null)));
        Check(restarted.ResolveOwnerProfile(storeId, recovery).ProfileId != orphan,
            "A fresh browser origin can recover a pointer to a missing profile using the exact current ID.");
        const string corruptProfile = """{"version":1,"choices":null,"sources":null}""";
        File.WriteAllText(orphanPath, corruptProfile);
        File.WriteAllText(pointer, orphan.ToString("D"));
        Expect("DefaultProfileUnavailable", () => restarted.ResolveOwnerProfile(storeId, new(storeId, orphan)));
        Check(restarted.ResolveOwnerProfile(storeId, recovery).ProfileId != orphan
            && File.ReadAllText(orphanPath) == corruptProfile
            && File.ReadAllBytes(signedOutPath).SequenceEqual(signedOutBytes),
            "Corrupt content is not reusable, and recovery changes only the pointer and a brand-new profile.");
        Expect("SessionRevoked", () => restarted.RenewOwner(storeId, new(null, profile, storeId), null));
    }

    internal static void OwnerLifetime(string root)
    {
        var clock = new Clock();
        var sessions = new ViewSessions(Path.Combine(root, "profiles"), timeProvider: clock);
        var store = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var sdk = sessions.RegisterNativeCanvas(store);
        var standaloneSdk = sessions.RegisterNativeCanvas(store);
        var owner = sessions.ConnectOwner(store, sdk.ViewId, profile);
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        var initial = sessions.ReadState(session);
        Expect("NotAttached", () => sessions.SetLayout(sdk.ViewId, sdk.PresentationCapability, new("tree")));
        sessions.AttachPresentation(owner.ViewId, owner.ContentCapability);
        sessions.SetLayout(sdk.ViewId, sdk.PresentationCapability, new("tree"));
        Check(initial.ExpiresAt == clock.Now.AddHours(24) && initial.RenewAfter == clock.Now.AddHours(12),
            "Human owner sessions must last exactly 24 hours and renew at 12 hours.");
        clock.Now += TimeSpan.FromMinutes(30);
        _ = sessions.Presentation(sdk.ViewId, sdk.PresentationCapability);
        sessions.SetLayout(sdk.ViewId, sdk.PresentationCapability, new("list"));
        Expect("SessionExpired", () => sessions.Presentation(standaloneSdk.ViewId, standaloneSdk.PresentationCapability));
        clock.Now += TimeSpan.FromHours(23.5) - TimeSpan.FromMinutes(1);
        Check(ReferenceEquals(session, sessions.Content(owner.ViewId, owner.ContentCapability, true)), "Human content remains valid at 23:59.");
        clock.Now += TimeSpan.FromMinutes(1);
        Expect("SessionExpired", () => sessions.Content(owner.ViewId, owner.ContentCapability, true));
        var recovered = sessions.RenewOwner(store, new(owner.ViewId, profile, store), owner.ContentCapability);
        var next = sessions.Content(recovered.Connection.ViewId, recovered.Connection.ContentCapability, true);
        var state = sessions.ReadState(next);
        Check(recovered.Recovered && state.ExpiresAt == clock.Now.AddHours(24) && state.RenewAfter == clock.Now.AddHours(12),
            "Expired/recovered human sessions receive the same 24-hour/12-hour policy.");
        clock.Now += TimeSpan.FromHours(12);
        var active = sessions.RenewOwner(store, new(next.Id, profile, store), recovered.Connection.ContentCapability);
        Check(!active.Recovered && active.Connection.ContentCapability == recovered.Connection.ContentCapability
            && sessions.ReadState(next).ExpiresAt == clock.Now.AddHours(24)
            && sessions.ReadState(next).RenewAfter == clock.Now.AddHours(12),
            "An active renewal extends human expiry by 24 hours without rotating its capability.");
        var browser = sessions.ConnectOwner(store, null, Guid.NewGuid());
        Check(sessions.ReadState(sessions.Content(browser.ViewId, browser.ContentCapability, true)).ExpiresAt == clock.Now.AddHours(24),
            "Initial non-canvas human owners use the same lifetime.");
    }

    internal static async Task DefaultDisplay(string root, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var sourceA = Path.Combine(root, "admitted-a");
        var sourceB = Path.Combine(root, "future-b");
        Directory.CreateDirectory(sourceA); Directory.CreateDirectory(sourceB);
        var gateway = new Gateway([sourceA, sourceB]) { TypeGaps = ["Fixture unsupported type-local rule"] };
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var storeId = Required(await store.ReadAsync(ct)).InstanceId;
        var panel = new NativePanel(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, new()));
        var preferences = Path.Combine(root, "presentation");
        var sessions = new ViewSessions(preferences);
        var profile = Guid.NewGuid();
        var owner = sessions.ConnectOwner(storeId, null, profile);
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        session.NativeContext = Required(await panel.OpenAsync(sourceA, ct));
        session.DisplayContexts.Add(session.NativeContext.Stamp.Connection, session.NativeContext);
        Check(session.NativeContext.Source.AllowOffline, "An independently admitted exact source must use local display by default, without a preference.");
        var bench = Guid.NewGuid();
        _ = Required(await store.SaveAsync(new(bench, "Cached source", [], connections: [session.NativeContext.Stamp.Connection]), 0, ct));
        gateway.Selection[sourceA] = bench;
        var choices = NativeViewChoices.Empty with { ViewingRoot = sourceA, DisplayRoots = [sourceA], BenchId = bench };
        var state = sessions.SaveState(session, new(0, choices));
        var retained = sessions.StateReferences(session).Single();
        Check(Directory.GetFiles(preferences, "*.json").All(file =>
            !File.ReadAllText(file).Contains("allowCachedDisplay", StringComparison.OrdinalIgnoreCase)),
            "Current profiles must not persist obsolete display opt-in compatibility fields.");
        sessions.SaveState(session, new(state.Revision, choices with { Layout = "tree" }));
        var restarted = new ViewSessions(preferences);
        var resolved = restarted.ResolveOwnerProfile(storeId, new(null, null));
        Check(resolved.ProfileId == profile && resolved.StoreId == storeId,
            "A new rendering origin without browser storage resolves the durable owner profile after daemon restart.");
        Check(restarted.ResolveOwnerProfile(storeId, new(storeId, Guid.NewGuid())).ProfileId == profile,
            "A random origin-local identity cannot replace the retained application profile.");
        var freshCanvas = restarted.RegisterNativeCanvas(storeId);
        var next = restarted.ConnectOwner(storeId, freshCanvas.ViewId, resolved.ProfileId);
        var nextSession = restarted.Content(next.ViewId, next.ContentCapability, true);
        Check(nextSession.NativeContext is null && nextSession.PopulationContext is null && !nextSession.Attached
            && nextSession.Agent.Grant is null, "Resolving a remembered profile does not restore source authority or per-view grants.");
        Check(restarted.ReadState(nextSession).Choices.ViewingRoot == sourceA
            && restarted.ReadState(nextSession).Choices.BenchId == bench,
            "Connection and Bench choices belong to the daemon profile, not the browser origin.");
        var onlineBefore = gateway.OnlineReads;
        await using (var host = await Host.Start(panel, restarted, ct))
        {
            var response = await host.Send("POST", $"/api/v1/views/{next.ViewId:D}/view-state/restore", new EmptyBody(),
                next.ContentCapability, null, ct);
            Check(response.Status == 200 && gateway.OnlineReads == onlineBefore && nextSession.NativeContext is not null,
                "Actual profile restoration must display the admitted exact source with zero first-paint online reads and no opt-in.");
        }
        var restored = nextSession.NativeContext!;
        Check(restored.Source.AllowOffline, "Restoration must not retain an opt-in gate.");
        var cached = Required(await panel.CaptureAsync(restored, ct, cachedGuidance: true)).Observation;
        Check(cached.Mode == ObservationMode.PermittedOfflineCache,
            "The internal observation still distinguishes local display from fresh mutation authority.");
        Check(cached.Schema.Types.Single().Gaps.SequenceEqual(gateway.TypeGaps)
            && cached.Schema.Types.Single().Rules.Single().Action == RuleAction.Default,
            "The exact schema cache roundtrip must retain type-local gaps and Default semantics.");
        var selection = new SourceSelection(restored.Source.Id, restored.Source.Binding, []);
        var definition = new BenchDefinition(bench, "Cached schema fixture", 1, [selection]);
        var projected = Required(SourceProjection.Create(definition, selection, cached, ct));
        var wire = ViewMapping.ViewLabeled(new(definition, storeId, 1, [projected]),
            new Dictionary<Guid, string> { [restored.Source.Id] = "Cached source" }).Sources.Single();
        Check(wire.Types!.Single().Gaps.SequenceEqual(gateway.TypeGaps)
            && wire.SchemaFingerprint == cached.Schema.Fingerprint,
            "Projection and source-generated DTOs must not erase type gaps or the fingerprint bound to them.");
        _ = Required(await panel.CaptureAsync(restored, ct, cachedGuidance: true));
        Check(gateway.OnlineReads == onlineBefore, "Preliminary key guidance uses admitted local data without releasing an editor form.");
        var evidencePath = Path.Combine(root, "evidence", retained.Binding + ".json");
        var originalEvidence = await File.ReadAllTextAsync(evidencePath, ct);
        var invalidEvidence = JsonNode.Parse(originalEvidence)!;
        invalidEvidence["schema"]!["fingerprint"] = new string('0', 64);
        await File.WriteAllTextAsync(evidencePath, invalidEvidence.ToJsonString(), ct);
        Check(!(await panel.OpenAsync(sourceA, ct, retained.Binding)).IsSuccess && gateway.OnlineReads == onlineBefore,
            "An invalid schema fingerprint must refuse local display, not silently reacquire online authority.");
        await File.WriteAllTextAsync(evidencePath, originalEvidence, ct);
        File.Delete(evidencePath);
        Check(!(await panel.OpenAsync(sourceA, ct, retained.Binding)).IsSuccess && gateway.OnlineReads == onlineBefore,
            "Missing admitted evidence must refuse restoration instead of treating the old reference as a new source.");
        await File.WriteAllTextAsync(evidencePath, originalEvidence, ct);
        Check(!(await panel.OpenAsync(sourceB, ct, retained.Binding)).IsSuccess && gateway.OnlineReads == onlineBefore,
            "A new foreign source cannot borrow another source's admission or silently acquire access.");
        _ = Required(await panel.OpenAsync(sourceB, ct));
        Check((Required(await panel.OpenAsync(sourceB, ct))).Source.AllowOffline,
            "A separately and independently admitted source receives the same normal local display behavior.");
        gateway.Denied.Add(sourceA);
        Check(!(await panel.ValidateDisplayAccessAsync(restored, ct)).IsSuccess, "Online denial must remain a failure.");
        gateway.Denied.Clear();
        var blocked = await panel.OpenAsync(sourceA, ct, retained.Binding);
        Check(!blocked.IsSuccess && blocked.Error.Code == ErrorCode.PolicyViolation, "A denied/pending durable cell must withhold cached content even after connectivity returns.");
        _ = Required(await panel.OpenAsync(sourceA, ct));
        gateway.ReplaceIdentity(sourceA);
        var changed = await panel.OpenAsync(sourceA, ct, retained.Binding);
        Check(!changed.IsSuccess && changed.Error.Code == ErrorCode.Conflict, "A store/identity replacement cannot inherit the prior admission.");
        session.NativeContext = Required(await panel.OpenAsync(sourceA, ct));
        session.DisplayContexts[session.NativeContext.Stamp.Connection] = session.NativeContext;
        sessions.SaveState(session, new(sessions.ReadState(session).Revision, choices));
    }

    internal static async Task Run(string root, CancellationToken ct)
    {
        var clock = new Clock();
        var preferences = Path.Combine(root, "presentation");
        var store = new UserBenchStore(Path.Combine(root, "benches.db"));
        var storeId = Required(await store.ReadAsync(ct)).InstanceId;
        var sourceA = Path.Combine(root, "explicit-a");
        var sourceB = Path.Combine(root, "explicit-b");
        var unselected = Path.Combine(root, "never-selected");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        Directory.CreateDirectory(unselected);
        var gateway = new Gateway([sourceA, sourceB, unselected]);
        var changes = new CatalogChanges();
        NativePanel Panel() => new(store, gateway, new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, changes));
        var panel = Panel();
        var sessions = new ViewSessions(preferences, timeProvider: clock);
        var profile = Guid.NewGuid();
        var registration = sessions.RegisterNativeCanvas(storeId);
        var owner = sessions.ConnectOwner(storeId, registration.ViewId, profile);
        var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
        session.NativeContext = Required(await panel.OpenAsync(sourceA, ct));
        session.PopulationContext = Required(await panel.OpenAsync(sourceB, ct));
        session.DisplayContexts.Add(session.NativeContext.Stamp.Connection, session.NativeContext);
        session.DisplayContexts.Add(session.PopulationContext.Stamp.Connection, session.PopulationContext);
        var chosenBench = Guid.NewGuid();
        var currentBench = Guid.NewGuid();
        _ = Required(await store.SaveAsync(new(chosenBench, "Selected session Bench", [],
            connections: [session.NativeContext.Stamp.Connection, session.PopulationContext.Stamp.Connection]), 0, ct));
        _ = Required(await store.SaveAsync(new(currentBench, "Independent CLI Bench", []), 0, ct));
        gateway.Selection[sourceA] = currentBench;
        var choices = new NativeViewChoices(sourceA, sourceB, [sourceA, sourceB], Guid.NewGuid().ToString("D"),
            "queries", Guid.NewGuid().ToString("D"), null, chosenBench, "tree", Guid.NewGuid(), "System.State");
        var saved = sessions.SaveState(session, new(0, choices));
        Check(saved.Revision == 1, "First profile save must advance its durable revision.");
        var sourceContext = session.NativeContext;
        var ticket = Guid.NewGuid();
        session.Editor.Ticket = ticket;
        var schemaReads = gateway.SchemaReads;
        await using (var host = await Host.Start(panel, sessions, ct))
        {
            var renewBody = new OwnerRenewBody(owner.ViewId, profile, storeId);
            Check((await host.Send("POST", "/api/v1/windows-renew", renewBody, owner.ContentCapability, null, ct)).Status == 401,
                "A content bearer alone must not authenticate the renewal endpoint.");
            Check((await host.Send("POST", "/api/v1/windows-renew", renewBody, registration.PresentationCapability, null, ct)).Status == 401,
                "An SDK bearer must not authenticate human renewal.");
            Check((await host.Send("POST", "/api/v1/windows-renew", renewBody, null, "different-owner", ct)).Status == 403,
                "A different authenticated OS owner must not renew this profile.");
            var newCanvas = sessions.RegisterNativeCanvas(storeId, new("fresh-owner-session", root));
            var newProfile = sessions.ResolveOwnerProfile(storeId, new(null, null, newCanvas.ViewId));
            var automatic = await host.Send("POST", "/api/v1/windows-renew",
                new OwnerRenewBody(newCanvas.ViewId, newProfile.ProfileId, storeId), null, "same-owner", ct);
            Check(automatic.Status == 200 && automatic.Body!["viewId"]!.GetValue<Guid>() == newCanvas.ViewId
                && automatic.Body["viewState"]!["sources"]!.AsArray().Count == 0,
                "A fresh SDK canvas may acquire human authority only through OS auth, without replacing its presentation handle or choosing a source.");
            var prefix = $"/api/v1/views/{owner.ViewId:D}/view-state";
            Check((await host.Send("GET", prefix, null, registration.PresentationCapability, null, ct)).Status == 403,
                "SDK presentation authority must not disclose saved root or planning references.");
            clock.Now += TimeSpan.FromMinutes(20);
            var renewed = await host.Send("POST", "/api/v1/windows-renew", renewBody, owner.ContentCapability, "same-owner", ct);
            Check(renewed.Status == 200 && renewed.Body!["recovered"]!.GetValue<bool>() == false
                && renewed.Body["contentCapability"]!.GetValue<string>() == owner.ContentCapability,
                "An active same-owner renewal must retain its live content capability.");
            Check(ReferenceEquals(sourceContext, session.NativeContext) && session.Editor.Ticket == ticket && gateway.SchemaReads == schemaReads,
                "Active renewal must preserve exact contexts/editor and perform no source/schema rediscovery.");
            clock.Now += TimeSpan.FromMinutes(11);
            _ = sessions.Content(owner.ViewId, owner.ContentCapability, true);
            _ = sessions.Presentation(owner.ViewId, registration.PresentationCapability);
            Check(session.OwnerAuthenticated, "Current authenticated SDK presentation follows the renewed owner lifetime.");
            var info = (await host.Send("GET", prefix, null, owner.ContentCapability, null, ct)).Body!;
            Check(info["expiresAt"]!.GetValue<DateTimeOffset>() == clock.Now.AddHours(24).AddMinutes(-11)
                && info["renewAfter"]!.GetValue<DateTimeOffset>() == clock.Now.AddHours(12).AddMinutes(-11),
                "The HTTP contract must expose exact expiry and timely renewal instants.");
            var update = new NativeViewStateBody(saved.Revision, choices with { Mode = "backlogs", BacklogId = "portfolio" });
            Check((await host.Send("PUT", prefix, update, owner.ContentCapability, null, ct)).Status == 200,
                "Chosen backlog mode must persist through the bounded typed HTTP contract.");
            var conflict = await host.Send("PUT", prefix, update, owner.ContentCapability, null, ct);
            Check(conflict.Status == 409 && conflict.Body!["code"]!.GetValue<string>() == "ViewStateConflict",
                "A stale state revision must never overwrite concurrent choices.");
            var foreignStore = await host.Send("POST", "/api/v1/windows-renew",
                renewBody with { StoreId = Guid.NewGuid() }, owner.ContentCapability, "same-owner", ct);
            Check(foreignStore.Status == 409 && foreignStore.Body!["code"]!.GetValue<string>() == "StoreReplaced",
                "Store replacement must be distinct from expiry and signout.");
        }
        var otherRegistry = new ViewSessions(preferences, timeProvider: clock);
        var otherConnection = otherRegistry.ConnectOwner(storeId, null, profile);
        var otherSession = otherRegistry.Content(otherConnection.ViewId, otherConnection.ContentCapability, true);
        var otherState = otherRegistry.ReadState(otherSession);
        Check(otherState.Choices.BenchId == chosenBench
            && otherState.Choices.GroupSource == choices.GroupSource
            && otherState.Choices.GroupField == choices.GroupField,
            "Same-owner reconnect retains explicit Bench and grouping references.");
        sessions.SaveState(session, new(otherState.Revision, choices));
        Expect("ViewStateConflict", () => otherRegistry.SaveState(otherSession, new(otherState.Revision, choices with { Layout = "list" })));
        var revision = sessions.ReadState(session).Revision;
        bool Compete(ViewSessions registry, ViewSession view, string layout)
        {
            try { registry.SaveState(view, new(revision, choices with { Layout = layout })); return true; }
            catch (HttpFailure error) when (error.Status == 409 && error.Code is "ViewStateBusy" or "ViewStateConflict") { return false; }
        }
        var competitors = await Task.WhenAll(Task.Run(() => Compete(sessions, session, "list"), ct),
            Task.Run(() => Compete(otherRegistry, otherSession, "tree"), ct));
        Check(competitors.Count(success => success) == 1 && sessions.ReadState(session).Revision == revision + 1,
            "Concurrent profile writers must commit exactly one durable revision, never silently overwrite.");
        var layoutView = sessions.RegisterNativeCanvas(storeId);
        var layoutOwner = sessions.ConnectOwner(storeId, layoutView.ViewId, profile);
        sessions.AttachPresentation(layoutOwner.ViewId, layoutOwner.ContentCapability);
        var newest = sessions.SaveState(session, new(sessions.ReadState(session).Revision, choices));
        sessions.SetLayout(layoutOwner.ViewId, layoutView.PresentationCapability, new("tree"));
        Check(sessions.ReadState(session).Choices.GroupSource == choices.GroupSource
            && sessions.ReadState(session).Revision == newest.Revision + 1,
            "SDK layout ACK must not overwrite newer human source, Bench or grouping references.");
        var freshProfile = sessions.ConnectOwner(storeId, null, Guid.NewGuid());
        var empty = sessions.ReadState(sessions.Content(freshProfile.ViewId, freshProfile.ContentCapability, true));
        Check(empty.Choices.ViewingRoot is null && empty.Sources.Length == 0,
            "A different profile must never borrow another profile's roots.");
        clock.Now += TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10);
        Expect("SessionExpired", () => sessions.Content(owner.ViewId, owner.ContentCapability, true));
        gateway.Denied.Add(sourceB);
        await using (var expiryHost = await Host.Start(panel, sessions, ct))
        {
            var expired = await expiryHost.Send("POST", "/api/v1/windows-renew",
                new OwnerRenewBody(owner.ViewId, profile, storeId), owner.ContentCapability, "same-owner", ct);
            Check(expired.Status == 200 && expired.Body!["recovered"]!.GetValue<bool>()
                && expired.Body["viewId"]!.GetValue<Guid>() != owner.ViewId && session.Editor.Retired,
                "An expired human capability must recover through OS auth and retained references, not keep a stale editor grant alive.");
        }
        var restarted = new ViewSessions(preferences, timeProvider: clock);
        await using (var host = await Host.Start(Panel(), restarted, ct))
        {
            gateway.Reads.Clear();
            gateway.NextReadDelay = TimeSpan.FromMilliseconds(100);
            var renewed = await host.Send("POST", "/api/v1/windows-renew",
                new OwnerRenewBody(owner.ViewId, profile, storeId), owner.ContentCapability, "same-owner", ct);
            Check(renewed.Status == 200 && renewed.Body!["recovered"]!.GetValue<bool>(),
                "A server restart must recover a same-owner profile without device enrollment.");
            var body = renewed.Body!;
            var id = body["viewId"]!.GetValue<Guid>();
            var token = body["contentCapability"]!.GetValue<string>();
            var state = body["viewState"]!;
            Check(state["sources"]!.AsArray().Single(source => source!["root"]!.GetValue<string>() == sourceA)!
                ["status"]!.GetValue<string>() == "restored",
                "A delayed admitted source must restore through its explicit current Bench connection.");
            Check(state["choices"]!["viewingRoot"]!.GetValue<string>() == sourceA
                && state["choices"]!["addRoot"]!.GetValue<string>() == sourceB
                && state["choices"]!["displayRoots"]!.AsArray().Count == 2
                && state["choices"]!["teamId"]!.GetValue<string>() == choices.TeamId
                && state["choices"]!["mode"]!.GetValue<string>() == "queries"
                && state["choices"]!["queryId"]!.GetValue<string>() == choices.QueryId,
                "Restart must retain every explicitly chosen source, team, query and mode.");
            Check(state["sources"]!.AsArray().Count(source => source!["status"]!.GetValue<string>() == "restored") == 1
                && state["sources"]!.AsArray().Count(source => source!["status"]!.GetValue<string>() == "unavailable") == 1,
                "One unavailable native source must retain its recovery reference without hiding another source.");
            var recovered = restarted.Content(id, token, true);
            Check(recovered.NativeContext?.Root == sourceA && recovered.PopulationContext is null
                && recovered.DisplayContexts.Count == 1 && gateway.Reads.All(path => path == sourceA || path == sourceB)
                && !gateway.Reads.Contains(unselected),
                "Restoration must never choose a registry sibling or workspace fallback.");
            Check(state["currentBenchId"]!.GetValue<Guid>() == chosenBench
                && state["choices"]!["benchId"]!.GetValue<Guid>() == chosenBench
                && gateway.SelectCalls == 0 && gateway.Selection[sourceA] == currentBench,
                "The selected session Bench must survive an independent CLI selection without writing either selection during resume.");
            var prefix = $"/api/v1/views/{id:D}/view-state";
            gateway.Denied.Clear();
            gateway.OnNextRead = () =>
            {
                lock (recovered) recovered.PopulationContext = recovered.NativeContext;
            };
            var concurrentChoice = await host.Send("POST", prefix + "/restore", new EmptyBody(), token, null, ct);
            Check(concurrentChoice.Status == 409 && concurrentChoice.Body!["code"]!.GetValue<string>() == "ViewStateConflict"
                && ReferenceEquals(recovered.PopulationContext, recovered.NativeContext),
                "An in-flight restore must not overwrite a newer explicit runtime source choice.");
            Check((await host.Send("POST", prefix + "/restore", new EmptyBody(), token, null, ct)).Status == 200
                && recovered.PopulationContext?.Root == sourceB && recovered.DisplayContexts.Count == 2,
                "Explicit source recovery must retry the retained root, not reenroll or choose a substitute.");
            var beforeIdentity = recovered.PopulationContext!.Stamp;
            var retainedBinding = restarted.StateReferences(recovered).Single(reference => reference.Root == sourceB).Binding;
            gateway.ReplaceIdentity(sourceB);
            var replacedIdentity = Required(await gateway.ReadAsync(sourceB, null, false, ct)).Source;
            Check(replacedIdentity.Connection == beforeIdentity.Connection && replacedIdentity.StoreInstance != beforeIdentity.StoreInstance,
                "The refusal fixture must actually replace the same connection's native store identity.");
            var changed = await host.Send("POST", prefix + "/restore", new EmptyBody(), token, null, ct);
            var changedReference = changed.Body!["sources"]!.AsArray()
                .Single(source => source!["root"]!.GetValue<string>() == sourceB)!;
            var refusedChange = changedReference["status"]!.GetValue<string>() == "unavailable"
                && changedReference["errorCode"]?.GetValue<string>() == "Conflict";
            Check(changed.Status == 200 && refusedChange && recovered.PopulationContext is null
                && recovered.DisplayContexts.Values.All(context => context.Root != sourceB)
                && restarted.StateReferences(recovered).Single(reference => reference.Root == sourceB).Binding == retainedBinding
                && gateway.SelectCalls == 0,
                "A root with changed native identity must remain a refused retained reference, never a new grant.");
            Console.WriteLine($"PASS replaced source refusal: status={changedReference["status"]}, errorCode={changedReference["errorCode"]}; reference retained, selection writes={gateway.SelectCalls}.");
            restarted.Revoke(recovered);
            var denied = await host.Send("POST", "/api/v1/windows-renew", new OwnerRenewBody(id, profile, storeId),
                token, "same-owner", ct);
            Check(denied.Status == 403 && denied.Body!["code"]!.GetValue<string>() == "SessionRevoked",
                "Explicit signout must not be undone by transparent renewal.");
        }
        var afterSignout = new ViewSessions(preferences, timeProvider: clock);
        Expect("SessionRevoked", () => afterSignout.RenewOwner(storeId, new(null, profile, storeId)));
        Expect("SessionRevoked", () => otherRegistry.Content(otherConnection.ViewId, otherConnection.ContentCapability, true));
        var signedIn = afterSignout.ConnectOwner(storeId, null, profile);
        Check(afterSignout.ReadState(afterSignout.Content(signedIn.ViewId, signedIn.ContentCapability, true)).Choices.ViewingRoot == sourceA,
            "Only explicit owner connect may clear sticky signout while preserving desired references.");
        var replacement = afterSignout.ConnectOwner(Guid.NewGuid(), null, profile);
        Check(afterSignout.ReadState(afterSignout.Content(replacement.ViewId, replacement.ContentCapability, true)).Sources.Length == 0,
            "A replaced catalogue instance must not inherit the previous catalogue's references.");
        var disk = string.Join("\n", Directory.GetFiles(preferences, "*.json").Select(File.ReadAllText));
        Check(!disk.Contains(owner.ContentCapability, StringComparison.Ordinal)
            && !disk.Contains(registration.PresentationCapability, StringComparison.Ordinal)
            && !disk.Contains(signedIn.ContentCapability, StringComparison.Ordinal)
            && !disk.Contains("synthetic-authority-secret", StringComparison.Ordinal)
            && !disk.Contains("synthetic-principal", StringComparison.Ordinal)
            && !disk.Contains("contentCapability", StringComparison.OrdinalIgnoreCase),
            "Durable UI state must contain references/presentation only, never capabilities, native grants or credentials.");
        var unauthenticated = afterSignout.RegisterNativeCanvas(storeId);
        Expect("CapabilityRejected", () => afterSignout.Content(unauthenticated.ViewId, unauthenticated.PresentationCapability, true));
        Expect("CapabilityRejected", () => afterSignout.Content(unauthenticated.ViewId, unauthenticated.PresentationCapability, false));
    }

    internal static T Required<T>(Result<T> result)
        => result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.ToString());
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        AssertionsPassed++;
    }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (HttpFailure failure) when (failure.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}

internal sealed class Clock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class Gateway
    : INativeSourceGateway
{
    private readonly Dictionary<string, NativeSourceStamp> stamps;
    internal HashSet<string> Denied { get; } = new(StringComparer.Ordinal);
    internal List<string> Reads { get; } = [];
    internal Dictionary<string, Guid?> Selection { get; } = new(StringComparer.Ordinal);
    internal int SchemaReads { get; private set; }
    internal int OnlineReads { get; private set; }
    internal string[] TypeGaps { get; init; } = [];
    internal int SelectCalls { get; private set; }
    internal Action? OnNextRead { get; set; }
    internal TimeSpan NextReadDelay { get; set; }
    internal Gateway(string[] roots)
    {
        stamps = roots.Select((root, index) => (root, stamp: new NativeSourceStamp(
            new(new("ado"), "synthetic-collection", "synthetic-project-" + index, "synthetic-identity"),
            Guid.NewGuid(), "synthetic-principal", "synthetic-authority-secret")))
            .ToDictionary(value => value.root, value => value.stamp, StringComparer.Ordinal);
    }
    internal void ReplaceIdentity(string root) => stamps[root] = stamps[root] with { StoreInstance = Guid.NewGuid() };
    public async Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Reads.Add(root);
        var delay = NextReadDelay;
        NextReadDelay = TimeSpan.Zero;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        var action = OnNextRead;
        OnNextRead = null;
        action?.Invoke();
        if (Denied.Contains(root)) return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "Synthetic native source unavailable.");
        if (!stamps.TryGetValue(root, out var stamp) || expected is not null && stamp != expected)
            return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict, "Synthetic native identity changed.");
        if (observeSchema) SchemaReads++;
        var now = DateTimeOffset.UtcNow;
        var types = TypeGaps.Length == 0 ? Array.Empty<ItemType>() : [new ItemType("Fixture.PartialType", "System.State",
            [new("System.Title", FieldKind.Text), new("System.State", FieldKind.Text)],
            [new("Active", StateCategory.InProgress)], [], [new("System.Title", RuleAction.Default, [], [FieldValue.String("Fixture default")])])
            { Gaps = TypeGaps.ToImmutableArray() }];
        return Result<NativeSourceObservation>.Ok(new(stamp, new(1), now,
            observeSchema ? Result<ProcessSchema>.Ok(new(stamp.Connection, types, [])) : null, observeSchema ? now : null)
            { SelectedBenchId = Selection.GetValueOrDefault(root) });
    }
    public Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
    {
        OnlineReads++;
        return ReadAsync(root, expected, false, ct);
    }
    public Task<Result<NativeSourceObservation>> SelectBenchAsync(string root, NativeSourceStamp expected, Guid? previous, Guid? next, CancellationToken ct)
    {
        SelectCalls++;
        throw new InvalidOperationException("Resume must never write the CLI selection.");
    }
}

internal sealed class Host(WebApplication app, HttpClient client) : IAsyncDisposable
{
    internal static async Task<Host> StartProduction(NativePanel panel, ViewSessions sessions, string root, CancellationToken ct)
    {
        var app = NativeBenchHost.Build(new(0, root), panel, sessions);
        var bindingReads = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == "/api/v1/views/{id:guid}/repository-bindings"
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        SessionTests.Check(bindingReads.Count() == 1, "The production owner repository-binding read must have exactly one POST handler.");
        await app.StartAsync(ct);
        return new(app, new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) });
    }

    internal static async Task<Host> Start(NativePanel panel, ViewSessions sessions, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("SyntheticOS")
            .AddScheme<AuthenticationSchemeOptions, OwnerHandler>("SyntheticOS", _ => { });
        builder.Services.AddAuthorizationBuilder().AddPolicy("WindowsOwner", policy => policy
            .RequireAuthenticatedUser().RequireClaim("owner", "same-owner"));
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            try { await next(http); }
            catch (HttpFailure error) { await BenchHost.Write(http, new Failure(error.Code, error.Message), error.Status); }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        NativeBenchHost.MapPersistentSessions(app, panel, sessions, async (http, id) =>
        {
            var session = sessions.Content(id, BenchHost.Bearer(http), true);
            if (!session.OwnerAuthenticated) throw new HttpFailure(403, "OwnerRequired", "Synthetic owner boundary.");
            if (session.CatalogId != SessionTests.Required(await panel.ListAsync(http.RequestAborted)).InstanceId)
                throw new HttpFailure(409, "StoreReplaced", "Synthetic replaced store.");
            return session;
        }, true, source => source);
        await app.StartAsync(ct);
        return new(app, new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) });
    }
    internal async Task<(int Status, JsonNode? Body)> Send(string method, string route, object? body,
        string? bearer, string? owner, CancellationToken ct, bool legacyBearer = false, bool csrf = true)
    {
        using var request = new HttpRequestMessage(new(method), route);
        if (route is "/api/v1/windows-renew" or "/api/v1/windows-profile" && !legacyBearer)
        {
            if (owner is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Negotiate", "synthetic-proof");
            if (bearer is not null) request.Headers.Add("X-Twig-Bench-Content", bearer);
        }
        else if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (owner is not null) request.Headers.Add("Test-Owner", owner);
        if (csrf) request.Headers.Add("X-Twig-Bench", "1");
        if (body is not null) request.Content = new StringContent(
            JsonSerializer.Serialize(body, TransportJson.Default.GetTypeInfo(body.GetType())!), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }
    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

internal sealed class OwnerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var owner = Request.Headers["Test-Owner"].ToString();
        return Task.FromResult(owner.Length == 0 || !Request.Headers.Authorization.ToString().StartsWith("Negotiate ", StringComparison.Ordinal)
            ? AuthenticateResult.NoResult()
            : AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("owner", owner)], Scheme.Name)), Scheme.Name)));
    }
}
