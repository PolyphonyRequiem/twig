using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bench.Server;

/// <summary>Stdio transport only: all data and scope decisions remain in the running daemon.</summary>
public static partial class DaemonMcpBridge
{
    public static async Task RunAsync(Uri origin, HttpClient owner, HttpClient plain,
        TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        async Task<string> Send(HttpClient client, string path, string body, string? capability = null,
            HttpMethod? method = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            using var request = new HttpRequestMessage(method ?? HttpMethod.Post, new Uri(origin, path));
            request.Headers.Add("X-Twig-Bench", "1");
            if (capability is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability);
            if (method != HttpMethod.Delete) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/json" && response.StatusCode != System.Net.HttpStatusCode.Accepted)
                throw new InvalidDataException("Unexpected daemon MCP content type.");
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (Encoding.UTF8.GetByteCount(text) > McpReadServer.MaximumResponse)
                throw new InvalidDataException("Daemon MCP response exceeded its bound.");
            return text;
        }
        var registration = JsonNode.Parse(await Send(plain, "/api/v1/canvas-views", "{}"))!.AsObject();
        if (registration.Count != 2 || !Guid.TryParseExact(registration["viewId"]?.GetValue<string>(), "D", out var id)
            || id == Guid.Empty || registration["presentationCapability"]?.GetValue<string>() is not { } capability
            || !CapabilityPattern().IsMatch(capability))
            throw new InvalidDataException("Invalid daemon MCP view registration.");
        var view = $"/api/v1/views/{id:D}";
        try
        {
            var acknowledged = JsonNode.Parse(await Send(owner, "/api/v1/windows-agent",
                new JsonObject { ["viewId"] = id }.ToJsonString()))!.AsObject();
            if (acknowledged.Count != 1 || acknowledged["acknowledged"]?.GetValue<bool>() != true)
                throw new InvalidDataException("The daemon did not authenticate the local MCP owner.");
            var buffer = new char[1];
            var line = new StringBuilder();
            while (await input.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
            {
                if (buffer[0] == '\r') continue;
                if (buffer[0] != '\n')
                {
                    if (line.Length >= 16384) throw new InvalidDataException("MCP request exceeds its bound.");
                    line.Append(buffer[0]);
                    continue;
                }
                if (line.Length == 0) continue;
                var response = await Send(plain, view + "/mcp", line.ToString(), capability);
                line.Clear();
                if (response.Length == 0) continue;
                await output.WriteLineAsync(response.AsMemory(), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (line.Length != 0) throw new InvalidDataException("Incomplete MCP request at end of input.");
        }
        finally
        {
            await Send(plain, view, "", capability, HttpMethod.Delete);
        }
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityPattern();
}
