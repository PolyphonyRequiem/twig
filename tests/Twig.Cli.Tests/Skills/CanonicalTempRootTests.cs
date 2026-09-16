using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Skills;
using Xunit;

namespace Twig.Cli.Tests.Skills;

/// <summary>
/// Regression cover for the macOS release gate: skill fixtures must build their sandbox under a
/// canonical temp root, because <see cref="SkillPaths.RejectLinks"/> refuses any symlink on the
/// path to the target and <c>Path.GetTempPath()</c> is <c>/var/folders/…</c> on macOS, under the
/// <c>/var</c> → <c>/private/var</c> symlink.
/// </summary>
public sealed class CanonicalTempRootTests : IDisposable
{
    private readonly string scratch = CanonicalTempRoot.Create("twig-canon-");

    [Fact]
    public void Created_root_exists_and_is_accepted_by_the_skill_link_guard()
    {
        Directory.Exists(scratch).ShouldBeTrue();
        Should.NotThrow(() => SkillPaths.RejectLinks(Path.Combine(scratch, "skills")));
    }

    [Fact]
    public void Created_root_has_no_symlinked_ancestor()
    {
        for (string? current = scratch; current is not null; current = Path.GetDirectoryName(current))
            Directory.ResolveLinkTarget(current, returnFinalTarget: false).ShouldBeNull($"'{current}' is a link");
    }

    [Fact]
    public void Canonicalize_is_idempotent()
    {
        CanonicalTempRoot.Canonicalize(scratch).ShouldBe(scratch);
    }

    [Fact]
    public void Canonicalize_resolves_a_symlinked_ancestor()
    {
        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        var link = Path.Combine(scratch, "link");
        Directory.CreateSymbolicLink(link, real);

        CanonicalTempRoot.Canonicalize(Path.Combine(link, "child"))
            .ShouldBe(Path.Combine(real, "child"));
    }

    [Fact]
    public void Deliberate_symlinks_are_still_rejected_the_guard_is_not_weakened()
    {
        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(scratch, "link");
        Directory.CreateSymbolicLink(link, real);

        Should.Throw<SkillLifecycleException>(() => SkillPaths.RejectLinks(link));
        Should.Throw<SkillLifecycleException>(() => SkillPaths.RejectLinks(Path.Combine(link, "nested")));
    }

    public void Dispose()
    {
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }
}
