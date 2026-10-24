namespace Twig.Infrastructure.GitHub;

/// <summary>Supplies the GitHub bearer token for the read-only adapter. In-memory only.</summary>
internal interface IGitHubTokenSource
{
    ValueTask<string> GetTokenAsync(CancellationToken ct);
}

/// <summary>Raised when no usable GitHub credential is available. Never carries the token.</summary>
internal sealed class GitHubCredentialUnavailableException(string message) : Exception(message);

/// <summary>
/// Epic #1167: borrows the GitHub CLI's credential via <c>gh auth token --hostname &lt;host&gt;</c>.
/// The token is held in memory for the process lifetime only — never persisted, logged or
/// sent to telemetry — and every failure is fail-closed with no fallback source.
/// </summary>
internal sealed class GhCliTokenSource(IBoundedProcessRunner runner, string host) : IGitHubTokenSource
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    internal const int MaxOutputChars = 4096;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public async ValueTask<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is { } cached)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } raced)
                return raced;

            var result = await runner.RunAsync(
                "gh", ["auth", "token", "--hostname", host], Timeout, MaxOutputChars, ct).ConfigureAwait(false);

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
