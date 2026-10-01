using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>The only pre-admission PAT request: read the authenticated ADO identity.</summary>
internal sealed class PatPrincipalAttestor(HttpClient http)
{
    internal static string NormalizeAuthority(string organization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        var url = AdoRestClient.NormalizeOrgUrl(organization);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("PAT authority must be an HTTPS organization endpoint without credentials, query or fragment.");
        var slug = OrganizationNormalizer.ToSlug(organization);
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                ? path.Length == 0 || path.Contains('/')
                : path.Length != 0)
                throw new ArgumentException("PAT enrollment requires the organization endpoint, not a project or API URL.");
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains('/') || slug.Contains(':'))
                throw new ArgumentException("PAT enrollment requires an organization endpoint.");
            // Modern and legacy ADO Services URLs denote the same authority namespace.
            return "https://dev.azure.com/" + slug;
        }
        return uri.AbsoluteUri.TrimEnd('/');
    }

    internal static string FormatAuthorization(string pat) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat));

    internal async Task<PatPrincipalEvidence> AttestAsync(string authority, string pat, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);
        using var request = new HttpRequestMessage(HttpMethod.Get, authority + "/_apis/connectionData?api-version=7.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AdoErrorHandler.ApplyAuthHeader(request, FormatAuthorization(pat));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Never surface a remote body, credential or arbitrary transport exception.
            throw new AdoAuthenticationException("PAT principal attestation could not reach the configured authority. No work request was admitted.");
        }
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new AdoAuthenticationException($"PAT principal attestation refused (HTTP {(int)response.StatusCode}). No work request was admitted; repair the selected identity with 'twig auth pat'.");
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != request.RequestUri)
                throw new AdoAuthenticationException("PAT principal attestation was redirected; refusing evidence from another endpoint.");
            AdoConnectionDataResponse? data;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                data = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.AdoConnectionDataResponse, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new AdoAuthenticationException("PAT principal attestation returned malformed identity evidence. No work request was admitted.");
            }
            if (!Guid.TryParse(data?.AuthenticatedUser?.Id, out var principalId) || principalId == Guid.Empty)
                throw new AdoAuthenticationException("PAT principal attestation did not return an authoritative authenticated-user identifier. No work request was admitted.");
            return new PatPrincipalEvidence(principalId.ToString("D"), data?.AuthenticatedUser?.ProviderDisplayName);
        }
    }
}

internal sealed record PatPrincipalEvidence(string PrincipalId, string? AccountName);
