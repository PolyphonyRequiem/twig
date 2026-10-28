using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Collection("ConsoleRedirect")]
public sealed class GitHubInitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-github-init-{Guid.NewGuid():N}");
    private readonly TwigPaths _paths;
    private readonly IAuthenticationProvider _auth = Substitute.For<IAuthenticationProvider>();
    private readonly HttpClient _http = new(new RefuseHttp());
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly InitCommand _command;

    public GitHubInitTests()
    {
        Directory.CreateDirectory(_root);
        InitCommandTestFixture.InitTempWorktree(_root).ShouldBeTrue();
        _paths = TwigPaths.ForContext(Path.Combine(_root, ".twig"), "github.com", "owner/repository", _root);
        var (registry, profiles) = InitCommandTestFixture.CreateSeams(_root);
        _registry = registry;
        _command = new InitCommand(_auth, _http, _paths,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new HintEngine(new DisplayConfig { Hints = false }),
            Substitute.For<IGlobalProfileStore>(), Substitute.For<IConsoleInput>(), null,
            Substitute.For<IManagedWorktreeInitializer>(), registry, profiles);
    }

    [Fact]
    public async Task TrackedGitHubManifest_InitializesRealLayoutAndCacheWithoutAdoOrCredentialCalls()
    {
        var config = new TwigConfiguration
        {
            Organization = "github.com",
            Project = "owner/repository",
            Provider = "github",
            GitHub = new GitHubConfig { Host = "github.com", Repository = "owner/repository" },
        };
        await config.SaveSplitAsync(_paths);
        using (var git = Process.Start(new ProcessStartInfo("git", "add twig.json")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
        }))
        {
            git.ShouldNotBeNull();
            await git.WaitForExitAsync();
            git.ExitCode.ShouldBe(0);
        }
        var manifest = await File.ReadAllBytesAsync(_paths.RepoConfigPath);

        (await _command.ExecuteAsync("github.com", "owner/repository")).ShouldBe(0);

        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(manifest);
        AssertInitializedWithoutBinding();
    }

    [Fact]
    public async Task ExplicitGitHubEntrypoint_ProducesStructuredOfflineInitAndPersistsScope()
    {
        using var services = new ServiceCollection().AddSingleton(_command).BuildServiceProvider();
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            (await new TwigCommands(services).Init(provider: "github", host: "github.com",
                repository: "owner/repository", output: "json")).ShouldBe(0);
        }
        finally { Console.SetOut(previous); }

        using var result = JsonDocument.Parse(output.ToString());
        result.RootElement.GetProperty("provider").GetString().ShouldBe("github");
        result.RootElement.GetProperty("next").GetString().ShouldBe(
            "twig provider discover github --host github.com --repository owner/repository -o json");
        var saved = await TwigConfiguration.LoadSplitAsync(_paths);
        saved.Provider.ShouldBe("github");
        saved.GitHub!.Host.ShouldBe("github.com");
        saved.GitHub.Repository.ShouldBe("owner/repository");
        saved.Organization.ShouldBe("github.com");
        saved.Project.ShouldBe("owner/repository");
        File.ReadAllText(Path.Combine(_root, ".gitignore")).ShouldContain(".twig/");
        AssertInitializedWithoutBinding();
    }

    [Theory]
    [InlineData("github", null, "owner/repository", "provider-unconfigured")]
    [InlineData("github", "github.com", null, "provider-unconfigured")]
    [InlineData("github", "https://github.com", "owner/repository", "provider-unconfigured")]
    [InlineData("github", "GitHub.com", "owner/repository", "provider-unconfigured")]
    [InlineData("github", "github.com", "invalid", "provider-unconfigured")]
    [InlineData("gitlab", "github.com", "owner/repository", "provider-unknown")]
    public async Task InvalidProviderOrScope_RefusesBeforeWritesWithActionableGuidance(
        string provider, string? host, string? repository, string reason)
    {
        using var services = new ServiceCollection().AddSingleton(_command).BuildServiceProvider();
        using var errors = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(errors);
            (await new TwigCommands(services).Init(provider: provider, host: host, repository: repository)).ShouldBe(1);
        }
        finally { Console.SetError(previous); }
        errors.ToString().ShouldContain(reason);
        errors.ToString().ShouldContain("--provider");
        File.Exists(_paths.RepoConfigPath).ShouldBeFalse();
        File.Exists(_paths.DbPath).ShouldBeFalse();
        _auth.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("team")]
    [InlineData("sprint")]
    [InlineData("area")]
    [InlineData("git-project")]
    [InlineData("profile")]
    [InlineData("force")]
    [InlineData("reinitialize")]
    public async Task GitHubInit_RefusesUnsupportedAdoOrDestructiveOptionsWithoutWrites(string option)
    {
        (await _command.ExecuteAsync("github.com", "owner/repository",
            team: option == "team" ? "Team" : null,
            sprint: option == "sprint" ? "@current" : null,
            area: option == "area" ? "Area" : null,
            gitProject: option == "git-project" ? "Other" : null,
            profile: option == "profile" ? "profile" : null,
            force: option == "force", reinitialize: option == "reinitialize",
            provider: "github", host: "github.com", repository: "owner/repository")).ShouldBe(1);
        File.Exists(_paths.RepoConfigPath).ShouldBeFalse();
        File.Exists(_paths.DbPath).ShouldBeFalse();
        _auth.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("gitlab", "provider-unknown")]
    [InlineData("github", "provider-unconfigured")]
    public async Task StoredUnknownOrUnconfiguredProvider_IsNotSilentlyResetToAdo(string provider, string reason)
    {
        await new TwigConfiguration
        {
            Organization = "github.com", Project = "owner/repository", Provider = provider,
        }.SaveSplitAsync(_paths);
        var before = File.ReadAllBytes(_paths.RepoConfigPath);
        using var errors = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(errors);
            (await _command.ExecuteAsync("github.com", "owner/repository")).ShouldBe(1);
        }
        finally { Console.SetError(previous); }
        errors.ToString().ShouldContain(reason);
        File.ReadAllBytes(_paths.RepoConfigPath).ShouldBe(before);
        File.Exists(_paths.DbPath).ShouldBeFalse();
        _auth.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task GitHubInit_RepeatedInvocationRefusesWithoutRewritingLocalWork()
    {
        (await _command.ExecuteAsync("github.com", "owner/repository", provider: "github",
            host: "github.com", repository: "owner/repository")).ShouldBe(0);
        var manifest = File.ReadAllBytes(_paths.RepoConfigPath);
        var cache = File.ReadAllBytes(_paths.DbPath);

        (await _command.ExecuteAsync("github.com", "owner/repository", provider: "github",
            host: "github.com", repository: "owner/other")).ShouldBe(1);

        File.ReadAllBytes(_paths.RepoConfigPath).ShouldBe(manifest);
        File.ReadAllBytes(_paths.DbPath).ShouldBe(cache);
        _auth.ReceivedCalls().ShouldBeEmpty();
    }

    private void AssertInitializedWithoutBinding()
    {
        File.Exists(Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.LayoutFileName)).ShouldBeTrue();
        File.Exists(_paths.DbPath).ShouldBeTrue();
        File.Exists(Path.Combine(_paths.TwigDir, ProviderTargetAttachmentDocument.FileName)).ShouldBeFalse();
        _auth.ReceivedCalls().ShouldBeEmpty();
    }

    public void Dispose()
    {
        _registry.Dispose();
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RefuseHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("GitHub init must not make any HTTP request.");
    }
}
