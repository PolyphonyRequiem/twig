using Xunit;

namespace Twig.Cli.Tests.TestSupport;

/// <summary>Runs migration fixtures only where native host closure can be verified.</summary>
public sealed class HostMigrationFactAttribute : FactAttribute
{
    public HostMigrationFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Skip = "Native migration requires Windows or Linux host inventory; this platform cannot verify host closure.";
    }
}

/// <summary>Keeps parameterized migration scenarios discoverable on unsupported platforms.</summary>
public sealed class HostMigrationTheoryAttribute : TheoryAttribute
{
    public HostMigrationTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            Skip = "Native migration requires Windows or Linux host inventory; this platform cannot verify host closure.";
    }
}
