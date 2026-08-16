using System.Diagnostics;
using System.Net;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#216: <c>--org</c>/<c>--project</c> overrides on the read-only process introspection
/// commands, exercised through the REAL production CLI.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>These MUST run the built binary, not the command classes.</b> The defect this card
/// fixes lived entirely at the CLI parse layer — <c>twig process --org X --project Y</c>
/// answered <c>Argument '--org' is not recognized.</c> while every unit test over
/// <c>ProcessCommand</c> was green, because those tests call the class directly and skip the
/// source-generated argument binder. A test that constructs <c>ProcessCommand</c> cannot
/// observe this defect at all, in either direction.
/// </para>
/// <para>
/// Modelled on <see cref="InitCommandProductionCliTests"/>, which pins the same property for
/// <c>init --org/--project</c> — deliberately, since AB#398 regressed those exact flags by
/// converting named options into positionals, and this card touches the same vocabulary on a
/// different command.
/// </para>
/// <para>
/// The ADO coordinates point at a local stub server, so nothing here talks to a real
/// organization and no live board is probed.
/// </para>
/// </remarks>
public sealed class ProcessOverrideProductionCliTests : IDisposable
{
    private readonly string _scratchRoot =
        Path.Combine(Path.GetTempPath(), $"twig-process-override-{Guid.NewGuid():N}");

    public ProcessOverrideProductionCliTests() => Directory.CreateDirectory(_scratchRoot);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_scratchRoot))
            Directory.Delete(_scratchRoot, recursive: true);
    }

    /// <summary>
    /// Acceptance 1: the overrides are ACCEPTED BY THE PARSER with no workspace present.
    /// </summary>
    /// <remarks>
    /// 🔴 The assertion is deliberately about the parse layer, not about the rendered process.
    /// "Argument '--org' is not recognized" is the pre-fix behaviour and the only thing this
    /// arm is here to exclude; anything downstream of it (a stub's response, an auth error, a
    /// not-found) means the argument surface accepted the flags, which IS the property under
    /// test. Asserting on rendered types instead would make the test a stub-fidelity test.
    /// </remarks>
    [Theory]
    [InlineData("process")]
    [InlineData("process layout")]
    public async Task Overrides_AreAcceptedByTheParser_WithNoWorkspace(string command)
    {
        await using var ado = ProcessAdoStub.Start();

        var args = command == "process"
            ? new[] { "process", "--org", ado.BaseUrl, "--project", "StubProject" }
            : ["process", "layout", "Bug", "--org", ado.BaseUrl, "--project", "StubProject"];

        var (_, stdout, stderr) = await RunTwigAsync(args);

        var combined = stdout + stderr;
        combined.ShouldNotContain("is not recognized", Case.Insensitive);
        combined.ShouldNotContain("No twig workspace found");
    }

    /// <summary>
    /// Acceptance 2: an override invocation writes NO cache, config, or workspace state.
    /// </summary>
    /// <remarks>
    /// 🔴 Asserted against the FILESYSTEM rather than against a success line, because a
    /// success line is exactly what a command that wrote a workspace would also print. The
    /// working directory is snapshotted whole so a stray <c>.twig/</c>, a <c>twig.json</c>, or
    /// a database anywhere beneath it fails the arm rather than only the paths this test
    /// thought to name.
    /// </remarks>
    [Fact]
    public async Task Override_WritesNothingToTheFilesystem()
    {
        await using var ado = ProcessAdoStub.Start();

        SnapshotTree().ShouldBeEmpty("precondition: the scratch directory starts empty");

        await RunTwigAsync("process", "--org", ado.BaseUrl, "--project", "StubProject");

        SnapshotTree().ShouldBeEmpty(
            "an --org/--project invocation is read-only and ephemeral (AB#216 acceptance 2)");
    }

    /// <summary>
    /// Acceptance 4: flag-vs-manifest precedence follows <c>InitCommand</c>'s
    /// manifest-is-authoritative rule.
    /// </summary>
    /// <remarks>
    /// Asserts the manifest's value appears in the refusal, not merely that a refusal
    /// happened: a message naming only the flag would satisfy a bare "did it fail" check while
    /// leaving the user unable to see what they conflicted with.
    /// </remarks>
    [Theory]
    [InlineData("--org", "ConflictingOrg", "--project", "ManifestProject")]
    [InlineData("--org", "ManifestOrg", "--project", "ConflictingProject")]
    public async Task ConflictingOverride_IsRefused_BecauseTheManifestIsAuthoritative(
        string flagA, string valueA, string flagB, string valueB)
    {
        await WriteManifestAsync("ManifestOrg", "ManifestProject");

        var (exitCode, _, stderr) = await RunTwigAsync("process", flagA, valueA, flagB, valueB);

        exitCode.ShouldBe(1);
        stderr.ShouldContain("The manifest is authoritative");

        // The refusal must name the MANIFEST's value, so the user can see the conflict.
        var conflicting = valueA == "ConflictingOrg" ? "ManifestOrg" : "ManifestProject";
        stderr.ShouldContain(conflicting);
    }

    /// <summary>
    /// Half an override cannot address a process, so it is refused rather than silently
    /// completed from the workspace's other coordinate.
    /// </summary>
    /// <remarks>
    /// 🔴 The <c>ShouldNotContain</c> is the load-bearing line, not decoration. Two guards
    /// live on this path — the half-override guard and the manifest-conflict guard — and both
    /// refuse with exit 1. A test asserting only "it was refused" passes against a version
    /// where one guard's message has been swapped for the other's, i.e. against a guard that
    /// has stopped telling the user which mistake they made. Asserting each guard's DISTINCT
    /// wording is what makes them separable. Proven by mutation
    /// (<c>tools/ab216-mutants.sh</c> M6).
    /// </remarks>
    [Theory]
    [InlineData("--org", "SomeOrg", "--project")]
    [InlineData("--project", "SomeProject", "--org")]
    public async Task HalfAnOverride_IsRefused_NamingTheMissingFlag(
        string suppliedFlag, string suppliedValue, string missingFlag)
    {
        var (exitCode, _, stderr) = await RunTwigAsync("process", suppliedFlag, suppliedValue);

        exitCode.ShouldBe(1);
        stderr.ShouldContain(suppliedFlag);
        stderr.ShouldContain(missingFlag);
        stderr.ShouldNotContain("The manifest is authoritative");
    }

    /// <summary>
    /// The flags are ADDED beside the existing surface, never converted — so every pre-existing
    /// spelling still parses.
    /// </summary>
    /// <remarks>
    /// 🔴 This arm exists because AB#398 regressed <c>init --org/--project</c> and
    /// <c>edit --field</c> by turning named options into positionals instead of adding
    /// alongside them. Same vocabulary, adjacent command; the mistake is one edit away.
    /// </remarks>
    [Theory]
    [InlineData("process")]
    [InlineData("process", "Bug")]
    [InlineData("process", "-o", "json")]
    [InlineData("process", "layout", "Bug")]
    public async Task PreExistingSpellings_StillParse(params string[] args)
    {
        var (_, stdout, stderr) = await RunTwigAsync(args);

        (stdout + stderr).ShouldNotContain("is not recognized", Case.Insensitive);
    }

    private IReadOnlyList<string> SnapshotTree() =>
        Directory.EnumerateFileSystemEntries(_scratchRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();

    private async Task WriteManifestAsync(string org, string project)
    {
        var json = $$"""
            {
              "organization": "{{org}}",
              "project": "{{project}}"
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_scratchRoot, "twig.json"), json);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunTwigAsync(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var twigAssembly = Path.Combine(
            repositoryRoot, "src", "Twig", "bin", configuration, "net11.0", "twig.dll");
        File.Exists(twigAssembly).ShouldBeTrue($"Twig CLI assembly not found at {twigAssembly}");

        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
            dotnetHost = "dotnet";

        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = _scratchRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(twigAssembly);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["TWIG_PAT"] = "test-pat";
        const string blockedProxy = "http://127.0.0.1:1";
        const string loopbackNoProxy = "127.0.0.1,localhost";
        startInfo.Environment["HTTP_PROXY"] = blockedProxy;
        startInfo.Environment["http_proxy"] = blockedProxy;
        startInfo.Environment["HTTPS_PROXY"] = blockedProxy;
        startInfo.Environment["https_proxy"] = blockedProxy;
        startInfo.Environment["NO_PROXY"] = loopbackNoProxy;
        startInfo.Environment["no_proxy"] = loopbackNoProxy;
        startInfo.Environment.Remove("ALL_PROXY");
        startInfo.Environment.Remove("all_proxy");
        startInfo.Environment.Remove("TWIG_TELEMETRY_ENDPOINT");

        using var process = System.Diagnostics.Process.Start(startInfo);
        process.ShouldNotBeNull();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>
/// A minimal ADO stub, so an override invocation has somewhere to point that is not a real
/// organization.
/// </summary>
/// <remarks>
/// Answers 404 to everything by design. These tests assert on the PARSE layer and on the
/// filesystem, so what the server returns is irrelevant — what matters is that the coordinates
/// resolve to a loopback address rather than to anyone's live board.
/// </remarks>
internal sealed class ProcessAdoStub : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string BaseUrl { get; }

    private ProcessAdoStub(HttpListener listener, string baseUrl)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public static ProcessAdoStub Start()
    {
        for (var port = 5300; port < 5400; port++)
        {
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return new ProcessAdoStub(listener, prefix.TrimEnd('/'));
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("No free loopback port for the ADO stub.");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            context.Response.StatusCode = 404;
            var body = Encoding.UTF8.GetBytes("{}");
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Close();
        try { await _loop; } catch (Exception) { /* shutdown race */ }
        _cts.Dispose();
    }
}
