using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="PatAuthProvider"/>.
/// Uses injectable readers to avoid actual env/config dependencies.
/// </summary>
public class PatAuthProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_EnvVarSet_ReturnsBasicAuth()
    {
        var provider = new PatAuthProvider(
            envVarReader: name => name == "TWIG_PAT" ? "my-pat-token" : null,
            configPatReader: () => null);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldStartWith("Basic ");
        // Decode and verify: base64(:my-pat-token)
        var base64 = token["Basic ".Length..];
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        decoded.ShouldBe(":my-pat-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConfigFallback_ReturnsBasicAuth()
    {
        var provider = new PatAuthProvider(
            envVarReader: _ => null,
            configPatReader: () => "config-pat");

        var token = await provider.GetAccessTokenAsync();

        token.ShouldStartWith("Basic ");
        var base64 = token["Basic ".Length..];
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        decoded.ShouldBe(":config-pat");
    }

    [Fact]
    public async Task GetAccessTokenAsync_EnvVarTakesPrecedenceOverConfig()
    {
        var provider = new PatAuthProvider(
            envVarReader: name => name == "TWIG_PAT" ? "env-pat" : null,
            configPatReader: () => "config-pat");

        var token = await provider.GetAccessTokenAsync();

        var base64 = token["Basic ".Length..];
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        decoded.ShouldBe(":env-pat");
    }

    [Fact]
    public async Task GetAccessTokenAsync_NeitherSet_ThrowsAuthException()
    {
        var provider = new PatAuthProvider(
            envVarReader: _ => null,
            configPatReader: () => null);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyEnvVar_FallsToConfig()
    {
        var provider = new PatAuthProvider(
            envVarReader: name => name == "TWIG_PAT" ? "  " : null,
            configPatReader: () => "config-pat");

        var token = await provider.GetAccessTokenAsync();

        var base64 = token["Basic ".Length..];
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        decoded.ShouldBe(":config-pat");
    }

    [Fact]
    public async Task GetAccessTokenAsync_BothEmpty_ThrowsAuthException()
    {
        var provider = new PatAuthProvider(
            envVarReader: _ => "",
            configPatReader: () => "");

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());
    }
}
