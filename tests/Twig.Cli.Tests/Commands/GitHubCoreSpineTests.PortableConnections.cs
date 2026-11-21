using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task PortableGitHubConnectionUsesLocalNamedAccountAndPreservesSharedManifest()
    {
        const string manifest = """{"provider":"github","github":{"host":"github.com","repository":"original/repo"}}""";
        using var first = new Fixture(account: "first-user", actor: "U_first");
        using var second = new Fixture(account: "friend", actor: "U_friend");
        foreach (var (fixture, account, actor) in new[] { (first, "first-user", "U_first"), (second, "friend", "U_friend") })
        {
            Directory.Exists(Path.Combine(fixture.Root, ".twig")).ShouldBeFalse();
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "twig.json"), manifest);
            fixture.Run("connection", "add", "--provider", "github", "--host", "github.com",
                "--repository", "original/repo", "-o", "json").ShouldBe(0, fixture.Error);
            fixture.Run("connection", "bind", "--provider", "github", "--host", "github.com",
                "--repository", "original/repo", "--account", account, "-o", "json").ShouldBe(0, fixture.Error);
            fixture.CredentialCalls.ShouldBe(0, "metadata registration must not acquire credentials");
            fixture.Run("connection", "status", "-o", "json").ShouldBe(0, fixture.Error);
            fixture.CredentialCalls.ShouldBe(0, "readiness must not claim freshly authenticated access");
            fixture.Run("init", "-o", "json").ShouldBe(0, fixture.Error);
            fixture.CredentialCalls.ShouldBe(1);
            var document = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".twig", "core-workspace.json")),
                CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            document.PrincipalId.ShouldBe(actor);
            var requests = fixture.Http.Requests;
            var credentials = fixture.CredentialCalls;
            fixture.Run("connection", "bind", "--provider", "github", "--host", "github.com",
                "--repository", "original/repo", "--account", "another-user", "-o", "json").ShouldBe(0, fixture.Error);
            fixture.Run("connection", "list", "--provider", "github", "-o", "json").ShouldBe(0, fixture.Error);
            using var listed = JsonDocument.Parse(fixture.Output);
            listed.RootElement.EnumerateArray().Select(row => row.GetProperty("credential").GetString())
                .Order(StringComparer.Ordinal).ShouldBe(new[] { account, "another-user" }.Order(StringComparer.Ordinal));
            fixture.Run("connection", "status", "-o", "json").ShouldBe(0, fixture.Error);
            using var status = JsonDocument.Parse(fixture.Output);
            status.RootElement.GetProperty("recordedActor").GetString().ShouldBe(actor);
            fixture.CredentialCalls.ShouldBe(credentials);
            fixture.Http.Requests.ShouldBe(requests);
            // The fixture accepts only the original named gh account, even after a second candidate exists.
            fixture.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, fixture.Error);
            fixture.CredentialCalls.ShouldBeGreaterThan(credentials);
            using (var read = JsonDocument.Parse(fixture.Output))
                read.RootElement.GetProperty("title").GetString().ShouldBe("original");
            (await File.ReadAllTextAsync(Path.Combine(fixture.Root, "twig.json"))).ShouldBe(manifest);
            fixture.Http.Writes.ShouldBe(0);
        }
    }

    [Fact]
    public async Task PortableConnectionInventoryIgnoresBrokenWorkspaceAndIncludesUnboundProviders()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "twig.json"), "{broken manifest");
        fixture.Run("connection", "list", "-o", "json").ShouldBe(0, fixture.Error);
        using (var empty = JsonDocument.Parse(fixture.Output))
            empty.RootElement.GetArrayLength().ShouldBe(0);
        fixture.Run("connection", "add", "--provider", "github", "--host", "github.com",
            "--repository", "original/repo", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Run("connection", "add", "--provider", "twig", "--database", "personal", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Run("connection", "list", "-o", "json").ShouldBe(0, fixture.Error);
        using var listed = JsonDocument.Parse(fixture.Output);
        listed.RootElement.GetArrayLength().ShouldBe(2);
        var github = listed.RootElement.EnumerateArray().Single(row => row.GetProperty("provider").GetString() == "github");
        github.GetProperty("ready").GetBoolean().ShouldBeFalse();
        github.GetProperty("credential").GetString().ShouldBe("");
        var native = listed.RootElement.EnumerateArray().Single(row => row.GetProperty("provider").GetString() == "twig");
        native.GetProperty("ready").GetBoolean().ShouldBeTrue();
        fixture.Run("connection", "list", "--provider", "github", "-o", "json").ShouldBe(0, fixture.Error);
        using (var filtered = JsonDocument.Parse(fixture.Output))
            filtered.RootElement.EnumerateArray().ShouldHaveSingleItem().GetProperty("provider").GetString().ShouldBe("github");
        fixture.CredentialCalls.ShouldBe(0);
        fixture.Http.Requests.ShouldBe(0);
        Directory.Exists(Path.Combine(fixture.Root, ".twig")).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(fixture.Root, "twig.json"))).ShouldBe("{broken manifest");
    }

    [Fact]
    public async Task PortableNativeConnectionRequiresNoCredentialsAndPreservesManifest()
    {
        using var fixture = new Fixture();
        const string manifest = """{"provider":"twig","nativeDatabase":"personal"}""";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "twig.json"), manifest);
        fixture.Run("init", "-o", "json").ShouldNotBe(0);
        fixture.Error.ShouldContain("not registered");
        Directory.Exists(Path.Combine(fixture.Root, ".twig")).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(fixture.Root, "twig.json"))).ShouldBe(manifest);
        fixture.Run("connection", "add", "--provider", "twig", "--database", "personal", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Run("connection", "status", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Run("init", "-o", "json").ShouldBe(0, fixture.Error);
        fixture.Run("connection", "status", "-o", "json").ShouldBe(0, fixture.Error);
        using (var status = JsonDocument.Parse(fixture.Output))
            status.RootElement.GetProperty("provider").GetString().ShouldBe("twig");
        fixture.CredentialCalls.ShouldBe(0);
        fixture.Http.Requests.ShouldBe(0);
        (await File.ReadAllTextAsync(Path.Combine(fixture.Root, "twig.json"))).ShouldBe(manifest);
    }
}
