using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;

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

    [Theory]
    [InlineData("rejected", "AAD rejected the enrollment refresh token (invalid_grant).")]
    [InlineData("unavailable", "Could not exchange the enrollment refresh token for an Azure DevOps access token.")]
    [InlineData("malformed", "The enrollment access token is not a decodable JWT.")]
    [InlineData("audience", "The enrollment access token does not carry the Azure DevOps audience.")]
    [InlineData("tenant-missing", "The enrollment access token is missing a tenant claim.")]
    [InlineData("object-missing", "The enrollment access token is missing an oid claim.")]
    [InlineData("tenant-mismatch", "The enrollment credential and access token have different tenant claims.")]
    [InlineData("object-mismatch", "The enrollment credential and access token have different oid claims.")]
    public async Task NativeEnrollmentReportsSafeReasonAfterSuccessfulSignIn(string scenario, string reason)
    {
        var credential = Credential();
        credential.RefreshToken = "private-refresh-token";
        if (scenario == "tenant-mismatch") credential.TenantId = "private-tenant";
        if (scenario == "object-mismatch") credential.ObjectId = "private-object";
        var accessToken = scenario switch
        {
            "rejected" or "unavailable" => null,
            "malformed" => "private-malformed-token",
            "audience" => JwtTestFactory.Build(audience: "private-audience"),
            "tenant-missing" => JwtTestFactory.Build(tenantId: null),
            "object-missing" => JwtTestFactory.Build(tenantId: Tenant),
            _ => Token("{}"),
        };
        _refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((accessToken, (string?)null, scenario == "rejected"));
        var effects = new NativeSetupEffects();

        var result = await NativeSetupEnrollment.EnrollAsync("private-alias", "microsoft.com",
            (_, _) => Task.FromResult(InteractiveAuthResult.Success(credential)),
            () => new ConnectionBindingService(_home, _refresher, setupEffects: effects),
            ownsBindings: true, effects, default);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unavailable);
        result.Error.Message.ShouldBe(reason);
        result.Error.Message.ShouldNotContain("private");
        var failure = NativeConnectionSetup.ReadFailure(result.Error).Value;
        failure.Phase.ShouldBe(NativeSetupPhase.Enrollment);
        failure.Cancelled.ShouldBeFalse();
        failure.Effects.ShouldContain(new NativeSetupEffect(
            NativeSetupEffectKind.InteractiveSignIn, NativeSetupEffectState.Confirmed));
        failure.Effects.ShouldNotContain(effect => effect.Kind == NativeSetupEffectKind.CredentialWrite
            || effect.Kind == NativeSetupEffectKind.IdentityRegistration);
        Directory.GetFiles(Path.Combine(_home, "credentials")).ShouldBeEmpty();
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

    internal static string Token(string claims, string principal = Principal)
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

    [Fact]
    public async Task NativeSetupBindingEnsuresOriginalRowWithoutChangingDefaultOrRenewingCredential()
    {
        SetToken("{}");
        using var bindings = new ConnectionBindingService(_home, _refresher);
        await bindings.RegisterAadIdentityAsync("personal", Credential());
        await bindings.RegisterAadIdentityAsync("second", Credential());
        var originalDefault = await bindings.CreateBindingAsync("fixture", "Work", "personal", true);
        var original = await bindings.CreateBindingAsync("fixture", "Work", "second", false);
        _refresher.ClearReceivedCalls();
        var setup = new NativeConnectionSetup(effects =>
            new ConnectionBindingService(_home, _refresher, setupEffects: effects), ownsBindings: true);

        var result = await setup.CreateAdoBindingAsync(new("fixture", "Work", "second"));

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.ShouldBe(new NativeAdoBinding(original.BindingId, original.ConnectionRef, original.IdentityId, original.Revision));
        using var registry = new SqliteSystemWorktreeRegistry(bindings.RegistryPath, TimeProvider.System);
        (await registry.FindDefaultBindingAsync(original.ConnectionRef)).Value!.BindingId.ShouldBe(originalDefault.BindingId);
        (await bindings.CreateBindingAsync("fixture", "Work", "second", false)).ShouldBe(original);
        await _refresher.DidNotReceiveWithAnyArgs().TryRefreshAsync(default!, default!, default!, default!, default);
    }

    [Theory]
    [InlineData(NativeSetupEffectKind.ConnectionRegistration)]
    [InlineData(NativeSetupEffectKind.ConnectionBinding)]
    public async Task NativeSetupBindingCancellationRollsBackRegistrationAndBinding(
        NativeSetupEffectKind cancelBefore)
    {
        SetToken("{}");
        using var original = new ConnectionBindingService(_home, _refresher);
        await original.RegisterAadIdentityAsync("personal", Credential());
        const string project = "NewExplicitProject";
        var connection = ConnectionRefResolver.Compute("fixture", project);
        using var cancellation = new CancellationTokenSource();
        var setup = new NativeConnectionSetup(effects =>
        {
            effects.Observed = (kind, state) =>
            {
                if (kind == cancelBefore && state == NativeSetupEffectState.Possible) cancellation.Cancel();
            };
            return new ConnectionBindingService(_home, _refresher, setupEffects: effects);
        }, ownsBindings: true);
        (await original.ListBindingsAsync("fixture", project)).ShouldBeEmpty();
        ConnectionCount().ShouldBe(0);

        var result = await setup.CreateAdoBindingAsync(new("fixture", project, "personal"), cancellation.Token);

        result.IsSuccess.ShouldBeFalse();
        var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
        report.Cancelled.ShouldBeTrue();
        report.Effects.ShouldContain(new NativeSetupEffect(cancelBefore, NativeSetupEffectState.Possible));
        report.Effects.ShouldNotContain(new NativeSetupEffect(NativeSetupEffectKind.ConnectionRegistration, NativeSetupEffectState.Confirmed));
        ConnectionCount().ShouldBe(0);
        (await original.ListBindingsAsync("fixture", project)).ShouldBeEmpty();
        using var registry = new SqliteSystemWorktreeRegistry(original.RegistryPath, TimeProvider.System);
        (await registry.FindDefaultBindingAsync(connection)).IsSuccess.ShouldBeFalse();

        int ConnectionCount()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = original.RegistryPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = """
                SELECT (SELECT COUNT(*) FROM connections WHERE connection_ref=$ref) +
                       (SELECT COUNT(*) FROM device_connections WHERE provider='ado' AND target=$target);
                """;
            query.Parameters.AddWithValue("$ref", connection);
            query.Parameters.AddWithValue("$target", project.ToLowerInvariant());
            return Convert.ToInt32(query.ExecuteScalar());
        }
    }
}
