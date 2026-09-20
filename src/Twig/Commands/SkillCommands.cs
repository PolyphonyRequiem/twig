using System.Text.Json;
using Twig.Formatters;
using Twig.Skills;

namespace Twig.Commands;

/// <summary>
/// Explicit, offline lifecycle for the executable-matched canonical Twig skill and separately named
/// integration/variant externals. Zero DI, zero SQLite, zero network, zero workspace/auth: this
/// command class instantiates with a parameterless constructor and is safe to route before every
/// Program.cs startup side effect. Verbs: install, update, configure, status, add. There is no
/// remove verb by design — externals are user content once installed, and Twig never claims to
/// undo them on the user's behalf.
/// </summary>
internal sealed class SkillCommands
{
    /// <summary>Install the canonical Twig skill into an explicit skills root; refuse conflicts and never edit provider settings.</summary>
    /// <param name="provider">-p, Agent provider: hermes, omp, or copilot.</param>
    /// <param name="target">-t, Explicit skills-root path; no ambient home/profile installation.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="scanRoot">Optional additional skills root to check for companions and potential shadowing.</param>
    public int Install(string provider, string target, string output = OutputFormats.Default, string? scanRoot = null) =>
        Run(output, service => service.Install(provider, target, scanRoot));

    /// <summary>Update only unedited manifest-owned Twig guidance; preserve companions, externals and settings.</summary>
    /// <param name="provider">-p, Agent provider: hermes, omp, or copilot; must match the target manifest.</param>
    /// <param name="target">-t, Explicit skills-root path containing a Twig installation.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="scanRoot">Optional additional skills root to check for companions and potential shadowing.</param>
    public int Update(string provider, string target, string output = OutputFormats.Default, string? scanRoot = null) =>
        Run(output, service => service.Update(provider, target, scanRoot));

    /// <summary>Select or clear a separately named user companion for one scenario; never rewrite user content.</summary>
    /// <param name="provider">-p, Agent provider: hermes, omp, or copilot; must match the target manifest.</param>
    /// <param name="target">-t, Explicit skills-root path containing a current Twig installation.</param>
    /// <param name="scenario">Scenario name, e.g. terminal or discord; lowercase letters, digits and hyphens.</param>
    /// <param name="companion">Separately named user skill found in target or scan-root; exclusive with --clear.</param>
    /// <param name="clear">Remove the scenario selection instead of selecting a companion.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="scanRoot">Optional additional root containing a user companion; not registered with the provider.</param>
    public int Configure(string provider, string target, string scenario, string? companion = null, bool clear = false,
        string output = OutputFormats.Default, string? scanRoot = null) =>
        Run(output, service => service.Configure(provider, target, scenario, companion, clear, scanRoot));

    /// <summary>Inspect package identity, integrity and bounded discovery without writing or contacting the provider.</summary>
    /// <param name="provider">-p, Agent provider: hermes, omp, or copilot.</param>
    /// <param name="target">-t, Explicit skills-root path; a missing target is reported without creating it.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="scanRoot">Optional additional skills root checked for companions and potential shadowing.</param>
    public int Status(string provider, string target, string output = OutputFormats.Default, string? scanRoot = null) =>
        Run(output, service => service.Status(provider, target, scanRoot));

    /// <summary>Install a separately named external integration/variant skill from a local source directory.
    /// Idempotent when source bytes and executable modes are unchanged; refuses collisions and, on reinstall, refuses edited installed bytes or modes.
    /// There is no companion remove verb: the user removes the installed skill directory themselves.</summary>
    /// <param name="provider">-p, Agent provider: hermes, omp, or copilot; must match the target manifest.</param>
    /// <param name="target">-t, Explicit skills-root path with a current Twig installation.</param>
    /// <param name="source">Local source directory containing the external skill's SKILL.md and support files.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="scanRoot">Optional additional root checked for shadowing.</param>
    public int Add(string provider, string target, string source, string output = OutputFormats.Default, string? scanRoot = null) =>
        Run(output, service => service.AddExternal(provider, target, source, scanRoot));

    private static int Run(string output, Func<SkillLifecycle, SkillLifecycleResult> action) =>
        Run(output, Console.Out, Console.Error, action);

    // Test seam: identical behaviour, but with injectable stdout/stderr so behaviour tests can
    // inspect the exact bytes each format writes without capturing process-wide Console handles.
    internal static int Run(string output, TextWriter stdout, TextWriter stderr,
        Func<SkillLifecycle, SkillLifecycleResult> action)
    {
        var normalized = OutputFormats.Normalize(output);
        if (normalized is null)
        {
            // Program routes commands after OutputFormatArgumentValidator, so an unknown value only
            // reaches this code when the caller bypassed argument validation (a test, or a direct
            // internal caller). Fail loud with the same message so behaviour never diverges.
            stderr.WriteLine(OutputFormatArgumentValidator.Message(output));
            return OutputFormatArgumentValidator.UsageExitCode;
        }
        // `ids` is a machine identifier format for verbs that return a list of work-item IDs.
        // `twig skills` is not an ID utility, so accepting it would either lie by printing the
        // state as an id, or silently hide the failure/warning surface. Reject up front.
        if (normalized == "ids")
        {
            stderr.WriteLine("`--output ids` is not supported by twig skills. Use human, json, json-full, json-compact or minimal.");
            return OutputFormatArgumentValidator.UsageExitCode;
        }
        try
        {
            var result = action(new SkillLifecycle(SkillPackage.Load()));
            WriteSuccess(stdout, normalized, result);
            return 0;
        }
        catch (Exception e) when (e is SkillLifecycleException or IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            WriteError(stderr, normalized, e.Message);
            return 1;
        }
    }

    private static void WriteSuccess(TextWriter stdout, string format, SkillLifecycleResult result)
    {
        switch (format)
        {
            case "json":
            case "json-full":
            case "json-compact":
                var payload = new SkillCommandOutput(
                    State: result.State,
                    Provider: result.Provider,
                    Target: result.Target,
                    PackageIdentity: result.PackageIdentity,
                    InstalledIdentity: result.InstalledIdentity,
                    Selections: result.Selections.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                    Externals: result.Externals.ToList(),
                    Warnings: result.Warnings.ToList(),
                    DiscoveryScope: result.DiscoveryScope,
                    ProviderHint: SkillLifecycle.ProviderHint(result.Provider));
                stdout.WriteLine(JsonSerializer.Serialize(payload, SkillJsonContext.Default.SkillCommandOutput));
                return;

            case "minimal":
                // Tagged, tab-separated rows: never filter out warnings, identities, selections or
                // externals. A downstream script or agent must see every row a JSON payload would
                // carry, in a shape it can `cut -f2 | while read` without parsing prose.
                WriteMinimalRow(stdout, "state", result.State);
                WriteMinimalRow(stdout, "provider", result.Provider);
                WriteMinimalRow(stdout, "target", result.Target);
                WriteMinimalRow(stdout, "bundled", result.PackageIdentity);
                WriteMinimalRow(stdout, "installed", result.InstalledIdentity ?? "none");
                foreach (var (scenario, companion) in result.Selections.OrderBy(p => p.Key, StringComparer.Ordinal))
                    WriteMinimalRow(stdout, "selection", $"{scenario}\t{companion}");
                foreach (var external in result.Externals)
                    WriteMinimalRow(stdout, "external", external);
                foreach (var warning in result.Warnings)
                    WriteMinimalRow(stdout, "warning", warning);
                return;

            default:
                stdout.WriteLine($"Twig skills: {result.State}");
                stdout.WriteLine($"Provider: {result.Provider}");
                stdout.WriteLine($"Target: {result.Target}");
                stdout.WriteLine($"Bundled package: {result.PackageIdentity}");
                stdout.WriteLine($"Installed package: {result.InstalledIdentity ?? "none"}");
                if (result.State == "stale") stdout.WriteLine("Action: run twig skills update with this --provider and --target.");
                foreach (var (scenario, companion) in result.Selections.OrderBy(p => p.Key, StringComparer.Ordinal))
                    stdout.WriteLine($"Selection: {result.Provider}/{scenario} -> {companion}");
                if (result.Selections.Count == 0) stdout.WriteLine("Selections: none (base guidance)");
                foreach (var external in result.Externals) stdout.WriteLine($"External: {external}");
                if (result.Externals.Count == 0) stdout.WriteLine("Externals: none");
                foreach (var warning in result.Warnings) stdout.WriteLine("Warning: " + warning);
                stdout.WriteLine(result.DiscoveryScope);
                stdout.WriteLine(SkillLifecycle.ProviderHint(result.Provider));
                return;
        }
    }

    private static void WriteMinimalRow(TextWriter stdout, string tag, string value) =>
        stdout.WriteLine(tag + "\t" + value);

    private static void WriteError(TextWriter stderr, string format, string message)
    {
        const string Recovery =
            "No force overwrite is available. If an I/O failure interrupted a write, inspect status and preserve the target before recovery; do not assume rollback.";
        switch (format)
        {
            case "json":
            case "json-full":
            case "json-compact":
                stderr.WriteLine(JsonSerializer.Serialize(
                    new SkillCommandErrorOutput(message, Recovery),
                    SkillJsonContext.Default.SkillCommandErrorOutput));
                return;

            case "minimal":
                // Minimal still preserves the correctness surface: the failure row and the recovery
                // row are both tagged, so a downstream reader never mistakes an error for a state.
                WriteMinimalRow(stderr, "error", message);
                WriteMinimalRow(stderr, "recovery", Recovery);
                return;

            default:
                stderr.WriteLine("Twig skills: " + message);
                stderr.WriteLine(Recovery);
                return;
        }
    }
}
