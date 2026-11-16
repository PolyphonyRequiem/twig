using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Daemon.Tests;

public sealed class NativeHostCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-native-host-" + Guid.NewGuid().ToString("N"));
    public NativeHostCleanupTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Native_host_ignores_retired_catalog_and_exposes_no_retired_routes()
    {
        var catalog = Path.Combine(root, "catalog.db");
        var receipt = Path.Combine(root, "native-migration.receipt");
        await File.WriteAllTextAsync(catalog, "not a database");
        await File.WriteAllTextAsync(receipt, "not a migration receipt");
        var changes = new CatalogChanges();
        var gateway = new NativeSourceGateway();
        var panel = new NativePanel(new UserBenchStore(Path.Combine(root, "native", "benches.db")), gateway,
            new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
        await app.StartAsync();
        using var client = Client(app.Services);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var registration = await Post(client, "/api/v1/canvas-views", "{}");
        var view = registration.RootElement.GetProperty("viewId").GetGuid();
        view.ShouldNotBe(Guid.Empty);
        var presentation = registration.RootElement.GetProperty("presentationCapability").GetString();
        presentation.ShouldNotBeNullOrEmpty();
        var instance = (await panel.ListAsync(default)).Value.InstanceId;
        instance.ShouldNotBe(Guid.Empty);
        var owner = sessions.ConnectOwner(instance, view, Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.ContentCapability);
        (await client.GetAsync($"/api/v1/views/{view:D}/session")).StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var route in new[]
        {
            "/api/v1/pair", "/api/v1/device-challenge", "/api/v1/device-resume",
            $"/api/v1/views/{view:D}/device", $"/api/v1/views/{view:D}/sources",
            $"/api/v1/views/{view:D}/sources/{Guid.NewGuid():D}/recheck",
            $"/api/v1/views/{view:D}/creations/preview", $"/api/v1/views/{view:D}/creations/confirm"
        })
        {
            using var request = Request(route, "{}");
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, route);
        }
        (await client.GetAsync($"/api/v1/views/{view:D}/catalog")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/views/{view:D}/sources/{Guid.NewGuid():D}/native-benches"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/views/{view:D}/creations/{Guid.NewGuid():D}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await File.ReadAllTextAsync(catalog)).ShouldBe("not a database");
        (await File.ReadAllTextAsync(receipt)).ShouldBe("not a migration receipt");
        (await panel.ListAsync(default)).Value.Benches.ShouldBeEmpty();
        await app.StopAsync();
    }

    [Fact]
    public async Task Mcp_initialization_reports_the_supported_negotiated_protocol()
    {
        var changes = new CatalogChanges();
        var gateway = new NativeSourceGateway();
        var panel = new NativePanel(new UserBenchStore(Path.Combine(root, "native", "benches.db")), gateway,
            new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        await using var app = NativeBenchHost.Build(new(0, root), panel, sessions, changes);
        await app.StartAsync();
        using var client = Client(app.Services);
        using var registration = await Post(client, "/api/v1/canvas-views", "{}");
        var view = registration.RootElement.GetProperty("viewId").GetGuid();
        view.ShouldNotBe(Guid.Empty);
        var presentation = registration.RootElement.GetProperty("presentationCapability").GetString();
        presentation.ShouldNotBeNullOrEmpty();
        var instance = (await panel.ListAsync(default)).Value.InstanceId;
        _ = sessions.ConnectOwner(instance, view, Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", presentation);
        var path = $"/api/v1/views/{view:D}/mcp";
        foreach (var protocol in new[] { "2025-03-26", "2025-06-18", "2025-11-25" })
        {
            using var request = Request(path,
                $$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{protocol}}"}}""");
            request.Headers.Add("MCP-Protocol-Version", protocol);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            result.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe(protocol);
        }
        using var unsupported = Request(path, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        unsupported.Headers.Add("MCP-Protocol-Version", "not-supported");
        using var refused = await client.SendAsync(unsupported);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await app.StopAsync();
    }

    private static HttpClient Client(IServiceProvider services) => new()
    {
        BaseAddress = new(services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    private static HttpRequestMessage Request(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Twig-Bench", "1");
        return request;
    }

    private static async Task<JsonDocument> Post(HttpClient client, string path, string json)
    {
        using var request = Request(path, json);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => Directory.Delete(root, true);
}
