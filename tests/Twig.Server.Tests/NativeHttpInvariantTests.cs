using Xunit;

namespace Twig.Server.Tests.Native;

public sealed class NativeHttpInvariantTests
{
    [Fact]
    public async Task ExecutionNegotiationPrecedesRegistrationAndOwnerAuthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), $"twig-negotiation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var changes = new Bench.Application.CatalogChanges();
            var gateway = new Bench.Infrastructure.NativeSourceGateway();
            var store = new Twig.Infrastructure.Services.UserBenchStore(Path.Combine(root, "benches.db"));
            var panel = new Bench.Infrastructure.NativePanel(store, gateway,
                new Bench.Infrastructure.NativeSourceAdapter(root, gateway, changes));
            var boot = Guid.NewGuid();
            await using var app = NativeBenchHost.Build(new(0, root, ProcessInstance: boot, ExecutionV2: true),
                panel, new ViewSessions(Path.Combine(root, "presentation")), changes);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            foreach (var path in new[] { "canvas-views", "windows-profile", "windows-renew", "windows-enroll" })
            {
                using var refused = await client.PostAsync("/api/v1/" + path, new StringContent("{}"));
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains("\"PolicyViolation\"", await refused.Content.ReadAsStringAsync());
            }
            var bytes = await client.GetByteArrayAsync("/api/v1/status");
            Assert.InRange(bytes.Length, 1, 3072);
            using var status = System.Text.Json.JsonDocument.Parse(bytes);
            client.DefaultRequestHeaders.Add("X-Twig-Execution", status.RootElement.GetProperty("executionSession").GetString());
            using var registered = await client.PostAsync("/api/v1/canvas-views", new StringContent("{}",
                System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.OK, registered.StatusCode);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProtectsNativePanelRoutesAndProfiles(bool profilesOnly) => NativePanelTests.Run(profilesOnly);

    [Fact]
    public Task AggregatesOnlyAdmittedNativeSources() => NativeAggregationTests.Run();

    [Fact]
    public void BoundsAndRedactsSourceDiagnostics() => NativeSourceDiagnosticsTests.Run();

    [Fact]
    public Task ScopesMcpBridgeAuthorityAndReleasesItsView() => McpBridgeTests.Run();

    [Theory]
    [InlineData("gates")]
    [InlineData("cancellation")]
    [InlineData("failures")]
    [InlineData("readback")]
    public Task ProtectsConnectionSetup(string scenario) => ConnectionSetupTests.Run(scenario);
}
