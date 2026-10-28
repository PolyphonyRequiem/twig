using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class StructuredReadProductionCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-read-isolation-{Guid.NewGuid():N}");

    public StructuredReadProductionCliTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("show")]
    [InlineData("tree")]
    [InlineData("states")]
    public async Task ImplicitStructuredReadRefusesBeforeWorkspaceAdmission(string command)
    {
        var twig = Path.Combine(_root, ".twig");
        Directory.CreateDirectory(twig);
        var config = Path.Combine(twig, "config");
        await File.WriteAllTextAsync(config, "invalid workspace configuration");

        var result = await RunAsync(command, "-o", "json");

        result.Exit.ShouldBe(2, result.Error);
        result.Out.ShouldBeEmpty();
        using var error = JsonDocument.Parse(result.Error);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain(
            command == "states" ? "twig process <type>" : "explicit work-item ID");
        (await File.ReadAllTextAsync(config)).ShouldBe("invalid workspace configuration");
        Directory.EnumerateFileSystemEntries(twig).ShouldBe([config]);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("tree")]
    public async Task ConflictingExplicitTargetsRefuseBeforeWorkspaceAdmission(string command)
    {
        var result = await RunAsync(command, "41", "--id", "42", "-o", "json");

        result.Exit.ShouldBe(2, result.Error);
        result.Out.ShouldBeEmpty();
        using var error = JsonDocument.Parse(result.Error);
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("Conflicting");
    }

    private Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
        => RunInWorkspaceAsync(_root, Path.Combine(_root, "home"), args);

    internal static async Task<(int Exit, string Out, string Error)> RunInWorkspaceAsync(string root, string home, params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(host) ? "dotnet" : host)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(OutputFormatArgumentValidator).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["TWIG_USER_HOME"] = home;
        start.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:1";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        start.Environment["ALL_PROXY"] = "http://127.0.0.1:1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The built CLI did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
