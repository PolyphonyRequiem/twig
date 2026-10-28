using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Regression for AB#1221: an interactive login through a multi-tenant
/// authority alias records that alias as the refresh entry's tenant. The
/// alias names no tenant, so registration must bind the minted token's
/// concrete tid while still refusing a concrete-tenant mismatch.
/// </summary>
public sealed class MultiTenantAuthorityRegistrationTests : IDisposable
{
    private const string TokenTenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string OtherTenant = "11111111-1111-1111-1111-111111111111";
    private const string Principal = "22222222-2222-2222-2222-222222222222";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "twig-mt-authority-" + Guid.NewGuid().ToString("N"));
    private readonly ITokenRefresher _refresher = Substitute.For<ITokenRefresher>();

    public MultiTenantAuthorityRegistrationTests()
        => _refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Token(), "rotated-refresh", false));

    [Theory]
    [InlineData("organizations")]
    [InlineData("common")]
    [InlineData("consumers")]
    [InlineData("Organizations")]
    public async Task AliasAuthorityRegistersAndPersistsConcreteTenant(string alias)
    {
        using (var bindings = new ConnectionBindingService(_home, _refresher))
        {
            var identity = await bindings.RegisterAadIdentityAsync("work", Credential(alias));
            identity.TenantId.ShouldBe(TokenTenant);
        }

        await _refresher.Received(1).TryRefreshAsync("fixture-refresh", "fixture-client", alias,
            "login.microsoftonline.com", Arg.Any<CancellationToken>());
        using var reopened = new ConnectionBindingService(_home, _refresher);
        (await reopened.ListIdentitiesAsync()).Single().TenantId.ShouldBe(TokenTenant);
        var blob = Directory.GetFiles(Path.Combine(_home, "credentials"), "*.json").ShouldHaveSingleItem();
        new TwigRefreshTokenStore(blob).TryRead().ShouldNotBeNull().TenantId.ShouldBe(TokenTenant);
    }

    [Fact]
    public async Task AliasRenewalMatchesConcreteTenantRegistration()
    {
        using var bindings = new ConnectionBindingService(_home, _refresher);
        var original = await bindings.RegisterAadIdentityAsync("work", Credential(TokenTenant));

        var renewed = await bindings.RegisterAadIdentityAsync("work", Credential("organizations"));

        renewed.IdentityId.ShouldBe(original.IdentityId);
        renewed.CredentialRef.ShouldBe(original.CredentialRef);
        renewed.TenantId.ShouldBe(TokenTenant);
    }

    [Fact]
    public async Task MatchingConcreteTenantIsAccepted()
    {
        using var bindings = new ConnectionBindingService(_home, _refresher);

        (await bindings.RegisterAadIdentityAsync("work", Credential(TokenTenant.ToUpperInvariant())))
            .TenantId.ShouldBe(TokenTenant.ToUpperInvariant());
    }

    [Theory]
    [InlineData(OtherTenant)]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("organizations-x")]
    public async Task ConcreteTenantMismatchIsStillRefused(string tenant)
    {
        using var bindings = new ConnectionBindingService(_home, _refresher);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => bindings.RegisterAadIdentityAsync("work", Credential(tenant)));

        error.Message.ShouldContain("disagrees with the minted token tenant");
        (await bindings.ListIdentitiesAsync()).ShouldBeEmpty();
        Directory.GetFiles(Path.Combine(_home, "credentials"), "*.json").ShouldBeEmpty();
    }

    private static TwigRefreshTokenStoreEntry Credential(string tenant) => new()
    {
        RefreshToken = "fixture-refresh",
        ClientId = "fixture-client",
        TenantId = tenant,
        ObjectId = Principal,
        AuthorityHost = "login.microsoftonline.com",
    };

    private static string Token()
    {
        var payload = new Dictionary<string, object>
        {
            ["aud"] = JwtTestFactory.AdoResourceId,
            ["tid"] = TokenTenant,
            ["oid"] = Principal,
            ["iss"] = "https://sts.windows.net/" + TokenTenant + "/",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
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
