using System.Text.Json;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
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
