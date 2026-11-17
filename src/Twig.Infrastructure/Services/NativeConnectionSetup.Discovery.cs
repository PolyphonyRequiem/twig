using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Twig.Core;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Services;

public sealed record NativeAdoDiscoveryScope(NativeAadEnrollment Enrollment, string Kind,
    string? Organization = null, string? Project = null);
public sealed record NativeAdoDiscoveryRequest(NativeAdoDiscoveryScope Scope, int Limit = 50, string? Continuation = null);
public sealed record NativeAdoSetupChoice(string Id, string Name, string DisplayLabel);
public sealed record NativeAdoDiscoveryPage(NativeAdoDiscoveryScope Scope, ImmutableArray<NativeAdoSetupChoice> Choices,
    string? Continuation, bool Complete);

internal sealed record AdoSetupDiscoveryWireChoice(string? Id = null, string? Name = null,
    string? AccountId = null, string? AccountName = null);
internal sealed record AdoSetupDiscoveryWirePage(AdoSetupDiscoveryWireChoice?[] Value, int? Count = null);
internal sealed record AdoSetupDiscoveryCursor(NativeAdoDiscoveryScope Scope, int Limit, int Offset,
    string? ProviderContinuation, long ExpiresAt, string? ListingHash = null);

public sealed partial class NativeConnectionSetup
{
    private const int DiscoveryBytes = 1024 * 1024;
    private readonly byte[] discoveryCursorKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Read-only, pre-workspace choices for one exact named enrollment. Continuations are short-lived,
    /// process-local and bound to the complete identity, target and page size. No binding is selected or created.
    /// </summary>
    public async Task<Result<NativeAdoDiscoveryPage>> DiscoverAdoAsync(NativeAdoDiscoveryRequest request,
        CancellationToken ct = default)
    {
        IConnectionBindingService? resolved = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!ValidDiscovery(request))
                return Result<NativeAdoDiscoveryPage>.Fail(ErrorCode.InvalidInput,
                    "Choose an exact named enrollment, organizations/projects/teams scope and a page size from 1 through 100. Teams require a canonical project ID.");
            var scope = request.Scope;
            var cursor = ReadDiscoveryCursor(request);
            resolved = bindings(new NativeSetupEffects());
            var provider = await resolved.CreateSetupDiscoveryProviderAsync(scope.Enrollment, token).ConfigureAwait(false);
            using var client = new HttpClient(transport?.CreateHandler()
                ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

            var offset = cursor?.Offset ?? 0;
            var upstream = cursor?.ProviderContinuation;
            string url;
            if (scope.Kind == "organizations")
            {
                var profile = await Get<AdoSetupDiscoveryWireChoice>(
                    "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1",
                    TwigJsonContext.Default.AdoSetupDiscoveryWireChoice).ConfigureAwait(false);
                if (!Guid.TryParse(profile.Value.Id, out var member) || member == Guid.Empty)
                    throw Refuse(ErrorCode.Unavailable, "ADO did not return the signed-in profile identity.");
                url = "https://app.vssps.visualstudio.com/_apis/accounts?api-version=7.1&memberId=" + member.ToString("D");
            }
            else
            {
                var root = "https://dev.azure.com/" + Uri.EscapeDataString(scope.Organization!);
                url = scope.Kind == "projects"
                    ? root + "/_apis/projects?api-version=7.1&$top=" + Number(request.Limit)
                        + (upstream is null ? "&$skip=" + Number(offset) : "")
                    : root + "/_apis/projects/" + scope.Project + "/teams?api-version=7.1&$top="
                        + Number(request.Limit) + "&$skip=" + Number(offset);
                if (scope.Kind == "projects" && upstream is not null)
                    url += "&continuationToken=" + Uri.EscapeDataString(upstream);
            }

            var response = await Get<AdoSetupDiscoveryWirePage>(url,
                TwigJsonContext.Default.AdoSetupDiscoveryWirePage).ConfigureAwait(false);
            var values = response.Value.Value;
            if (values is null || values.Length > 10000 || scope.Kind != "organizations" && values.Length > request.Limit
                || response.Value.Count is { } count && count != values.Length)
                throw Refuse(ErrorCode.Partial, "ADO exceeded the bounded discovery page. No truncated choices were returned.");
            var choices = values.Select(value =>
            {
                if (value is null)
                    throw Refuse(ErrorCode.Unavailable, "ADO returned a null discovery choice.");
                var id = scope.Kind == "organizations" ? value.AccountId : value.Id;
                var name = scope.Kind == "organizations" ? value.AccountName : value.Name;
                if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty || !DiscoveryText(name, 256)
                    || scope.Kind == "organizations" && !DiscoveryOrganization(name))
                    throw Refuse(ErrorCode.Unavailable, "ADO returned a discovery choice without canonical identity and name.");
                return new NativeAdoSetupChoice(guid.ToString("D"), name!, name!);
            }).ToArray();
            if (choices.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != choices.Length)
                throw Refuse(ErrorCode.Unavailable, "ADO returned duplicate discovery identities.");

            string? continuation = null;
            if (scope.Kind == "organizations")
            {
                if (response.Continuation is not null)
                    throw Refuse(ErrorCode.Partial, "ADO returned undocumented account pagination. No incomplete organization list was released.");
                var listingHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    choices, TwigJsonContext.Default.NativeAdoSetupChoiceArray)));
                if (cursor?.ListingHash is { } previousHash && previousHash != listingHash)
                    throw Refuse(ErrorCode.Conflict, "The organization listing changed; start discovery again.");
                if (offset > choices.Length)
                    throw Refuse(ErrorCode.Conflict, "The organization listing changed; start discovery again.");
                var end = Math.Min(choices.Length, offset + request.Limit);
                if (end < choices.Length)
                    continuation = WriteDiscoveryCursor(new(scope, request.Limit, end, upstream, Expiry(), listingHash));
                choices = choices.Skip(offset).Take(request.Limit).ToArray();
            }
            else if (scope.Kind == "projects" && response.Continuation is not null)
                continuation = NextProvider(response.Continuation);
            else if (scope.Kind is "projects" or "teams")
            {
                if (response.Continuation is not null)
                    throw Refuse(ErrorCode.Partial, "ADO returned unsupported team pagination. No incomplete page was marked complete.");
                if (choices.Length == request.Limit)
                    continuation = WriteDiscoveryCursor(new(scope, request.Limit, checked(offset + choices.Length), null, Expiry()));
            }

            // Re-resolve the exact registry enrollment after HTTP, even if an external boundary ignored cancellation.
            _ = await resolved.CreateSetupDiscoveryProviderAsync(scope.Enrollment, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return Result<NativeAdoDiscoveryPage>.Ok(new(scope, choices.ToImmutableArray(), continuation, continuation is null));

            string NextProvider(string next)
            {
                if (next == upstream)
                    throw Refuse(ErrorCode.Partial, "ADO repeated its continuation. Discovery is incomplete; no loop was followed.");
                return WriteDiscoveryCursor(new(scope, request.Limit, checked(offset + choices.Length), next, Expiry()));
            }
            async Task<(T Value, string? Continuation)> Get<T>(string address,
                System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
            {
                token.ThrowIfCancellationRequested();
                _ = await resolved.CreateSetupDiscoveryProviderAsync(scope.Enrollment, token).ConfigureAwait(false);
                using var message = new HttpRequestMessage(HttpMethod.Get, address);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                    await provider.GetAccessTokenAsync(token).ConfigureAwait(false));
                message.Headers.Accept.Add(new("application/json"));
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation
                    && response.Content.Headers.ContentType?.MediaType is not "application/json")
                    throw new NativeSetupException(new(ErrorCode.Unavailable,
                        "ADO returned an authentication challenge. Explicitly renew the named enrollment; no sign-in was started.")
                    { Evidence = ["http:203", "discovery:unauthenticated"] });
                if (!response.IsSuccessStatusCode)
                {
                    var code = response.StatusCode is HttpStatusCode.Forbidden ? ErrorCode.PolicyViolation
                        : response.StatusCode is HttpStatusCode.NotFound ? ErrorCode.NotFound : ErrorCode.Unavailable;
                    throw new NativeSetupException(new(code, "ADO setup discovery was refused; no alternate account or choices were used.")
                    { Evidence = ["http:" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)] });
                }
                if (response.Content.Headers.ContentType?.MediaType is not "application/json"
                    || response.Content.Headers.ContentLength > DiscoveryBytes)
                    throw Refuse(ErrorCode.Partial, "ADO discovery returned an unsupported or oversized response; no choices were truncated.");
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var bytes = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    if (bytes.Length + read > DiscoveryBytes)
                        throw Refuse(ErrorCode.Partial, "ADO discovery exceeded its response budget; no choices were truncated.");
                    bytes.Write(buffer, 0, read);
                }
                var value = JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), type)
                    ?? throw Refuse(ErrorCode.Unavailable, "ADO returned an empty discovery document.");
                string? next = null;
                if (response.Headers.TryGetValues("x-ms-continuationtoken", out var headers))
                {
                    var continuations = headers.ToArray();
                    if (continuations.Length != 1 || !DiscoveryText(continuations[0], 2048))
                        throw Refuse(ErrorCode.Partial, "ADO returned unsupported discovery continuation evidence.");
                    next = continuations[0];
                }
                return (value, next);
            }
        }
        catch (NativeSetupException error) { return Result<NativeAdoDiscoveryPage>.Fail(error.Error); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Result<NativeAdoDiscoveryPage>.Fail(new(ErrorCode.Unavailable,
                ct.IsCancellationRequested ? "Setup discovery was cancelled." : "Setup discovery exceeded its 20-second deadline.")
            { Evidence = [ct.IsCancellationRequested ? "discovery:cancelled" : "discovery:timeout"] });
        }
        catch (Exception error) when (error is AdoAuthenticationException or ConnectionIdentityMismatchException)
        {
            return Result<NativeAdoDiscoveryPage>.Fail(ErrorCode.PolicyViolation,
                "The exact enrolled AAD credential could not be verified. No alternate identity was used.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        {
            return Result<NativeAdoDiscoveryPage>.Fail(ErrorCode.Unavailable,
                "Native setup discovery is unavailable or its response is incompatible. No substitute choices were returned.");
        }
        finally { if (ownsBindings && resolved is IDisposable disposable) disposable.Dispose(); }
    }

    private static bool ValidDiscovery(NativeAdoDiscoveryRequest? request)
        => request is { Limit: >= 1 and <= 100, Scope.Enrollment: { } enrollment }
            && DiscoveryText(enrollment.Alias, 64) && DiscoveryText(enrollment.IdentityId, 256)
            && DiscoveryText(enrollment.TenantId, 256) && DiscoveryText(enrollment.ObjectId, 256)
            && DiscoveryText(enrollment.Issuer, 1024) && DiscoveryText(enrollment.AuthorityHost, 256)
            && (request.Scope.Kind switch
            {
                "organizations" => request.Scope.Organization is null && request.Scope.Project is null,
                "projects" => DiscoveryOrganization(request.Scope.Organization) && request.Scope.Project is null,
                "teams" => DiscoveryOrganization(request.Scope.Organization)
                    && Guid.TryParseExact(request.Scope.Project, "D", out var project) && project != Guid.Empty
                    && project.ToString("D") == request.Scope.Project,
                _ => false,
            });

    private static bool DiscoveryText(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    private static bool DiscoveryOrganization(string? value)
        => DiscoveryText(value, 50) && char.IsAsciiLetterOrDigit(value![0]) && char.IsAsciiLetterOrDigit(value[^1])
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static long Expiry() => DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
    private static NativeSetupException Refuse(ErrorCode code, string message) => new(new(code, message));

    private string WriteDiscoveryCursor(AdoSetupDiscoveryCursor value)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, TwigJsonContext.Default.AdoSetupDiscoveryCursor);
        return Convert.ToBase64String(data) + "." + Convert.ToHexString(HMACSHA256.HashData(discoveryCursorKey, data));
    }

    private AdoSetupDiscoveryCursor? ReadDiscoveryCursor(NativeAdoDiscoveryRequest request)
    {
        if (request.Continuation is null) return null;
        try
        {
            if (request.Continuation.Length > 16384) throw new FormatException();
            var pieces = request.Continuation.Split('.');
            if (pieces.Length != 2) throw new FormatException();
            var data = Convert.FromBase64String(pieces[0]);
            var signature = Convert.FromHexString(pieces[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(discoveryCursorKey, data)))
                throw new FormatException();
            var value = JsonSerializer.Deserialize(data, TwigJsonContext.Default.AdoSetupDiscoveryCursor);
            if (value is null || value.Scope != request.Scope || value.Limit != request.Limit
                || value.Offset < 0 || value.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new FormatException();
            return value;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw Refuse(ErrorCode.Conflict, "Discovery continuation expired or belongs to another enrollment, target, page size or native process. Start a new listing.");
        }
    }
}
