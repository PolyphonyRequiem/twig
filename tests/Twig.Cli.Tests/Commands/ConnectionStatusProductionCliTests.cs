using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ConnectionStatusProductionCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-connection-layout-{Guid.NewGuid():N}");

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

        var result = await StructuredReadProductionCliTests.RunInWorkspaceAsync(
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

        var result = await StructuredReadProductionCliTests.RunInWorkspaceAsync(
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
