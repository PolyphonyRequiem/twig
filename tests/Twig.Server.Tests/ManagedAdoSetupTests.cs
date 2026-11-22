using System.Net;
using System.Text;
using System.Text.Json;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Server.Tests;

public sealed class ManagedAdoSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-managed-ado-host-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ready")]
    [InlineData("cancelled")]
    [InlineData("schema-mismatch")]
    public async Task ManagedAdoUsesOriginalOwnerScopedOperationWithoutAWorkspacePath(string outcome)
    {
        var setup = new SetupGateway();
        var sourceGateway = new NativeSourceGateway();
        var changes = new CatalogChanges();
        var panel = new NativePanel(new UserBenchStore(Path.Combine(root, "benches.db")), sourceGateway,
            new NativeSourceAdapter(Path.Combine(root, "evidence"), sourceGateway, changes));
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes, setup);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
        var registration = sessions.RegisterNativeCanvas((await panel.ListAsync(default)).Value.InstanceId);
        var path = $"/api/v1/views/{registration.ViewId:D}/setup";
        const string body = """{"organization":"fixture","project":"Project","bindingId":"exact-binding","team":"Team","gitProject":"Git project","epoch":0}""";
        using (var denied = await client.PostAsync(path + "/initialize-managed-ado", Json(body)))
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        setup.Calls.ShouldBe(0);
        var owner = sessions.ConnectOwner((await panel.ListAsync(default)).Value.InstanceId,
            registration.ViewId, Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.ContentCapability);

        using (var suppliedPath = await client.PostAsync(path + "/initialize-managed-ado",
            Json(body.Replace("\"epoch\":0", "\"workspaceRoot\":\"invented\",\"epoch\":0", StringComparison.Ordinal))))
            suppliedPath.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        setup.Calls.ShouldBe(0);
        using (var stale = await client.PostAsync(path + "/initialize-managed-ado",
            Json(body.Replace("\"epoch\":0", "\"epoch\":1", StringComparison.Ordinal))))
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        setup.Calls.ShouldBe(0);
        using var started = await client.PostAsync(path + "/initialize-managed-ado", Json(body));
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using var original = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var operation = original.RootElement.GetProperty("operationId").GetGuid();
        await setup.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        setup.Target.ShouldBe(new NativeAdoSetupTarget("fixture", "Project", "exact-binding", "Team", "Git project"));
        using (var duplicate = await client.PostAsync(path + "/initialize-managed-ado", Json(body)))
            duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        setup.Calls.ShouldBe(1);
        if (outcome == "cancelled")
        {
            using var cancelled = await client.SendAsync(new(HttpMethod.Delete, path + $"/operations/{operation:D}")
                { Content = Json("{}") });
            cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        setup.Completion.SetResult(setup.Ready(outcome == "schema-mismatch"));
        var observed = await Observe(client, path, operation);
        using (observed)
        {
            observed.RootElement.GetProperty("status").GetString().ShouldBe(outcome switch
            { "cancelled" => "Cancelled", "schema-mismatch" => "Failed", _ => "Ready" });
            if (outcome != "ready" && observed.RootElement.TryGetProperty("source", out var value))
                value.ValueKind.ShouldBe(JsonValueKind.Null);
            if (outcome == "ready")
                observed.RootElement.GetProperty("source").GetProperty("workspaceRoot").GetString().ShouldBe(setup.ManagedRoot);
        }
        setup.Calls.ShouldBe(1);
        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/v1/status"));
        status.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString())
            .ShouldContain("managed-ado-setup-v1");
        await app.StopAsync();
    }

    private static async Task<JsonDocument> Observe(HttpClient client, string path, Guid operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var value = JsonDocument.Parse(await client.GetStringAsync(path + $"/operations/{operation:D}", deadline.Token));
            if (!value.RootElement.GetProperty("pending").GetBoolean()) return value;
            value.Dispose();
            await Task.Delay(10, deadline.Token);
        }
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class SetupGateway : INativeConnectionSetupGateway
    {
        internal int Calls;
        internal NativeSetupTarget? Target;
        internal string ManagedRoot { get; } = Path.Combine(Path.GetTempPath(), "native-private-context");
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Result<NativeManagedSetupResult>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result<NativeManagedSetupResult>> InitializeManagedAsync(NativeSetupTarget target, CancellationToken ct)
        {
            Calls++;
            Target = target;
            Started.SetResult();
            // Deliberately ignore cancellation to prove late successful completion cannot disclose a source.
            return Completion.Task;
        }
        internal Result<NativeManagedSetupResult> Ready(bool mismatch)
        {
            var connection = new Connection(new ProviderId("ado"), "fixture", "project", "exact-principal");
            var schemaConnection = mismatch
                ? new Connection(connection.Provider, connection.ResourceScope, connection.Destination, "other-principal") : connection;
            var observation = new NativeSourceObservation(new(connection, Guid.NewGuid(), "exact-principal", "exact-authority"),
                new StoreSnapshot(0), DateTimeOffset.UtcNow, Result<ProcessSchema>.Ok(new(schemaConnection, [], [])), DateTimeOffset.UtcNow);
            return Result<NativeManagedSetupResult>.Ok(new(ManagedRoot, observation));
        }
        public Task<Result<NativeAadEnrollment>> EnrollAadAsync(NativeAadEnrollmentRequest request, Action<Uri> authorizationUrl, CancellationToken ct)
            => throw new InvalidOperationException("Managed ADO must not enroll or switch an identity.");
        public Task<Result<NativeAdoBinding>> CreateAdoBindingAsync(NativeAdoBindingRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Managed ADO must use the exact supplied binding.");
        public Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request, CancellationToken ct)
            => throw new InvalidOperationException("The HTTP contract must not invent a client workspace path.");
    }
}
