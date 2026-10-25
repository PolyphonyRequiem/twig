using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class DraftEditingProductionCliTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task RealRetainParserEmitsStructuredUsageAndJsonRefusalsWithoutWrites()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        foreach (var (args, input, code) in new[]
        {
            (new[] { "draft", "retain", "-o", "json" }, "", "usage"),
            (new[] { "draft", "retain", "--stdin", "-o", "json" }, "{invalid json", "invalid-json"),
        })
        {
            var result = await RunAsync(args, input);
            result.Exit.ShouldBe(2, $"stdout={result.Out}\nstderr={result.Error}");
            using var output = JsonDocument.Parse(result.Out);
            output.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
            output.RootElement.GetProperty("status").GetString().ShouldBe("refused");
            output.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
            output.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
            output.RootElement.TryGetProperty("capture", out _).ShouldBeFalse();
            using var error = JsonDocument.Parse(result.Error);
            error.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        }
        (await runtime.GetRequiredService<IPlanJournalRepository>().GetUnresolvedAsync()).ShouldBeEmpty();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RealParserBindsStdinBenchAndOutputAndEmitsStructuredUsageRefusals()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Retained"));
        foreach (var (args, input, code) in new[]
        {
            (new[] { "draft", "stage", "-o", "json" }, "", "usage"),
            (new[] { "draft", "stage", "--stdin", "-o", "json" }, "{invalid json", "invalid-json"),
            (new[] { "draft", "open", "0", "--bench", "default", "-o", "json" }, "", "usage"),
        })
        {
            var result = await RunAsync(args, input);
            result.Exit.ShouldBe(2, $"stdout={result.Out}\nstderr={result.Error}");
            result.Out.ShouldNotBeNullOrWhiteSpace(
                $"The real endpoint must emit its structured envelope. Args={string.Join(' ', args)}; stderr={result.Error}");
            using var output = JsonDocument.Parse(result.Out);
            output.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
            output.RootElement.GetProperty("committed").GetBoolean().ShouldBeFalse();
            output.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
            result.Error.ShouldStartWith("{", customMessage: $"Args={string.Join(' ', args)}; stderr={result.Error}");
            using var formattedError = JsonDocument.Parse(result.Error);
            formattedError.RootElement.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.String);
        }
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBeEmpty();
        (await repository.GetByIdAsync(42))!.Title.ShouldBe("Retained");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    private async Task<(int Exit, string Out, string Error)> RunAsync(string[] args, string input)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _fixture.Paths.StartDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add(typeof(OutputFormatArgumentValidator).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["TWIG_USER_HOME"] = _fixture.Home;
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
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            throw;
        }
    }
}
