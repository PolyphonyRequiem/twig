namespace Twig.Cli.Tests.TestSupport;

/// <summary>
/// Creates scratch directories under a <em>canonical</em> temp root — one whose ancestors contain
/// no symlinks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path.GetTempPath"/> is not canonical on every platform. On macOS it returns
/// <c>/var/folders/…</c>, and <c>/var</c> is a symlink to <c>/private/var</c>. Production code that
/// deliberately refuses symlinked paths (skill installation, which rejects any link on the path to
/// the target) therefore refuses the fixture's own sandbox, and the test fails for an environmental
/// reason that has nothing to do with the behaviour under test.
/// </para>
/// <para>
/// This helper resolves the link chain <em>before</em> the sandbox path is handed to production
/// code, so fixtures exercise the real contract instead of tripping over the host's temp layout.
/// It does not weaken that contract: a symlink deliberately planted inside the returned root is
/// still a symlink and is still rejected.
/// </para>
/// </remarks>
internal static class CanonicalTempRoot
{
    /// <summary>Creates, and returns the path of, a fresh uniquely named directory under the canonical temp root.</summary>
    /// <param name="prefix">Prefix for the directory name; a GUID is appended.</param>
    public static string Create(string prefix)
    {
        var path = Path.Combine(Canonicalize(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Resolves every symlinked component of <paramref name="path"/>, from the root down.</summary>
    internal static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);

        var segments = new List<string>();
        for (string? current = full; current is not null;)
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                segments.Add(current);
                break;
            }

            segments.Add(current[parent.Length..].Trim(Path.DirectorySeparatorChar));
            current = parent;
        }

        segments.Reverse();

        var resolved = segments[0];
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Length == 0) continue;
            resolved = Path.Combine(resolved, segment);

            // Only directories can appear as an ancestor; a missing component cannot be a link.
            if (!Directory.Exists(resolved)) continue;

            // Re-resolve after each substitution: returnFinalTarget follows a chain of links, but
            // the final target may itself sit under a symlinked ancestor that has not been walked.
            // Bounded so a pathological layout cannot spin.
            for (var hop = 0; hop < 40; hop++)
            {
                if (Directory.ResolveLinkTarget(resolved, returnFinalTarget: true) is not { } target) break;
                resolved = Canonicalize(target.FullName);
            }
        }

        return resolved;
    }
}
