using System;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

public sealed class EmbeddedReferenceProfileProviderTests
{
    [Fact]
    public void Absent_profile_block_stays_absent_through_the_reader_and_validate_pin()
    {
        var source = new TwigJsonReferenceProfilePinSource(new TwigConfiguration());

        source.GetPin().ShouldBeNull();

        var result = new EmbeddedReferenceProfileProvider(source).ValidatePin();

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.TwigJsonProfileBlockMissing);
    }

    [Theory]
    [InlineData(nameof(ProfilePinConfig.Identity))]
    [InlineData(nameof(ProfilePinConfig.ProfileVersion))]
    [InlineData(nameof(ProfilePinConfig.BaseProcessVersion))]
    public void A_present_but_incomplete_profile_block_remains_present_and_fails_closed(string blankField)
    {
        var profile = BuildShippedProfile();
        SetBlankField(profile, blankField);

        var source = new TwigJsonReferenceProfilePinSource(new TwigConfiguration { Profile = profile });
        var pin = source.GetPin();

        pin.ShouldNotBeNull();
        pin.ShouldBe(ExpectedPin(blankField));

        var result = new EmbeddedReferenceProfileProvider(source).ValidatePin();

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileSchemaInvalid);
    }

    [Fact]
    public void A_complete_matching_profile_pin_still_validates()
    {
        var profile = BuildShippedProfile();
        var source = new TwigJsonReferenceProfilePinSource(new TwigConfiguration { Profile = profile });

        source.GetPin().ShouldBe(ExpectedPin());

        var result = new EmbeddedReferenceProfileProvider(source).ValidatePin();

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    private static ProfilePinConfig BuildShippedProfile() => new()
    {
        Identity = ProfilePinSources.ShippedIdentity,
        ProfileVersion = ProfilePinSources.ShippedProfileVersion,
        BaseProcessVersion = ProfilePinSources.ShippedBaseProcessVersion,
    };

    private static void SetBlankField(ProfilePinConfig profile, string blankField)
    {
        switch (blankField)
        {
            case nameof(ProfilePinConfig.Identity):
                profile.Identity = string.Empty;
                break;
            case nameof(ProfilePinConfig.ProfileVersion):
                profile.ProfileVersion = string.Empty;
                break;
            case nameof(ProfilePinConfig.BaseProcessVersion):
                profile.BaseProcessVersion = string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blankField), blankField, null);
        }
    }

    private static ReferenceProfilePin ExpectedPin(string? blankField = null) => new(
        blankField == nameof(ProfilePinConfig.Identity) ? string.Empty : ProfilePinSources.ShippedIdentity,
        blankField == nameof(ProfilePinConfig.ProfileVersion) ? string.Empty : ProfilePinSources.ShippedProfileVersion,
        blankField == nameof(ProfilePinConfig.BaseProcessVersion) ? string.Empty : ProfilePinSources.ShippedBaseProcessVersion);
}
