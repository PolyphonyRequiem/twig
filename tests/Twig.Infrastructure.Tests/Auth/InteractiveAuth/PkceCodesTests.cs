using System.Net;
using System.Net.Http;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth.InteractiveAuth;

/// <summary>
/// Tests for <see cref="PkceCodes"/> — RFC 7636 verifier/challenge generation.
/// </summary>
public class PkceCodesTests
{
    [Fact]
    public void Generate_ProducesVerifierWithinRfcLength()
    {
        var pkce = PkceCodes.Generate();
        // RFC 7636 §4.1: verifier MUST be 43-128 chars from the base64url unreserved set.
        pkce.Verifier.Length.ShouldBeInRange(43, 128);
    }

    [Fact]
    public void Generate_ProducesBase64UrlVerifier()
    {
        var pkce = PkceCodes.Generate();
        // base64url alphabet: A-Z a-z 0-9 - _
        foreach (var c in pkce.Verifier)
            (char.IsLetterOrDigit(c) || c == '-' || c == '_').ShouldBeTrue($"char '{c}' not base64url");
    }

    [Fact]
    public void Generate_ChallengeIsSha256OfVerifier()
    {
        var pkce = PkceCodes.Generate();
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier), hash);
        var expected = PkceCodes.Base64UrlEncode(hash);
        pkce.Challenge.ShouldBe(expected);
    }

    [Fact]
    public void Generate_ProducesUniqueVerifiers()
    {
        // 1000 iterations is overkill for true CSPRNG but cheap and catches "did someone
        // accidentally seed a deterministic RNG" regressions.
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++)
        {
            var pkce = PkceCodes.Generate();
            seen.Add(pkce.Verifier).ShouldBeTrue();
        }
    }

    [Fact]
    public void ChallengeMethod_IsS256()
    {
        // Plain text challenge method is RFC-allowed but inferior; we always use S256.
        PkceCodes.ChallengeMethod.ShouldBe("S256");
    }

    [Fact]
    public void Base64UrlEncode_StripsPadding()
    {
        // 1 byte → 2 base64 chars + 2 padding chars normally; we strip padding.
        PkceCodes.Base64UrlEncode(new byte[] { 0x01 }).ShouldNotEndWith("=");
    }

    [Fact]
    public void Base64UrlEncode_UsesUrlSafeAlphabet()
    {
        // Bytes that produce '+' and '/' in standard base64.
        var bytes = new byte[] { 0xfb, 0xff, 0xbf };
        var encoded = PkceCodes.Base64UrlEncode(bytes);
        encoded.ShouldNotContain('+');
        encoded.ShouldNotContain('/');
    }
}
