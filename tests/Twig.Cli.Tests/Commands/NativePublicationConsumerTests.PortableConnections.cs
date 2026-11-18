using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task PortableConnectionUsesEachMachinesOwnPrincipalWithoutChangingManifest()
    {
        const string manifest = """{"organization":"fixture","project":"Project","team":"Shared team"}""";
        _transport.CoreMetadataEnabled = true;
        _transport.SetItem(42, "First user's private item", 1, Actor);
        _transport.SetItem(44, "Second user's private item", 1, Sibling);
        using var second = new ConnectionBindingService(Path.Combine(_temp, "second-user-home"), patHttpClient: _http);
        var principals = new List<string>();
        try
        {
            foreach (var (bindings, alias, pat, item, principal) in new[]
            {
                (_bindings, "first-user", ActorPat, "42", Actor),
                (second, "friend", SiblingPat, "44", Sibling),
            })
            {
                var root = Path.Combine(_temp, alias);
                Directory.CreateDirectory(root);
                InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
                var manifestPath = Path.Combine(root, "twig.json");
                await File.WriteAllTextAsync(manifestPath, manifest);
                Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
                (await bindings.ListIdentitiesAsync()).ShouldBeEmpty("each device starts without another user's credentials");
                await bindings.RegisterPatIdentityAsync(alias, "fixture", pat);
                var otherDirectory = Path.Combine(_temp, alias + "-outside-workspace");
                Directory.CreateDirectory(otherDirectory);
                RunPortableConnection(bindings, otherDirectory,
                    ["connection", "add", "--provider", "ado", "--org", "https://dev.azure.com/FIXTURE/", "--project", "PROJECT", "-o", "json"])
                    .Exit.ShouldBe(0);
                RunPortableConnection(bindings, otherDirectory,
                    ["connection", "bind", "--provider", "ado", "--org", "fixture", "--project", "Project",
                        "--identity", alias, "-o", "json"]).Exit.ShouldBe(0);
                var listed = RunPortableConnection(bindings, otherDirectory, ["connection", "list", "-o", "json"]);
                listed.Exit.ShouldBe(0, listed.Error);
                using (var json = JsonDocument.Parse(listed.Output))
                {
                    var connection = json.RootElement.EnumerateArray().ShouldHaveSingleItem();
                    connection.GetProperty("provider").GetString().ShouldBe("ado");
                    connection.GetProperty("target").GetString().ShouldBe("project");
                    connection.GetProperty("credential").GetString().ShouldBe(alias);
                }
                var initialized = RunPortableConnection(bindings, root, ["init", "-o", "json"]);
                initialized.Exit.ShouldBe(0, initialized.Error);
                (await File.ReadAllTextAsync(manifestPath)).ShouldBe(manifest);
                var document = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(root, ".twig", "core-workspace.json")),
                    CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
                document.PrincipalId.ShouldBe(principal);
                principals.Add(document.Connection.IdentityRef);
                var read = RunPortableConnection(bindings, root, ["show", item, "--refresh", "-o", "json"]);
                read.Exit.ShouldBe(0, read.Error);
                using (var json = JsonDocument.Parse(read.Output))
                    json.RootElement.GetProperty("title").GetString().ShouldBe(
                        principal == Actor ? "First user's private item" : "Second user's private item");
                var denied = RunPortableConnection(bindings, root,
                    ["show", principal == Actor ? "44" : "42", "--refresh", "-o", "json"]);
                denied.Exit.ShouldNotBe(0, "another user's binding must never be used to retry denied access");
                (await File.ReadAllTextAsync(manifestPath)).ShouldBe(manifest);
            }
            principals.Distinct().Count().ShouldBe(2, "provider access remains principal-specific even though the manifest is identical");
            _transport.CommittedWrites.ShouldBe(0);
        }
        finally
        {
            second.Dispose();
            using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = second.RegistryPath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30,
            }.ToString());
            SqliteConnection.ClearPool(pool);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableConnectionMissingOrUnboundRefusesBeforeWorkspaceCreation(bool registered)
    {
        var root = Path.Combine(_temp, "missing-connection");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        const string manifest = """{"organization":"fixture","project":"Project"}""";
        await File.WriteAllTextAsync(Path.Combine(root, "twig.json"), manifest);
        if (registered)
            RunPortableConnection(_bindings, root,
                ["connection", "add", "--provider", "ado", "--org", "fixture", "--project", "Project", "-o", "json"]).Exit.ShouldBe(0);
        var requests = _transport.TotalRequests;
        var result = RunPortableConnection(_bindings, root, ["init", "-o", "json"]);
        result.Exit.ShouldNotBe(0);
        result.Error.ShouldContain(registered ? "credential binding" : "not registered");
        Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
        _transport.TotalRequests.ShouldBe(requests);
        (await File.ReadAllTextAsync(Path.Combine(root, "twig.json"))).ShouldBe(manifest);
    }

    private (int Exit, string Output, string Error) RunPortableConnection(IConnectionBindingService bindings,
        string directory, string[] arguments)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = TwigConsoleApp.Run(arguments, services =>
            {
                services.AddSingleton(bindings);
                services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                services.AddTwigWorkspaceServices(directory, arguments, Path.Combine(_temp, "display.json"));
            });
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
}
