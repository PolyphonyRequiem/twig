using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

public sealed class AuthProviderFactoryTests
{
    [Theory]
    [InlineData("pat")]
    [InlineData("PAT")]
    [InlineData("Pat")]
    public void Create_PatMethod_ReturnsPatAuthProvider(string method)
    {
        AuthProviderFactory.Create(method).ShouldBeOfType<PatAuthProvider>();
    }

    [Theory]
    [InlineData("azcli")]
    [InlineData("AZCLI")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Create_NonPatMethod_ReturnsMsalCacheTokenProvider(string method)
    {
        AuthProviderFactory.Create(method).ShouldBeOfType<MsalCacheTokenProvider>();
    }
}
