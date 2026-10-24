namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Off-by-default switch for the experimental provider-neutral durable store (Epic #1168).
/// Only the exact value <c>1</c> enables it; anything else, including unset, leaves current behaviour.
/// </summary>
internal static class NeutralStoreOptIn
{
    public const string EnvironmentVariable = "TWIG_EXPERIMENTAL_NEUTRAL_STORE";

    public static bool IsEnabled(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

    public static bool IsEnabledFromEnvironment() => IsEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable));
}
