using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Core;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Providers.Native;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class NativeDatabaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-named-databases-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionBindingService bindings;

    public NativeDatabaseTests()
    {
        Directory.CreateDirectory(root);
        bindings = new ConnectionBindingService(Path.Combine(root, "home"));
    }

    public void Dispose()
    {
        bindings.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Named_databases_isolate_items_and_plans_but_share_across_workspaces()
    {
        var singleton = await NativeAuthority.CreateAsync(Path.Combine(root, "home", "native-authority.db"));
        singleton.IsSuccess.ShouldBeTrue();
        var alpha = await Initialize("alpha", "first");
        var beta = await Initialize("beta", "second");
        var shared = await Initialize("alpha", "third");
        using var first = await Open(alpha);
        using var second = await Open(beta);
        using var third = await Open(shared);
        first.Document.DatabaseInstance.ShouldNotBe(third.Document.DatabaseInstance);
        first.Source.Connection.ShouldBe(third.Source.Connection);
        first.Source.Connection.ShouldNotBe(second.Source.Connection);
        first.Source.Connection.ShouldNotBe(singleton.Value.Connection, "the old singleton must not be adopted");

        var seed = await first.Workspace.StageSeedAsync(NativeProvider.ItemType,
            FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("Only alpha"))
                .Set(NativeProvider.StateField, FieldValue.String("Open")), default);
        seed.IsSuccess.ShouldBeTrue();
        var preview = await first.Workspace.PreviewDraftAsync(seed.Value.Key, default);
        preview.IsSuccess.ShouldBeTrue();
        var saved = await first.Workspace.SaveDraftAsync(preview.Value, default);
        saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        saved.Value.EffectsVerified.ShouldBeTrue();
        var key = (await first.Workspace.SnapshotAsync(default)).Value.Mappings.Single().Remote;

        var sharedItems = await third.Workspace.QueryAsync(new(QueryKind.All), default);
        sharedItems.IsSuccess.ShouldBeTrue();
        sharedItems.Value.Items.Single().Key.ShouldBe(key);
        var otherItems = await second.Workspace.QueryAsync(new(QueryKind.All), default);
        otherItems.IsSuccess.ShouldBeTrue();
        otherItems.Value.Items.ShouldBeEmpty();
        (await second.Workspace.ReadAsync(key, default)).IsSuccess.ShouldBeFalse();
        (await second.Workspace.StageEditAsync(key,
            FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("Wrong database")), default)).IsSuccess.ShouldBeFalse();
        (await second.Workspace.ApplyAsync(preview.Value.Plan!, default)).IsSuccess.ShouldBeFalse();
        var untouched = (await second.Workspace.SnapshotAsync(default)).Value;
        untouched.Journal.ShouldBeEmpty();
        untouched.RetainedItems.ShouldBeEmpty();

        var betaSeed = (await second.Workspace.StageSeedAsync(NativeProvider.ItemType,
            FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("Only beta")), default)).Value;
        var betaPreview = (await second.Workspace.PreviewDraftAsync(betaSeed.Key, default)).Value;
        (await second.Workspace.SaveDraftAsync(betaPreview, default)).Value.EffectsVerified.ShouldBeTrue();
        var betaKey = (await second.Workspace.SnapshotAsync(default)).Value.Mappings.Single().Remote;
        betaKey.NativeId.ShouldBe(key.NativeId, "numeric IDs are local to each database");
        betaKey.ShouldNotBe(key);
        (await third.Workspace.QueryAsync(new(QueryKind.All), default)).Value.Items.Single().Key.ShouldBe(key);
    }

    [Fact]
    public async Task Managed_setup_uses_the_same_explicit_database_as_repository_setup()
    {
        var repository = await Initialize("shared-work", "repository");
        using var original = await Open(repository);
        var setup = new NativeConnectionSetup(Path.Combine(root, "home"));
        var shared = await setup.InitializeManagedAsync(new NativeTwigSetupTarget("shared-work"));
        shared.IsSuccess.ShouldBeTrue(shared.IsSuccess ? "" : shared.Error.Message);
        shared.Value.Observation.Source.Connection.ShouldBe(original.Source.Connection);
        shared.Value.WorkspaceRoot.ShouldNotBe(Path.Combine(root, "repository"));
        var isolated = await setup.InitializeManagedAsync(new NativeTwigSetupTarget("separate-work"));
        isolated.IsSuccess.ShouldBeTrue(isolated.IsSuccess ? "" : isolated.Error.Message);
        isolated.Value.Observation.Source.Connection.ShouldNotBe(original.Source.Connection);
        (await TwigConfiguration.LoadSplitAsync(Paths(shared.Value.WorkspaceRoot))).NativeDatabase.ShouldBe("shared-work");
    }

    [Fact]
    public async Task Reopen_refuses_missing_replaced_or_retargeted_database_without_creating_an_authority()
    {
        var factory = await Initialize("alpha", "first");
        var beta = await Initialize("beta", "second");
        using var original = await Open(factory);
        using var other = await Open(beta);
        original.Source.Connection.ShouldNotBe(other.Source.Connection);
        var path = Path.Combine(root, "home", "native-databases", "database-alpha.db");
        var retained = path + ".retained";
        File.Exists(path).ShouldBeTrue();
        File.Move(path, retained);
        var missing = await factory.OpenAsync(default);
        missing.IsSuccess.ShouldBeFalse();
        missing.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        File.Exists(path).ShouldBeFalse("opening a workspace must not recreate its missing authority");
        var replacement = await NativeAuthority.CreateAsync(path);
        replacement.IsSuccess.ShouldBeTrue();
        replacement.Value.Connection.ShouldNotBe(original.Source.Connection);
        var replaced = await factory.OpenAsync(default);
        replaced.IsSuccess.ShouldBeFalse();
        replaced.Error.Code.ShouldBe(ErrorCode.Conflict);

        var paths = Paths(Path.Combine(root, "first"));
        var configuration = await TwigConfiguration.LoadSplitAsync(paths);
        configuration.NativeDatabase = "beta";
        var retargeted = await new CoreWorkspaceFactory(configuration, paths, bindings,
            Substitute.For<IAuthenticationProvider>()).OpenAsync(default);
        retargeted.IsSuccess.ShouldBeFalse();
        retargeted.Error.Code.ShouldBe(ErrorCode.Conflict);
        File.Delete(path);
        File.Move(retained, path);
        using var restored = await Open(factory);
        restored.Source.ShouldBe(original.Source);
    }

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task Production_cli_creates_named_database_and_persists_selection(string format)
    {
        var work = Path.Combine(root, "cli");
        Directory.CreateDirectory(work);
        InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
        var previousHome = Environment.GetEnvironmentVariable("TWIG_USER_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TWIG_USER_HOME", Path.Combine(root, "home"));
            string[] args = ["init", "--provider", "twig", "--database", "cli-work", "-o", format];
            var result = await StdoutCapture.RunAsync(() => Task.FromResult(TwigConsoleApp.Run(args, services =>
                services.AddTwigWorkspaceServices(work, args, Path.Combine(root, "display.json")))));
            result.result.ShouldBe(0);
            result.stdout.ShouldContain("cli-work");
            if (format == "json")
            {
                using var json = JsonDocument.Parse(result.stdout);
                json.RootElement.GetProperty("nativeDatabase").GetString().ShouldBe("cli-work");
                json.RootElement.GetProperty("coreInitialized").GetBoolean().ShouldBeTrue();
            }
            var config = await TwigConfiguration.LoadSplitAsync(Paths(work));
            config.NativeDatabase.ShouldBe("cli-work");
            config.ResolveWorkItemProviderKind().ShouldBe("twig");
            File.Exists(Path.Combine(root, "home", "native-databases", "database-cli-work.db")).ShouldBeTrue();
            File.Exists(Path.Combine(root, "home", "native-authority.db")).ShouldBeFalse();
        }
        finally { Environment.SetEnvironmentVariable("TWIG_USER_HOME", previousHome); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("MixedCase")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two words")]
    public async Task Missing_or_invalid_names_refuse_before_native_or_checkout_effects(string? name)
    {
        var work = Path.Combine(root, "invalid");
        Directory.CreateDirectory(work);
        InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
        var paths = Paths(work);
        var calls = 0;
        var command = new InitCommand(paths, new RendererFactory(), () =>
        {
            calls++;
            throw new InvalidOperationException("Invalid names must not resolve the binding service.");
        });
        var result = await StdoutCapture.RunAsync(() => command.ExecuteAsync("", "",
            outputFormat: "json", provider: "twig", database: name));
        result.result.ShouldBe(2);
        calls.ShouldBe(0);
        Directory.Exists(paths.TwigDir).ShouldBeFalse();
        File.Exists(paths.RepoConfigPath).ShouldBeFalse();
        File.Exists(Path.Combine(work, ".gitignore")).ShouldBeFalse();
        Directory.Exists(Path.Combine(root, "home", "native-databases")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(64, 0)]
    [InlineData(65, 2)]
    public async Task Database_name_length_limit_is_enforced_by_initialization(int length, int expectedExit)
    {
        var work = Path.Combine(root, "length");
        Directory.CreateDirectory(work);
        InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
        var paths = Paths(work);
        var name = new string('a', length);
        var command = new InitCommand(paths, new RendererFactory(), () => bindings);
        var initialized = await StdoutCapture.RunAsync(() => command.ExecuteAsync("", "",
            provider: "twig", database: name, outputFormat: "json"));
        initialized.result.ShouldBe(expectedExit);
        File.Exists(Path.Combine(root, "home", "native-databases", "database-" + name + ".db")).ShouldBe(expectedExit == 0);
        Directory.Exists(paths.TwigDir).ShouldBe(expectedExit == 0);
    }

    [Fact]
    public async Task Database_option_refuses_manifest_conflicts_and_other_providers_before_effects()
    {
        var work = Path.Combine(root, "conflict");
        Directory.CreateDirectory(work);
        InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
        var paths = Paths(work);
        const string manifest = """{"provider":"twig","nativeDatabase":"alpha"}""";
        await File.WriteAllTextAsync(paths.RepoConfigPath, manifest);
        var calls = 0;
        var command = new InitCommand(paths, new RendererFactory(), () =>
        {
            calls++;
            throw new InvalidOperationException("Conflicting targets must not resolve bindings.");
        });
        var conflict = await StdoutCapture.RunAsync(() => command.ExecuteAsync("", "",
            provider: "twig", database: "beta", outputFormat: "json"));
        conflict.result.ShouldBe(2);
        (await File.ReadAllTextAsync(paths.RepoConfigPath)).ShouldBe(manifest);
        File.Delete(paths.RepoConfigPath);
        var otherProvider = await StdoutCapture.RunAsync(() => command.ExecuteAsync("fixture", "Project",
            provider: "ado", database: "alpha", outputFormat: "json"));
        otherProvider.result.ShouldBe(2);
        calls.ShouldBe(0);
        Directory.Exists(paths.TwigDir).ShouldBeFalse();
    }

    private async Task<CoreWorkspaceFactory> Initialize(string database, string directory)
    {
        var work = Path.Combine(root, directory);
        Directory.CreateDirectory(work);
        InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
        await File.WriteAllTextAsync(Path.Combine(work, "twig.json"),
            $$"""{"provider":"twig","nativeDatabase":"{{database}}"}""");
        await new DeviceConnectionCatalog(bindings.RegistryPath).AddAsync(
            ProviderConnectionRequirement.Create("twig", database: database));
        var paths = Paths(work);
        var command = new InitCommand(paths, new RendererFactory(), () => bindings);
        var initialized = await StdoutCapture.RunAsync(() => command.ExecuteAsync("", "", outputFormat: "json", provider: "twig"));
        initialized.result.ShouldBe(0);
        var configuration = await TwigConfiguration.LoadSplitAsync(paths);
        configuration.NativeDatabase.ShouldBe(database);
        var inventory = await NativeCoreExecution.InventoryAsync(bindings.RegistryPath);
        inventory.IsSuccess.ShouldBeTrue();
        var activated = await NativeCoreExecution.ActivateAsync(bindings.RegistryPath, inventory.Value,
            inventory.Value.Digest, completeInventoryConfirmed: true, oldProcessesQuiesced: true);
        activated.IsSuccess.ShouldBeTrue();
        return new(configuration, paths, bindings, Substitute.For<IAuthenticationProvider>());
    }

    private static TwigPaths Paths(string work)
        => new(Path.Combine(work, ".twig"), Path.Combine(work, ".twig", "config"), Path.Combine(work, ".twig", "core.db"), work);

    private static async Task<CoreWorkspaceHandle> Open(CoreWorkspaceFactory factory)
    {
        var opened = await factory.OpenAsync(default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        return opened.Value;
    }
}
