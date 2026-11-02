using Shouldly;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task NativeSourceGitHubReadsWithoutCredentialAcquisitionAndObservesSchemaExplicitly()
    {
        using var f = new Fixture();
        await f.Initialize();
        var credentials = f.CredentialCalls;
        var requests = f.Http.Requests;
        var local = await f.ReadSource();
        local.IsSuccess.ShouldBeTrue(local.IsSuccess ? "" : local.Error.Message);
        local.Value.Source.Connection.Provider.Value.ShouldBe("github");
        local.Value.Source.PrincipalId.ShouldBe("U_actor");
        local.Value.Schema.ShouldBeNull();
        local.Value.Snapshot.Version.ShouldBe(0);
        local.Value.Snapshot.Benches.ShouldBeEmpty();
        f.CredentialCalls.ShouldBe(credentials);
        f.Http.Requests.ShouldBe(requests);
        var online = await f.ReadSource(local.Value.Source, schema: true);
        online.IsSuccess.ShouldBeTrue(online.IsSuccess ? "" : online.Error.Message);
        online.Value.Source.ShouldBe(local.Value.Source);
        online.Value.Schema.ShouldNotBeNull().Value.Connection.ShouldBe(local.Value.Source.Connection);
        online.Value.SchemaObservedAt.ShouldNotBeNull();
        f.CredentialCalls.ShouldBeGreaterThan(credentials);
        f.Http.SchemaRequests.ShouldBeGreaterThan(0);
        f.Http.WorkRequests.ShouldBe(0);
        f.Http.Writes.ShouldBe(0);
        (await f.Snapshot()).Version.ShouldBe(0);
    }

    [Fact]
    public async Task NativeSourceGitHubRefusesExpectedStoreMismatchBeforeCredentials()
    {
        using var f = new Fixture();
        await f.Initialize();
        var local = (await f.ReadSource()).Value;
        var requests = f.Http.Requests;
        var credentials = f.CredentialCalls;
        var result = await f.ReadSource(local.Source with { StoreInstance = Guid.NewGuid() }, schema: true);
        result.IsSuccess.ShouldBeFalse();
        f.Http.Requests.ShouldBe(requests);
        f.CredentialCalls.ShouldBe(credentials);
    }
}
