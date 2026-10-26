namespace Twig.Infrastructure.GitHub;

/// <summary>Supplies the GitHub bearer token for the read-only adapter. In-memory only.</summary>
internal interface IGitHubTokenSource
{
    ValueTask<string> GetTokenAsync(CancellationToken ct);
}

/// <summary>Raised when no usable GitHub credential is available. Never carries the token.</summary>
internal sealed class GitHubCredentialUnavailableException(string message) : Exception(message);

/// <summary>
/// Borrows the explicitly selected GitHub CLI account via
/// <c>gh auth token --hostname &lt;host&gt; --user &lt;account&gt;</c>.
/// Child-only removal of token environment overrides prevents ambient credentials winning.
/// The token is held in memory for the process lifetime only — never persisted, logged or
/// sent to telemetry — and every failure is fail-closed with no fallback source.
/// </summary>
internal sealed class GhCliTokenSource(IBoundedProcessRunner runner, string host, string? account) : IGitHubTokenSource
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    internal const int MaxOutputChars = 4096;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public async ValueTask<string> GetTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account) || !string.Equals(account, account.Trim(), StringComparison.Ordinal)
            || account.Any(char.IsControl) || account.Any(char.IsWhiteSpace) || account.StartsWith('-'))
            throw new GitHubCredentialUnavailableException(
                "GitHub credential unavailable: set github.account to explicitly select a GitHub CLI account; the active account is never assumed.");
        if (_token is { } cached)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } raced)
                return raced;

            var result = await runner.RunAsync(
                "gh", ["auth", "token", "--hostname", host, "--user", account, "--no-refresh"], Timeout, MaxOutputChars, ct,
                ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]).ConfigureAwait(false);

            var problem = result.Outcome switch
            {
                BoundedProcessOutcome.NotStarted => "the GitHub CLI (gh) was not found",
                BoundedProcessOutcome.TimedOut => "gh auth token did not finish in time",
                BoundedProcessOutcome.OutputLimitExceeded => "gh auth token returned unexpected output",
                _ when result.ExitCode != 0 => $"gh auth token exited with code {result.ExitCode}",
                _ => null,
            };

            var token = result.StandardOutput.Trim();
            if (problem is null && (token.Length == 0 || token.AsSpan().IndexOfAny('\r', '\n') >= 0 || token.Contains(' ')))
                problem = "gh auth token returned no usable token";

            if (problem is not null)
                throw new GitHubCredentialUnavailableException(
                    $"GitHub credential unavailable for {host}: {problem}. Run `gh auth login --hostname {host}` and retry.");

            _token = token;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }
}
