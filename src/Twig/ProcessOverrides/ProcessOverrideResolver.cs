using Twig.Infrastructure.Config;

namespace Twig.ProcessOverrides;

/// <summary>
/// Decides what <c>--org</c>/<c>--project</c> mean on a read-only process introspection
/// command, and refuses the shapes that have no honest answer.
/// </summary>
/// <remarks>
/// <para>
/// AB#216 acceptance 4 requires flag-vs-manifest precedence to be decided deliberately.
/// <b>This follows <c>InitCommand</c>'s manifest-is-authoritative rule rather than departing
/// from it</b>, so twig has ONE story about what a coordinate flag means beside a manifest
/// instead of two that a reader has to hold apart. The wording deliberately echoes
/// <c>InitCommand.GetManifestCoordinateConflict</c>, including its
/// <see cref="StringComparison.OrdinalIgnoreCase"/> comparison.
/// </para>
/// <para>
/// 🔴 <b>The departure that would have been tempting, and why it is wrong:</b> these commands
/// are read-only, so letting a flag silently win inside a workspace looks harmless — nothing
/// is written either way. It is not harmless, because the two invocations produce DIFFERENT
/// documents under the same manifest, and a reader diffing two outputs has no way to see
/// which coordinates each came from. Refusing is the only outcome that keeps a process
/// description attributable to a project.
/// </para>
/// <para>
/// Both flags are required together: an org without a project (or the reverse) cannot address
/// an ADO process, and half an override silently falling back to the workspace's other half
/// is exactly the ambiguity above.
/// </para>
/// </remarks>
internal static class ProcessOverrideResolver
{
    /// <summary>Exit code for a usage error, matching the CLI's other argument guards.</summary>
    internal const int UsageExitCode = 1;

    /// <summary>
    /// Resolves what an invocation carrying <paramref name="org"/>/<paramref name="project"/>
    /// should do.
    /// </summary>
    /// <param name="org">The <c>--org</c> value, or null when not supplied.</param>
    /// <param name="project">The <c>--project</c> value, or null when not supplied.</param>
    /// <param name="workspaceConfig">
    /// The ambient workspace configuration, or null when no workspace was discovered.
    /// </param>
    /// <returns>
    /// <see cref="ProcessOverrideDecision.UseWorkspace"/> when no override was requested,
    /// <see cref="ProcessOverrideDecision.UseOverride"/> when one was and it is legal, or
    /// <see cref="ProcessOverrideDecision.Refuse"/> carrying the message to print.
    /// </returns>
    public static ProcessOverrideDecision Resolve(
        string? org,
        string? project,
        TwigConfiguration? workspaceConfig)
    {
        var hasOrg = !string.IsNullOrWhiteSpace(org);
        var hasProject = !string.IsNullOrWhiteSpace(project);

        if (!hasOrg && !hasProject)
            return ProcessOverrideDecision.UseWorkspace();

        if (hasOrg != hasProject)
        {
            var supplied = hasOrg ? "--org" : "--project";
            var missing = hasOrg ? "--project" : "--org";
            return ProcessOverrideDecision.Refuse(
                $"{supplied} requires {missing}. Both name one Azure DevOps project; "
                + "supplying one alone cannot address a process.");
        }

        // Manifest-is-authoritative, matching InitCommand. Only a manifest-backed workspace
        // conflicts: with no workspace there is nothing to be authoritative.
        if (workspaceConfig is not null && !string.IsNullOrWhiteSpace(workspaceConfig.Organization))
        {
            if (!string.Equals(workspaceConfig.Organization, org, StringComparison.OrdinalIgnoreCase))
            {
                return ProcessOverrideDecision.Refuse(
                    $"--org '{org}' conflicts with existing twig.json value "
                    + $"'{workspaceConfig.Organization}'. The manifest is authoritative.");
            }

            if (!string.Equals(workspaceConfig.Project, project, StringComparison.OrdinalIgnoreCase))
            {
                return ProcessOverrideDecision.Refuse(
                    $"--project '{project}' conflicts with existing twig.json value "
                    + $"'{workspaceConfig.Project}'. The manifest is authoritative.");
            }

            // Flags agree with the manifest — the workspace path answers identically and is
            // cheaper (cache rather than a live fetch), so prefer it.
            return ProcessOverrideDecision.UseWorkspace();
        }

        return ProcessOverrideDecision.UseOverride(org!.Trim(), project!.Trim());
    }
}

/// <summary>
/// The outcome of <see cref="ProcessOverrideResolver.Resolve"/>.
/// </summary>
/// <remarks>
/// Three distinct outcomes rather than a nullable pair, because "no override requested" and
/// "override requested and refused" must not collapse: collapsing them would make a refusal
/// silently fall back to the workspace, which is the false-green shape this repo keeps paying
/// for.
/// </remarks>
internal sealed record ProcessOverrideDecision
{
    private ProcessOverrideDecision() { }

    /// <summary>True when the ambient workspace should serve the request.</summary>
    public bool IsWorkspace { get; private init; }

    /// <summary>True when an ephemeral override scope should serve the request.</summary>
    public bool IsOverride { get; private init; }

    /// <summary>The refusal message when the invocation is illegal; null otherwise.</summary>
    public string? Error { get; private init; }

    /// <summary>The override organization, set only when <see cref="IsOverride"/>.</summary>
    public string? Org { get; private init; }

    /// <summary>The override project, set only when <see cref="IsOverride"/>.</summary>
    public string? Project { get; private init; }

    public static ProcessOverrideDecision UseWorkspace() => new() { IsWorkspace = true };

    public static ProcessOverrideDecision UseOverride(string org, string project) =>
        new() { IsOverride = true, Org = org, Project = project };

    public static ProcessOverrideDecision Refuse(string error) => new() { Error = error };
}
