using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData("partial")]
    [InlineData("relative-store")]
    [InlineData("sidecar")]
    [InlineData("collision")]
    [InlineData("provider")]
    [InlineData("coordinates")]
    public void NativeSetupFullInitPreflightRefusesBeforeNativeFactory(string defect)
    {
        var root = Path.Combine(_temp, "setup-preflight");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var database = Path.Combine(root, "new.db");
        if (defect == "partial") File.WriteAllText(Path.Combine(root, ".twig"), "preserve partial evidence");
        if (defect == "sidecar") File.WriteAllText(database + "-wal", "preserve sidecar");
        if (defect == "collision") database = Path.Combine(root, "twig.policy.json");
        if (defect == "relative-store") database = "relative.db";
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var factoryCalls = 0;
        string[] args = ["init", defect == "coordinates" ? "http://insecure.test" : "fixture", "Project",
            "--provider", defect == "provider" ? "unsupported" : "ado", "--core-store", database, "-o", "json"];
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = TwigConsoleApp.Run(args, services =>
            {
                services.AddSingleton<IConnectionBindingService>(_ =>
                {
                    factoryCalls++;
                    throw new InvalidOperationException("preflight must not resolve native bindings");
                });
                services.AddTwigWorkspaceServices(root, args, Path.Combine(_temp, "display.json"));
            });
            exit.ShouldNotBe(0);
            factoryCalls.ShouldBe(0);
            output.ToString().ShouldBeEmpty();
            using var parsed = JsonDocument.Parse(error.ToString());
            parsed.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
            Directory.GetFiles(root, "*", SearchOption.AllDirectories).ShouldBe(before.Keys);
            foreach (var (path, bytes) in before) File.ReadAllBytes(path).ShouldBe(bytes);
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    [Fact]
    public async Task NativeSetupAdoReadyRequiresCreatedStampSchemaAndActualOnlineAccess()
    {
        var (setup, root) = await FreshConnectionSetup();
        var result = await setup.InitializeFreshAsync(new(root,
            new NativeAdoSetupTarget("fixture", "Project", (await _bindings.CreateBindingAsync("fixture", "Project", "actor", false)).BindingId)));
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        var created = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(root, ".twig", "core-workspace.json")),
            CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
        result.Value.Source.StoreInstance.ShouldBe(created.DatabaseInstance);
        result.Value.Source.Connection.ShouldBe(created.Connection);
        result.Value.Source.PrincipalId.ShouldBe(created.PrincipalId);
        result.Value.Schema.ShouldNotBeNull().IsSuccess.ShouldBeTrue();
        result.Value.SchemaObservedAt.ShouldNotBeNull();
        var online = await NativeSourceReader.ReadOnlineAsync(root, result.Value.Source, services =>
        {
            services.AddSingleton<IConnectionBindingService>(_bindings);
            services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        }, default);
        online.IsSuccess.ShouldBeTrue();
        _transport.WorkRequests.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Theory]
    [InlineData("schema-denied")]
    [InlineData("store-replaced")]
    [InlineData("authority-replaced")]
    [InlineData("cancelled")]
    public async Task NativeSetupAdoReadbackFailurePreservesCreationAndCannotAdoptReplacement(string defect)
    {
        var (setup, root) = await FreshConnectionSetup();
        var selected = await _bindings.CreateBindingAsync("fixture", "Project", "actor", false);
        var replacementBinding = await _bindings.CreateBindingAsync("fixture", "Project", "sibling", false);
        using var cancellation = new CancellationTokenSource();
        var projectReads = 0;
        Guid? originalStore = null;
        string? originalDatabase = null;
        _transport.BeforeProjectMetadata = async () =>
        {
            if (++projectReads != 2) return;
            var marker = Path.Combine(root, ".twig", "core-workspace.json");
            var created = JsonSerializer.Deserialize(await File.ReadAllTextAsync(marker),
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            originalStore = created.DatabaseInstance;
            originalDatabase = created.DatabasePath;
            if (defect == "store-replaced")
            {
                var path = Path.Combine(root, ".twig", "replacement.db");
                var replacement = (await SqliteStore.CreateAsync(path)).Value;
                (await replacement.PinWorkspaceAsync(created.Connection)).IsSuccess.ShouldBeTrue();
                await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(created with
                { DatabaseInstance = replacement.InstanceId, DatabasePath = path }, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
            }
            if (defect == "authority-replaced")
            {
                var attachment = Path.Combine(root, ".twig", "attachment.json");
                var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(attachment))!;
                json["bindingPin"] = replacementBinding.BindingId;
                await File.WriteAllTextAsync(attachment, json.ToJsonString());
            }
            if (defect == "cancelled")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        if (defect == "schema-denied")
        {
            _transport.FailSchema = true;
            _transport.SchemaFailureStatus = System.Net.HttpStatusCode.Forbidden;
        }
        var result = await setup.InitializeFreshAsync(new(root,
            new NativeAdoSetupTarget("fixture", "Project", selected.BindingId)), cancellation.Token);

        result.IsSuccess.ShouldBeFalse();
        var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
        report.Cancelled.ShouldBe(defect == "cancelled");
        report.Effects.ShouldContain(new NativeSetupEffect(NativeSetupEffectKind.CoreStore, NativeSetupEffectState.Confirmed));
        report.Effects.ShouldContain(new NativeSetupEffect(NativeSetupEffectKind.WorkspaceMarker, NativeSetupEffectState.Confirmed));
        report.Effects.ShouldContain(new NativeSetupEffect(NativeSetupEffectKind.SourceReadback, NativeSetupEffectState.Possible));
        if (defect != "schema-denied")
        {
            originalStore.ShouldNotBeNull();
            File.Exists(originalDatabase).ShouldBeTrue();
            (await SqliteStore.OpenAsync(originalDatabase!, expectedInstance: originalStore!.Value)).IsSuccess.ShouldBeTrue();
        }
        else result.Error.Evidence.ShouldContain("http:403");
        _transport.WorkRequests.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
        using var released = ConnectionOperationGate.Acquire(root, exclusive: true);
    }

    private async Task<(NativeConnectionSetup Setup, string Root)> FreshConnectionSetup()
    {
        await AttachAsync();
        var root = Path.Combine(_temp, "fresh-setup");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
        _transport.CoreMetadataEnabled = true;
        _transport.ObservedPaths.Clear();
        return (new NativeConnectionSetup(_ => _bindings, transport: new CoreTransportOverride(_transport.NewHandle)), root);
    }
}
