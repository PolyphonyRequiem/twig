using System.Net;
using System.Security.Cryptography;
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

public sealed class CanvasPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-package-" + Guid.NewGuid().ToString("N"));
    public CanvasPackageTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Daemon_without_canvas_keeps_api_and_views_alive_across_compatible_asset_switch()
    {
        var assets = Path.Combine(root, "canvas");
        var changes = new CatalogChanges();
        var gateway = new NativeSourceGateway();
        var adapter = new NativeSourceAdapter(Path.Combine(root, "evidence"), gateway, changes);
        var panel = new NativePanel(new UserBenchStore(Path.Combine(root, "user", "benches.db")), gateway, adapter);
        var sessions = new ViewSessions(Path.Combine(root, "presentation"));
        await using var app = NativeBenchHost.Build(new(0, root, CanvasInstallRoot: assets), panel, sessions, changes);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/canvas-views")
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Twig-Bench", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadAsStringAsync();
        using var registration = JsonDocument.Parse(view);
        var pair = sessions.ConnectOwner((await panel.ListAsync(default)).Value.InstanceId,
            registration.RootElement.GetProperty("viewId").GetGuid(), Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new("Bearer", pair.ContentCapability);
        var sessionPath = $"/api/v1/views/{pair.ViewId:D}/session";
        var state = await client.GetStringAsync(sessionPath);
        Select(assets, new string('a', 40), "first");
        (await client.GetStringAsync("/")).ShouldBe("first");
        Select(assets, new string('b', 40), "second");
        (await client.GetStringAsync("/")).ShouldBe("second");
        (await client.GetStringAsync(sessionPath)).ShouldBe(state);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.AppendAllText(Path.Combine(assets, "versions", new string('b', 40), "wwwroot", "native-index.html"), "tampered");
        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        File.Delete(Path.Combine(assets, "versions", new string('b', 40), "wwwroot", "native-index.html"));
        (await client.GetAsync("/")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("extra")]
    [InlineData("traversal")]
    [InlineData("license")]
    [InlineData("hash")]
    public void Package_rejects_incompatible_incomplete_or_unlisted_content(string fault)
    {
        Select(root, new string('c', 40), "content", fault);
        Should.Throw<InvalidDataException>(() => CanvasAssets.Read(root, "native-index.html"));
    }

    private static void Select(string installation, string version, string text, string? fault = null)
    {
        var package = Path.Combine(installation, "versions", version);
        var entries = new List<(string Path, string Hash)>();
        foreach (var file in CanvasAssets.RequiredFiles)
        {
            var path = Path.Combine(package, file.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            if (fault == "license" && file == @"licenses\marked.LICENSE") continue;
            entries.Add((file, Hash(File.ReadAllBytes(path))));
        }
        if (fault == "extra") File.WriteAllText(Path.Combine(package, "unlisted.txt"), "not admitted");
        if (fault == "traversal") entries.Add((@"wwwroot\..\outside", new string('A', 64)));
        var manifest = Path.Combine(package, "assets.json");
        using (var stream = File.Create(manifest))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("application", CanvasAssets.Application);
            writer.WriteNumber("schema", 1);
            writer.WriteString("version", version);
            writer.WriteString("protocol", fault == "protocol" ? "future-incompatible" : CanvasAssets.Protocol);
            writer.WriteStartArray("files");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Path);
                writer.WriteString("sha256", fault == "hash" ? new string('0', 64) : entry.Hash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var pointer = File.Create(Path.Combine(installation, "current.json"));
        using var output = new Utf8JsonWriter(pointer);
        output.WriteStartObject();
        output.WriteString("application", CanvasAssets.Application);
        output.WriteNumber("schema", 1);
        output.WriteString("version", version);
        output.WriteString("manifestHash", Hash(File.ReadAllBytes(manifest)));
        output.WriteEndObject();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose() => Directory.Delete(root, true);
}
