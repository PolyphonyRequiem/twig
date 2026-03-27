using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Telemetry;

/// <summary>
/// Application Insights envelope types for AOT-compatible JSON serialization.
/// Matches the AI ingestion API schema for custom events.
/// </summary>
internal sealed class AppInsightsEnvelope
{
    public string Name { get; set; } = "AppEvents";
    public string IKey { get; set; } = string.Empty;
    public AppInsightsData Data { get; set; } = new();
}

internal sealed class AppInsightsData
{
    public string BaseType { get; set; } = "EventData";
    public AppInsightsBaseData BaseData { get; set; } = new();
}

internal sealed class AppInsightsBaseData
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string>? Properties { get; set; }
    public Dictionary<string, double>? Measurements { get; set; }
}

/// <summary>
/// Lightweight telemetry client that POSTs anonymous events to an Application Insights
/// endpoint. Reads <c>TWIG_TELEMETRY_ENDPOINT</c> and <c>TWIG_TELEMETRY_KEY</c> env vars
/// at construction. If either is unset, all methods are no-ops. Fire-and-forget — failures
/// are silently swallowed and never affect command execution.
/// </summary>
internal sealed class TelemetryClient : ITelemetryClient, IDisposable
{
    private readonly string? _endpoint;
    private readonly string? _instrumentationKey;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;

    public TelemetryClient()
    {
        _endpoint = Environment.GetEnvironmentVariable("TWIG_TELEMETRY_ENDPOINT");
        _instrumentationKey = Environment.GetEnvironmentVariable("TWIG_TELEMETRY_KEY");

        if (!string.IsNullOrWhiteSpace(_endpoint) && !string.IsNullOrWhiteSpace(_instrumentationKey))
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _ownsHttpClient = true;
        }
    }

    /// <summary>
    /// Constructor for testing — accepts explicit endpoint, key, and HttpClient.
    /// Caller owns the HttpClient lifetime; it will NOT be disposed by this instance.
    /// </summary>
    internal TelemetryClient(string? endpoint, string? instrumentationKey, HttpClient? httpClient)
    {
        _endpoint = endpoint;
        _instrumentationKey = instrumentationKey;
        _httpClient = httpClient;
    }

    [MemberNotNullWhen(true, nameof(_endpoint), nameof(_instrumentationKey), nameof(_httpClient))]
    internal bool IsEnabled => _httpClient is not null
        && !string.IsNullOrWhiteSpace(_endpoint)
        && !string.IsNullOrWhiteSpace(_instrumentationKey);

    public void TrackEvent(string eventName, Dictionary<string, string>? properties = null, Dictionary<string, double>? metrics = null)
    {
        if (!IsEnabled)
            return;

        var endpoint = _endpoint;
        var key = _instrumentationKey;
        var client = _httpClient;

        _ = Task.Run(async () =>
        {
            try
            {
                var envelope = new AppInsightsEnvelope
                {
                    Name = "AppEvents",
                    IKey = key,
                    Data = new AppInsightsData
                    {
                        BaseType = "EventData",
                        BaseData = new AppInsightsBaseData
                        {
                            Name = eventName,
                            Properties = properties,
                            Measurements = metrics
                        }
                    }
                };

                var json = JsonSerializer.Serialize(envelope, TwigJsonContext.Default.AppInsightsEnvelope);
                var content = new StringContent(json);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                // Fire-and-forget — we don't care about the response
                await client.PostAsync(endpoint, content).ConfigureAwait(false);
            }
            catch
            {
                // Telemetry failures must never affect command execution
            }
        });
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }
}
