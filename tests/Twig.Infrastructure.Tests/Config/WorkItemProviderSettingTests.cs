using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class WorkItemProviderSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "twig-provider-" + Guid.NewGuid().ToString("N"));

    public WorkItemProviderSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AbsentSetting_DefaultsToAdo()
    {
        var config = new TwigConfiguration();

        config.Provider.ShouldBeNull();
        config.ResolveWorkItemProviderKind().ShouldBe("ado");
        config.GetValue("provider").ShouldBe(("ado", true));
    }

    [Theory]
    [InlineData("github", "github")]
    [InlineData("ado", "ado")]
    [InlineData(" GitHub ", "github")]
    public void SetValue_KnownToken_StoresCanonicalToken(string input, string expected)
    {
        var config = new TwigConfiguration();

        config.SetValue("provider", input).ShouldBeTrue();

        config.Provider.ShouldBe(expected);
        config.ResolveWorkItemProviderKind().ShouldBe(expected);
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("")]
    [InlineData("azure-devops")]
    public void SetValue_UnknownToken_IsRejectedAndLeavesSettingUnchanged(string input)
    {
        var config = new TwigConfiguration { Provider = "github" };

        config.SetValue("provider", input).ShouldBeFalse();

        config.Provider.ShouldBe("github");
    }

    [Fact]
    public void ProviderKey_IsRepoScoped()
        => TwigConfiguration.GetConfigScope("provider").ShouldBe(ConfigScope.Repo);

    [Theory]
    [InlineData("ADO")]
    [InlineData("jira")]
    public void Resolve_NonCanonicalStoredToken_FailsClosed(string stored)
    {
        var ex = Should.Throw<TwigConfigurationException>(() => WorkItemProviderSetting.Resolve(stored));
        ex.Message.ShouldStartWith("provider-unknown:");
    }

    [Fact]
    public async Task RepoManifest_OmitsProviderWhenUnset_SoExistingManifestsKeepTheirBytes()
    {
        var bytes = await new TwigConfiguration { Organization = "o", Project = "p" }.GetRepoBytesAsync();

        System.Text.Encoding.UTF8.GetString(bytes).ShouldNotContain("\"provider\"");
    }

    [Fact]
    public async Task RepoManifest_RoundTripsExplicitProvider()
    {
        var paths = new TwigPaths(
            Path.Combine(_dir, ".twig"),
            Path.Combine(_dir, ".twig", "config"),
            Path.Combine(_dir, ".twig", "twig.db"),
            _dir);
        var config = new TwigConfiguration { Organization = "o", Project = "p" };
        config.SetValue("provider", "github").ShouldBeTrue();
        await config.SaveRepoAsync(paths.RepoConfigPath);

        (await File.ReadAllTextAsync(paths.RepoConfigPath)).ShouldContain("\"provider\": \"github\"");
        var loaded = await TwigConfiguration.LoadSplitAsync(paths);
        loaded.ResolveWorkItemProviderKind().ShouldBe("github");
    }
}
