using System.Net;
using System.Text;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;

namespace Twig.Infrastructure.Tests.Ado;

internal sealed class FakeAuthProvider : IAuthenticationProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult("fake-bearer-token");
}

/// <summary>
/// Fake HttpMessageHandler that returns canned JSON responses for ADO endpoints.
/// Extend this class to add behaviour (e.g. call counting) without duplicating response setup.
/// </summary>
internal class FakeHandler : HttpMessageHandler
{
    protected readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

    public void SetWorkItemTypesResponse(params string[] typeNames)
    {
        var types = typeNames.Select(n =>
            $"{{\"name\":\"{n}\",\"description\":\"\",\"referenceName\":\"System.{n.Replace(" ", "")}\",\"color\":\"AABBCC\",\"icon\":{{\"id\":\"icon_test\",\"url\":\"https://example.com\"}},\"isDisabled\":false}}");
        var json = $"{{\"count\":{typeNames.Length},\"value\":[{string.Join(',', types)}]}}";
        _responses["/_apis/wit/workitemtypes"] = json;
    }

    public void SetWorkItemTypesResponseDetailed(params (string name, string? color, string? iconId, bool isDisabled)[] types)
    {
        var typeJsons = types.Select(t =>
        {
            var colorPart = t.color is not null ? $"\"color\":\"{t.color}\"" : "\"color\":null";
            var iconPart = t.iconId is not null
                ? $"\"icon\":{{\"id\":\"{t.iconId}\",\"url\":\"https://example.com\"}}"
                : "\"icon\":null";
            return $"{{\"name\":\"{t.name}\",\"description\":\"\",\"referenceName\":\"System.{t.name.Replace(" ", "")}\",{colorPart},{iconPart},\"isDisabled\":{t.isDisabled.ToString().ToLowerInvariant()}}}";
        });
        var json = $"{{\"count\":{types.Length},\"value\":[{string.Join(',', typeJsons)}]}}";
        _responses["/_apis/wit/workitemtypes"] = json;
    }

    public void SetIterationResponse(string iterationPath)
    {
        var escapedPath = iterationPath.Replace(@"\", @"\\");
        var json = $"{{\"count\":1,\"value\":[{{\"id\":\"guid-1\",\"name\":\"Sprint 1\",\"path\":\"{escapedPath}\",\"attributes\":{{\"startDate\":\"2026-01-01\",\"finishDate\":\"2026-01-14\",\"timeFrame\":\"current\"}}}}]}}";
        _responses["/_apis/work/teamsettings/iterations"] = json;
    }

    public void SetRawResponse(string urlFragment, string json)
    {
        _responses[urlFragment] = json;
    }

    public void SetWorkItemTypesResponseWithStates(params (string name, string? color, string? iconId, bool isDisabled, (string name, string category)[] states)[] types)
    {
        var typeJsons = types.Select(t =>
        {
            var colorPart = t.color is not null ? $"\"color\":\"{t.color}\"" : "\"color\":null";
            var iconPart = t.iconId is not null
                ? $"\"icon\":{{\"id\":\"{t.iconId}\",\"url\":\"https://example.com\"}}"
                : "\"icon\":null";
            var stateJsons = t.states.Select(s => $"{{\"name\":\"{s.name}\",\"color\":\"FFFFFF\",\"category\":\"{s.category}\"}}");
            var statesJson = $"\"states\":[{string.Join(',', stateJsons)}]";
            return $"{{\"name\":\"{t.name}\",\"description\":\"\",\"referenceName\":\"System.{t.name.Replace(" ", "")}\",{colorPart},{iconPart},\"isDisabled\":{t.isDisabled.ToString().ToLowerInvariant()},{statesJson}}}";
        });
        var json = $"{{\"count\":{types.Length},\"value\":[{string.Join(',', typeJsons)}]}}";
        _responses["/_apis/wit/workitemtypes"] = json;
    }

    public void SetProcessConfigurationResponse(string json)
    {
        _responses["/_apis/work/processconfiguration"] = json;
    }

    public void SetProjectCapabilitiesResponse(string templateName)
    {
        var json = $"{{\"capabilities\":{{\"processTemplate\":{{\"templateName\":\"{templateName}\"}}}}}}";
        _responses["/_apis/projects/"] = json;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        foreach (var kvp in _responses)
        {
            if (url.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(kvp.Value, Encoding.UTF8, "application/json"),
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(""),
        });
    }

    internal static AdoIterationService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) }, new FakeAuthProvider(),
            "https://dev.azure.com/testorg", "testproject", "testteam");
}
