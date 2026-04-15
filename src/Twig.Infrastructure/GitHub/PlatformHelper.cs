using System.Runtime.InteropServices;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Shared platform-detection helpers used by both <c>SelfUpdateCommand</c> (CLI project)
/// and <c>CompanionFirstRunCheck</c> (Infrastructure project).
/// Extracted from <c>SelfUpdateCommand</c> to resolve the cross-project dependency direction.
/// </summary>
internal static class PlatformHelper
{
    /// <summary>
    /// Detects the Runtime Identifier (RID) for the current platform.
    /// In a Native AOT binary, <see cref="RuntimeInformation.RuntimeIdentifier"/> returns
    /// the compile-time target RID directly (e.g., <c>win-x64</c>, <c>linux-x64</c>).
    /// Falls back to manual OS/arch detection if the runtime value is empty or unrecognized.
    /// </summary>
    internal static string? DetectRid()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(rid)
            && (rid.StartsWith("win-", StringComparison.Ordinal)
                || rid.StartsWith("linux-", StringComparison.Ordinal)
                || rid.StartsWith("osx-", StringComparison.Ordinal)))
            return rid;

        // Fallback: manual OS/arch detection
        string os;
        if (OperatingSystem.IsWindows()) os = "win";
        else if (OperatingSystem.IsLinux()) os = "linux";
        else if (OperatingSystem.IsMacOS()) os = "osx";
        else return null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => null
        };

        return arch is null ? null : $"{os}-{arch}";
    }

    /// <summary>
    /// Finds the release asset matching the given platform RID.
    /// Convention: <c>twig-{rid}.zip</c> (Windows) or <c>twig-{rid}.tar.gz</c> (Unix).
    /// </summary>
    internal static (GitHubReleaseAssetInfo? asset, string archiveName) FindAsset(
        GitHubReleaseInfo release, string rid)
    {
        var ext = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var expectedName = $"twig-{rid}{ext}";

        foreach (var asset in release.Assets)
        {
            if (string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                return (asset, expectedName);
        }

        return (null, expectedName);
    }
}