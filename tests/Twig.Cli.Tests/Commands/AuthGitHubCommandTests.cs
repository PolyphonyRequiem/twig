using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class AuthGitHubCommandTests : IDisposable
{
    private const string AppId = "Iv1syntheticclient";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "twig-app-command-" + Guid.NewGuid().ToString("N"));
    private readonly OutputFormatterFactory _formatters = new(new HumanOutputFormatter());

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    [InlineData("minimal")]
    public async Task LoginStatusLogout_AreExplicitTokenFreeAndLocalStatusDoesNotRefresh(string format)
    {
        var wire = new Wire();
        var config = new TwigConfiguration { GitHubAppClientId = AppId, GitHubOAuthIdentity = "personal" };
        var command = Command(config, wire);
        var (login, output) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync("login", null, null, format, CancellationToken.None));
        login.ShouldBe(0);
        output.ShouldContain("signed-in");
        output.ShouldNotContain("ghu_");
        output.ShouldNotContain("ghr_");
        if (format == "json")
        {
            using var json = JsonDocument.Parse(output);
            json.RootElement.GetProperty("state").GetString().ShouldBe("signed-in");
        }
        wire.Calls.ShouldBe(3);

        var (status, statusOutput) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync("status", null, null, format, CancellationToken.None));
        status.ShouldBe(0);
        statusOutput.ShouldContain("U_fixture");
        wire.Calls.ShouldBe(3);

        var (logout, logoutOutput) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync("logout", null, null, format, CancellationToken.None));
        logout.ShouldBe(0);
        logoutOutput.ShouldContain("NOT revoked");
        wire.Calls.ShouldBe(3);
        (await StdoutCapture.RunAsync(() => command.ExecuteAsync("status", null, null, format, CancellationToken.None)))
            .stdout.ShouldContain("signed-out");
    }

    [Fact]
    public async Task MissingSelection_RefusesBeforeTransportOrStorage()
    {
        var wire = new Wire();
        var command = Command(new TwigConfiguration(), wire);
        using var errors = new StringWriter();
        var (exit, _) = await StdoutCapture.RunAsync(async () =>
        {
            var previous = Console.Error;
            try
            {
                Console.SetError(errors);
                return await command.ExecuteAsync("login", null, null, "json", CancellationToken.None);
            }
            finally { Console.SetError(previous); }
        });
        exit.ShouldBe(2);
        using var error = JsonDocument.Parse(errors.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("configure the registered GitHub App");
        wire.Calls.ShouldBe(0);
        Directory.Exists(_directory).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelledLoginHasNoTransportOrStorage()
    {
        var wire = new Wire();
        var command = Command(new TwigConfiguration(), wire);
        var (exit, _) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync("login", "personal", AppId, "minimal", new CancellationToken(true)));
        exit.ShouldBe(1);
        wire.Calls.ShouldBe(0);
        Directory.Exists(_directory).ShouldBeFalse();
    }

    [Fact]
    public async Task ProviderRouting_UsesExplicitGitHubCommandAndRejectsIncompatibleOptions()
    {
        var config = new TwigConfiguration { GitHubAppClientId = AppId, GitHubOAuthIdentity = "personal" };
        var wire = new Wire();
        var services = new ServiceCollection();
        services.AddSingleton(_formatters);
        services.AddSingleton(Command(config, wire));
        using var provider = services.BuildServiceProvider();
        var commands = new TwigCommands(provider);
        var (status, output) = await StdoutCapture.RunAsync(
            () => commands.AuthStatus(output: "json", provider: "github"));
        status.ShouldBe(0);
        output.ShouldContain("signed-out");
        (await StdoutCapture.RunAsync(() => commands.AuthLogout(provider: "github", output: "json")))
            .result.ShouldBe(0);
        (await StdoutCapture.RunAsync(() => commands.AuthLogin(provider: "github", tenant: "organizations")))
            .result.ShouldBe(2);
        (await StdoutCapture.RunAsync(() => commands.AuthStatus(provider: "ado", clientId: AppId)))
            .result.ShouldBe(2);
        (await StdoutCapture.RunAsync(() => commands.AuthLogout()))
            .result.ShouldBe(2);
        wire.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("github.credentialsource", "github-app")]
    [InlineData("github.appclientid", AppId)]
    [InlineData("github.oauthidentity", "personal")]
    public async Task Configuration_RoundTripsUserOnlyWithoutAcquiringCredentials(string key, string value)
    {
        Directory.CreateDirectory(_directory);
        var twig = Path.Combine(_directory, ".twig");
        Directory.CreateDirectory(twig);
        var paths = new TwigPaths(twig, Path.Combine(twig, "config"), Path.Combine(twig, "db"), _directory);
        var config = new TwigConfiguration();
        var command = new ConfigCommand(config, paths, _formatters);
        var (exit, _) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(key, value, "json"));
        exit.ShouldBe(0);
        var (read, output) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(key, outputFormat: "json"));
        read.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("value").GetString().ShouldBe(value);
        (await File.ReadAllTextAsync(paths.RepoConfigPath)).ShouldNotContain(value);
        (await TwigConfiguration.LoadSplitAsync(paths)).GetValue(key).Value.ShouldBe(value);
    }

    private AuthGitHubCommand Command(TwigConfiguration config, Wire wire) => new(
        config, _formatters, createAuthentication: (client, alias) => new(
            new(_directory, client, alias),
            () => new(new Forwarder(wire), TimeProvider.System, (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }), TimeProvider.System));

    private sealed class Forwarder(Wire wire) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            wire.Send(request);
    }

    private sealed class Wire
    {
        internal int Calls { get; private set; }
        internal Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            Calls++;
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/login/device/code" => """{"device_code":"fixture_device","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""",
                "/login/oauth/access_token" => """{"access_token":"ghu_fixture_access","refresh_token":"ghr_fixture_refresh","expires_in":28800,"refresh_token_expires_in":15897600,"token_type":"bearer","scope":""}""",
                "/user" => """{"node_id":"U_fixture","login":"fixture-login"}""",
                _ => throw new InvalidOperationException("Unexpected fake request"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
