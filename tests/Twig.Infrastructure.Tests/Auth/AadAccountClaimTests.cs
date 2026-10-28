using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

public sealed class AadAccountClaimTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Principal = "22222222-2222-2222-2222-222222222222";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "twig-aad-account-" + Guid.NewGuid().ToString("N"));
    private readonly ITokenRefresher _refresher = Substitute.For<ITokenRefresher>();

    public static TheoryData<string, string?> Accounts => new()
    {
        { """{"upn":"upn@example.test","preferred_username":"preferred@example.test","unique_name":"unique@example.test","email":"email@example.test"}""", "upn@example.test" },
        { """{"preferred_username":"preferred@example.test","unique_name":"unique@example.test","email":"email@example.test"}""", "preferred@example.test" },
        { """{"upn":" ","preferred_username":"","unique_name":"unique@example.test","email":"email@example.test"}""", "unique@example.test" },
        { """{"unique_name":"live.com#personal@example.test","email":"email@example.test"}""", "personal@example.test" },
        { """{"unique_name":"LIVE.COM#personal@example.test"}""", "personal@example.test" },
        { """{"upn":"","preferred_username":" ","unique_name":"","email":"email@example.test"}""", "email@example.test" },
        { """{"upn":"guest_example.test#EXT#@tenant.example.test"}""", "guest_example.test#EXT#@tenant.example.test" },
        { """{"unique_name":"other#personal@example.test"}""", "other#personal@example.test" },
        { """{"unique_name":"live.com#","email":"email@example.test"}""", "email@example.test" },
        { """{"upn":" ","preferred_username":"","unique_name":" ","email":" "}""", null },
        { "{}", null },
    };

    [Theory]
    [MemberData(nameof(Accounts))]
    public void IdTokenUsesFirstNonblankAccountClaim(string claims, string? expected)
    {
        var decoded = IdTokenDecoder.Decode(Token(claims));

        decoded.UserPrincipalName.ShouldBe(expected);
        decoded.TenantId.ShouldBe(Tenant);
        decoded.ObjectId.ShouldBe(Principal);
    }

    [Theory]
    [MemberData(nameof(Accounts))]
    public async Task RegistrationPersistsAccountFromRefreshedAccessToken(string claims, string? expected)
    {
        SetToken(claims);
        using (var bindings = new ConnectionBindingService(_home, _refresher))
            (await bindings.RegisterAadIdentityAsync("personal", Credential())).AccountName.ShouldBe(expected);

        using var reopened = new ConnectionBindingService(_home, _refresher);
        (await reopened.ListIdentitiesAsync()).Single().AccountName.ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePrincipalRenewalBackfillsEmptyAccountWithoutChangingBinding(bool fromIdToken)
    {
        SetToken("{}");
        using var bindings = new ConnectionBindingService(_home, _refresher);
        var original = await bindings.RegisterAadIdentityAsync("personal", Credential());
        original.AccountName.ShouldBeNull();
        var binding = await bindings.CreateBindingAsync("fixture", "Work", "personal", makeDefault: true);

        const string claims = """{"preferred_username":"personal@example.test"}""";
        SetToken(fromIdToken ? "{}" : claims);
        var credential = Credential();
        if (fromIdToken)
            credential.UserPrincipalName = IdTokenDecoder.Decode(Token(claims)).UserPrincipalName;
        var renewed = await bindings.RegisterAadIdentityAsync("personal", credential);

        renewed.AccountName.ShouldBe("personal@example.test");
        renewed.IdentityId.ShouldBe(original.IdentityId);
        renewed.CredentialRef.ShouldBe(original.CredentialRef);
        (await bindings.CreateBindingAsync("fixture", "Work", "personal", makeDefault: true)).ShouldBe(binding);
        using var reopened = new ConnectionBindingService(_home, _refresher);
        (await reopened.ListIdentitiesAsync()).Single().ShouldBe(renewed);
    }

    [Fact]
    public async Task RenewalWithoutAccountClaimsPreservesKnownAccount()
    {
        SetToken("""{"upn":"known@example.test"}""");
        using var bindings = new ConnectionBindingService(_home, _refresher);
        var original = await bindings.RegisterAadIdentityAsync("personal", Credential());
        SetToken("{}");

        (await bindings.RegisterAadIdentityAsync("personal", Credential())).ShouldBe(original);
    }

    [Fact]
    public async Task AccountFallbackCannotOverwriteAnotherPrincipal()
    {
        SetToken("{}");
        using var bindings = new ConnectionBindingService(_home, _refresher);
        var original = await bindings.RegisterAadIdentityAsync("personal", Credential());
        SetToken("""{"preferred_username":"personal@example.test"}""", "33333333-3333-3333-3333-333333333333");
        var credential = Credential();
        credential.ObjectId = null;

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => bindings.RegisterAadIdentityAsync("personal", credential));

        error.Message.ShouldContain("different principal");
        (await bindings.ListIdentitiesAsync()).Single().ShouldBe(original);
    }

    private void SetToken(string claims, string principal = Principal)
        => _refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Token(claims, principal), (string?)null, false));

    private static TwigRefreshTokenStoreEntry Credential() => new()
    {
        RefreshToken = "fixture-refresh",
        ClientId = "fixture-client",
        TenantId = Tenant,
        ObjectId = Principal,
        AuthorityHost = "login.microsoftonline.com",
    };

    private static string Token(string claims, string principal = Principal)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(claims)!;
        payload["aud"] = JwtTestFactory.AdoResourceId;
        payload["tid"] = Tenant;
        payload["oid"] = principal;
        payload["iss"] = "https://sts.windows.net/" + Tenant + "/";
        payload["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "header." + encoded + ".signature";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }
}
