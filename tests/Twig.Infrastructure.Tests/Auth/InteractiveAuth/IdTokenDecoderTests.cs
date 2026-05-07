using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth.InteractiveAuth;

/// <summary>
/// Tests for <see cref="IdTokenDecoder"/> — extracts identity claims from an OIDC id_token.
/// Decode-only; signature is not verified (we trust TLS + our own PKCE state validation).
/// </summary>
public class IdTokenDecoderTests
{
    [Fact]
    public void Decode_ExtractsUpnOidTidFromValidIdToken()
    {
        var idToken = BuildIdToken(new
        {
            upn = "alice@contoso.com",
            oid = "00000000-0000-0000-0000-000000000001",
            tid = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        });

        var claims = IdTokenDecoder.Decode(idToken);

        claims.UserPrincipalName.ShouldBe("alice@contoso.com");
        claims.ObjectId.ShouldBe("00000000-0000-0000-0000-000000000001");
        claims.TenantId.ShouldBe("72f988bf-86f1-41af-91ab-2d7cd011db47");
    }

    [Fact]
    public void Decode_NullToken_ReturnsAllNull()
    {
        var claims = IdTokenDecoder.Decode(null);
        claims.UserPrincipalName.ShouldBeNull();
        claims.ObjectId.ShouldBeNull();
        claims.TenantId.ShouldBeNull();
    }

    [Fact]
    public void Decode_EmptyToken_ReturnsAllNull()
    {
        var claims = IdTokenDecoder.Decode("");
        claims.UserPrincipalName.ShouldBeNull();
    }

    [Fact]
    public void Decode_MalformedToken_DoesNotThrowAndReturnsNull()
    {
        // Not a JWT at all.
        var claims = IdTokenDecoder.Decode("not-a-token");
        claims.UserPrincipalName.ShouldBeNull();
    }

    [Fact]
    public void Decode_InvalidBase64Payload_DoesNotThrow()
    {
        var idToken = "header.!!!not-base64!!!.signature";
        var claims = IdTokenDecoder.Decode(idToken);
        claims.UserPrincipalName.ShouldBeNull();
    }

    [Fact]
    public void Decode_PayloadMissingClaims_ReturnsNullForMissing()
    {
        var idToken = BuildIdToken(new { upn = "bob@contoso.com" });
        var claims = IdTokenDecoder.Decode(idToken);
        claims.UserPrincipalName.ShouldBe("bob@contoso.com");
        claims.ObjectId.ShouldBeNull();
        claims.TenantId.ShouldBeNull();
    }

    private static string BuildIdToken(object payload)
    {
        // id_token is header.payload.signature. We only decode the payload — the signature
        // can be any non-empty string for our purposes.
        var header = Base64Url("{\"typ\":\"JWT\",\"alg\":\"RS256\"}");
        var json = JsonSerializer.Serialize(payload);
        var body = Base64Url(json);
        return $"{header}.{body}.fakesig";
    }

    private static string Base64Url(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
