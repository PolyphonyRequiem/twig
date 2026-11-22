using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Services;

namespace Twig.Server.Tests.Native;

internal static class ConnectionSetupTests
{
    internal static async Task Run(string scenario)
    {
        if (scenario is not ("gates" or "cancellation" or "failures" or "readback"))
            throw new ArgumentException("Choose gates, cancellation, failures or readback.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var directory = Path.Combine(Path.GetTempPath(), "twig-connection-setup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var gateway = new SetupGateway();
        ViewSession? ownedSession = null;
        try
        {
            var store = new UserBenchStore(Path.Combine(directory, "benches.db"));
            var before = Required(await store.ReadAsync(ct));
            var sources = new NoSourceReads();
            var changes = new CatalogChanges();
            var panel = new NativePanel(store, sources, new NativeSourceAdapter(Path.Combine(directory, "evidence"), sources, changes));
            var sessions = new ViewSessions(Path.Combine(directory, "presentation"));
            var canvas = sessions.RegisterNativeCanvas(before.InstanceId);
            var human = sessions.ConnectOwner(before.InstanceId, canvas.ViewId, Guid.NewGuid());
            var humanSession = sessions.Content(human.ViewId, human.ContentCapability, true);
            ownedSession = humanSession;
            await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes, gateway);
            await app.StartAsync(ct);
            using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(5) };
            var root = Path.Combine(directory, "fresh-root");
            JsonObject Ado() => new() { ["workspaceRoot"] = root, ["organization"] = "synthetic-org",
                ["project"] = "synthetic-project", ["bindingId"] = "exact-binding", ["team"] = "explicit-team", ["gitProject"] = null };
            JsonObject GitHub() => new() { ["workspaceRoot"] = root, ["host"] = "github.com",
                ["repository"] = "synthetic/repository", ["account"] = "named-account" };
            var enrollment = new JsonObject { ["alias"] = "named-identity", ["tenant"] = "synthetic-tenant" };
            var binding = new JsonObject { ["organization"] = "synthetic-org", ["project"] = "synthetic-project", ["identityAlias"] = "named-identity" };
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, HttpMethod method, JsonNode? body = null,
                string? bearer = null, bool csrf = true, string? origin = null, Guid? view = null)
            {
                var setup = !path.StartsWith("/", StringComparison.Ordinal);
                using var request = new HttpRequestMessage(method, $"/api/v1/views/{view ?? human.ViewId:D}"
                    + (setup ? "/setup/" + path : path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? human.ContentCapability);
                if (csrf) request.Headers.Add("X-Twig-Bench", "1");
                if (origin is not null) request.Headers.Add("Origin", origin);
                if (body is not null)
                {
                    body = body.DeepClone();
                    if (setup && method == HttpMethod.Post && body is JsonObject fields && !fields.ContainsKey("epoch"))
                        fields["epoch"] = humanSession.Setup.Epoch;
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }
                using var response = await client.SendAsync(request, ct);
                return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!);
            }
            async Task<JsonNode> Ok(string path, HttpMethod method, JsonNode? body = null)
            {
                var result = await Send(path, method, body);
                Check(result.Status is HttpStatusCode.OK or HttpStatusCode.Accepted, result.Body.ToJsonString());
                return result.Body;
            }
            async Task<JsonNode> Wait(JsonNode operation)
            {
                while (operation["pending"]!.GetValue<bool>())
                {
                    await Task.Delay(10, ct);
                    operation = await Ok("operations/" + operation["operationId"]!.GetValue<string>(), HttpMethod.Get);
                }
                return operation;
            }
            if (scenario == "gates")
            {
                foreach (var (path, valid, missing) in new[]
                {
                    ("enroll-aad", enrollment, "alias"), ("ado-binding", binding, "identityAlias"),
                    ("initialize-ado", Ado(), "bindingId"), ("initialize-github", GitHub(), "account")
                })
                {
                    Check((await Send(path, HttpMethod.Post, valid, canvas.PresentationCapability)).Status == HttpStatusCode.Forbidden,
                        "SDK presentation capability cannot invoke setup.");
                    Check((await Send(path, HttpMethod.Post, valid, csrf: false)).Status == HttpStatusCode.Forbidden,
                        "Missing content-CSRF header refuses before facade dispatch.");
                    Check((await Send(path, HttpMethod.Post, valid, origin: "https://untrusted.example")).Status == HttpStatusCode.Forbidden,
                        "Foreign origin refuses before facade dispatch.");
                    var incomplete = valid.DeepClone().AsObject();
                    incomplete.Remove(missing);
                    Check((await Send(path, HttpMethod.Post, incomplete)).Status == HttpStatusCode.BadRequest, "Missing required input refuses.");
                    incomplete[missing] = " ";
                    Check((await Send(path, HttpMethod.Post, incomplete)).Status == HttpStatusCode.BadRequest, "Blank input refuses.");
                }
                var anonymous = sessions.RegisterNativeCanvas(before.InstanceId);
                Check((await Send("enroll-aad", HttpMethod.Post, enrollment, anonymous.PresentationCapability,
                    view: anonymous.ViewId)).Status == HttpStatusCode.Forbidden, "A paired non-owner cannot invoke setup.");
                var staleRequest = Ado();
                staleRequest["epoch"] = -1;
                Check((await Send("initialize-ado", HttpMethod.Post, staleRequest)).Status == HttpStatusCode.Conflict,
                    "Stale setup navigation refuses before facade dispatch.");
                humanSession.ProfileGeneration = Guid.NewGuid();
                Check((await Send("enroll-aad", HttpMethod.Post, enrollment)).Status == HttpStatusCode.Forbidden,
                    "A stale owner generation refuses before facade dispatch.");
                Check(gateway.Calls == 0, "No refused input, owner, SDK, CSRF or origin call reaches the facade.");

                var nativeHome = Path.Combine(directory, "inert-native-home");
                var production = new NativeConnectionSetupGateway(new NativeConnectionSetup(nativeHome));
                CheckValidation(await production.EnrollAadAsync(new(""), _ =>
                    throw new InvalidOperationException("Invalid enrollment must never start PKCE."), ct));
                CheckValidation(await production.CreateAdoBindingAsync(new("", "synthetic-project", "named-identity"), ct));
                CheckValidation(await production.InitializeFreshAsync(new("",
                    new NativeGitHubSetupTarget("github.com", "synthetic/repository", "named-account")), ct));
                Check(!Directory.Exists(nativeHome) && !File.Exists(nativeHome),
                    "Actual production gateway/facade validation is inert; no native home or effects are created.");
                void CheckValidation<T>(Result<T> result)
                {
                    Check(!result.IsSuccess && result.Error.Code == ErrorCode.InvalidInput,
                        "Actual native facade must refuse invalid setup before composition/effects.");
                    var failure = NativeConnectionSetup.ReadFailure(result.Error);
                    Check(failure.IsSuccess && failure.Value.Phase == NativeSetupPhase.Validation
                        && !failure.Value.Cancelled && failure.Value.Effects.IsEmpty,
                        "Actual facade reports typed Validation and no effects, not a host-inferred failure.");
                }
            }
            else if (scenario == "cancellation")
            {
                gateway.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var admitted = await Ok("initialize-ado", HttpMethod.Post, Ado());
                await gateway.Entered.Task.WaitAsync(ct);
                var id = admitted["operationId"]!.GetValue<string>();
                Check((await Send("initialize-ado", HttpMethod.Post, Ado())).Status == HttpStatusCode.Conflict,
                    "Pending native setup cannot be dispatched twice.");
                var beforeRecheck = humanSession.Setup.Epoch;
                var recheck = await Send("/context/recheck", HttpMethod.Post, new JsonObject());
                Check(recheck.Status == HttpStatusCode.Conflict && recheck.Body["code"]!.GetValue<string>() == "ContextRequired",
                    "The recheck fixture deliberately has no viewing context, so recheck fails after the setup fence.");
                var original = humanSession.Setup.Operation ?? throw new InvalidOperationException("The original setup must remain retained.");
                Check(original.Lifetime.IsCancellationRequested && humanSession.Setup.Epoch != beforeRecheck,
                    "Even a failed viewing recheck must cancel the original setup and advance its epoch before source admission.");
                var cancelled = await Ok("operations/" + id, HttpMethod.Get);
                Check(cancelled["status"]!.GetValue<string>() == "Cancelled" && cancelled["source"] is null,
                    "Failed viewing recheck withholds Ready immediately without deleting native effects.");
                gateway.Release.TrySetResult();
                var completed = await Wait(cancelled);
                Check(completed["status"]!.GetValue<string>() == "Cancelled" && completed["source"] is null && gateway.Calls == 1,
                    "Late facade success after cancellation cannot become Ready or replay.");
                var oldCount = gateway.Calls;
                await Ok("operations/" + id, HttpMethod.Get);
                await Ok("operation", HttpMethod.Get);
                Check(gateway.Calls == oldCount, "Observation is read-only.");

                gateway.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                gateway.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                gateway.Failure = new(ErrorCode.Unavailable, "Original partial native effects.")
                { Evidence = ["native-setup:v1", "native-setup:phase:Readback", "native-setup:effect:CoreStore:Confirmed"] };
                var partial = await Ok("initialize-ado", HttpMethod.Post, Ado());
                await gateway.Entered.Task.WaitAsync(ct);
                var partialCancelled = await Ok("operations/" + partial["operationId"]!.GetValue<string>(), HttpMethod.Delete, new JsonObject());
                gateway.Release.TrySetResult();
                var partialObserved = await Wait(partialCancelled);
                Check(partialObserved["status"]!.GetValue<string>() == "Cancelled" && partialObserved["source"] is null
                    && partialObserved["nativeFailure"]!["effects"]![0]!["state"]!.GetValue<string>() == "Confirmed",
                    "Host cancellation retains the original native partial-effect diagnostics.");
                gateway.Failure = null;
                var beforeNavigation = humanSession.Setup.Epoch;
                await Ok("operation", HttpMethod.Delete, new JsonObject { ["epoch"] = beforeNavigation });
                var lostRequest = Ado();
                lostRequest["epoch"] = beforeNavigation;
                Check((await Send("initialize-ado", HttpMethod.Post, lostRequest)).Status == HttpStatusCode.Conflict,
                    "Cancelling an unknown/lost admission fences the delayed original request before effects.");
                gateway.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                gateway.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var stale = await Ok("initialize-github", HttpMethod.Post, GitHub());
                await gateway.Entered.Task.WaitAsync(ct);
                var session = sessions.Content(human.ViewId, human.ContentCapability, true);
                session.ProfileGeneration = Guid.NewGuid();
                gateway.Release.TrySetResult();
                var active = session.Setup.Operation ?? throw new InvalidOperationException("The stale operation must remain observable.");
                Check(active.Current.OperationId.ToString("D") == stale["operationId"]!.GetValue<string>(),
                    "The retained task is the exact original operation, not a replacement.");
                await (active.Work ?? throw new InvalidOperationException("The original operation task is required.")).WaitAsync(ct);
                Check(active.Current.Status is "Cancelled" or "Failed"
                    && active.Current.Source is null, "Stale owner generation cannot release Ready.");
                Check(gateway.Calls == 3, "No stale generation replay.");
            }
            else if (scenario == "failures")
            {
                gateway.Failure = new(ErrorCode.Unavailable, "Synthetic native readback failed.")
                { Evidence = ["native-setup:v1", "native-setup:phase:Readback", "native-setup:effect:CoreStore:Confirmed",
                    "native-setup:effect:SourceReadback:Possible"] };
                var failed = await Wait(await Ok("initialize-ado", HttpMethod.Post, Ado()));
                Check(failed["status"]!.GetValue<string>() == "Failed" && failed["source"] is null,
                    "Partial native effects remain failure, not initialized-as-Ready.");
                Check(failed["nativeFailure"]!["phase"]!.GetValue<string>() == "Readback"
                    && failed["nativeFailure"]!["effects"]!.AsArray().Count == 2 && failed["diagnosticsError"] is null,
                    "Preserve typed native phase and possible/confirmed effects.");
                gateway.Failure = new(ErrorCode.Unavailable, "Unsupported synthetic evidence.") { Evidence = ["native-setup:v2"] };
                var unsupported = await Wait(await Ok("enroll-aad", HttpMethod.Post, enrollment));
                Check(unsupported["status"]!.GetValue<string>() == "Failed" && unsupported["enrollment"] is null
                    && unsupported["nativeFailure"] is null && unsupported["diagnosticsError"]!["code"]!.GetValue<string>() == "Unsupported",
                    "ReadFailure unsupported diagnostics never become success, default phase or empty effect claims.");
                gateway.Failure = new(ErrorCode.Unavailable, "Synthetic cancellation.")
                { Evidence = ["native-setup:v1", "native-setup:phase:Cancelled", "native-setup:cancelled",
                    "native-setup:effect:IdentityRegistration:Possible"] };
                var nativeCancelled = await Wait(await Ok("ado-binding", HttpMethod.Post, binding));
                Check(nativeCancelled["status"]!.GetValue<string>() == "Cancelled"
                    && nativeCancelled["nativeFailure"]!["cancelled"]!.GetValue<bool>(),
                    "Native cancellation is preserved independently of host cancellation.");
                Check(gateway.Calls == 3, "Each explicit failed gesture invokes its facade exactly once.");
            }
            else
            {
                var enrolled = await Wait(await Ok("enroll-aad", HttpMethod.Post, enrollment));
                Check(enrolled["enrollment"]!["identityId"]!.GetValue<string>() == "native-identity"
                    && gateway.Enrollment == new NativeAadEnrollmentRequest("named-identity", "synthetic-tenant"),
                    "Forward explicit named PKCE enrollment and preserve native identity.");
                var bound = await Wait(await Ok("ado-binding", HttpMethod.Post, binding));
                Check(bound["binding"]!["bindingId"]!.GetValue<string>() == "native-binding" && bound["binding"]!["revision"]!.GetValue<long>() == 7
                    && gateway.Binding == new NativeAdoBindingRequest("synthetic-org", "synthetic-project", "named-identity"),
                    "Binding request never substitutes a default; native ownership/revision is retained.");
                var ado = await Wait(await Ok("initialize-ado", HttpMethod.Post, Ado()));
                Check(gateway.Fresh == new NativeFreshSetupRequest(root,
                    new NativeAdoSetupTarget("synthetic-org", "synthetic-project", "exact-binding", "explicit-team")),
                    "Creation receives exactly the explicit fresh root and binding, not another context.");
                CheckReady(ado);
                var github = await Wait(await Ok("initialize-github", HttpMethod.Post, GitHub()));
                Check(gateway.Fresh == new NativeFreshSetupRequest(root,
                    new NativeGitHubSetupTarget("github.com", "synthetic/repository", "named-account")), "Named GitHub target is exact.");
                CheckReady(github);
                gateway.BadSchema = true;
                var bad = await Wait(await Ok("initialize-github", HttpMethod.Post, GitHub()));
                Check(bad["status"]!.GetValue<string>() == "Failed" && bad["source"] is null,
                    "Missing or foreign schema readback cannot manufacture Ready.");
                Check(sources.Reads == 0, "Setup must not recompose native readback through a substituted viewing context.");
                var after = Required(await store.ReadAsync(ct));
                var current = sessions.Content(human.ViewId, human.ContentCapability, true);
                Check(after.Benches.Length == before.Benches.Length && current.NativeContext is null && current.Preferences.BenchId is null,
                    "Setup never creates/selects a Bench or viewing connection.");
                Check(gateway.Calls == 5, "Only explicit setup gestures dispatch.");
                void CheckReady(JsonNode ready)
                {
                    var source = ready["source"]!;
                    Check(ready["status"]!.GetValue<string>() == "Ready" && source["workspaceRoot"]!.GetValue<string>() == root
                        && source["source"]!["storeInstance"]!.GetValue<Guid>() == gateway.Stamp.StoreInstance
                        && source["source"]!["principalId"]!.GetValue<string>() == gateway.Stamp.PrincipalId
                        && source["source"]!["authorityStamp"]!.GetValue<string>() == gateway.Stamp.AuthorityStamp
                        && JsonNode.DeepEquals(source["schemaConnection"], source["source"]!["connection"]),
                        "Ready retains the real facade's exact creation/source/schema evidence, never ambient or root-only proof.");
                }
            }
            await app.StopAsync(ct);
        }
        finally
        {
            ownedSession?.Setup.Invalidate();
            gateway.Release?.TrySetResult();
            if (ownedSession?.Setup.Operation?.Work is { } work) await work.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("PASS connection setup " + scenario);
    }

    private static T Required<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class NoSourceReads : INativeSourceGateway
    {
        internal int Reads;
        public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected, bool observeSchema, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            throw new InvalidOperationException("Setup must consume only its facade's original readback.");
        }
    }
    private sealed class SetupGateway : INativeConnectionSetupGateway
    {
        internal int Calls;
        internal Error? Failure;
        internal bool BadSchema;
        internal NativeAadEnrollmentRequest? Enrollment;
        internal NativeAdoBindingRequest? Binding;
        internal NativeFreshSetupRequest? Fresh;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Release;
        internal NativeSourceStamp Stamp { get; private set; } = new(new(new("ado"), "native-resource", "native-destination", "native-identity"),
            Guid.NewGuid(), "native-principal", "native-authority");
        public Task<Result<NativeAadEnrollment>> EnrollAadAsync(NativeAadEnrollmentRequest request, Action<Uri> authorizationUrl, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Enrollment = request;
            authorizationUrl(new("https://login.microsoftonline.com/synthetic/authorize?state=synthetic"));
            return Task.FromResult(Failure is { } error ? Result<NativeAadEnrollment>.Fail(error)
                : Result<NativeAadEnrollment>.Ok(new(request.Alias, "native-identity", "native-tenant", "native-object",
                    "native-issuer", "login.microsoftonline.com", "named-user")));
        }
        public Task<Result<NativeAdoBinding>> CreateAdoBindingAsync(NativeAdoBindingRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Binding = request;
            return Task.FromResult(Failure is { } error ? Result<NativeAdoBinding>.Fail(error)
                : Result<NativeAdoBinding>.Ok(new("native-binding", "native-connection", "native-identity", 7)));
        }
        public async Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Fresh = request; Entered.TrySetResult();
            // Deliberately returns late success even if cancellation was requested at the external boundary.
            if (Release is { } release) await release.Task;
            if (Failure is { } error) return Result<NativeSourceObservation>.Fail(error);
            Stamp = Stamp with { Connection = request.Target is NativeGitHubSetupTarget
                ? new(new("github"), "native-github-resource", "native-github-repository", "native-github-identity")
                : new(new("ado"), "native-resource", "native-destination", "native-identity") };
            var connection = BadSchema ? new Connection(new("github"), "foreign", "foreign", "foreign") : Stamp.Connection;
            return Result<NativeSourceObservation>.Ok(new(Stamp, new(1, []), DateTimeOffset.UtcNow,
                Result<ProcessSchema>.Ok(new(connection, [], [])), DateTimeOffset.UtcNow));
        }
    }
}
