using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class TwigConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-config-" + Guid.NewGuid().ToString("N"));
    private TwigPaths Paths => new(Path.Combine(_root, ".twig"), Path.Combine(_root, ".twig", "config"),
        Path.Combine(_root, ".twig", "mirror.db"), _root, Path.Combine(_root, "display.json"));

    public TwigConfigurationTests() => Directory.CreateDirectory(Paths.TwigDir);

    [Fact]
    public async Task FlatLocalConfigurationIsRejectedInsteadOfAdoptingPortableCoordinates()
    {
        await File.WriteAllTextAsync(Paths.ConfigPath,
            """{"organization":"old-org","project":"old-project","display":{"icons":"nerd"}}""");
        File.Exists(Paths.RepoConfigPath).ShouldBeFalse();

        await Should.ThrowAsync<TwigConfigurationException>(() => TwigConfiguration.LoadSplitAsync(Paths));
        Should.Throw<TwigConfigurationException>(() => TwigConfiguration.LoadSplit(Paths));
    }

    [Fact]
    public async Task SplitPersistenceSeparatesCoordinatesAndIdentityPreferences()
    {
        var config = new TwigConfiguration
        {
            Organization = "selected-org", Project = "selected-project", Provider = "github",
            GitHub = new() { Host = "github.com", Repository = "owner/repository" },
            GitHubAccount = "local-account",
        };
        config.Defaults.AreaPathEntries = [new() { Path = "area", IncludeChildren = false }];
        await config.SaveSplitAsync(Paths);

        var repoBytes = await File.ReadAllBytesAsync(Paths.RepoConfigPath);
        var repo = System.Text.Encoding.UTF8.GetString(repoBytes);
        repo.ShouldNotContain("local-account");
        (await File.ReadAllTextAsync(Paths.ConfigPath)).ShouldNotContain("selected-org");
        var loaded = await TwigConfiguration.LoadSplitAsync(Paths);
        loaded.Project.ShouldBe("selected-project");
        loaded.ResolveWorkItemProviderKind().ShouldBe("github");
        loaded.GitHub!.Repository.ShouldBe("owner/repository");
        loaded.GitHubAccount.ShouldBe("local-account");
        loaded.Defaults.ResolveAreaPaths().ShouldBe([("area", false)]);
        await loaded.SaveSplitAsync(Paths);
        (await File.ReadAllBytesAsync(Paths.RepoConfigPath)).ShouldBe(repoBytes);
    }

    [Fact]
    public async Task SharedManifestCannotSelectLocalCredentialAccount()
    {
        await File.WriteAllTextAsync(Paths.RepoConfigPath,
            """{"project":"selected-project","githubAccount":"repo-account","github":{"host":"github.com","repository":"o/r","account":"repo-account"}}""");
        await Should.ThrowAsync<TwigConfigurationException>(() => TwigConfiguration.LoadSplitAsync(Paths));
        await File.WriteAllTextAsync(Paths.RepoConfigPath,
            """{"project":"selected-project","github":{"host":"github.com","repository":"o/r"}}""");
        await new TwigConfiguration { GitHubAccount = "local-account" }.SaveUserAsync(Paths.ConfigPath);
        (await TwigConfiguration.LoadSplitAsync(Paths)).GitHubAccount.ShouldBe("local-account");
    }

    [Theory]
    [InlineData("""{"defaults":{"areaPath":"old-root"}}""")]
    [InlineData("""{"defaults":{"areaPaths":["old-list"]}}""")]
    [InlineData("""{"defaults":{"inheritParentArea":true}}""")]
    [InlineData("""{"processTemplate":"old-process"}""")]
    [InlineData("""{"seed":{"staleDays":14}}""")]
    [InlineData("""{"unknown":true}""")]
    public async Task RetiredOrUnknownManifestControlsFailClosed(string json)
    {
        await File.WriteAllTextAsync(Paths.RepoConfigPath, json);
        await Should.ThrowAsync<TwigConfigurationException>(() => TwigConfiguration.LoadSplitAsync(Paths));
        Should.Throw<TwigConfigurationException>(() => TwigConfiguration.LoadSplit(Paths));
    }

    [Theory]
    [InlineData("""{"auth":{"method":"pat"}}""")]
    [InlineData("""{"tracking":{"cleanupPolicy":"on-complete"}}""")]
    [InlineData("""{"display":{"cacheStaleMinutes":5}}""")]
    public async Task RetiredLocalControlsFailClosed(string json)
    {
        await File.WriteAllTextAsync(Paths.ConfigPath, json);
        await Should.ThrowAsync<TwigConfigurationException>(() => TwigConfiguration.LoadSplitAsync(Paths));
        Should.Throw<TwigConfigurationException>(() => TwigConfiguration.LoadSplit(Paths));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"defaults":{"areaPathEntries":[]}}""")]
    public async Task AbsentOrEmptyCurrentAreaScopeStaysUnfiltered(string json)
    {
        await File.WriteAllTextAsync(Paths.RepoConfigPath, json);
        var config = await TwigConfiguration.LoadSplitAsync(Paths);
        config.Defaults.ResolveAreaPaths().ShouldBeNull();
        config.Defaults.IterationPath.ShouldBeNull();
    }

    [Fact]
    public async Task DisplayDefaultsAreInheritedWithoutBecomingLocalOverrides()
    {
        await File.WriteAllTextAsync(Paths.GlobalDisplayPath, """{"icons":"nerd"}""");
        var config = await TwigConfiguration.LoadSplitAsync(Paths);
        config.Display.Icons.ShouldBe("nerd");
        config.Display.IconsOverride.ShouldBeNull();
        await config.SaveUserAsync(Paths.ConfigPath);
        File.Exists(Paths.ConfigPath).ShouldBeFalse();

        config.Display.Icons = "unicode";
        config.Display.TreeDepthDown = 7;
        await config.SaveUserAsync(Paths.ConfigPath);
        config = await TwigConfiguration.LoadSplitAsync(Paths);
        config.Display.Icons.ShouldBe("unicode");
        config.Display.TreeDepthDown.ShouldBe(7);
        config.Display.ClearIconsOverride();
        await config.SaveUserAsync(Paths.ConfigPath);
        (await TwigConfiguration.LoadSplitAsync(Paths)).Display.Icons.ShouldBe("nerd");
    }

    [Fact]
    public async Task MalformedManifestFailsInsteadOfReadingLocalCoordinates()
    {
        await File.WriteAllTextAsync(Paths.RepoConfigPath, "{");
        await File.WriteAllTextAsync(Paths.ConfigPath, """{"organization":"old-org"}""");
        await Should.ThrowAsync<TwigConfigurationException>(() => TwigConfiguration.LoadSplitAsync(Paths));
        Should.Throw<TwigConfigurationException>(() => TwigConfiguration.LoadSplit(Paths));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
