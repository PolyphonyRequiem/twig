namespace Twig.Infrastructure.Persistence;

internal static class NativePathComparer
{
    internal static StringComparer Instance => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
