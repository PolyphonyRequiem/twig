using Microsoft.Extensions.DependencyInjection;
using Twig.Formatters;
using Twig.Infrastructure.Config;

namespace Twig.ProcessOverrides;

/// <summary>
/// Routes a read-only process introspection command to either the ambient workspace's service
/// provider or a throwaway <c>--org</c>/<c>--project</c> override scope.
/// </summary>
/// <remarks>
/// <para>
/// AB#216. The single entry point for the override, so both <c>process</c> and
/// <c>process layout</c> get identical precedence, refusal wording, and lifetime handling —
/// rather than two copies that drift, which is the mechanism wayfinder 0016 already recorded.
/// </para>
/// <para>
/// 🔴 <b>The override provider is disposed on every path.</b> It owns an
/// <see cref="HttpClient"/> and an auth provider of its own; leaking one per invocation would
/// be invisible in a CLI that exits immediately and load-bearing in any long-lived host that
/// later reuses this seam.
/// </para>
/// <para>
/// An override invocation announces itself on <b>stderr</b> before running, because it behaves
/// materially differently from the command's usual shape: <c>process</c> normally answers from
/// the local cache and returns instantly, while an override always goes to the network. A user
/// who does not know that reads a multi-second pause as a hang. Announced once, here, so both
/// commands say it identically.
/// </para>
/// <para>
/// 🔴 <b>stderr, never stdout, and it is not suppressed for machine formats.</b> stdout is the
/// document — a caller piping <c>-o json</c> into <c>jq</c> must see exactly the same bytes
/// whether or not the override was used, which is what keeps this notice from being a breaking
/// change. Writing it to stdout for human format only would make the two formats disagree
/// about what the command emits, which is the drift this file exists to prevent.
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
    public static async Task<int> RunAsync(
        IServiceProvider workspaceServices,
        string? org,
        string? project,
        Func<IServiceProvider, Task<int>> run,
        string outputFormat,
        CancellationToken ct = default)
    {
        var decision = ProcessOverrideResolver.Resolve(org, project, TryGetConfig(workspaceServices));

        if (decision.Error is not null)
        {
            // Resolved from the workspace provider deliberately: a refusal happens before any
            // override scope is built, so the override's own formatter does not exist yet.
            var fmt = new OutputFormatterFactory(new HumanOutputFormatter())
                .GetFormatter(outputFormat);
            Console.Error.WriteLine(fmt.FormatError(decision.Error));
            return ProcessOverrideResolver.UsageExitCode;
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
