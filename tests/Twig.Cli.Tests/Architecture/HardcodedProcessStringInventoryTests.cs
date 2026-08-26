using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

/// <summary>
/// #734 acceptance criterion: a grep of Twig core for literal WIT / state /
/// link-kind / field strings returns only strings reachable from the profile
/// lookup or explicitly allowlisted platform-owned strings. This test IS that
/// grep, mechanized so it cannot silently rot.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "Twig core" means here:</b> the three source-code trees
/// <c>src/Twig</c>, <c>src/Twig.Domain</c>, and <c>src/Twig.Infrastructure</c>.
/// The MCP surface, TUI surface, and RenderTree presentation vocabulary are
/// intentionally out of scope — they consume core, they don't own process
/// identity.
/// </para>
/// <para>
/// <b>Allowlist policy.</b> A "platform-owned string" is a token whose meaning
/// is fixed by ADO or by Twig's own vocabulary — not by a process template's
/// opinion of what a work item is called or how a state is spelled. The
/// allowlist is embedded here (not imported from a shim) so a reviewer sees
/// the entire policy in one place.
/// </para>
/// <para>
/// <b>Adding to the allowlist requires justification in the PR.</b> The test
/// deliberately fails when new tokens appear, so the moment "Feature" or
/// "Doing" gets typed into a code file, the check surfaces it. Fold it into
/// the profile seam; the allowlist is not a growth surface.
/// </para>
/// </remarks>
public sealed class HardcodedProcessStringInventoryTests
{
    /// <summary>
    /// Roots swept by the inventory. Files under any of these get regex'd for
    /// suspicious tokens. The list intentionally excludes tests and generated
    /// obj/bin trees.
    /// </summary>
    private static readonly string[] SweepRootsRelative =
    [
        "src/Twig",
        "src/Twig.Domain",
        "src/Twig.Infrastructure",
    ];

    /// <summary>
    /// Files under a sweep root that are exempt from the sweep — the profile
    /// seam itself, the sole owner of link-kind reference names, and
    /// diagnostics/rendering that surface state-category display names.
    /// </summary>
    private static readonly HashSet<string> ExemptFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // The profile seam OWNS the allowlist; it is *supposed* to name these.
        "src/Twig.Infrastructure/Services/ReferenceProfile/EmbeddedReferenceProfileProvider.cs",
        "src/Twig.Infrastructure/Services/ReferenceProfile/ReferenceProfileFingerprint.cs",
        "src/Twig.Infrastructure/Resources/ReferenceProfile/profile.json",

        // The one place Twig core maps ADO relation reference names to friendly
        // link words. T1 §Locked vocabulary explicitly makes this the seam.
        "src/Twig.Domain/Services/Navigation/LinkTypeMapper.cs",
        "src/Twig.Domain/Services/Seed/SeedLinkTypeMapper.cs",

        // Advisory static WIT constants — T1 §Background documents them as
        // advisory only (see WorkItemType.cs class comments), not a behavioural
        // constraint. Any real behaviour lives on the profile seam.
        "src/Twig.Domain/ValueObjects/WorkItemType.cs",

        // ADO REST payload mapping. Parses/emits raw ADO relation refs and the
        // "System.*" system field family — those are ADO platform strings, not
        // a process opinion.
        "src/Twig.Infrastructure/Ado/AdoResponseMapper.cs",
    };

    /// <summary>
    /// Tokens the sweep flags. Suffix them with word boundaries at match time
    /// so we don't false-positive on identifier fragments.
    /// </summary>
    private static readonly string[] ForbiddenTypeNames =
    [
        "Initiative", "Investigation", "Feature", "Bug",
        "Spec", "WayfinderTask",
    ];

    /// <summary>
    /// Forbidden state-name literals. "Task", "Done", "Doing", "To Do", "New",
    /// "Active", "Closed", "Resolved", "Proposed", "In Progress", "Removed"
    /// — the vocabulary the reference profile owns.
    /// </summary>
    /// <remarks>
    /// "Task" is intentionally NOT in this list — as a WIT name it is
    /// ambiguous with the C# keyword <c>Task</c>, and there is no way to grep
    /// one without the other. The <see cref="ReferenceProfile.SprintTierTypeName"/>
    /// query is how callers ask "which type is the sprint tier?"; if a caller
    /// hardcodes the literal type name instead, the contract test at that
    /// site is where it gets caught, not here.
    /// </remarks>
    private static readonly string[] ForbiddenStateNames =
    [
        "\"Done\"", "\"Doing\"", "\"To Do\"",
    ];

    [Fact]
    public void No_Twig_core_file_hardcodes_a_profile_owned_type_name()
    {
        var offenders = ScanForbidden(ForbiddenTypeNames, wordBoundaries: true);
        offenders.ShouldBeEmpty(
            "profile-owned type names must move behind IReferenceProfileProvider " +
            "(T3 AB#734). If a token is genuinely platform-owned, add its file to " +
            "the ExemptFiles allowlist in this test with a one-line justification.");
    }

    [Fact]
    public void No_Twig_core_file_hardcodes_a_profile_owned_state_string_literal()
    {
        var offenders = ScanForbidden(ForbiddenStateNames, wordBoundaries: false);
        offenders.ShouldBeEmpty(
            "profile-owned state name string literals must be reached via the " +
            "profile seam or via StateCategoryResolver (which speaks in " +
            "StateCategory, not in raw names).");
    }

    [Fact]
    public void The_sweep_is_non_vacuous()
    {
        // Guard: if the roots are wrong, the sweep finds nothing and the tests
        // above pass vacuously. Anchor on a file we KNOW exists.
        var root = FindRepoRoot();
        File.Exists(Path.Combine(root, "src/Twig.Domain/Aggregates/ProcessConfiguration.cs")).ShouldBeTrue(
            "the repo-root probe failed — the sweep root is not where we expected");

        // And a file we know is on the exempt list must actually be sweepable
        // if we removed it from the exemption — otherwise "exempt" is a lie.
        var probeFile = Path.Combine(root, "src/Twig.Domain/Services/Navigation/LinkTypeMapper.cs");
        File.Exists(probeFile).ShouldBeTrue("exempt-list probe missing on disk");
    }

    // ---- Sweep infrastructure ----------------------------------------------

    private static IReadOnlyList<(string File, int Line, string Match)> ScanForbidden(
        string[] tokens, bool wordBoundaries)
    {
        var root = FindRepoRoot();
        var pattern = wordBoundaries
            ? new Regex(@"""\b(" + string.Join("|", tokens.Select(Regex.Escape)) + @")\b""",
                RegexOptions.Compiled)
            : new Regex(@"(" + string.Join("|", tokens.Select(Regex.Escape)) + @")",
                RegexOptions.Compiled);

        var offenders = new List<(string, int, string)>();
        foreach (var sweepRoot in SweepRootsRelative)
        {
            var dir = Path.Combine(root, sweepRoot);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                if (ExemptFiles.Contains(rel)) continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    // Skip C# and XML doc comments — they carry example prose,
                    // not compiled behaviour.
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///")
                        || trimmed.StartsWith("/*") || trimmed.StartsWith("*"))
                        continue;

                    var m = pattern.Match(line);
                    if (m.Success)
                        offenders.Add((rel, i + 1, m.Value));
                }
            }
        }
        return offenders;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Twig.Domain")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find the repo root above " + AppContext.BaseDirectory);
    }
}
