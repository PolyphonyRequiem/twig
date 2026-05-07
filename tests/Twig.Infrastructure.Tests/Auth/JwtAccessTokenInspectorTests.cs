using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

public sealed class JwtAccessTokenInspectorTests
{
    [Fact]
    public void TryDecode_NullOrWhitespace_ReturnsNull()
    {
        JwtAccessTokenInspector.TryDecode(null).ShouldBeNull();
        JwtAccessTokenInspector.TryDecode("").ShouldBeNull();
        JwtAccessTokenInspector.TryDecode("   ").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_PatLikeBasicAuth_ReturnsNull()
    {
        // Defensive: the inspector should never claim a Basic auth string is a JWT.
        JwtAccessTokenInspector.TryDecode("Basic dXNlcjpwYXQ=").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_OpaqueToken_ReturnsNull()
    {
        // Plain string with no dots is not a JWT.
        JwtAccessTokenInspector.TryDecode("opaque-token-no-dots").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_TwoSegments_ReturnsNull()
    {
        JwtAccessTokenInspector.TryDecode("only.two").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_FourSegments_ReturnsNull()
    {
        JwtAccessTokenInspector.TryDecode("a.b.c.d").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_MalformedBase64Payload_ReturnsNull()
    {
        // Middle segment is not valid Base64Url
        JwtAccessTokenInspector.TryDecode("aGVhZGVy.@@@@@.signature").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_NonJsonPayload_ReturnsNull()
    {
        // Payload decodes to "not json"
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not json"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        JwtAccessTokenInspector.TryDecode($"hdr.{encoded}.sig").ShouldBeNull();
    }

    [Fact]
    public void TryDecode_ValidAdoToken_ReturnsAllClaims()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(1);
        var jwt = JwtTestFactory.Build(
            audience: JwtTestFactory.AdoResourceId,
            expiresAt: exp,
            tenantId: "tenant-xyz",
            upn: "alice@contoso.com",
            appId: "client-abc");

        var info = JwtAccessTokenInspector.TryDecode(jwt);

        info.ShouldNotBeNull();
        info!.Audience.ShouldBe(JwtTestFactory.AdoResourceId);
        info.TenantId.ShouldBe("tenant-xyz");
        info.UserPrincipalName.ShouldBe("alice@contoso.com");
        info.AppId.ShouldBe("client-abc");
        info.ExpiresAt!.Value.ToUnixTimeSeconds().ShouldBe(exp.ToUnixTimeSeconds());
        info.IsValidAdoAudience.ShouldBeTrue();
    }

    [Fact]
    public void TryDecode_StripsBearerPrefix()
    {
        var jwt = "Bearer " + JwtTestFactory.Build();
        JwtAccessTokenInspector.TryDecode(jwt)!.IsValidAdoAudience.ShouldBeTrue();
    }

    [Fact]
    public void IsValidAdoAudience_AdoResourceId_ReturnsTrue()
    {
        var info = JwtAccessTokenInspector.TryDecode(
            JwtTestFactory.Build(audience: JwtTestFactory.AdoResourceId));
        info!.IsValidAdoAudience.ShouldBeTrue();
    }

    [Fact]
    public void IsValidAdoAudience_AdoResourceUri_ReturnsTrue()
    {
        var info = JwtAccessTokenInspector.TryDecode(
            JwtTestFactory.Build(audience: JwtTestFactory.AdoResourceUri));
        info!.IsValidAdoAudience.ShouldBeTrue();
    }

    [Fact]
    public void IsValidAdoAudience_WrongAudience_ReturnsFalse()
    {
        var info = JwtAccessTokenInspector.TryDecode(
            JwtTestFactory.Build(audience: "https://management.azure.com/"));
        info!.IsValidAdoAudience.ShouldBeFalse();
    }

    [Fact]
    public void IsValidAdoAudience_GraphAudience_ReturnsFalse()
    {
        var info = JwtAccessTokenInspector.TryDecode(
            JwtTestFactory.Build(audience: "https://graph.microsoft.com/"));
        info!.IsValidAdoAudience.ShouldBeFalse();
    }

    [Fact]
    public void IsValidAdoAudience_MissingAudienceClaim_ReturnsFalse()
    {
        var info = JwtAccessTokenInspector.TryDecode(JwtTestFactory.Build(audience: null));
        info!.IsValidAdoAudience.ShouldBeFalse();
    }

    [Fact]
    public void HasValidAdoAudience_NullToken_ReturnsFalse()
    {
        JwtAccessTokenInspector.HasValidAdoAudience(null).ShouldBeFalse();
    }

    [Fact]
    public void HasValidAdoAudience_OpaqueToken_ReturnsFalse()
    {
        JwtAccessTokenInspector.HasValidAdoAudience("not-a-jwt").ShouldBeFalse();
    }

    [Fact]
    public void HasValidAdoAudience_AdoJwt_ReturnsTrue()
    {
        JwtAccessTokenInspector.HasValidAdoAudience(JwtTestFactory.Build()).ShouldBeTrue();
    }

    [Fact]
    public void HasValidAdoAudience_WrongAudienceJwt_ReturnsFalse()
    {
        JwtAccessTokenInspector.HasValidAdoAudience(JwtTestFactory.BuildWrongAudience()).ShouldBeFalse();
    }

    [Fact]
    public void IsNotExpired_WithBuffer_RespectsBuffer()
    {
        var now = DateTimeOffset.UtcNow;
        var info = JwtAccessTokenInspector.TryDecode(
            JwtTestFactory.Build(expiresAt: now.AddMinutes(2)))!;

        // 5-min buffer rejects a token expiring in 2 min
        info.IsNotExpired(now, TimeSpan.FromMinutes(5)).ShouldBeFalse();
        // 1-min buffer accepts it
        info.IsNotExpired(now, TimeSpan.FromMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void DescribeForDiagnostics_AdoToken_MarksAsAdo()
    {
        var jwt = JwtTestFactory.Build(audience: JwtTestFactory.AdoResourceId);
        var info = JwtAccessTokenInspector.TryDecode(jwt)!;

        var description = JwtAccessTokenInspector.DescribeForDiagnostics(info, DateTimeOffset.UtcNow);

        description.ShouldContain("ADO ✓");
        description.ShouldContain(JwtTestFactory.AdoResourceId);
    }

    [Fact]
    public void DescribeForDiagnostics_WrongAudience_MarksAsNotAdo()
    {
        var jwt = JwtTestFactory.BuildWrongAudience();
        var info = JwtAccessTokenInspector.TryDecode(jwt)!;

        var description = JwtAccessTokenInspector.DescribeForDiagnostics(info, DateTimeOffset.UtcNow);

        description.ShouldContain("NOT ADO");
        description.ShouldContain("management.azure.com");
    }

    [Fact]
    public void DescribeForDiagnostics_DoesNotIncludeRawToken()
    {
        var jwt = JwtTestFactory.Build();
        var info = JwtAccessTokenInspector.TryDecode(jwt)!;

        var description = JwtAccessTokenInspector.DescribeForDiagnostics(info, DateTimeOffset.UtcNow);

        // Privacy: the raw JWT must never appear in diagnostic output.
        description.ShouldNotContain(jwt);
        description.ShouldNotContain("signature-placeholder");
    }
}
