using System.Text;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Implements <see cref="IAuthenticationProvider"/> via Personal Access Token.
/// Precedence: <c>$TWIG_PAT</c> environment variable → <c>.twig/config</c> <c>auth.pat</c> field.
/// Returns a Basic auth header value.
/// </summary>
internal sealed class PatAuthProvider : IAuthenticationProvider
{
    private const string EnvVarName = "TWIG_PAT";

    private readonly Func<string, string?> _envVarReader;
    private readonly Func<string?> _configPatReader;

    /// <summary>
    /// Creates a PatAuthProvider with default environment and config readers.
    /// </summary>
    public PatAuthProvider(string? configPat = null)
        : this(Environment.GetEnvironmentVariable, () => configPat)
    {
    }

    /// <summary>
    /// Creates a PatAuthProvider with injectable readers (for testing).
    /// </summary>
    internal PatAuthProvider(Func<string, string?> envVarReader, Func<string?> configPatReader)
    {
        _envVarReader = envVarReader;
        _configPatReader = configPatReader;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Priority 1: environment variable
        var pat = _envVarReader(EnvVarName);
        if (!string.IsNullOrWhiteSpace(pat))
            return Task.FromResult(FormatBasicAuth(pat));

        // Priority 2: config file
        pat = _configPatReader();
        if (!string.IsNullOrWhiteSpace(pat))
            return Task.FromResult(FormatBasicAuth(pat));

        return Task.FromException<string>(new AdoAuthenticationException(
            $"No PAT found. Set the {EnvVarName} environment variable or configure 'auth.pat' in .twig/config."));
    }

    /// <summary>
    /// Formats a PAT as a Basic auth header value: <c>Basic base64(:PAT)</c>.
    /// </summary>
    private static string FormatBasicAuth(string pat)
    {
        var bytes = Encoding.UTF8.GetBytes($":{pat}");
        return $"Basic {Convert.ToBase64String(bytes)}";
    }
}
