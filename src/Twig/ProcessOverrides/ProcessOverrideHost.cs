using Microsoft.Extensions.DependencyInjection;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.ProcessOverrides;

/// <summary>
/// Validates explicit process targets before choosing an admitted workspace or legacy layout scope.
/// </summary>
/// <remarks>
/// <para>
/// Core schema inspection discovers live metadata through the pinned workspace.
/// It refuses unpinned targets and explicit overrides before HTTP, rather than substituting
/// ambient scope. Invalid pairs are usage errors; manifest conflicts remain target refusals.
/// </para>
/// <para>
/// Only the legacy native layout reader uses a throwaway override scope. Its live read is
/// announced on stderr, never stdout, and its provider is disposed on every path.
/// Schema inspection does not construct that legacy graph or print a misleading live-read notice.
/// </para>
/// </remarks>
internal static class ProcessOverrideHost
{
    /// <summary>
    /// Runs <paramref name="run"/> against the right service provider.
    /// </summary>
    /// <param name="workspaceServices">The ambient (workspace) provider.</param>
    /// <param name="org">The <c>--org</c> value, or null.</param>
    /// <param name="project">The <c>--project</c> value, or null.</param>
    /// <param name="run">The command invocation, given the provider it should resolve from.</param>
    /// <param name="outputFormat">Output format, used to format a refusal on stderr.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="requiresCoreSchema">Schema inspection must use a pinned Core workspace; unpinned targets refuse before graph construction.</param>
    public static async Task<int> RunAsync(
        IServiceProvider workspaceServices,
        string? org,
        string? project,
        Func<IServiceProvider, Task<int>> run,
        string outputFormat,
        CancellationToken ct = default,
        bool requiresCoreSchema = false)
    {
        var decision = ProcessOverrideResolver.Resolve(org, project, TryGetConfig(workspaceServices));

        if (decision.Error is not null)
        {
            Twig.Commands.CommandError.Write(new Twig.Rendering.RendererFactory(), Console.Error, outputFormat, decision.Error);
            return string.IsNullOrWhiteSpace(org) != string.IsNullOrWhiteSpace(project)
                ? 2 : ProcessOverrideResolver.UsageExitCode;
        }

        if ((org is not null || project is not null)
            && (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project)))
        {
            Twig.Commands.CommandError.Write(new Twig.Rendering.RendererFactory(), Console.Error, outputFormat,
                "--org and --project must both be nonempty when an explicit target is supplied; no ambient fallback was used.");
            return 2;
        }

        if (requiresCoreSchema && workspaceServices.GetService<Twig.Infrastructure.Services.CoreWorkspaceFactory>() is null)
        {
            Twig.Commands.CommandError.Write(new Twig.Rendering.RendererFactory(), Console.Error, outputFormat,
                Twig.Commands.ProcessCommand.WorkspaceRequired);
            return 1;
        }

        if ((!string.IsNullOrWhiteSpace(org) || !string.IsNullOrWhiteSpace(project))
            && workspaceServices.GetService<Twig.Infrastructure.Services.CoreWorkspaceFactory>() is not null)
        {
            Twig.Commands.CommandError.Write(new Twig.Rendering.RendererFactory(), Console.Error, outputFormat,
                "Process target override is not qualified for the pinned Core workspace; no ambient fallback or override HTTP was attempted. "
                + "Initialize a fresh isolated Core workspace pinned to the requested target.");
            return 1;
        }

        if (!decision.IsOverride)
            return await run(workspaceServices);

        var userPrefs = TryGetConfig(workspaceServices)?.UserPrefs ?? new TwigUserConfig();

        // Announce the live read before doing it, not after: the whole point is to explain the
        // pause the user is about to sit through, and a notice printed afterwards explains a
        // wait that has already ended.
        var infoFormatter = new OutputFormatterFactory(new HumanOutputFormatter())
            .GetFormatter(outputFormat);
        Console.Error.WriteLine(infoFormatter.FormatInfo(
            $"Reading {decision.Org}/{decision.Project} live from Azure DevOps (no workspace; nothing is written)."));

        await using var overrideServices = ProcessOverrideScope.Build(
            decision.Org!, decision.Project!, userPrefs);

        return await run(overrideServices);
    }

    /// <summary>
    /// Reads the ambient <see cref="TwigConfiguration"/>, tolerating its absence.
    /// </summary>
    /// <remarks>
    /// Outside a workspace the CLI still registers a configuration (loaded from a
    /// non-existent path, so its coordinates are empty), but resolution can throw if the
    /// config file is unreadable. An override invocation must not be blocked by a broken
    /// ambient config it is not going to use — that would defeat the whole "works without a
    /// workspace" acceptance criterion.
    /// </remarks>
    private static TwigConfiguration? TryGetConfig(IServiceProvider services)
    {
        try
        {
            return services.GetService<TwigConfiguration>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
