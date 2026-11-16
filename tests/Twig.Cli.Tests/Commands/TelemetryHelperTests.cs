using System.Diagnostics;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Infrastructure.Telemetry;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class TelemetryHelperTests
{
    [Fact]
    public void HostHookReceivesOnlySafeFixedKeysWithoutActivityOrIdentifierExtras()
    {
        var client = Substitute.For<ITelemetryClient>();
        Dictionary<string, string>? properties = null;
        Dictionary<string, double>? metrics = null;
        client.When(value => value.TrackEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, double>>())).Do(call =>
            {
                properties = call.ArgAt<Dictionary<string, string>>(1);
                metrics = call.ArgAt<Dictionary<string, double>>(2);
            });
        using var activity = new Activity("private-work-123").Start();
        TelemetryHelper.TrackCommand(client, "show", "json", 0, Stopwatch.GetTimestamp());

        properties.ShouldNotBeNull().Keys.ShouldBe(
            ["command", "exit_code", "output_format", "twig_version", "os_platform"], ignoreOrder: true);
        metrics.ShouldNotBeNull().Keys.ShouldBe(["duration_ms"]);
        properties.Values.ShouldNotContain(activity.Id!);
        properties.Values.ShouldNotContain("private-work-123");
    }

    [Fact]
    public void HostFailureCannotChangeCommandOutcome()
    {
        var client = Substitute.For<ITelemetryClient>();
        client.When(value => value.TrackEvent(Arg.Any<string>(), Arg.Any<Dictionary<string, string>>(),
            Arg.Any<Dictionary<string, double>>())).Do(_ => throw new InvalidOperationException("hook failed"));
        Should.NotThrow(() => TelemetryHelper.TrackCommand(client, "show", "json", 0, Stopwatch.GetTimestamp()));
        Should.NotThrow(() => TelemetryHelper.TrackCommand(null, "show", "json", 0, Stopwatch.GetTimestamp()));
    }
}
