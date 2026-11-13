using Shouldly;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed partial class AdoProviderTests
{
    private const string ProfilePng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [Fact]
    public async Task IdentityProfilesResolveExactNamesAndPhotosThroughTheBoundIdentityServiceWithoutWrites()
    {
        using var f = await Fixture.Create();
        f.Access.Organization = new("https://dev.azure.com/organization");
        var profiles = await f.Provider.ReadIdentityProfilesAsync(f.Connection, [Fixture.Actor], default);
        profiles.IsSuccess.ShouldBeTrue(profiles.IsSuccess ? "" : profiles.Error.Message);
        var profile = profiles.Value.Single();
        profile.Id.ShouldBe(Fixture.Actor);
        profile.DisplayName.ShouldBe("Native Full Name");
        profile.ImageDataUrl.ShouldBe("data:image/png;base64," + ProfilePng);
        profile.Error.ShouldBeNull();
        f.Http.ProfileRequests.Count.ShouldBe(2);
        f.Http.ProfileRequests.ShouldAllBe(uri => uri.Host == "vssps.dev.azure.com");
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("wrong-id", false)]
    [InlineData("denied", true)]
    [InlineData("invalid-image", true)]
    [InlineData("wrong-media", true)]
    [InlineData("oversized-image", true)]
    public async Task IdentityProfilesNeverInventNamesOrAcceptUnverifiedImages(string defect, bool retainName)
    {
        using var f = await Fixture.Create();
        f.Http.ProfileDefect = defect;
        var result = await f.Provider.ReadIdentityProfilesAsync(f.Connection, [Fixture.Actor], default);
        result.IsSuccess.ShouldBeTrue();
        var profile = result.Value.Single();
        profile.DisplayName.ShouldBe(retainName ? "Native Full Name" : null);
        profile.ImageDataUrl.ShouldBeNull();
        profile.Error.ShouldNotBeNullOrWhiteSpace();
        if (defect == "oversized-image") profile.Error.ShouldContain("bounded payload limit");
        if (defect == "wrong-media") profile.Error.ShouldBe("Profile photo is not a PNG image.");
        f.Http.ProfileRequests.Count.ShouldBe(retainName ? 2 : 1);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task IdentityProfilesKeepUnresolvableAssigneesSeparateFromVerifiedIdentities()
    {
        using var f = await Fixture.Create();
        var result = await f.Provider.ReadIdentityProfilesAsync(f.Connection, ["unresolved-assignee", Fixture.Actor], default);
        result.IsSuccess.ShouldBeTrue();
        result.Value[0].DisplayName.ShouldBeNull();
        result.Value[0].Error.ShouldNotBeNullOrWhiteSpace();
        result.Value[1].DisplayName.ShouldBe("Native Full Name");
        result.Value[1].ImageDataUrl.ShouldNotBeNull();
        f.Http.ProfileRequests.Count.ShouldBe(2);
        f.Http.Writes.ShouldBe(0);
    }
}
