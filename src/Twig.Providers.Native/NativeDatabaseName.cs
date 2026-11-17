namespace Twig.Providers.Native;

/// <summary>Portable, exact names for owner-local native databases; never filesystem paths.</summary>
public static class NativeDatabaseName
{
    public static bool IsValid(string? name)
        => name is { Length: >= 1 and <= 64 } && name[0] != '-' && name[^1] != '-'
            && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
