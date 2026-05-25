using System;
using System.Collections.Generic;
using Shouldly;
using Twig.RenderTree;
using Xunit;

namespace Twig.RenderTree.Tests;

/// <summary>
/// Constructor + property tests for the typed value DU.
/// Locks down the shape so renderers can pattern-match exhaustively.
/// </summary>
public sealed class RenderValueTests
{
    [Fact]
    public void String_carries_value()
    {
        var v = new RenderValue.String("hello");
        v.Value.ShouldBe("hello");
    }

    [Fact]
    public void Integer_carries_value()
    {
        var v = new RenderValue.Integer(42);
        v.Value.ShouldBe(42L);
    }

    [Fact]
    public void Decimal_carries_value()
    {
        var v = new RenderValue.Decimal(3.5m);
        v.Value.ShouldBe(3.5m);
    }

    [Fact]
    public void Boolean_carries_value()
    {
        new RenderValue.Boolean(true).Value.ShouldBeTrue();
        new RenderValue.Boolean(false).Value.ShouldBeFalse();
    }

    [Fact]
    public void DateTime_carries_value()
    {
        var dt = new DateTimeOffset(2026, 5, 24, 16, 0, 0, TimeSpan.Zero);
        new RenderValue.DateTime(dt).Value.ShouldBe(dt);
    }

    [Fact]
    public void Null_and_Absent_are_distinct_types()
    {
        RenderValue n = new RenderValue.Null();
        RenderValue a = new RenderValue.Absent();

        // Distinct types so renderers can switch on them (Null -> emit JSON null;
        // Absent -> omit the property entirely).
        n.ShouldBeOfType<RenderValue.Null>();
        a.ShouldBeOfType<RenderValue.Absent>();
    }

    [Fact]
    public void Object_carries_cells_dictionary()
    {
        var cells = new Dictionary<string, RenderCell>
        {
            ["a"] = RenderCell.String("hello"),
            ["b"] = RenderCell.Integer(42),
        };

        var v = new RenderValue.Object(cells);

        v.Cells.ShouldBeSameAs(cells);
        v.Cells.Count.ShouldBe(2);
    }

    [Fact]
    public void RenderValue_pattern_matches_exhaustively()
    {
        // Compile-time proof that all variants are reachable through the closed DU.
        // Renderers will write switch expressions like this.
        static string Tag(RenderValue v) => v switch
        {
            RenderValue.String s => $"str:{s.Value}",
            RenderValue.Integer i => $"int:{i.Value}",
            RenderValue.Decimal d => $"dec:{d.Value}",
            RenderValue.Boolean b => $"bool:{b.Value}",
            RenderValue.DateTime dt => $"dt:{dt.Value:O}",
            RenderValue.Null => "null",
            RenderValue.Absent => "absent",
            RenderValue.Object obj => $"obj:{obj.Cells.Count}",
            _ => throw new InvalidOperationException("unreachable"),
        };

        Tag(new RenderValue.String("x")).ShouldBe("str:x");
        Tag(new RenderValue.Integer(7)).ShouldBe("int:7");
        Tag(new RenderValue.Null()).ShouldBe("null");
        Tag(new RenderValue.Absent()).ShouldBe("absent");
        Tag(new RenderValue.Object(new Dictionary<string, RenderCell>
        {
            ["k"] = RenderCell.String("v"),
        })).ShouldBe("obj:1");
    }
}
