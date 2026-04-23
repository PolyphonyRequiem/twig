using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="AuthProviderFactory"/>.
/// Verifies the factory returns the correct provider type for each auth method string.
/// </summary>
public sealed class AuthProviderFactoryTests
{
    [Fact]
    public void Create_PatMethod_ReturnsPatAuthProvider()
    {
        var provider = AuthProviderFactory.Create("pat");

        provider.ShouldBeOfType<PatAuthProvider>();
    }

    [Fact]
    public void Create_PatMethod_CaseInsensitive_ReturnsPatAuthProvider()
    {
        var provider = AuthProviderFactory.Create("PAT");

        provider.ShouldBeOfType<PatAuthProvider>();
    }

    [Fact]
    public void Create_PatMethod_MixedCase_ReturnsPatAuthProvider()
    {
        var provider = AuthProviderFactory.Create("Pat");

        provider.ShouldBeOfType<PatAuthProvider>();
    }

    [Fact]
    public void Create_AzCliMethod_ReturnsMsalCacheTokenProvider()
    {
        var provider = AuthProviderFactory.Create("azcli");

        provider.ShouldBeOfType<MsalCacheTokenProvider>();
    }

    [Fact]
    public void Create_AzCliMethod_CaseInsensitive_ReturnsMsalCacheTokenProvider()
    {
        var provider = AuthProviderFactory.Create("AZCLI");

        provider.ShouldBeOfType<MsalCacheTokenProvider>();
    }

    [Fact]
    public void Create_UnknownMethod_DefaultsToMsalCacheTokenProvider()
    {
        var provider = AuthProviderFactory.Create("unknown");

        provider.ShouldBeOfType<MsalCacheTokenProvider>();
    }

    [Fact]
    public void Create_EmptyString_DefaultsToMsalCacheTokenProvider()
    {
        var provider = AuthProviderFactory.Create("");

        provider.ShouldBeOfType<MsalCacheTokenProvider>();
    }
}
