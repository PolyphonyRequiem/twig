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
        if (OperatingSystem.IsWindows()) return; // Windows symlink privilege is not assumed.

        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        var link = Path.Combine(scratch, "link");
        Directory.CreateSymbolicLink(link, real);

        CanonicalTempRoot.Canonicalize(Path.Combine(link, "child"))
            .ShouldBe(Path.Combine(real, "child"));
    }

    [Fact]
    public void Canonicalize_resolves_a_link_whose_own_target_sits_under_a_link()
    {
        if (OperatingSystem.IsWindows()) return;

        // outer -> inner/real, and inner is itself a link. A single ResolveLinkTarget hop lands on
        // a path that still contains a symlink, which is exactly the case the guard would refuse.
        var realInner = Path.Combine(scratch, "inner-real");
        Directory.CreateDirectory(Path.Combine(realInner, "real"));
        var innerLink = Path.Combine(scratch, "inner");
        Directory.CreateSymbolicLink(innerLink, realInner);
        var outer = Path.Combine(scratch, "outer");
        Directory.CreateSymbolicLink(outer, Path.Combine(innerLink, "real"));

        var canonical = CanonicalTempRoot.Canonicalize(outer);

        canonical.ShouldBe(Path.Combine(realInner, "real"));
        Should.NotThrow(() => SkillPaths.RejectLinks(canonical));
    }

    [Fact]
    public void Canonicalize_resolves_a_link_declared_with_a_relative_target()
    {
        if (OperatingSystem.IsWindows()) return;

        var real = Path.Combine(scratch, "rel-real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(scratch, "rel-link");
        Directory.CreateSymbolicLink(link, "./rel-real"); // relative, resolved against the link's dir

        CanonicalTempRoot.Canonicalize(link).ShouldBe(real);
    }

    [Fact]
    public void Canonicalize_leaves_a_not_yet_existing_tail_alone()
    {
        var tail = Path.Combine(scratch, "does-not-exist", "nor-this");

        CanonicalTempRoot.Canonicalize(tail).ShouldBe(tail);
    }

    [Fact]
    public void Canonicalize_handles_the_filesystem_root()
    {
        var root = Path.GetPathRoot(scratch)!;

        Should.NotThrow(() => CanonicalTempRoot.Canonicalize(root));
    }

    [Fact]
    public void Deliberate_symlinks_are_still_rejected_the_guard_is_not_weakened()
    {
        if (OperatingSystem.IsWindows()) return;

        var real = Path.Combine(scratch, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(scratch, "link");
        Directory.CreateSymbolicLink(link, real);

        Should.Throw<SkillLifecycleException>(() => SkillPaths.RejectLinks(link));
        Should.Throw<SkillLifecycleException>(() => SkillPaths.RejectLinks(Path.Combine(link, "nested")));
    }

    [Fact]
    public void A_dangling_link_inside_the_canonical_root_is_still_rejected()
    {
        if (OperatingSystem.IsWindows()) return;

        var link = Path.Combine(scratch, "dangling");
        Directory.CreateSymbolicLink(link, Path.Combine(scratch, "no-such-target"));

        Should.Throw<SkillLifecycleException>(() => SkillPaths.RejectLinks(link));
    }

    public void Dispose()
    {
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
    }
}
