using Shouldly;
using Xunit;

namespace Twig.Providers.GitHub.Tests;

public sealed partial class GitHubProviderTests
{
    private const string ProfilePng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [Fact]
    public async Task IdentityProfilesResolveExactNamesAndAnonymousPublicPhotosWithoutWrites()
    {
        using var f = await Fixture.Create();
        var result = await f.Provider.ReadIdentityProfilesAsync(f.Connection, ["U1", "U2"], default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Select(profile => profile.Id).ShouldBe(["U1", "U2"]);
        result.Value.ShouldAllBe(profile => profile.DisplayName == "Native Full Name"
            && profile.ImageDataUrl == "data:image/png;base64," + ProfilePng && profile.Error == null);
        f.Http.AvatarReads.ShouldBe(2);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("wrong-id", 0)]
    [InlineData("untrusted", 0)]
    [InlineData("redirect", 1)]
    [InlineData("oversized", 1)]
    [InlineData("svg", 1)]
    public async Task IdentityProfilesRejectWrongIdentityUnsafeOriginsRedirectsAndUnboundedContent(string defect, int avatarReads)
    {
        using var f = await Fixture.Create();
        f.Http.ProfileDefect = defect;
        var result = await f.Provider.ReadIdentityProfilesAsync(f.Connection, ["U1"], default);
        result.IsSuccess.ShouldBeTrue();
        var profile = result.Value.Single();
        profile.ImageDataUrl.ShouldBeNull();
        profile.Error.ShouldNotBeNullOrWhiteSpace();
        f.Http.AvatarReads.ShouldBe(avatarReads);
        f.Http.Writes.ShouldBe(0);
    }
}
