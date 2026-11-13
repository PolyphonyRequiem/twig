using System.Collections.Immutable;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(Connection connection,
        ImmutableArray<string> identities, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            if (identities.IsDefaultOrEmpty || identities.Length > 16 || identities.Distinct().Count() != identities.Length
                || identities.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)))
                throw Wire.Refuse(ErrorCode.InvalidInput, "Profile reads require 1-16 distinct native identity IDs.");
            // Only the known Azure DevOps identity service may receive this collection's credential.
            var organization = bound.OrganizationUrl;
            var segments = organization.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var identityRoot = organization.Host == "dev.azure.com" && organization.IsDefaultPort && segments.Length == 1
                ? new Uri($"https://vssps.dev.azure.com/{segments[0]}/")
                : organization.Host.EndsWith(".visualstudio.com", StringComparison.Ordinal) && organization.IsDefaultPort && segments.Length == 0
                    ? new Uri($"https://vssps.dev.azure.com/{organization.Host[..^".visualstudio.com".Length]}/")
                    : organization;
            var identityBound = bound with { OrganizationUrl = identityRoot };
            var profiles = ImmutableArray.CreateBuilder<IdentityProfile>();
            foreach (var id in identities)
            {
                string? name = null;
                try
                {
                    if (!Guid.TryParseExact(id, "D", out var value) || value == Guid.Empty || value.ToString("D") != id)
                        throw Wire.Invalid("Profile unavailable: the assignee is not a canonical native identity ID.");
                    var identity = await Get(identityBound, $"_apis/identities?identityIds={Wire.Escape(id)}&queryMembership=None&api-version=7.1", token);
                    var values = Wire.Rows(identity);
                    if (values.Length != 1 || Wire.Text(values[0], "id") != id)
                        throw Wire.Invalid("The identity service did not return the exact requested identity.");
                    var observedName = Wire.Text(values[0], "providerDisplayName");
                    if (observedName.Length > 512 || observedName.Any(char.IsControl))
                        throw Wire.Invalid("Identity display name exceeds its presentation bounds.");
                    name = observedName;
                    var descriptor = Wire.Text(values[0], "subjectDescriptor");
                    if (descriptor.Length > 1024) throw Wire.Invalid("Identity descriptor exceeds its bound.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, identityBound.Url(
                        $"_apis/graph/Subjects/{Wire.Escape(descriptor)}/avatars?size=small&format=png&api-version=7.1"));
                    request.Headers.Authorization = identityBound.Authorization;
                    request.Headers.Accept.ParseAdd("image/png");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode);
                    if (response.Content.Headers.ContentType?.MediaType != "image/png")
                        throw Wire.Invalid("Profile photo is not a PNG image.");
                    var bytes = await ResponseBytes(response, token, ProfileImage.MaximumBytes);
                    var image = ProfileImage.Encode(bytes);
                    profiles.Add(image.IsSuccess ? new(id, name, image.Value, null) : new(id, name, null, image.Error.Message));
                }
                catch (AdoFailure ex) { profiles.Add(new(id, name, null, ex.Error.Message)); }
                catch (Exception ex) when (ex is HttpRequestException or IOException || Malformed(ex))
                { profiles.Add(new(id, name, null, "Identity profile unavailable; no alternate account or target was used.")); }
            }
            return profiles.ToImmutable();
        }, ct));
}
