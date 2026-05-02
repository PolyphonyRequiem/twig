using Shouldly;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public sealed class RenderBreakpointTests
{
    [Fact]
    public void Enum_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<RenderBreakpoint>();
        values.Length.ShouldBe(3);
    }

    [Fact]
    public void Compact_HasOrdinalZero()
    {
        ((int)RenderBreakpoint.Compact).ShouldBe(0);
    }

    [Fact]
    public void Standard_HasOrdinalOne()
    {
        ((int)RenderBreakpoint.Standard).ShouldBe(1);
    }

    [Fact]
    public void Wide_HasOrdinalTwo()
    {
        ((int)RenderBreakpoint.Wide).ShouldBe(2);
    }

    [Fact]
    public void Enum_ParsesAllNamedValues()
    {
        Enum.TryParse<RenderBreakpoint>("Compact", out _).ShouldBeTrue();
        Enum.TryParse<RenderBreakpoint>("Standard", out _).ShouldBeTrue();
        Enum.TryParse<RenderBreakpoint>("Wide", out _).ShouldBeTrue();
    }

    [Fact]
    public void Enum_DoesNotParseInvalidName()
    {
        Enum.TryParse<RenderBreakpoint>("Narrow", out _).ShouldBeFalse();
    }
}
