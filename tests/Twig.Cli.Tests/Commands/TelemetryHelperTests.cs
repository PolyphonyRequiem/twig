using System.Diagnostics;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TelemetryHelperTests
{
    [Fact]
    public void TrackCommand_With_Null_Client_Does_Not_Throw()
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        Should.NotThrow(() =>
            TelemetryHelper.TrackCommand(null, "status", "human", 0, startTimestamp));
    }

    [Fact]
    public void TrackCommand_Emits_Event_With_Correct_Properties()
    {
        var client = Substitute.For<ITelemetryClient>();
        var startTimestamp = Stopwatch.GetTimestamp();

        TelemetryHelper.TrackCommand(client, "status", "json", 0, startTimestamp);

        client.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "status" &&
                p["exit_code"] == "0" &&
                p["output_format"] == "json" &&
                p.ContainsKey("twig_version") &&
                p.ContainsKey("os_platform")),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms")));
    }

    [Fact]
    public void TrackCommand_Emits_Event_With_NonZero_Exit_Code()
    {
        var client = Substitute.For<ITelemetryClient>();
        var startTimestamp = Stopwatch.GetTimestamp();

        TelemetryHelper.TrackCommand(client, "tree", "human", 1, startTimestamp);

        client.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "tree" &&
                p["exit_code"] == "1"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public void TrackCommand_Merges_Extra_Properties()
    {
        var client = Substitute.For<ITelemetryClient>();
        var startTimestamp = Stopwatch.GetTimestamp();
        var extras = new Dictionary<string, string>
        {
            ["hash_changed"] = "true",
        };

        TelemetryHelper.TrackCommand(client, "refresh", "human", 0, startTimestamp,
            extraProperties: extras);

        client.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "refresh" &&
                p["hash_changed"] == "true"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public void TrackCommand_Merges_Extra_Metrics()
    {
        var client = Substitute.For<ITelemetryClient>();
        var startTimestamp = Stopwatch.GetTimestamp();
        var extraMetrics = new Dictionary<string, double>
        {
            ["item_count"] = 42.0,
        };

        TelemetryHelper.TrackCommand(client, "refresh", "human", 0, startTimestamp,
            extraMetrics: extraMetrics);

        client.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Any<Dictionary<string, string>>(),
            Arg.Is<Dictionary<string, double>>(m =>
                m["duration_ms"] >= 0 &&
                m["item_count"] == 42.0));
    }

    [Fact]
    public void TrackCommand_Duration_Is_Non_Negative()
    {
        var client = Substitute.For<ITelemetryClient>();
        var startTimestamp = Stopwatch.GetTimestamp();

        TelemetryHelper.TrackCommand(client, "status", "human", 0, startTimestamp);

        client.Received(1).TrackEvent(
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Is<Dictionary<string, double>>(m => m["duration_ms"] >= 0));
    }
}
