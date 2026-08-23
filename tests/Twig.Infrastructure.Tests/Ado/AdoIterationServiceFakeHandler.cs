using System.Net;
using System.Text;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;

namespace Twig.Infrastructure.Tests.Ado;

internal sealed class FakeAuthProvider : IAuthenticationProvider
{
    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        => Task.FromResult("fake-bearer-token");

    public void InvalidateToken() { }
}

/// <summary>
/// Fake HttpMessageHandler that returns canned JSON responses for ADO endpoints.
/// Extend this class to add behaviour (e.g. call counting) without duplicating response setup.
/// </summary>
internal class FakeHandler : HttpMessageHandler
{
    protected readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);
    protected readonly Dictionary<string, (HttpStatusCode Status, string Body)> _statusResponses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Responses matched by predicate rather than by substring, for routes that no substring
    /// separates. Matched after the status overrides and BEFORE the substring table.
    /// </summary>
    protected readonly List<(Func<string, bool> Matches, string Body)> _predicateResponses = [];

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

    public void SetTeamIterationsResponse(params (string path, string? startDate, string? finishDate)[] iterations)
    {
        var items = iterations.Select(i =>
        {
            var escapedPath = i.path.Replace(@"\", @"\\");
            var startPart = i.startDate is not null ? $"\"startDate\":\"{i.startDate}\"" : "\"startDate\":null";
            var finishPart = i.finishDate is not null ? $"\"finishDate\":\"{i.finishDate}\"" : "\"finishDate\":null";
            return $"{{\"id\":\"guid-{Guid.NewGuid():N}\",\"name\":\"{escapedPath.Split('\\').Last()}\",\"path\":\"{escapedPath}\",\"attributes\":{{{startPart},{finishPart}}}}}";
        });
        var json = $"{{\"count\":{iterations.Length},\"value\":[{string.Join(',', items)}]}}";
        _responses["/_apis/work/teamsettings/iterations"] = json;
    }

    public void SetRawResponse(string urlFragment, string json)
    {
        _responses[urlFragment] = json;
    }

    /// <summary>
    /// Canned answer for the PROCESS's own work item type roster —
    /// <c>_apis/work/processes/{id}/workItemTypes</c>.
    /// </summary>
    /// <remarks>
    /// 🔴 Deliberately distinct from <see cref="SetWorkItemTypesResponse"/>, which answers
    /// the PROJECT route. The two rosters really do disagree on the reference name of every
    /// inherited type against a live org, and the layout fetch resolves against this one
    /// (AB#247). A fixture that conflated them could not catch the wrong-roster defect.
    /// <para>
    /// 🔴 Registered as a PREDICATE, not a substring key, because no substring separates the
    /// three routes involved. The process roster is
    /// <c>/_apis/work/processes/{id}/workItemTypes?…</c>, the project roster is
    /// <c>/_apis/wit/workitemtypes?…</c> — which contains <c>workItemTypes?</c> too — and the
    /// layout route is that same process path plus <c>/{ref}/layout</c>. Keying on
    /// <c>/workItemTypes?</c> made this stub answer the PROJECT route as well, which turned
    /// the wrong-roster regression test into a tautology that passed against the unfixed
    /// code. Caught only by running the baseline proof.
    /// </para>
    /// </remarks>
    public void SetProcessWorkItemTypesResponse(
        params (string name, string referenceName)[] types)
    {
        var typeJsons = types.Select(t =>
            $"{{\"name\":\"{t.name}\",\"referenceName\":\"{t.referenceName}\"," +
            "\"description\":\"\",\"customization\":\"inherited\",\"isDisabled\":false}");
        var json = $"{{\"count\":{types.Length},\"value\":[{string.Join(',', typeJsons)}]}}";

        _predicateResponses.Add((
            url => url.Contains("/work/processes/", StringComparison.OrdinalIgnoreCase)
                && url.Contains("/workItemTypes?", StringComparison.OrdinalIgnoreCase),
            json));
    }

    /// <summary>
    /// Makes a URL fragment answer with a specific status code and body, so a test can
    /// reproduce a real server ERROR rather than only a happy path.
    /// </summary>
    /// <remarks>
    /// 🔴 This exists because the locked-type defect (AB#247) was invisible to the seam
    /// tests: a fixture that only ever returns 200 or 404 cannot produce the
    /// <b>400 VS403115</b> the layout route answers for a locked type, which is how that
    /// failure reached production in the description path — found by running the command
    /// live, not by the suite. Status responses are matched BEFORE the canned-JSON table so
    /// a test can override a fragment it has also stubbed.
    /// </remarks>
    public void SetStatusResponse(string urlFragment, HttpStatusCode status, string body)
    {
        _statusResponses[urlFragment] = (status, body);
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

    /// <summary>
    /// Canned answer for the work item type CATEGORIES route —
    /// <c>_apis/wit/workitemtypecategories</c> (AB#656).
    /// </summary>
    /// <remarks>
    /// 🔴 Takes category → member type names, i.e. the CATEGORY-MAJOR shape the real route
    /// returns, so a type appearing in several categories is expressed the way ADO expresses
    /// it. A fixture that took type → categories would pre-invert the data and the inversion
    /// under test would never run.
    /// <para>
    /// Keyed on <c>/_apis/wit/workitemtypecategories</c>, which is safely distinct from
    /// <see cref="SetWorkItemTypesResponse"/>'s <c>/_apis/wit/workitemtypes</c> — the strings
    /// diverge at <c>workitemtype[c]</c> vs <c>workitemtype[s]</c>, so neither key is a
    /// substring of the other URL.
    /// </para>
    /// </remarks>
    public void SetWorkItemTypeCategoriesResponse(
        params (string categoryReferenceName, string[] typeNames)[] categories)
    {
        var categoryJsons = categories.Select(c =>
        {
            var members = c.typeNames.Select(n =>
                $"{{\"name\":\"{n}\",\"referenceName\":\"System.{n.Replace(" ", "")}\"}}");
            return $"{{\"referenceName\":\"{c.categoryReferenceName}\"," +
                $"\"name\":\"{c.categoryReferenceName}\"," +
                $"\"workItemTypes\":[{string.Join(',', members)}]}}";
        });
        var json = $"{{\"count\":{categories.Length},\"value\":[{string.Join(',', categoryJsons)}]}}";
        _responses["/_apis/wit/workitemtypecategories"] = json;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // Status overrides are matched first so a test can make an already-stubbed fragment
        // answer with a real server error.
        foreach (var kvp in _statusResponses)
        {
            if (url.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(kvp.Value.Status)
                {
                    Content = new StringContent(kvp.Value.Body, Encoding.UTF8, "application/json"),
                });
            }
        }

        foreach (var entry in _predicateResponses)
        {
            if (entry.Matches(url))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
                });
            }
        }

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
