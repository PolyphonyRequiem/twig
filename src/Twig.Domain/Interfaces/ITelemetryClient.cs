namespace Twig.Domain.Interfaces;

/// <summary>
/// Emits anonymous, privacy-safe telemetry events. Implementation is fire-and-forget —
/// callers never await. All methods are no-ops when <c>TWIG_TELEMETRY_ENDPOINT</c> is unset.
/// </summary>
public interface ITelemetryClient
{
    void TrackEvent(string eventName, Dictionary<string, string>? properties = null, Dictionary<string, double>? metrics = null);
}
