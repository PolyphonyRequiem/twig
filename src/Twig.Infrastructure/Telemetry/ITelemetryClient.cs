namespace Twig.Infrastructure.Telemetry;

/// <summary>
/// Optional host-injected hook for anonymous, privacy-safe telemetry.
/// Twig does not register a sender or initiate telemetry network requests.
/// Hosts must isolate failures and reject identifiers or content.
/// </summary>
public interface ITelemetryClient
{
    void TrackEvent(string eventName, Dictionary<string, string>? properties = null, Dictionary<string, double>? metrics = null);
}
