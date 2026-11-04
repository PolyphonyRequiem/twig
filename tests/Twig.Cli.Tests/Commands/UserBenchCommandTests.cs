using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class UserBenchCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "twig-user-bench-cli-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionBindingService bindings;
    private readonly string repository;
    private readonly string worktree;
    private string output = "", error = "";

    public UserBenchCommandTests()
    {
        repository = Path.Combine(directory, "repository");
        worktree = Path.Combine(directory, "worktree");
        Directory.CreateDirectory(repository); Directory.CreateDirectory(worktree);
        bindings = new(Path.Combine(directory, "home"));
    }

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    [InlineData("minimal")]
    public async Task MetadataCommandsShareNativeDefinitionsOutsideInitializedWorkspaces(string format)
    {
        File.Exists(Path.Combine(repository, ".twig", CoreWorkspaceFactory.FileName)).ShouldBeFalse();
        Run(repository, "bench", "create", "Release", "-o", format).ShouldBe(0, error);
        var saved = (await UserBenchStore.ForRegistry(bindings.RegistryPath).ReadAsync()).Value.Benches.Single();
        Run(worktree, "bench", "list", "-o", format).ShouldBe(0, error);
        output.ShouldContain("Release");
        Run(worktree, "bench", "rename", saved.Id.ToString("D"), "Delivery", "-o", format).ShouldBe(0, error);
        Run(repository, "bench", "list", "-o", format).ShouldBe(0, error);
        output.ShouldContain("Delivery");
        Run(repository, "bench", "delete", "Delivery", "-o", format).ShouldBe(0, error);
        (await UserBenchStore.ForRegistry(bindings.RegistryPath).ReadAsync()).Value.Benches.ShouldBeEmpty();
        Directory.Exists(Path.Combine(repository, ".twig")).ShouldBeFalse();
        Directory.Exists(Path.Combine(worktree, ".twig")).ShouldBeFalse();
    }

    [Fact]
    public async Task ManagementCaptureAndExplicitConfirmationGuardTheSharedDefinition()
    {
        var store = UserBenchStore.ForRegistry(bindings.RegistryPath);
        var connection = new Connection(new("ado"), "collection", "project", "identity");
        var created = (await store.SaveAsync(new(Guid.NewGuid(), "Pinned",
            [new(SelectorKind.Subtree, ItemKey.Remote(connection.Provider, connection.ResourceScope, "41"),
                hierarchyRelation: "native.parent-child", connection: connection)]), 0)).Value;
        Run(repository, "bench", "list", "--include-management", "-o", "json").ShouldBe(0, error);
        using var capture = JsonDocument.Parse(output);
        capture.RootElement.GetProperty("version").GetInt32().ShouldBe(3);
        var digest = capture.RootElement.GetProperty("benches")[0].GetProperty("contentsDigest").GetString()!;
        Run(worktree, "bench", "delete", "Pinned", "-o", "json").ShouldBe(1);
        using (var refusal = JsonDocument.Parse(error)) refusal.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("--confirm");
        Run(worktree, "bench", "rename", "Pinned", "Renamed", "--expect-bench", created.Id.ToString("D"),
            "--expect-contents", digest, "-o", "json").ShouldBe(0, error);
        Run(repository, "bench", "delete", "Renamed", "--confirm", "Renamed", "--expect-contents", digest, "-o", "json").ShouldBe(1);
        (await store.ReadAsync()).Value.Benches.Single().Name.ShouldBe("Renamed");
        Run(worktree, "bench", "delete", "Renamed", "--confirm", "Renamed", "-o", "json").ShouldBe(0, error);
        (await store.ReadAsync()).Value.Benches.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidInputAndDuplicateNamesRemainStructuredAndIdsContainOnlyGuids()
    {
        Run(repository, "bench", "create", "Chosen", "-o", "json").ShouldBe(0, error);
        Run(worktree, "bench", "create", "cHOSEN", "-o", "json").ShouldBe(1);
        using (var duplicate = JsonDocument.Parse(error)) duplicate.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        Run(repository, "bench", "rename", "missing", "Another", "-o", "json").ShouldBe(1);
        Run(repository, "bench", "list", "--include-management", "-o", "minimal").ShouldBe(2);
        Run(worktree, "bench", "list", "-o", "ids").ShouldBe(0, error);
        Guid.TryParseExact(output.Trim(), "D", out _).ShouldBeTrue();
        Run(repository, "bench", "rename", "--help").ShouldBe(0, error);
        output.ShouldContain("New unique Bench name");
    }

    private int Run(string root, params string[] args)
    {
        var previousOut = Console.Out; var previousError = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            return TwigConsoleApp.Run(args, services =>
            {
                services.AddSingleton<IConnectionBindingService>(bindings);
                services.AddTwigWorkspaceServices(root, args, Path.Combine(directory, "display.json"));
            });
        }
        finally
        {
            output = stdout.ToString(); error = stderr.ToString();
            Console.SetOut(previousOut); Console.SetError(previousError);
        }
    }

    public void Dispose()
    {
        bindings.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
