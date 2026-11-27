using System.Text.Json;
using Shouldly;
using Twig.DependencyInjection;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ConnectionStatusProductionCliTests : ConnectionStatusCases
{
    protected override Task<(int Exit, string Out, string Error)> RunAsync(string root, string home, params string[] args)
        => StructuredReadProductionCliTests.RunInWorkspaceAsync(root, home, args);
}

public sealed class ConnectionStatusCommandTests : ConnectionStatusCases
{
    protected override Task<(int Exit, string Out, string Error)> RunAsync(string root, string home, params string[] args)
    {
        var previousHome = Environment.GetEnvironmentVariable("TWIG_USER_HOME");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("TWIG_USER_HOME", home);
            Console.SetOut(output);
            Console.SetError(error);
            var exit = TwigConsoleApp.Run(args, services =>
                services.AddTwigWorkspaceServices(root, args, Path.Combine(home, "display.json")));
            return Task.FromResult((exit, output.ToString(), error.ToString()));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("TWIG_USER_HOME", previousHome);
        }
    }
}

public abstract class ConnectionStatusCases : IDisposable
{
    protected abstract Task<(int Exit, string Out, string Error)> RunAsync(string root, string home, params string[] args);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-connection-layout-{Guid.NewGuid():N}");

    [Fact]
    public async Task PortableConnectionInventoryPersistsAcrossInvocationsWithoutWorkspace()
    {
        Directory.CreateDirectory(_root);
        const string manifest = "{broken manifest";
        await File.WriteAllTextAsync(Path.Combine(_root, "twig.json"), manifest);
        var home = Path.Combine(_root, "home");
        var added = await RunAsync(_root, home,
            "connection", "add", "--provider", "ado", "--org", "fixture", "--project", "Project", "-o", "json");
        added.Exit.ShouldBe(0, added.Error);
        var otherDirectory = Path.Combine(_root, "outside-workspace");
        Directory.CreateDirectory(otherDirectory);
        var listed = await RunAsync(otherDirectory, home,
            "connection", "list", "-o", "json");
        listed.Exit.ShouldBe(0, listed.Error);
        using var json = JsonDocument.Parse(listed.Out);
        var connection = json.RootElement.EnumerateArray().ShouldHaveSingleItem();
        connection.GetProperty("provider").GetString().ShouldBe("ado");
        connection.GetProperty("scope").GetString().ShouldBe("https://dev.azure.com/fixture");
        connection.GetProperty("target").GetString().ShouldBe("project");
        connection.GetProperty("ready").GetBoolean().ShouldBeFalse();
        Directory.Exists(Path.Combine(_root, ".twig")).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(_root, "twig.json"))).ShouldBe(manifest);
    }

    [Theory]
    [InlineData("json", "missing-marker")]
    [InlineData("human", "missing-marker")]
    [InlineData("minimal", "missing-marker")]
    [InlineData("json", "unsupported-marker")]
    [InlineData("json", "missing-attachment")]
    public async Task InvalidLocalLayoutNamesRecoveryWithoutBindingGuidance(string format, string damage)
    {
        var paths = await InitializeLayoutAsync();
        var marker = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName);
        var attachment = Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
        File.Exists(marker).ShouldBeTrue();
        File.Exists(attachment).ShouldBeTrue();
        if (damage == "missing-marker") File.Delete(marker);
        else if (damage == "missing-attachment") File.Delete(attachment);
        else await File.WriteAllTextAsync(marker, """{"schema":"unsupported","version":999}""");
        Directory.Exists(paths.TwigDir).ShouldBeTrue();
        var before = Directory.GetFiles(paths.TwigDir).ToDictionary(path => path, File.ReadAllBytes);

        var result = await RunAsync(
            _root, Path.Combine(_root, "home"), "connection", "status", "-o", format);

        result.Exit.ShouldBe(1, result.Error);
        result.Out.ShouldBeEmpty();
        var message = result.Error;
        if (format == "json")
        {
            using var error = JsonDocument.Parse(message);
            message = error.RootElement.GetProperty("error").GetString().ShouldNotBeNull();
        }
        message.ShouldContain("Local Twig workspace");
        message.ShouldContain(paths.TwigDir);
        message.ShouldContain("layout-marker-missing");
        message.ShouldContain("back up");
        message.ShouldContain("twig init <org> <project>");
        message.ShouldNotContain("--reinitialize");
        message.ShouldNotContain("auth login");
        message.ShouldNotContain("connection bind");
        message.ShouldNotContain("Register a named identity");
        message.ShouldNotContain("No recorded connection binding");
        Directory.GetFiles(paths.TwigDir).Order().ShouldBe(before.Keys.Order());
        foreach (var (path, bytes) in before) File.ReadAllBytes(path).ShouldBe(bytes);
    }

    [Fact]
    public async Task ValidLayoutWithoutBindingKeepsExistingGuidance()
    {
        var paths = await InitializeLayoutAsync();
        File.Exists(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName)).ShouldBeTrue();

        var result = await RunAsync(
            _root, Path.Combine(_root, "home"), "connection", "status", "-o", "json");

        result.Exit.ShouldBe(1, result.Error);
        result.Out.ShouldBeEmpty();
        using var error = JsonDocument.Parse(result.Error);
        var message = error.RootElement.GetProperty("error").GetString().ShouldNotBeNull();
        message.ShouldContain("No recorded connection binding");
        message.ShouldContain("No explicit default binding");
        message.ShouldContain("Register a named identity, then bind explicit org/project coordinates.");
        message.ShouldNotContain("layout-marker-missing");
        message.ShouldNotContain("move aside");
    }

    private async Task<TwigPaths> InitializeLayoutAsync()
    {
        Directory.CreateDirectory(_root);
        InitCommandTestFixture.InitTempWorktree(_root).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture", Project = "fixture" };
        config.ResolveWorkItemProviderKind().ShouldBe("ado");
        var paths = TwigPaths.BuildPaths(Path.Combine(_root, ".twig"), config, _root);
        await config.SaveSplitAsync(paths);
        using var store = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System);
        (await store.InitializeAsync()).IsSuccess.ShouldBeTrue();
        (await store.ReadForBindingAsync(CancellationToken.None)).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(_root).ShouldNotBeNull();
        using var registry = new SqliteSystemWorktreeRegistry(Path.Combine(_root, "home", "system.db"), TimeProvider.System);
        var connection = ConnectionRefResolver.Compute(config);
        (await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, null)).IsSuccess.ShouldBeTrue();
        (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot)).IsSuccess.ShouldBeTrue();
        return paths;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
