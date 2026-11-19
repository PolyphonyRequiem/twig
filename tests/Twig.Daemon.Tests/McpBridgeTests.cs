using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Server;

namespace Twig.Daemon.Tests.Native;

internal static class McpBridgeTests
{
    internal static async Task Run()
    {
        var id = Guid.NewGuid();
        var capability = new string('b', 64);
        var initialized = false;
        var closed = false;
        var forwards = 0;
        using var owner = new HttpClient(new Handler(async request =>
        {
            Check(request.RequestUri!.AbsolutePath == "/api/v1/windows-agent" && request.Headers.Authorization is null,
                "Only the owner client may perform Windows bootstrap, without a bearer token.");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Check(body["viewId"]!.GetValue<Guid>() == id, "Bootstrap must use exactly the registered view.");
            initialized = true;
            return Json("""{"acknowledged":true}""");
        }));
        using var plain = new HttpClient(new Handler(async request =>
        {
            Check(request.Headers.GetValues("X-Twig-Bench").Single() == "1", "Every bridge request retains transport protection.");
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/canvas-views")
                return Json(new JsonObject { ["viewId"] = id, ["presentationCapability"] = capability }.ToJsonString());
            Check(request.Headers.Authorization?.Parameter == capability
                && request.RequestUri.Query.Length == 0, "Capabilities belong only in the exact view's Authorization header.");
            if (request.Method == HttpMethod.Delete)
            {
                Check(path == $"/api/v1/views/{id:D}", "EOF closes only the bridge's own view.");
                closed = true; return Json("""{"acknowledged":true}""");
            }
            Check(initialized && path == $"/api/v1/views/{id:D}/mcp", "Stdio must forward only after owner authentication.");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            forwards++;
            if (body["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);
            return Json(new JsonObject
            { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone(), ["result"] = new JsonObject() }.ToJsonString());
        }));
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}

            """);
        using var output = new StringWriter();
        await DaemonMcpBridge.RunAsync(new Uri("https://localhost:52971"), owner, plain, input, output, default);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(closed && forwards == 3 && lines.Length == 2
            && JsonNode.Parse(lines[0])!["id"]!.GetValue<int>() == 1
            && JsonNode.Parse(lines[1])!["id"]!.GetValue<int>() == 2
            && !output.ToString().Contains(capability, StringComparison.Ordinal),
            "The bridge must preserve request IDs, suppress notification responses, keep stdout protocol-only, and release its view.");
        Console.WriteLine("MCP STDIO PASSED: exact owner bootstrap, scoped forwarding, no credentials on stdout, EOF cleanup.");
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
