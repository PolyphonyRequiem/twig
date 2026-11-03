using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    private static string? PresentationText(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() : null;

    private async Task<FormLayout> ReadFormLayout(Bound bound, string typeRoute, string reference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, bound.Url(typeRoute + "/layout?api-version=7.1"));
        request.Headers.Authorization = bound.Authorization;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        FormLayout Absent(LayoutAvailability availability) => new(bound.ProcessId, reference, availability, [], []);
        if (response.StatusCode == HttpStatusCode.NotFound) return Absent(LayoutAvailability.Unavailable);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            throw HttpFailure(response.StatusCode);
        var root = JsonSerializer.Deserialize(await ResponseBytes(response, ct).ConfigureAwait(false),
            TwigJsonContext.Default.JsonElement);
        Wire.Unique(root);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            if (PresentationText(root, "message")?.Contains("VS403115", StringComparison.Ordinal) == true)
                return Absent(LayoutAvailability.Locked);
            throw HttpFailure(response.StatusCode);
        }
        if (response.Headers.Contains("x-ms-continuationtoken"))
            throw Wire.Refuse(ErrorCode.Unsupported, "The native form layout is paginated; no complete layout was observed.");
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            throw Wire.Invalid("The native form layout has no pages array.");
        var remaining = 8192;
        JsonElement[] Children(JsonElement parent, string name, bool ordered)
        {
            if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
            var children = value.EnumerateArray().ToArray();
            remaining -= children.Length;
            if (remaining < 0) throw Wire.Invalid("The native form layout exceeds its control bound.");
            return ordered ? children.OrderBy(child => child.TryGetProperty("order", out var order)
                && order.ValueKind != JsonValueKind.Null ? order.GetInt32() : int.MaxValue).ToArray() : children;
        }
        static bool Visible(JsonElement value) => !value.TryGetProperty("visible", out var flag)
            || flag.ValueKind == JsonValueKind.Null || flag.GetBoolean();
        static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag)
            && flag.ValueKind != JsonValueKind.Null && flag.GetBoolean();
        LayoutControl Control(JsonElement value) => new(PresentationText(value, "id") ?? "",
            PresentationText(value, "label") ?? "", PresentationText(value, "controlType") ?? "",
            Flag(value, "readOnly"), Visible(value), Flag(value, "isContribution"),
            PresentationText(value, "watermark"), value.TryGetProperty("height", out var height)
                && height.ValueKind != JsonValueKind.Null ? height.GetInt32() : null);
        var mapped = Children(root, "pages", true).Select(page => new LayoutPage(
            PresentationText(page, "id") ?? "", PresentationText(page, "label") ?? "",
            PresentationText(page, "pageType") ?? "custom", Visible(page), Flag(page, "isContribution"),
            Children(page, "sections", false).Select(section => new LayoutSection(
                PresentationText(section, "id") ?? "",
                Children(section, "groups", true).Select(group => new LayoutGroup(
                    PresentationText(group, "id") ?? "", PresentationText(group, "label") ?? "",
                    Visible(group), Flag(group, "isContribution"),
                    Children(group, "controls", true).Select(Control).ToImmutableArray())).ToImmutableArray()))
                .ToImmutableArray())).ToImmutableArray();
        return new(bound.ProcessId, reference, LayoutAvailability.Served, mapped,
            Children(root, "systemControls", true).Select(Control).ToImmutableArray());
    }
}
