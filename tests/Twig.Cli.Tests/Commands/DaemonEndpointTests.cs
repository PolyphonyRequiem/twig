using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class DaemonEndpointTests
{
    private static string Endpoint(string daemonId = "null", string origin = "https://localhost:7341",
        string? certificate = null, string? selection = null) =>
        $$"""{"origin":"{{origin}}","certificateThumbprint":"{{certificate ?? new string('A', 40)}}","selectionId":"{{selection ?? new string('B', 64)}}","daemonId":{{daemonId}}}""";

    [Theory]
    [InlineData("null")]
    [InlineData("\"550e8400-e29b-41d4-a716-446655440000\"")]
    public void AcceptsStoppedOrStartedIdentityAndOptionalResponseMembers(string daemonId)
    {
        var response = Endpoint(daemonId);
        response = response[..^1] + ",\"future\":true}";
        DaemonLifecycle.ValidateEndpoint(response).ShouldBe("https://localhost:7341");
    }

    [Theory]
    [InlineData("http://localhost:7341")]
    [InlineData("https://example.com:7341")]
    [InlineData("https://localhost:443")]
    [InlineData("https://localhost:0")]
    [InlineData("https://localhost:7341/path")]
    [InlineData("https://user@localhost:7341")]
    public void RefusesNoncanonicalOrNonlocalOrigins(string origin) =>
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(origin: origin)));

    [Theory]
    [InlineData("\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"invalid\"")]
    [InlineData("false")]
    public void RefusesInvalidDurableIdentity(string daemonId) =>
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(daemonId)));

    [Fact]
    public void RefusesMissingOrMalformedBindingAndOversizeOutput()
    {
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint("{}"));
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(certificate: "invalid")));
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint(selection: "invalid")));
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(Endpoint() + new string(' ', 4096)));
        Should.Throw<JsonException>(() => DaemonLifecycle.ValidateEndpoint("not json"));
    }

    [Fact]
    public async Task BoundsRawOperatorOutputBeforeTrimmingOrParsing()
    {
        using var valid = new StringReader(Endpoint() + "\r\n");
        (await DaemonLifecycle.ReadEndpointOutputAsync(valid)).ShouldBe(Endpoint());
        using var padded = new StringReader(Endpoint() + new string(' ', 4096));
        await Should.ThrowAsync<InvalidDataException>(() => DaemonLifecycle.ReadEndpointOutputAsync(padded));
        using var unicode = new StringReader(new string('\u00e9', 2049));
        await Should.ThrowAsync<InvalidDataException>(() => DaemonLifecycle.ReadEndpointOutputAsync(unicode));
    }

    [Fact]
    public void OutputLimitIncludesCliLineEnding()
    {
        var response = Endpoint()[..^1] + ",\"future\":\"\"}";
        var padding = 4096 - System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine)
            - System.Text.Encoding.UTF8.GetByteCount(response);
        response = response.Insert(response.Length - 2, new string('x', padding));
        DaemonLifecycle.ValidateEndpoint(response).ShouldBe("https://localhost:7341");
        Should.Throw<InvalidDataException>(() => DaemonLifecycle.ValidateEndpoint(response + " "));
    }

    [Fact]
    public async Task EndpointDispatchUsesSelectedOperatorWithoutLifecycleOrWorkspaceFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "twig-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT", root);
            Console.SetOut(output);
            Console.SetError(error);
            var launcher = Path.Combine(root, "daemon-launch.ps1");
            File.WriteAllText(launcher, "param([switch]$Endpoint)\n"
                + "if (-not $Endpoint) { throw 'Unexpected lifecycle action' }\n"
                + "Write-Output '" + Endpoint() + "'\n");
            (await DaemonLifecycle.RunAsync(["server", "endpoint", "-o", "json"])).ShouldBe(0, error.ToString());
            output.ToString().Trim().ShouldBe(Endpoint());
            error.ToString().ShouldBeEmpty();
            output.GetStringBuilder().Clear();
            File.WriteAllText(launcher, "Write-Output '{\"running\":false}'\n");
            (await DaemonLifecycle.RunAsync(["server", "endpoint", "-o", "json"])).ShouldBe(1);
            output.ToString().ShouldBeEmpty();
            using var failure = JsonDocument.Parse(error.ToString());
            failure.RootElement.GetProperty("error").GetString()!.ShouldContain("invalid endpoint binding");
            Directory.GetFileSystemEntries(root).ShouldHaveSingleItem();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("TWIG_DAEMON_INSTALL_ROOT", previousRoot);
            Directory.Delete(root, recursive: true);
        }
    }
}
