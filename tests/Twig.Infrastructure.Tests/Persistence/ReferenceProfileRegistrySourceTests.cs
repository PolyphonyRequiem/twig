using NSubstitute;
using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;
using Twig.Infrastructure.Tests.Services.ReferenceProfile;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class ReferenceProfileRegistrySourceTests
{

    /// <summary>
    /// A profile that fails to load must surface its own named identifier, not a
    /// fabricated identity/version. T1 §6.3: no synthetic identity, no partial
    /// workspace.
    /// </summary>
    [Fact]
    public void Resolve_propagates_the_profile_load_error_verbatim()
    {
        var provider = Substitute.For<IReferenceProfileProvider>();
        provider.Load().Returns(
            Result.Fail<Twig.Domain.ValueObjects.ReferenceProfile>("profile-schema-unknown"));

        var result = new ReferenceProfileRegistrySource(provider).Resolve("selected-profile");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("profile-schema-unknown");
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("twig.reference-profile.Hyperbright")]
    [InlineData(" twig.reference-profile.hyperbright")]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Resolve_refuses_unknown_or_non_byte_equal_identity(string profileIdentity)
    {
        var source = new ReferenceProfileRegistrySource(new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching()));

        var result = source.Resolve(profileIdentity);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileIdentityUnknown);
    }
}
