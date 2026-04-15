using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Telemetry;
using Xunit;

namespace Twig.Infrastructure.Tests.Telemetry;

public class TelemetryClientTests
{
    /// <summary>
    /// Allowlist of property keys that are safe to send in telemetry events.
    /// Any key not in this list must be rejected.
    /// </summary>
    private static readonly HashSet<string> SafePropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "command",
        "duration_ms",
        "exit_code",
        "output_format",
        "twig_version",
        "os_platform",
        "had_global_profile",
        "had_profile", // reserved for future per-workspace profile tracking
        "merge_needed",
        "field_count",
        "item_count",
        "result_count",
        "hash_changed",
        "used_all",
        "had_filters"
    };

    /// <summary>
    /// Substrings that must never appear in telemetry property keys.
    /// </summary>
    private static readonly string[] ForbiddenKeySubstrings =
    [
        "org", "project", "user", "type", "name", "path", "template", "field"
    ];

    private static readonly Regex KeyPattern = new(@"\[""(\w+)""\]\s*=", RegexOptions.Compiled);

    [Fact]
    public void TrackEvent_WhenNoEnvVars_IsNoOp()
    {
        // Arrange — no endpoint or key
        var client = new TelemetryClient(null, null, null);

        // Act — should not throw
        client.TrackEvent("TestEvent", new Dictionary<string, string> { ["command"] = "test" });

        // Assert — IsEnabled should be false
        client.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void TrackEvent_WhenEndpointOnly_IsNoOp()
    {
        var client = new TelemetryClient("https://example.com/track", null, null);
        client.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void TrackEvent_WhenKeyOnly_IsNoOp()
    {
        var client = new TelemetryClient(null, "some-key", null);
        client.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void TrackEvent_WhenEmptyStrings_IsNoOp()
    {
        var client = new TelemetryClient("", "", null);
        client.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void TrackEvent_WhenWhitespace_IsNoOp()
    {
        var client = new TelemetryClient("  ", "  ", null);
        client.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task TrackEvent_WhenConfigured_BuildsCorrectJsonEnvelope()
    {
        // Arrange
        string? capturedJson = null;
        var handler = new CaptureHandler(request =>
        {
            capturedJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new TelemetryClient("https://example.com/track", "test-ikey", httpClient);

        client.IsEnabled.ShouldBeTrue();

        // Act
        client.TrackEvent("CommandExecuted",
            new Dictionary<string, string>
            {
                ["command"] = "status",
                ["exit_code"] = "0"
            },
            new Dictionary<string, double>
            {
                ["duration_ms"] = 42.5
            });

        // Wait for the fire-and-forget Task.Run to complete
        await handler.RequestReceived.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        capturedJson.ShouldNotBeNull();

        using var doc = JsonDocument.Parse(capturedJson);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().ShouldBe("AppEvents");
        root.GetProperty("iKey").GetString().ShouldBe("test-ikey");

        var data = root.GetProperty("data");
        data.GetProperty("baseType").GetString().ShouldBe("EventData");

        var baseData = data.GetProperty("baseData");
        baseData.GetProperty("name").GetString().ShouldBe("CommandExecuted");

        var props = baseData.GetProperty("properties");
        props.GetProperty("command").GetString().ShouldBe("status");
        props.GetProperty("exit_code").GetString().ShouldBe("0");

        var measurements = baseData.GetProperty("measurements");
        measurements.GetProperty("duration_ms").GetDouble().ShouldBe(42.5);
    }

    [Fact]
    public async Task TrackEvent_WhenHttpFails_DoesNotThrowOrBlock()
    {
        // Arrange — handler throws
        var handler = new CaptureHandler(_ => throw new HttpRequestException("Network error"));
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new TelemetryClient("https://example.com/track", "test-ikey", httpClient);

        // Act — should not throw
        client.TrackEvent("CommandExecuted",
            new Dictionary<string, string> { ["command"] = "test" });

        // Wait for the fire-and-forget Task.Run to complete
        await handler.RequestReceived.WaitAsync(TimeSpan.FromSeconds(5));

        // If we get here, the test passes — no exception propagated
    }

    [Fact]
    public async Task TrackEvent_WhenServerReturns500_DoesNotThrowOrBlock()
    {
        var handler = new CaptureHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new TelemetryClient("https://example.com/track", "test-ikey", httpClient);

        // Act — should not throw
        client.TrackEvent("CommandExecuted",
            new Dictionary<string, string> { ["command"] = "test" });

        await handler.RequestReceived.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TrackEvent_WithNullProperties_DoesNotThrow()
    {
        var client = new TelemetryClient(null, null, null);
        client.TrackEvent("TestEvent", null, null);
    }

    [Fact]
    public async Task TrackEvent_PostsToCorrectEndpoint()
    {
        // Arrange
        string? capturedUrl = null;
        var handler = new CaptureHandler(request =>
        {
            capturedUrl = request.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new TelemetryClient("https://dc.services.example.com/v2/track", "test-ikey", httpClient);

        // Act
        client.TrackEvent("TestEvent");

        await handler.RequestReceived.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        capturedUrl.ShouldBe("https://dc.services.example.com/v2/track");
    }

    [Fact]
    public void AppInsightsEnvelope_SerializesWithCamelCase()
    {
        var envelope = new AppInsightsEnvelope
        {
            Name = "AppEvents",
            IKey = "test-key",
            Data = new AppInsightsData
            {
                BaseType = "EventData",
                BaseData = new AppInsightsBaseData
                {
                    Name = "TestEvent",
                    Properties = new Dictionary<string, string> { ["command"] = "status" },
                    Measurements = new Dictionary<string, double> { ["duration_ms"] = 100.0 }
                }
            }
        };

        var json = JsonSerializer.Serialize(envelope, TwigJsonContext.Default.AppInsightsEnvelope);
        json.ShouldNotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify camelCase serialization
        root.TryGetProperty("name", out _).ShouldBeTrue();
        root.TryGetProperty("iKey", out _).ShouldBeTrue();
        root.TryGetProperty("data", out _).ShouldBeTrue();

        var data = root.GetProperty("data");
        data.TryGetProperty("baseType", out _).ShouldBeTrue();
        data.TryGetProperty("baseData", out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("command")]
    [InlineData("duration_ms")]
    [InlineData("exit_code")]
    [InlineData("output_format")]
    [InlineData("twig_version")]
    [InlineData("os_platform")]
    [InlineData("had_global_profile")]
    [InlineData("had_profile")]
    [InlineData("merge_needed")]
    [InlineData("field_count")]
    [InlineData("item_count")]
    [InlineData("result_count")]
    [InlineData("hash_changed")]
    [InlineData("used_all")]
    public void AllowlistTest_SafeKeys_AreAccepted(string key)
    {
        SafePropertyKeys.Contains(key).ShouldBeTrue($"Key '{key}' should be in the safe allowlist");
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("org_name")]
    [InlineData("project_name")]
    [InlineData("project")]
    [InlineData("user_name")]
    [InlineData("username")]
    [InlineData("work_item_type")]
    [InlineData("type_name")]
    [InlineData("display_name")]
    [InlineData("area_path")]
    [InlineData("iteration_path")]
    [InlineData("template_name")]
    [InlineData("process_template")]
    [InlineData("field_name")]
    [InlineData("field_reference")]
    public void AllowlistTest_UnsafeKeys_AreRejected(string key)
    {
        // Key must contain at least one forbidden substring
        var containsForbidden = ForbiddenKeySubstrings.Any(s =>
            key.Contains(s, StringComparison.OrdinalIgnoreCase));
        containsForbidden.ShouldBeTrue(
            $"Key '{key}' should contain a forbidden substring and be rejected");

        // Verify it's NOT in the safe list
        SafePropertyKeys.Contains(key).ShouldBeFalse(
            $"Key '{key}' must NOT be in the safe allowlist");
    }

    [Fact]
    public void AllowlistTest_AllCommandCallSites_UseOnlySafeKeys()
    {
        // Source-scanning test: reads command source files and extracts all dictionary keys
        // from TrackEvent calls to verify they are in the safe allowlist. This closes the gap
        // between "the allowlist is correct" and "all call sites respect the allowlist".
        var dir = FindSolutionRoot();
        if (dir is null) return;

        var commandsDir = Path.Combine(dir, "src", "Twig", "Commands");
        if (!Directory.Exists(commandsDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(commandsDir, "*Command.cs"))
        {
            var content = File.ReadAllText(file);
            var trackIdx = content.IndexOf(".TrackEvent(", StringComparison.Ordinal);
            if (trackIdx < 0) continue;

            var callBlock = content[trackIdx..(FindCallEnd(content, trackIdx) + 1)];
            foreach (Match match in KeyPattern.Matches(callBlock))
            {
                var key = match.Groups[1].Value;
                if (!SafePropertyKeys.Contains(key))
                    violations.Add($"{Path.GetFileName(file)}: key '{key}' is not in the telemetry allowlist");
            }
        }

        violations.ShouldBeEmpty(
            "Found telemetry keys not present in the safe allowlist:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void AllowlistTest_Scanner_ExtractsAtLeastOneKey()
    {
        // Regression test: verifies the paren-matching scanner actually extracts keys
        // from a known command file. Prevents the scanner from silently becoming a no-op.
        var dir = FindSolutionRoot();
        if (dir is null) return;

        var discardFile = Path.Combine(dir, "src", "Twig", "Commands", "DiscardCommand.cs");
        if (!File.Exists(discardFile)) return;

        var content = File.ReadAllText(discardFile);
        var trackIdx = content.IndexOf(".TrackEvent(", StringComparison.Ordinal);
        trackIdx.ShouldBeGreaterThan(-1, "DiscardCommand.cs should contain a .TrackEvent( call");

        var end = FindCallEnd(content, trackIdx);
        var callBlock = content[trackIdx..(end + 1)];
        callBlock.Length.ShouldBeGreaterThan(20, "callBlock should span the full TrackEvent call, not just '.T'");

        var keys = KeyPattern.Matches(callBlock).Select(m => m.Groups[1].Value).ToList();
        keys.Count.ShouldBeGreaterThan(0, "Scanner should extract at least one key from DiscardCommand.cs");
        keys.ShouldContain("command");
        keys.ShouldContain("used_all");
    }

    [Fact]
    public void TelemetryClient_ImplementsIDisposable()
    {
        // Verify the client can be disposed without error
        var client = new TelemetryClient(null, null, null);
        client.Dispose(); // Should not throw
    }

    [Fact]
    public void TelemetryClient_Dispose_DoesNotDisposeInjectedHttpClient()
    {
        // Arrange — inject an HttpClient (test constructor does NOT own it)
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var client = new TelemetryClient("https://example.com/track", "test-ikey", httpClient);

        // Act — dispose the telemetry client
        client.Dispose();

        // Assert — the injected HttpClient should still be usable (not disposed)
        // If it were disposed, this would throw ObjectDisposedException
        httpClient.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    private static string? FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Twig.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir;
    }

    private static int FindCallEnd(string content, int startIdx)
    {
        var depth = 0;
        var entered = false;
        for (var i = startIdx; i < content.Length; i++)
        {
            if (content[i] == '(') { depth++; entered = true; }
            if (content[i] == ')') depth--;
            if (entered && depth == 0) return i;
        }
        return startIdx;
    }

    /// <summary>
    /// Custom HttpMessageHandler that captures requests for testing.
    /// Uses a <see cref="TaskCompletionSource{TResult}"/> to signal when a request
    /// has been received, eliminating fragile <c>Task.Delay</c> synchronization.
    /// </summary>
    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _requestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the handler has processed a request.</summary>
        public Task RequestReceived => _requestReceived.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(handler(request));
            }
            finally
            {
                _requestReceived.TrySetResult(true);
            }
        }
    }
}
