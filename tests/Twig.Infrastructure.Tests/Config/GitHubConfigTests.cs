using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class GitHubConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "twig-github-config-" + Guid.NewGuid().ToString("N"));

    public GitHubConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("github.host")]
    [InlineData("github.repository")]
    public void Keys_AreRepoScoped(string key) => TwigConfiguration.GetConfigScope(key).ShouldBe(ConfigScope.Repo);

    [Fact]
    public void Account_IsUserScoped() => TwigConfiguration.GetConfigScope("github.account").ShouldBe(ConfigScope.User);

    [Theory]
    [InlineData("github.com", "github.com")]
    [InlineData("GitHub.Com", "github.com")]
    [InlineData("ghe.example.com", "ghe.example.com")]
    public void SetValue_Host_StoresLowercaseHost(string input, string expected)
    {
        var config = new TwigConfiguration();

        config.SetValue("github.host", input).ShouldBeTrue();

        config.GitHub!.Host.ShouldBe(expected);
        config.GetValue("github.host").ShouldBe((expected, true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("https://github.com")]
    [InlineData("github.com/owner")]
    [InlineData("github.com:443")]
    [InlineData(" github.com")]
    [InlineData("user@github.com")]
    public void SetValue_MalformedHost_IsRejected(string input)
    {
        var config = new TwigConfiguration();

        config.SetValue("github.host", input).ShouldBeFalse();

        config.GitHub.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" selected-account")]
    [InlineData("two accounts")]
    [InlineData("--account")]
    [InlineData("account\nname")]
    public void SetValue_MalformedAccount_IsRejectedWithoutChangingExistingSelection(string input)
    {
        var config = new TwigConfiguration { GitHubAccount = "selected-account" };
        config.SetValue("github.account", input).ShouldBeFalse();
        config.GitHubAccount.ShouldBe("selected-account");
    }

    [Theory]
    [InlineData("owner/name")]
    [InlineData("Owner-1/repo.name_x")]
    public void SetValue_Repository_StoresExactValue(string input)
    {
        var config = new TwigConfiguration();

        config.SetValue("github.repository", input).ShouldBeTrue();

        config.GitHub!.Repository.ShouldBe(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("owner/name/extra")]
    [InlineData("owner/..")]
    [InlineData("-owner/name")]
    [InlineData("https://github.com/owner/name")]
    [InlineData("owner/ name")]
    public void SetValue_MalformedRepository_IsRejected(string input)
        => new TwigConfiguration().SetValue("github.repository", input).ShouldBeFalse();

    public static TheoryData<string?, string?, string> Problems => new()
    {
        { null, null, "must both be set explicitly" },
        { "github.com", null, "must both be set explicitly" },
        { null, "o/r", "must both be set explicitly" },
        { "GitHub.com", "o/r", "bare lowercase host" },
        { "not a host", "o/r", "bare lowercase host" },
        { "github.com", "o", "exactly owner/name" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void TryCreate_MissingOrMalformed_FailsClosed(string? host, string? repository, string expected)
    {
        GitHubWorkspaceScope.TryCreate(new GitHubConfig { Host = host, Repository = repository }, out var scope, out var problem)
            .ShouldBeFalse();

        scope.ShouldBeNull();
        problem.ShouldContain(expected);
    }

    [Theory]
    [InlineData("github.com", "https://api.github.com/graphql")]
    [InlineData("ghe.example.com", "https://ghe.example.com/api/graphql")]
    public void TryCreate_Valid_DerivesHttpsEndpointFromHostOnly(string host, string endpoint)
    {
        GitHubWorkspaceScope.TryCreate(new GitHubConfig { Host = host, Repository = "o/r" }, out var scope, out _).ShouldBeTrue();

        scope!.GraphQlEndpoint.ShouldBe(new Uri(endpoint));
        scope.Repository.ShouldBe("o/r");
    }

    [Fact]
    public void TryCreate_NullConfig_FailsClosed()
        => GitHubWorkspaceScope.TryCreate(null, out _, out _).ShouldBeFalse();

    [Fact]
    public async Task SharedManifestCannotSelectOrReplaceUserCredentialAccount()
    {
        var paths = new TwigPaths(
            Path.Combine(_dir, ".twig"), Path.Combine(_dir, ".twig", "config"),
            Path.Combine(_dir, ".twig", "twig.db"), _dir);
        Directory.CreateDirectory(paths.TwigDir);
        await File.WriteAllTextAsync(paths.RepoConfigPath, """
            {"organization":"o","project":"p","githubAccount":"repo-selected",
             "github":{"host":"github.com","repository":"owner/name","account":"repo-selected"}}
            """);
        (await TwigConfiguration.LoadSplitAsync(paths)).GitHubAccount.ShouldBeNull();
        var config = new TwigConfiguration { GitHubAccount = "user-selected" };
        await config.SaveUserAsync(paths.ConfigPath);

        (await TwigConfiguration.LoadSplitAsync(paths)).GitHubAccount.ShouldBe("user-selected");
        TwigConfiguration.LoadSplit(paths).GitHubAccount.ShouldBe("user-selected");
    }

    [Fact]
    public async Task RepoManifest_OmitsGitHubWhenUnset_AndRoundTripsWhenSet()
    {
        var paths = new TwigPaths(
            Path.Combine(_dir, ".twig"),
            Path.Combine(_dir, ".twig", "config"),
            Path.Combine(_dir, ".twig", "twig.db"),
            _dir);

        var unset = await new TwigConfiguration { Organization = "o", Project = "p" }.GetRepoBytesAsync();
        System.Text.Encoding.UTF8.GetString(unset).ShouldNotContain("\"github\"");

        var config = new TwigConfiguration { Organization = "o", Project = "p" };
        config.SetValue("provider", "github").ShouldBeTrue();
        config.SetValue("github.host", "github.com").ShouldBeTrue();
        config.SetValue("github.repository", "owner/name").ShouldBeTrue();
        config.SetValue("github.account", "selected-account").ShouldBeTrue();
        await config.SaveRepoAsync(paths.RepoConfigPath);
        await config.SaveUserAsync(paths.ConfigPath);
        (await File.ReadAllTextAsync(paths.RepoConfigPath)).ShouldNotContain("selected-account");
        (await File.ReadAllTextAsync(paths.ConfigPath)).ShouldContain("selected-account");

        var loaded = await TwigConfiguration.LoadSplitAsync(paths);
        loaded.GitHub!.Host.ShouldBe("github.com");
        loaded.GitHub.Repository.ShouldBe("owner/name");
        loaded.GitHubAccount.ShouldBe("selected-account");
        loaded.GetValue("github.account").ShouldBe(("selected-account", true));
    }
}
