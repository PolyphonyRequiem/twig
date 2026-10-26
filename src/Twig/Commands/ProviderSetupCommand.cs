using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Serialization;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

internal sealed class ProviderSetupCommand(
    Func<GitHubProviderSetupService> resolveService,
    RendererFactory rendererFactory,
    ITelemetryClient? telemetryClient = null)
{
    public Task<int> DiscoverAsync(string provider, string? host, string? repository,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default) =>
        ExecuteAsync(false, provider, host, repository, null, null, null, null, outputFormat, ct);

    public Task<int> BindAsync(string provider, string? host, string? repository,
        string? expectedOwnerId, string? expectedRepositoryId, long? expectedAttachmentRevision,
        string? replaceBinding, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default) =>
        ExecuteAsync(true, provider, host, repository, expectedOwnerId, expectedRepositoryId,
            expectedAttachmentRevision, replaceBinding, outputFormat, ct);

    private async Task<int> ExecuteAsync(bool bind, string provider, string? host, string? repository,
        string? expectedOwnerId, string? expectedRepositoryId, long? expectedAttachmentRevision,
        string? replaceBinding, string outputFormat, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();
        var exit = await ExecuteCoreAsync(bind, provider, host, repository, expectedOwnerId,
            expectedRepositoryId, expectedAttachmentRevision, replaceBinding, outputFormat, ct).ConfigureAwait(false);
        try
        {
            TelemetryHelper.TrackCommand(telemetryClient, bind ? "provider bind" : "provider discover",
                OutputFormats.Normalize(outputFormat) ?? OutputFormatterFactory.DefaultFormat, exit, start);
        }
        catch (Exception)
        {
            Trace.TraceWarning("Optional command telemetry was unavailable.");
        }
        return exit;
    }

    private async Task<int> ExecuteCoreAsync(bool bind, string provider, string? host, string? repository,
        string? expectedOwnerId, string? expectedRepositoryId, long? expectedAttachmentRevision,
        string? replaceBinding, string outputFormat, CancellationToken ct)
    {
        var normalized = OutputFormats.Normalize(outputFormat);
        if (provider != "github" || host is null || repository is null
            || normalized is not ("human" or "json" or "json-full" or "json-compact" or "minimal")
            || (bind && (string.IsNullOrWhiteSpace(expectedOwnerId) || string.IsNullOrWhiteSpace(expectedRepositoryId)
                || expectedAttachmentRevision is null or < 0
                || (replaceBinding is not null && !ProviderTargetBindingId.TryParse(replaceBinding, out _)))))
        {
            CommandError.Write(rendererFactory, Console.Error, outputFormat,
                "Use provider discover github --host <host> --repository <owner/name>, or provider bind github with --expected-owner-id, --expected-repository-id and --expected-attachment-revision. Output: human, json, minimal.");
            return 2;
        }
        try
        {
            _ = GitHubProviderSetupService.Scope(host, repository);
            if (bind)
                _ = new GitHubRepositoryTarget(expectedOwnerId!, expectedRepositoryId!);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            CommandError.Write(rendererFactory, Console.Error, outputFormat, ex.Message);
            return 2;
        }

        try
        {
            var service = resolveService();
            var report = bind
                ? await service.BindAsync(host, repository, expectedOwnerId!, expectedRepositoryId!,
                    expectedAttachmentRevision!.Value, replaceBinding, ct).ConfigureAwait(false)
                : await service.DiscoverAsync(host, repository, ct).ConfigureAwait(false);
            if (normalized is "json" or "json-full" or "json-compact")
                Console.Out.WriteLine(JsonSerializer.Serialize(report, TwigJsonContext.Default.GitHubProviderSetupReport));
            else
            {
                var text = normalized == "minimal"
                    ? string.Join('\t', report.Provider, report.ResourceScope, report.OwnerId, report.RepositoryId,
                        report.Locator, report.AttachmentRevision.ToString(CultureInfo.InvariantCulture),
                        report.CurrentBindingId ?? "", report.BindingId ?? "", report.BindingValidated ? "true" : "false")
                    : $"{(bind ? "Bound" : "Observed")} GitHub repository {report.Locator}\n" +
                        $"API origin: {report.ResourceScope}\nOwner ID: {report.OwnerId}\nRepository ID: {report.RepositoryId}\n" +
                        $"Attachment revision: {report.AttachmentRevision}\nCurrent binding pointer: {report.CurrentBindingId ?? "none"}\n" +
                        (bind ? $"Binding: {report.BindingId}\nWorkspace configuration was not changed."
                            : "Pointer observation only; binding authority was not validated. Discovery wrote no target state. Bind using these expected IDs and attachment revision.");
                rendererFactory.GetRenderer(outputFormat).Render(
                    new Twig.RenderTree.RenderTree([new RenderNode.Text(text)]));
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException
            or GitHubReadOnlyRequestRefusedException or GitHubCredentialUnavailableException)
        {
            CommandError.Write(rendererFactory, Console.Error, outputFormat, $"Provider setup refused: {ex.Message}");
            return 1;
        }
        catch (ArgumentException ex)
        {
            CommandError.Write(rendererFactory, Console.Error, outputFormat, ex.Message);
            return 2;
        }
    }
}
