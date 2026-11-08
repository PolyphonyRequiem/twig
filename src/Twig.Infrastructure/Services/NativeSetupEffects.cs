using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Twig.Core;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Services;

internal sealed partial class NativeSetupEffects
{
    private const string Header = "native-setup:v1";
    private const string Prefix = "native-setup:";
    private readonly Dictionary<NativeSetupEffectKind, NativeSetupEffectState> effects = [];
    internal NativeSetupPhase Phase { get; set; } = NativeSetupPhase.Validation;
    internal int CommandExitCode { get; set; } = 1;
    internal Action<NativeSetupEffectKind, NativeSetupEffectState>? Observed { get; set; }

    internal void Before(NativeSetupEffectKind kind)
    {
        effects[kind] = NativeSetupEffectState.Possible;
        Observed?.Invoke(kind, NativeSetupEffectState.Possible);
    }
    internal void Confirm(NativeSetupEffectKind kind)
    {
        effects[kind] = NativeSetupEffectState.Confirmed;
        Observed?.Invoke(kind, NativeSetupEffectState.Confirmed);
    }
    internal Result<T> Fail<T>(Error error)
    {
        var evidence = ImmutableArray.CreateBuilder<string>();
        evidence.Add(Header);
        evidence.Add(Prefix + "phase:" + Phase);
        if (Phase == NativeSetupPhase.Cancelled) evidence.Add(Prefix + "cancelled");
        foreach (var effect in effects.OrderBy(pair => pair.Key))
            evidence.Add(Prefix + "effect:" + effect.Key + ":" + effect.Value);
        evidence.AddRange(error.Evidence.Where(value => value is "http:401" or "http:403").Distinct());
        return Result<T>.Fail(new Error(error.Code, Scrub(error.Message)) { Evidence = evidence.ToImmutable() });
    }
    internal Result<T> Cancel<T>()
    {
        Phase = NativeSetupPhase.Cancelled;
        return Fail<T>(new(ErrorCode.Unavailable, "Native setup was cancelled; possible local effects were preserved."));
    }
    internal Result<T> Exception<T>(Exception exception)
        => Fail<T>(exception is NativeSetupException native ? native.Error
            : new(exception is ArgumentException ? ErrorCode.InvalidInput
                : exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
                    ? ErrorCode.StorageUnavailable : ErrorCode.Unavailable,
                "Native setup did not complete; possible local effects were preserved. " + exception.GetType().Name));

    internal static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException
        or HttpRequestException or Twig.Infrastructure.Ado.Exceptions.AdoException or Twig.Infrastructure.Config.TwigConfigurationException;

    internal static Result<NativeSetupFailureInfo> Read(Error error)
    {
        if (error is null || error.Evidence.IsDefault || error.Evidence.Length > 64
            || error.Evidence.Count(value => value == Header) != 1)
            return Unsupported();
        NativeSetupPhase? phase = null;
        var cancelled = false;
        var found = new Dictionary<NativeSetupEffectKind, NativeSetupEffectState>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in error.Evidence)
        {
            if (marker is null || !seen.Add(marker)) return Unsupported();
            if (marker == Header || marker is "http:401" or "http:403") continue;
            if (marker == Prefix + "cancelled") { cancelled = true; continue; }
            if (marker.StartsWith(Prefix + "phase:", StringComparison.Ordinal))
            {
                var name = marker[(Prefix.Length + 6)..];
                if (phase is not null || !Enum.TryParse<NativeSetupPhase>(name, out var value)
                    || !Enum.IsDefined(value) || value.ToString() != name) return Unsupported();
                phase = value;
                continue;
            }
            if (!marker.StartsWith(Prefix + "effect:", StringComparison.Ordinal)) return Unsupported();
            var parts = marker[(Prefix.Length + 7)..].Split(':');
            if (parts.Length != 2 || !Enum.TryParse<NativeSetupEffectKind>(parts[0], out var kind)
                || !Enum.IsDefined(kind) || kind.ToString() != parts[0]
                || !Enum.TryParse<NativeSetupEffectState>(parts[1], out var state)
                || !Enum.IsDefined(state) || state.ToString() != parts[1] || !found.TryAdd(kind, state)) return Unsupported();
        }
        if (phase is null || cancelled != (phase == NativeSetupPhase.Cancelled)) return Unsupported();
        return Result<NativeSetupFailureInfo>.Ok(new(phase.Value, cancelled,
            found.OrderBy(pair => pair.Key).Select(pair => new NativeSetupEffect(pair.Key, pair.Value)).ToImmutableArray()));

        static Result<NativeSetupFailureInfo> Unsupported() => Result<NativeSetupFailureInfo>.Fail(ErrorCode.Unsupported,
            "Native setup diagnostics are missing, foreign, malformed or unsupported; no phase or effects were inferred.");
    }

    private static string Scrub(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Native setup did not complete.";
        var scrubbed = PrivateValue().Replace(message, "[redacted]");
        return new string(scrubbed.Take(2048).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
    }

    [GeneratedRegex(@"(?:https?://\S+|[A-Za-z]:[\\/][^\r\n]*|(?<!\w)/(?:[^\s/]+/)+[^\s]*|cred-[A-Za-z0-9_-]+|[A-Za-z0-9_-]{80,})", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateValue();
}

internal sealed class NativeSetupException(Error error) : InvalidOperationException(error.Message)
{
    internal Error Error { get; } = error;
}
