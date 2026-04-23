using System.Diagnostics;
using Shouldly;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class ParentProcessWatchdogTests
{
    [Fact]
    public void GetParentProcessId_ReturnsPositiveValue()
    {
        var pid = ParentProcessWatchdog.GetParentProcessId();
        pid.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GetParentProcessId_ReturnsValidRunningProcess()
    {
        var pid = ParentProcessWatchdog.GetParentProcessId();
        var parent = Process.GetProcessById(pid);
        parent.HasExited.ShouldBeFalse();
    }

    [Fact]
    public void GetParentProcessId_ReturnsSameValueOnRepeatedCalls()
    {
        var pid1 = ParentProcessWatchdog.GetParentProcessId();
        var pid2 = ParentProcessWatchdog.GetParentProcessId();
        pid1.ShouldBe(pid2);
    }
}
