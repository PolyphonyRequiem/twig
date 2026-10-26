namespace Twig.Infrastructure.Plan;

/// <summary>
/// Resolves a path to its physical location by following every symlink or junction one
/// segment at a time, so a linked intermediate directory cannot hide where a file really is.
/// Only a segment that genuinely does not exist (<see cref="FileNotFoundException"/> or
/// <see cref="DirectoryNotFoundException"/>) is preserved verbatim, so a not-yet-created
/// proposal file still resolves. Every other failure — an invalid path, a segment that
/// cannot be inspected, or a link chain deeper than <see cref="MaxLinkDepth"/> (a loop) —
/// is a named refusal; the unresolved path is never returned in its place.
/// </summary>
internal static class CanonicalPathResolver
{
    internal const int MaxLinkDepth = 40;

    internal enum RefusalKind
    {
        InvalidPath,
        InaccessibleSegment,
        LinkDepthExceeded,
    }

    internal readonly record struct Resolution(string? Path, RefusalKind? Refusal, string? Reason)
    {
        internal static Resolution Resolved(string path) => new(path, null, null);

        internal static Resolution Refused(RefusalKind kind, string reason) => new(null, kind, reason);
    }

    internal static Resolution Resolve(string path)
    {
        string absolute;
        try
        {
            absolute = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Resolution.Refused(RefusalKind.InvalidPath, $"'{path}' is not a valid path: {ex.Message}");
        }

        return ResolveComponents(absolute, depth: 0);
    }

    private static Resolution ResolveComponents(string absolute, int depth)
    {
        if (depth > MaxLinkDepth)
            return Resolution.Refused(RefusalKind.LinkDepthExceeded,
                $"link resolution exceeded {MaxLinkDepth} levels at '{absolute}' (a link loop?).");

        var root = System.IO.Path.GetPathRoot(absolute) ?? string.Empty;
        if (string.IsNullOrEmpty(root))
            root = System.IO.Path.DirectorySeparatorChar.ToString();

        var rel = absolute.Length > root.Length ? absolute[root.Length..] : string.Empty;
        var parts = rel.Split(
            new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = System.IO.Path.Combine(current, parts[i]);

            string? link;
            try
            {
                var attributes = File.GetAttributes(current);
                link = (attributes & FileAttributes.Directory) != 0
                    ? Directory.ResolveLinkTarget(current, returnFinalTarget: false)?.FullName
                    : File.ResolveLinkTarget(current, returnFinalTarget: false)?.FullName;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                for (var j = i + 1; j < parts.Length; j++)
                    current = System.IO.Path.Combine(current, parts[j]);
                return Resolution.Resolved(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Resolution.Refused(RefusalKind.InaccessibleSegment,
                    $"path segment '{current}' could not be inspected: {ex.Message}");
            }

            if (link is null) continue;

            if (!System.IO.Path.IsPathRooted(link))
            {
                var parent = System.IO.Path.GetDirectoryName(current) ?? current;
                link = System.IO.Path.Combine(parent, link);
            }

            string linkTarget;
            try
            {
                linkTarget = System.IO.Path.GetFullPath(link);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Resolution.Refused(RefusalKind.InvalidPath,
                    $"link '{current}' has an invalid target: {ex.Message}");
            }

            var target = ResolveComponents(linkTarget, depth + 1);
            if (target.Refusal is not null)
                return target;
            current = target.Path!;
        }

        return Resolution.Resolved(current);
    }
}
