using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Collection("ConsoleRedirect")]
public sealed class GovernanceFreeConnectionCommandTests
{
    [Theory]
    [InlineData(null, "digest", "evidence")]
    [InlineData("intent", null, "evidence")]
    [InlineData("intent", "digest", null)]
    [InlineData("intent", "digest", " ")]
    public async Task MissingMechanicalEvidenceIsAUsageError(string? intent, string? digest, string? rationale)
    {
        var service = Substitute.For<IConnectionRemoteWriteReconciliationService>();
        var command = CreateCommand(service);
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);

            var exit = await command.ExecuteAsync(true, intent, digest, rationale, output: "json");

            exit.ShouldBe(2);
            var message = error.ToString();
            message.ShouldContain("--intent, --confirm and --rationale");
            message.ShouldNotContain("--authorize");
            service.ReceivedCalls().ShouldBeEmpty();
        }
        finally { Console.SetError(originalError); }
    }

    [Fact]
    public async Task ExactIntentAndRationaleReachReconciliationWithoutASignerAndUnknownRemainsFailure()
    {
        var service = Substitute.For<IConnectionRemoteWriteReconciliationService>();
        service.ReconcileAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(),
            "intent", "digest", "readback evidence", Arg.Any<CancellationToken>())
            .Returns(new ConnectionRemoteWriteInspection("unknown", "intent", "digest", false,
                "fixture-checkout", "fixture-binding", "fixture-identity",
                new ConnectionRemoteWriteRequest("comment-add", "POST", "https://example.invalid", "{}", null),
                [], null, ["remote-write-attribution-unavailable"], []));
        var command = CreateCommand(service);
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);

            var exit = await command.ExecuteAsync(true, "intent", "digest", "readback evidence", output: "json");

            exit.ShouldBe(1);
            using var json = JsonDocument.Parse(output.ToString());
            json.RootElement.GetProperty("state").GetString().ShouldBe("unknown");
            json.RootElement.GetProperty("blockers").EnumerateArray().ShouldHaveSingleItem()
                .GetString().ShouldBe("remote-write-attribution-unavailable");
            await service.Received(1).ReconcileAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(),
                "intent", "digest", "readback evidence", Arg.Any<CancellationToken>());
        }
        finally { Console.SetOut(originalOutput); }
    }

    private static ConnectionWritesCommand CreateCommand(IConnectionRemoteWriteReconciliationService service)
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-command-contract");
        var paths = new TwigPaths(root, Path.Combine(root, "config"), Path.Combine(root, "twig.db"),
            root, Path.Combine(root, "display.json"));
        return new ConnectionWritesCommand(service, new TwigConfiguration(), paths,
            new OutputFormatterFactory(new HumanOutputFormatter()), new RendererFactory());
    }
}
