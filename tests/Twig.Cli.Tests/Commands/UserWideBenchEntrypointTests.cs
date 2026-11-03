using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class UserWideBenchEntrypointTests
{
    [Fact]
    public void ActualEntrypointUsesOneBenchAcrossUninitializedRepositoryAndWorktreeFolders()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-user-bench-entrypoint-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(directory, "repository");
        var worktree = Path.Combine(directory, "worktree");
        Directory.CreateDirectory(repository); Directory.CreateDirectory(worktree);
        var bindings = new ConnectionBindingService(Path.Combine(directory, "home"));
        var previousOut = Console.Out; var previousError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            Directory.Exists(Path.Combine(repository, ".twig")).ShouldBeFalse();
            Directory.Exists(Path.Combine(worktree, ".twig")).ShouldBeFalse();
            Run(repository, "bench", "create", "Shared", "-o", "json").ShouldBe(0, error.ToString());
            using var created = JsonDocument.Parse(output.ToString());
            var id = created.RootElement.GetProperty("id").GetString()!;
            output.GetStringBuilder().Clear();
            Run(worktree, "bench", "list", "-o", "json").ShouldBe(0, error.ToString());
            using var listed = JsonDocument.Parse(output.ToString());
            listed.RootElement.EnumerateArray().ShouldContain(row => row.GetProperty("id").GetString() == id);
            output.GetStringBuilder().Clear();
            Run(worktree, "bench", "rename", id, "Renamed", "-o", "json").ShouldBe(0, error.ToString());
            output.GetStringBuilder().Clear();
            Run(repository, "bench", "list", "-o", "json").ShouldBe(0, error.ToString());
            using var renamed = JsonDocument.Parse(output.ToString());
            renamed.RootElement[0].GetProperty("id").GetString().ShouldBe(id);
            renamed.RootElement[0].GetProperty("name").GetString().ShouldBe("Renamed");
            Directory.Exists(Path.Combine(repository, ".twig")).ShouldBeFalse();
            Directory.Exists(Path.Combine(worktree, ".twig")).ShouldBeFalse();
        }
        finally
        {
            Console.SetOut(previousOut); Console.SetError(previousError);
            bindings.Dispose();
            Directory.Delete(directory, recursive: true);
        }

        int Run(string root, params string[] args) => TwigConsoleApp.Run(args, services =>
        {
            services.AddSingleton<IConnectionBindingService>(bindings);
            services.AddTwigWorkspaceServices(root, args, Path.Combine(directory, "display.json"));
        });
    }
}
