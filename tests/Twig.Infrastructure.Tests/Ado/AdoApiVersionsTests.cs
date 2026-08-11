using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Guards the per-route api-version pins (AB#233, spec Implementation Decision 7).
/// </summary>
/// <remarks>
/// <para>
/// The fetch layer used to share one <c>ApiVersion = "7.1"</c> constant across every ADO
/// call. That is unsafe here because the api-version selects the response <b>schema</b>,
/// not just the route version: the same per-type fields URL returns disjoint attribute
/// sets at <c>7.1-preview.1</c> and <c>7.1-preview.2</c> (59 required fields at one
/// version, ZERO at the other), and the process-wide fields route 404s at plain
/// <c>7.1</c> with a count-shaped error body that misreads as thin data.
/// </para>
/// <para>
/// Two regressions are guarded, and they are different failure modes. <b>Drift</b> — a
/// pinned version silently changing — is caught by the per-constant assertions, which
/// spell the version out as a literal so a change to <see cref="AdoApiVersions"/> must be
/// made deliberately in two places. <b>Bypass</b> — a new or edited call site inlining
/// <c>api-version=7.1</c> instead of naming a constant — is caught by the source scan,
/// which is the mechanism that actually keeps the pins central as the layer grows.
/// </para>
/// </remarks>
public sealed class AdoApiVersionsTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Twig.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        dir.ShouldNotBeNull("Could not find repository root (looked for Twig.slnx)");
        return dir;
    }

    // ── Drift: the GA-pinned routes ──────────────────────────────────────────
    // These carry the versions the shipped behaviour is verified against. #233 is a
    // refactor: every one of them must be exactly what it was before the change.

    [Theory]
    [InlineData(nameof(AdoApiVersions.WorkItems))]
    [InlineData(nameof(AdoApiVersions.WorkItemTemplate))]
    [InlineData(nameof(AdoApiVersions.WorkItemUpdates))]
    [InlineData(nameof(AdoApiVersions.Wiql))]
    [InlineData(nameof(AdoApiVersions.WorkItemTypes))]
    [InlineData(nameof(AdoApiVersions.Fields))]
    [InlineData(nameof(AdoApiVersions.ClassificationNodes))]
    [InlineData(nameof(AdoApiVersions.TeamIterations))]
    [InlineData(nameof(AdoApiVersions.TeamFieldValues))]
    [InlineData(nameof(AdoApiVersions.ProcessConfiguration))]
    [InlineData(nameof(AdoApiVersions.Projects))]
    [InlineData(nameof(AdoApiVersions.Profile))]
    [InlineData(nameof(AdoApiVersions.GitPullRequests))]
    [InlineData(nameof(AdoApiVersions.GitRepositories))]
    [InlineData(nameof(AdoApiVersions.ProcessRules))]
    [InlineData(nameof(AdoApiVersions.ProcessLayout))]
    public void ExistingRoutes_StayOnTheirShippedVersion(string constantName)
    {
        ReadConstant(constantName).ShouldBe(
            "7.1",
            $"{constantName} carries the version its shipped behaviour is verified against. " +
            "Changing it changes what the server returns, which is a behaviour change, not a refactor.");
    }

    // ── Drift: the routes where the version is load-bearing ──────────────────

    [Fact]
    public void ProcessFields_IsPinnedToPreview1_BecausePlain71Is404()
    {
        // The process-wide fields route is not served at plain 7.1 at all. Its 404 body is
        // count-shaped ({"count":1,"value":{"Message":...}}), so calling it at GA reads as
        // thin data rather than a failure.
        AdoApiVersions.ProcessFields.ShouldBe("7.1-preview.1");
    }

    [Fact]
    public void ProcessWorkItemTypes_IsPinnedToPreview2_ForReferenceNameAndCustomization()
    {
        // preview.1 returns id + class on this URL; preview.2 returns referenceName +
        // customization. Display names lie across processes, so only preview.2 supports
        // matching two processes to each other.
        AdoApiVersions.ProcessWorkItemTypes.ShouldBe("7.1-preview.2");
    }

    [Fact]
    public void ProcessWorkItemTypeFields_IsPinnedToPreview2_ForRequiredAndDefaultValue()
    {
        // Same URL, same counts, disjoint keys: preview.1 has neither `required` nor
        // `defaultValue`. A survey at preview.1 reported required on 0 of 628 rows; the
        // identical survey at preview.2 reported 59.
        AdoApiVersions.ProcessWorkItemTypeFields.ShouldBe("7.1-preview.2");
    }

    [Fact]
    public void ProcessLists_IsPinnedToPreview1()
    {
        AdoApiVersions.ProcessLists.ShouldBe("7.1-preview.1");
    }

    [Fact]
    public void WorkItemComments_StayOnPreview4_BecauseCommentsHaveNoGaRoute()
    {
        AdoApiVersions.WorkItemComments.ShouldBe("7.1-preview.4");
    }

    // ── Discoverability ──────────────────────────────────────────────────────

    [Fact]
    public void EveryPinnedVersion_IsDeclaredOnAdoApiVersions()
    {
        var constants = PinnedConstants();

        constants.ShouldNotBeEmpty();
        constants.Count.ShouldBeGreaterThanOrEqualTo(
            20,
            "the pins are supposed to be discoverable from one place; a shrinking set means " +
            "a route moved its version somewhere else");
    }

    [Fact]
    public void EveryPinnedVersion_LooksLikeAnApiVersion()
    {
        foreach (var (name, value) in PinnedConstants())
        {
            Regex.IsMatch(value, @"^\d+\.\d+(-preview\.\d+)?$").ShouldBeTrue(
                $"{name} = '{value}' is not a well-formed ADO api-version");
        }
    }

    // ── Bypass: nothing in the fetch layer may inline a version ──────────────

    [Fact]
    public void NoAdoCallSite_InlinesAnApiVersionLiteral()
    {
        var adoDir = Path.Combine(FindRepoRoot(), "src", "Twig.Infrastructure", "Ado");
        Directory.Exists(adoDir).ShouldBeTrue($"expected the ADO fetch layer at {adoDir}");

        // Matches anything after `api-version=` that is NOT an {AdoApiVersions.X}
        // interpolation — a bare literal (`api-version=7.1`) or an interpolation of some
        // other symbol (`api-version={ApiVersion}`, the shared constant this ticket
        // removed). Both are the bypass; only the pin table is allowed to name a version.
        var inlined = new Regex(@"api-version=(?!\{AdoApiVersions\.)");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(adoDir, "*.cs", SearchOption.AllDirectories))
        {
            // The pin table itself documents the versions in prose — that is where they belong.
            if (Path.GetFileName(path) == "AdoApiVersions.cs") continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                // Doc comments may cite a route's version descriptively.
                if (trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                if (inlined.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{i + 1}: {trimmed}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "every ADO call site must name its pinned version from AdoApiVersions so the pins " +
            "stay discoverable from one place, each with a comment saying what it buys. " +
            "Offending lines:\n" + string.Join("\n", offenders));
    }

    private static string ReadConstant(string name)
    {
        var field = typeof(AdoApiVersions).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        field.ShouldNotBeNull($"AdoApiVersions.{name} does not exist");
        return (string)field.GetRawConstantValue()!;
    }

    private static IReadOnlyList<(string Name, string Value)> PinnedConstants()
        => typeof(AdoApiVersions)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();
}
