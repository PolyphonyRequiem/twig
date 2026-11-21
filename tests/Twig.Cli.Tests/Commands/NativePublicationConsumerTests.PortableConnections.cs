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
    public async Task ExplicitSecondAdoBindAddsCandidateWithoutSwitchingEitherWorkspace()
    {
        await _bindings.RegisterPatIdentityAsync("actor", "fixture", ActorPat);
        await _bindings.RegisterPatIdentityAsync("sibling", "fixture", SiblingPat);
        string[] coordinates = ["--provider", "ado", "--org", "fixture", "--project", "Project"];
        var added = RunPortableConnection(_bindings, _temp, ["connection", "add", .. coordinates, "-o", "json"]);
        added.Exit.ShouldBe(0, added.Error);
        var bound = RunPortableConnection(_bindings, _temp,
            ["connection", "bind", .. coordinates, "--identity", "actor", "-o", "json"]);
        bound.Exit.ShouldBe(0, bound.Error);
        var original = (await _bindings.ListBindingsAsync("fixture", "Project")).ShouldHaveSingleItem();

        const string manifest = """{"organization":"fixture","project":"Project"}""";
        _transport.CoreMetadataEnabled = true;
        var first = await FreshCheckout("first", manifest);
        var initialized = RunPortableConnection(_bindings, first, ["init", "-o", "json"]);
        initialized.Exit.ShouldBe(0, initialized.Error);
        var pin = Path.Combine(first, ".twig", "attachment.json");
        var originalPin = await File.ReadAllBytesAsync(pin);
        var secondBound = RunPortableConnection(_bindings, _temp,
            ["connection", "bind", .. coordinates, "--identity", "sibling", "-o", "json"]);
        secondBound.Exit.ShouldBe(0, secondBound.Error);
        var before = _transport.TotalRequests;
        var listed = RunPortableConnection(_bindings, _temp, ["connection", "list", .. coordinates, "-o", "json"]);
        listed.Exit.ShouldBe(0, listed.Error);
        using var json = JsonDocument.Parse(listed.Output);
        var candidates = json.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("credential").GetString()!, row => row.GetProperty("bindingId").GetString()!);
        candidates.Keys.Order().ShouldBe(["actor", "sibling"]);
        candidates["actor"].ShouldBe(original.BindingId);
        var second = await FreshCheckout("second", manifest);
        foreach (string[] args in new string[][] { ["connection", "status", "-o", "json"], ["init", "-o", "json"] })
        {
            var ambiguous = RunPortableConnection(_bindings, second, args);
            ambiguous.Exit.ShouldBe(1, ambiguous.Error);
            ambiguous.Error.ShouldContain("--binding");
            Directory.Exists(Path.Combine(second, ".twig")).ShouldBeFalse();
        }
        _transport.TotalRequests.ShouldBe(before);

        var explicitInit = RunPortableConnection(_bindings, second, ["init", "--binding", candidates["sibling"], "-o", "json"]);
        explicitInit.Exit.ShouldBe(0, explicitInit.Error);
        using var secondPin = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(second, ".twig", "attachment.json")));
        secondPin.RootElement.GetProperty("bindingPin").GetString().ShouldBe(candidates["sibling"]);
        (await File.ReadAllBytesAsync(pin)).ShouldBe(originalPin);
        before = _transport.TotalRequests;
        foreach (var (root, alias, principal) in new[] { (first, "actor", Actor), (second, "sibling", Sibling) })
        {
            var reopened = RunPortableConnection(_bindings, root, ["connection", "status", "-o", "json"]);
            reopened.Exit.ShouldBe(0, reopened.Error);
            using var status = JsonDocument.Parse(reopened.Output);
            status.RootElement.GetProperty("bindingId").GetString().ShouldBe(candidates[alias]);
            status.RootElement.GetProperty("adoPrincipalId").GetString().ShouldBe(principal);
            var doc = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(root, ".twig", "core-workspace.json")),
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            doc.PrincipalId.ShouldBe(principal);
            (await File.ReadAllTextAsync(Path.Combine(root, "twig.json"))).ShouldBe(manifest);
        }
        _transport.TotalRequests.ShouldBe(before);
    }

    [Fact]
    public async Task ConnectionListIncludesExplicitNativeAdoSetupWithoutPortableAdd()
    {
        await _bindings.RegisterPatIdentityAsync("sibling", "fixture", SiblingPat);
        var setup = new NativeConnectionSetup(Home);
        var bound = await setup.CreateAdoBindingAsync(new("fixture", "Project", "sibling"));
        bound.IsSuccess.ShouldBeTrue(bound.IsSuccess ? "" : bound.Error.Message);
        (await _bindings.ListBindingsAsync("fixture", "Project")).ShouldHaveSingleItem().BindingId.ShouldBe(bound.Value.BindingId);

        var listed = RunPortableConnection(_bindings, _temp, ["connection", "list", "--provider", "ado", "-o", "json"]);

        listed.Exit.ShouldBe(0, listed.Error);
        using var json = JsonDocument.Parse(listed.Output);
        var row = json.RootElement.EnumerateArray().ShouldHaveSingleItem();
        row.GetProperty("scope").GetString().ShouldBe("https://dev.azure.com/fixture");
        row.GetProperty("target").GetString().ShouldBe("project");
        row.GetProperty("credential").GetString().ShouldBe("sibling");
        row.GetProperty("organization").GetString().ShouldBe("fixture");
        row.GetProperty("project").GetString().ShouldBe("Project");
        row.GetProperty("bindingId").GetString().ShouldBe(bound.Value.BindingId);

        var otherOrg = "https://dev.azure.com/fixture";
        var root = await FreshCheckout("raw-mismatch", $$"""{"organization":"{{otherOrg}}","project":"Project"}""");
        var requests = _transport.TotalRequests;
        var refused = RunPortableConnection(_bindings, root, ["init", "-o", "json"]);
        refused.Exit.ShouldBe(1, refused.Error);
        using var result = JsonDocument.Parse(refused.Error);
        var guidance = result.RootElement.GetProperty("error").GetString()!;
        guidance.ShouldContain("connection bind");
        guidance.ShouldContain("exactly as spelled in twig.json");
        guidance.ShouldContain("Project");
        var status = RunPortableConnection(_bindings, root, ["connection", "status", "-o", "json"]);
        status.Exit.ShouldBe(1, status.Error);
        using var statusResult = JsonDocument.Parse(status.Error);
        statusResult.RootElement.GetProperty("error").GetString()!.ShouldContain($"--org \"{otherOrg}\" --project \"Project\"");
        (await _bindings.ListBindingsAsync(otherOrg, "Project")).ShouldBeEmpty();
        Directory.Exists(Path.Combine(root, ".twig")).ShouldBeFalse();
        _transport.TotalRequests.ShouldBe(requests);
    }

    private async Task<string> FreshCheckout(string name, string manifest)
    {
        var root = Path.Combine(_temp, name);
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        await File.WriteAllTextAsync(Path.Combine(root, "twig.json"), manifest);
        return root;
    }

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
