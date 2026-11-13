using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider
{
    public Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(Connection connection,
        ImmutableArray<string> identities, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            if (identities.IsDefaultOrEmpty || identities.Length > 16 || identities.Distinct().Count() != identities.Length
                || identities.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)))
                throw Wire.Refuse(ErrorCode.InvalidInput, "Profile reads require 1-16 distinct native identity IDs.");
            var profiles = ImmutableArray.CreateBuilder<IdentityProfile>();
            foreach (var id in identities)
            {
                string? name = null;
                try
                {
                    var data = await Graph(bound, "query($id: ID!) { node(id: $id) { __typename ... on User { id name login avatarUrl(size: 40) } } }",
                        json => json.WriteString("id", id), token);
                    var user = Wire.Member(data, "node");
                    if (Wire.Text(user, "__typename") != "User" || Wire.Text(user, "id") != id)
                        throw Wire.Invalid("The profile response did not identify the requested user.");
                    var displayName = Wire.Member(user, "name");
                    var observedName = displayName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(displayName.GetString())
                        ? displayName.GetString() : Wire.Text(user, "login");
                    if (string.IsNullOrWhiteSpace(observedName) || observedName.Length > 512 || observedName.Any(char.IsControl))
                        throw Wire.Invalid("Identity display name exceeds its presentation bounds.");
                    name = observedName;
                    var url = new Uri(Wire.Text(user, "avatarUrl"), UriKind.Absolute);
                    var sameOrigin = url.GetLeftPart(UriPartial.Authority) == bound.Endpoint.GetLeftPart(UriPartial.Authority);
                    var publicAvatar = bound.Endpoint.Host == "api.github.com" && url.Host == "avatars.githubusercontent.com";
                    if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Fragment.Length != 0
                        || !(publicAvatar || sameOrigin && url.AbsolutePath.StartsWith("/avatars/", StringComparison.Ordinal)))
                        throw Wire.Refuse(ErrorCode.Unsupported, "The profile photo does not use this provider's trusted avatar origin.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd("twig-cli");
                    // Public avatar CDN requests never carry the provider credential.
                    if (sameOrigin) request.Headers.Authorization = bound.Authorization;
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode)
                        throw Wire.Refuse(ErrorCode.Unavailable, $"Profile photo returned HTTP {(int)response.StatusCode}; no redirect or retry was used.");
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(chunk, token)) != 0)
                    {
                        if (buffer.Length + count > ProfileImage.MaximumBytes)
                            throw Wire.Invalid("Profile photo exceeds its bounded payload limit.");
                        buffer.Write(chunk, 0, count);
                    }
                    var image = ProfileImage.Encode(buffer.ToArray());
                    profiles.Add(image.IsSuccess ? new(id, name, image.Value, null) : new(id, name, null, image.Error.Message));
                }
                catch (GitHubFailure ex) { profiles.Add(new(id, name, null, ex.Error.Message)); }
                catch (Exception ex) when (ex is HttpRequestException or IOException || Malformed(ex))
                { profiles.Add(new(id, name, null, "Identity profile unavailable; no alternate account or target was used.")); }
            }
            return profiles.ToImmutable();
        }, ct));
}
