using System.Text.Json;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task NativeManagedGitHubRegistersTheNamedAccountWithoutAUserCheckout()
    {
        using var fixture = new Fixture();
        var managedRoot = Path.Combine(fixture.Root, "managed-sources");
        var setup = new NativeConnectionSetup(_ => fixture._bindings,
            transport: new CoreTransportOverride(fixture.Http.NewHandle), processes: fixture._processes,
            managedRoot: managedRoot);
        var result = await setup.InitializeManagedAsync(new NativeGitHubSetupTarget("github.com", "original/repo", "fixture"));

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        var root = result.Value.WorkspaceRoot;
        Path.GetDirectoryName(root).ShouldBe(managedRoot);
        File.Exists(Path.Combine(fixture.Root, ".twig", "core-workspace.json")).ShouldBeFalse();
        result.Value.Observation.Source.PrincipalId.ShouldBe("U_actor");
        var schema = result.Value.Observation.Schema.ShouldNotBeNull();
        schema.Value.Connection.ShouldBe(result.Value.Observation.Source.Connection);
        var workspaces = (await UserBenchStore.ForRegistry(fixture._bindings.RegistryPath).WorkspacesAsync()).Value;
        var registered = workspaces.Single();
        registered.Root.ShouldBe(root);
        registered.Connection.ShouldBe(result.Value.Observation.Source.Connection);
        fixture.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSetupGitHubRequiresCreationBoundSchemaAndOnlineReadback(bool denied)
    {
        using var fixture = new Fixture();
        var setup = new NativeConnectionSetup(_ => fixture._bindings,
            transport: new CoreTransportOverride(fixture.Http.NewHandle), processes: fixture._processes);
        if (denied) fixture.Http.Defect = "schema-denied";
        var result = await setup.InitializeFreshAsync(new(fixture.Root,
            new NativeGitHubSetupTarget("github.com", "original/repo", "fixture")));

        result.IsSuccess.ShouldBe(!denied, result.IsSuccess ? "" : result.Error.Message);
        var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".twig", "core-workspace.json")),
            CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
        if (denied)
        {
            var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
            report.Effects.ShouldContain(new NativeSetupEffect(NativeSetupEffectKind.CoreStore, NativeSetupEffectState.Confirmed));
            report.Effects.ShouldContain(new NativeSetupEffect(NativeSetupEffectKind.SourceReadback, NativeSetupEffectState.Possible));
            result.Error.Evidence.ShouldContain("http:403");
        }
        else
        {
            result.Value.Source.StoreInstance.ShouldBe(document.DatabaseInstance);
            result.Value.Source.Connection.ShouldBe(document.Connection);
            result.Value.Source.PrincipalId.ShouldBe("U_actor");
            result.Value.Schema.ShouldNotBeNull().IsSuccess.ShouldBeTrue();
            fixture.Http.OnlineAccessRequests.ShouldBe(1);
        }
        fixture.Http.WorkRequests.ShouldBe(fixture.Http.OnlineAccessRequests,
            "the required issues-count access probe is the only work read; no issue bodies are acquired");
        fixture.Http.Writes.ShouldBe(0);
        using var released = ConnectionOperationGate.Acquire(fixture.Root, exclusive: true);
    }

    [Fact]
    public async Task NativeSetupGitHubCannotAdoptAStoreReplacedDuringSchemaAwait()
    {
        using var fixture = new Fixture();
        var setup = new NativeConnectionSetup(_ => fixture._bindings,
            transport: new CoreTransportOverride(fixture.Http.NewHandle), processes: fixture._processes);
        Guid? originalInstance = null;
        fixture.Http.BeforeSchema = async () =>
        {
            var marker = Path.Combine(fixture.Root, ".twig", "core-workspace.json");
            var created = JsonSerializer.Deserialize(await File.ReadAllTextAsync(marker),
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            originalInstance = created.DatabaseInstance;
            var path = Path.Combine(fixture.Root, ".twig", "replacement.db");
            var replacement = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(path)).Value;
            (await replacement.PinWorkspaceAsync(created.Connection)).IsSuccess.ShouldBeTrue();
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(created with
            { DatabaseInstance = replacement.InstanceId, DatabasePath = path }, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        };
        var result = await setup.InitializeFreshAsync(new(fixture.Root,
            new NativeGitHubSetupTarget("github.com", "original/repo", "fixture")));
        result.IsSuccess.ShouldBeFalse();
        originalInstance.ShouldNotBeNull();
        NativeConnectionSetup.ReadFailure(result.Error).Value.Phase.ShouldBe(NativeSetupPhase.Readback);
        fixture.Http.Writes.ShouldBe(0);
        using var released = ConnectionOperationGate.Acquire(fixture.Root, exclusive: true);
    }
}
