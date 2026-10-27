using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Ado;

/// <summary>Downloads only attachments re-observed on the requested item revision.</summary>
internal sealed class AdoWorkItemAttachmentReader : IWorkItemAttachmentReader, IDisposable
{
    private readonly HttpClient http;
    private readonly IAuthenticationProvider authentication;
    private readonly Uri organization;
    private readonly string project;
    private readonly AdoConcurrencyThrottle? throttle;

    internal AdoWorkItemAttachmentReader(
        IAuthenticationProvider authentication, string organization, string project,
        AdoConcurrencyThrottle? throttle = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        var uri = new Uri(AdoRestClient.NormalizeOrgUrl(organization), UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Attachment organization must be an HTTPS authority.", nameof(organization));
        this.authentication = authentication;
        this.organization = uri;
        this.project = project;
        this.throttle = throttle;
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    public async Task<WorkItemAttachmentInventory> ReadAsync(int workItemId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workItemId);
        using var admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct).ConfigureAwait(false);
        return await ReadInventoryAsync(workItemId, ct).ConfigureAwait(false);
    }

    public async Task<WorkItemAttachmentDownload> DownloadAsync(
        int workItemId, int expectedRevision, Guid attachmentId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workItemId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        if (attachmentId == Guid.Empty) throw new ArgumentException("Attachment identity is required.", nameof(attachmentId));
        using var admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct).ConfigureAwait(false);
        var inventory = await ReadInventoryAsync(workItemId, ct).ConfigureAwait(false);
        if (inventory.Revision != expectedRevision)
            throw Failure("attachment-stale", "The item revision changed; request a fresh inventory.");
        var attachment = inventory.Attachments.SingleOrDefault(value => value.Id == attachmentId)
            ?? throw Failure("attachment-not-linked", "The attachment is not linked to the requested item.");
        if (attachment.ReportedSize > AttachmentReadLimits.MaximumDownloadBytes)
            throw Failure("attachment-too-large", "The attachment exceeds the Twig download limit.");
        var url = $"{ProjectRoot}/_apis/wit/attachments/{attachmentId:D}?download=true&api-version={AdoApiVersions.WorkItemAttachments}";
        var (bytes, contentType) = await GetAsync(url, AttachmentReadLimits.MaximumDownloadBytes, binary: true, ct)
            .ConfigureAwait(false);
        if (attachment.ReportedSize is { } reported && reported != bytes.LongLength)
            throw Failure("attachment-size-mismatch", "The attachment body does not match its reported size.");
        // Do not return bytes after removal or a revision change during the transfer.
        var after = await ReadInventoryAsync(workItemId, ct).ConfigureAwait(false);
        if (after.Revision != expectedRevision || !after.Attachments.Contains(attachment))
            throw Failure("attachment-stale", "Attachment membership changed during the download.");
        return new(workItemId, expectedRevision, attachment, contentType, bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private string ProjectRoot => $"{organization.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(project)}";

    private async Task<WorkItemAttachmentInventory> ReadInventoryAsync(int workItemId, CancellationToken ct)
    {
        var url = $"{ProjectRoot}/_apis/wit/workitems/{workItemId.ToString(CultureInfo.InvariantCulture)}"
            + $"?$expand=relations&api-version={AdoApiVersions.WorkItems}";
        var (bytes, _) = await GetAsync(url, AttachmentReadLimits.MaximumInventoryBytes, binary: false, ct)
            .ConfigureAwait(false);
        using var json = ParseInventory(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt32(out var actualId) || actualId != workItemId
            || !root.TryGetProperty("rev", out var rev) || rev.ValueKind != JsonValueKind.Number
            || !rev.TryGetInt32(out var revision) || revision <= 0)
            throw Failure("attachment-response-invalid", "The inventory has no matching item and revision.");
        var items = new List<WorkItemAttachment>();
        if (!root.TryGetProperty("relations", out var relations))
            return new(workItemId, revision, items);
        if (relations.ValueKind != JsonValueKind.Array)
            throw Failure("attachment-response-invalid", "The inventory relations are invalid.");
        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.ValueKind != JsonValueKind.Object || !relation.TryGetProperty("rel", out var kind)
                || kind.ValueKind != JsonValueKind.String)
                throw Failure("attachment-response-invalid", "An inventory relation is invalid.");
            if (kind.GetString() != "AttachedFile") continue;
            if (!relation.TryGetProperty("url", out var target) || target.ValueKind != JsonValueKind.String
                || !TryAttachmentId(target.GetString(), out var attachmentId)
                || !relation.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object
                || !attributes.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !ValidFileName(name.GetString()))
                throw Failure("attachment-response-invalid", "An attachment has invalid origin, identity or filename.");
            long? size = null;
            if (attributes.TryGetProperty("resourceSize", out var resourceSize))
            {
                if (resourceSize.ValueKind != JsonValueKind.Number || !resourceSize.TryGetInt64(out var length) || length < 0)
                    throw Failure("attachment-response-invalid", "An attachment size is invalid.");
                size = length;
            }
            if (items.Any(value => value.Id == attachmentId))
                throw Failure("attachment-response-invalid", "The inventory repeats an attachment identity.");
            items.Add(new(attachmentId, name.GetString()!, size));
        }
        return new(workItemId, revision, items);
    }

    private bool TryAttachmentId(string? value, out Guid id)
    {
        id = Guid.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != organization.Scheme
            || !string.Equals(uri.Host, organization.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != organization.Port || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var prefix = organization.AbsolutePath.TrimEnd('/') + "/_apis/wit/attachments/";
        var projectPrefix = organization.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(project)
            + "/_apis/wit/attachments/";
        var path = uri.AbsolutePath;
        var suffix = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..]
            : path.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase) ? path[projectPrefix.Length..] : null;
        return Guid.TryParseExact(suffix, "D", out id) && id != Guid.Empty;
    }

    private static bool ValidFileName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 255 && value is not ("." or "..")
        && !value.Any(character => char.IsControl(character) || character is '/' or '\\');

    private static JsonDocument ParseInventory(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try { RejectDuplicates(document.RootElement); }
            catch { document.Dispose(); throw; }
            return document;
        }
        catch (JsonException) { throw Failure("attachment-response-invalid", "The inventory is not valid JSON."); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private async Task<(byte[] Bytes, string ContentType)> GetAsync(
        string url, int maximumBytes, bool binary, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await GetCoreAsync(url, maximumBytes, binary, ct).ConfigureAwait(false); }
            catch (Exception ex) when (attempt == 0 && AdoErrorHandler.IsAuthChallenge(ex))
            {
                authentication.InvalidateToken();
            }
        }
    }

    private async Task<(byte[] Bytes, string ContentType)> GetCoreAsync(
        string url, int maximumBytes, bool binary, CancellationToken ct)
    {
        var budget = await AdoRateLimitBudget.FromProviderAsync(authentication, organization.AbsoluteUri, ct)
            .ConfigureAwait(false);
        using var slot = throttle is null ? null : await throttle.AcquireAsync(budget, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await authentication.GetAccessTokenAsync(ct).ConfigureAwait(false);
        AdoErrorHandler.ApplyAuthHeader(request, token);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw Failure("attachment-redirect-refused", "Attachment reads do not follow redirects.");
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw Failure("attachment-too-large", "The response exceeds the Twig transfer limit.");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > maximumBytes)
                    throw Failure("attachment-too-large", "The response exceeds the Twig transfer limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (response.Content.Headers.ContentLength is { } expected && output.Length != expected)
                throw Failure("attachment-size-mismatch", "The response body is incomplete.");
            var bytes = output.ToArray();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var previous = response.Content;
                response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentType = previous.Headers.ContentType;
                previous.Dispose();
                await AdoErrorHandler.ThrowOnErrorAsync(response, url, ct, budget, token).ConfigureAwait(false);
                throw Failure("attachment-response-invalid", "An attachment read did not return a complete entity.");
            }
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!binary && !string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw Failure("attachment-response-invalid", "The inventory response is not JSON.");
            return (bytes, response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream");
        }
        catch (AdoRateLimitException ex)
        {
            throttle?.SetPause(budget, ex.RetryAfter);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AdoOfflineException(ex);
        }
    }

    private static AdoException Failure(string code, string message) => new(code + ": " + message);
    public void Dispose() => http.Dispose();
}
