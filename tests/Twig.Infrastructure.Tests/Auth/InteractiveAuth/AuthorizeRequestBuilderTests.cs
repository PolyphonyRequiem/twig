using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth.InteractiveAuth;

/// <summary>
/// Tests for <see cref="AuthorizeRequestBuilder"/> — pure URL/state helpers.
/// </summary>
public class AuthorizeRequestBuilderTests
{
    [Fact]
    public void BuildAuthorizeUrl_IncludesAllRequiredParams()
    {
        var url = AuthorizeRequestBuilder.BuildAuthorizeUrl(
            clientId: "abc",
            redirectUri: "http://localhost:1234/",
            codeChallenge: "challenge",
            state: "state",
            tenant: "organizations");

        url.ShouldStartWith("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?");
        url.ShouldContain("client_id=abc");
        url.ShouldContain("response_type=code");
        url.ShouldContain("redirect_uri=http%3A%2F%2Flocalhost%3A1234%2F");
        url.ShouldContain("response_mode=query");
        url.ShouldContain("code_challenge=challenge");
        url.ShouldContain("code_challenge_method=S256");
        url.ShouldContain("state=state");
        url.ShouldContain("prompt=select_account");
    }

    [Fact]
    public void BuildAuthorizeUrl_UrlEncodesScopeWithPlusForSpace()
    {
        var url = AuthorizeRequestBuilder.BuildAuthorizeUrl(
            "id", "http://localhost/", "c", "s",
            scopes: "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access");
        // WebUtility.UrlEncode encodes spaces as '+' which is valid in query strings.
        url.ShouldContain("scope=499b84ac-1321-427f-aa17-267ca6975798%2F.default+offline_access");
    }

    [Fact]
    public void BuildAuthorizeUrl_UsesCustomTenantWhenProvided()
    {
        var url = AuthorizeRequestBuilder.BuildAuthorizeUrl(
            "id", "http://localhost/", "c", "s",
            tenant: "72f988bf-86f1-41af-91ab-2d7cd011db47");
        url.ShouldContain("/72f988bf-86f1-41af-91ab-2d7cd011db47/oauth2/v2.0/authorize");
    }

    [Fact]
    public void BuildAuthorizeUrl_UsesCustomAuthorityHostForSovereignClouds()
    {
        var url = AuthorizeRequestBuilder.BuildAuthorizeUrl(
            "id", "http://localhost/", "c", "s",
            authorityHost: "login.microsoftonline.us");
        url.ShouldStartWith("https://login.microsoftonline.us/");
    }

    [Fact]
    public void GenerateState_ProducesUrlSafeOpaqueString()
    {
        var state = AuthorizeRequestBuilder.GenerateState();
        state.Length.ShouldBeGreaterThan(20);
        foreach (var c in state)
            (char.IsLetterOrDigit(c) || c == '-' || c == '_').ShouldBeTrue();
    }

    [Fact]
    public void GenerateState_ProducesUniqueValues()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
            seen.Add(AuthorizeRequestBuilder.GenerateState()).ShouldBeTrue();
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("abc", "abcd", false)]
    [InlineData("", "", true)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    [InlineData(null, null, false)]
    public void ValidateState_ConstantTimeComparison(string? expected, string? actual, bool expectedResult)
    {
        AuthorizeRequestBuilder.ValidateState(expected, actual).ShouldBe(expectedResult);
    }

    [Fact]
    public void DefaultTenant_IsOrganizationsBecauseAdoIsAlwaysWorkSchool()
    {
        AuthorizeRequestBuilder.DefaultTenant.ShouldBe("organizations");
    }

    [Fact]
    public void Scopes_IncludeAdoResourceAndOfflineAccess()
    {
        // offline_access is mandatory — without it AAD won't return a refresh_token.
        AuthorizeRequestBuilder.Scopes.ShouldContain("499b84ac-1321-427f-aa17-267ca6975798/.default");
        AuthorizeRequestBuilder.Scopes.ShouldContain("offline_access");
    }
}
