using Twig.Infrastructure.Telemetry;
using System.Diagnostics;
using System.Globalization;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

internal sealed class AuthGitHubCommand(
    TwigConfiguration config,
    RendererFactory? rendererFactory = null, ITelemetryClient? telemetryClient = null,
    Func<string, string, GitHubAppAuthentication>? createAuthentication = null)
{
    private readonly RendererFactory _renderer = rendererFactory ?? new();

    internal async Task<int> ExecuteAsync(string operation, string? identity, string? clientId,
        string outputFormat, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = await ExecuteCoreAsync(operation, identity, clientId, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "auth " + operation, outputFormat, exit, started);
        return exit;
    }

    private async Task<int> ExecuteCoreAsync(string operation, string? identity, string? clientId,
        string outputFormat, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var selectedClient = clientId ?? config.GitHubAppClientId ?? "";
            var selectedIdentity = identity ?? (operation == "status" ? config.GitHubOAuthIdentity : null) ?? "";
            try { GitHubAppCredentialStore.ValidateSelection(selectedClient, selectedIdentity); }
            catch (GitHubDeviceFlowException ex)
            {
                CommandError.Write(_renderer, Console.Error, outputFormat, ex.Message);
                return 2;
            }
            var auth = (createAuthentication ?? GitHubAppAuthentication.CreateDefault)(
                selectedClient, selectedIdentity);
            GitHubAppAuthStatus status;
            switch (operation)
            {
                case "login":
                    status = await auth.LoginAsync(instructions =>
                        Console.Error.WriteLine(
                            $"Open {instructions.VerificationUri} and enter {instructions.UserCode}. " +
                            "Authorize only the intended GitHub App. Tokens are never printed."), ct);
                    break;
                case "status":
                    status = auth.Inspect();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
            var message = "Local GitHub App identity status; repository permissions and write admission are not established.";
            var values = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["provider"] = RenderCell.String(status.Provider),
                ["host"] = RenderCell.String(status.Host),
                ["clientId"] = RenderCell.String(status.ClientId),
                ["identity"] = RenderCell.String(status.Identity),
                ["actorId"] = RenderCell.String(status.ActorId ?? ""),
                ["login"] = RenderCell.String(status.Login ?? ""),
                ["state"] = RenderCell.String(status.State),
                ["accessExpiresAt"] = RenderCell.String(status.AccessExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                ["refreshExpiresAt"] = RenderCell.String(status.RefreshExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                ["message"] = RenderCell.String(message),
            };
            RenderNode node = ConnectionRenderHelpers.IsHumanFormat(outputFormat)
                ? new RenderNode.Section($"GitHub OAuth: {status.State}", new RenderNode[]
                {
                    new RenderNode.Text($"Identity: {status.Identity}; app: {status.ClientId}; host: {status.Host}"),
                    new RenderNode.Text($"Actor: {status.ActorId ?? "(not enrolled)"}; login: {status.Login ?? "(unknown)"}"),
                    new RenderNode.Text($"Access expires: {status.AccessExpiresAt?.ToString("u") ?? "(none)"}"),
                    new RenderNode.Text($"Refresh expires: {status.RefreshExpiresAt?.ToString("u") ?? "(none)"}"),
                    new RenderNode.Hint(message),
                })
                : outputFormat == "minimal"
                    ? new RenderNode.Text($"{status.Provider}\t{status.Identity}\t{status.State}\t{status.ActorId}\t{message}")
                    : new RenderNode.Record("githubAuth", values);
            _renderer.GetRenderer(outputFormat).Render(new global::Twig.RenderTree.RenderTree(new[] { node }));
            return 0;
        }
        catch (GitHubDeviceFlowException ex)
        {
            CommandError.Write(_renderer, Console.Error, outputFormat, ex.Message);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CommandError.Write(_renderer, Console.Error, outputFormat, "GitHub sign-in was cancelled; no alternate credential source was used.");
            return 1;
        }
    }

}
