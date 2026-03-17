using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class PendingNoteTests
{
    [Fact]
    public void Construction_SetsProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var note = new PendingNote("Hello world", now, false);
        note.Text.ShouldBe("Hello world");
        note.CreatedAt.ShouldBe(now);
        note.IsHtml.ShouldBeFalse();
    }

    [Fact]
    public void Construction_HtmlNote()
    {
        var now = DateTimeOffset.UtcNow;
        var note = new PendingNote("<b>Bold</b>", now, true);
        note.Text.ShouldBe("<b>Bold</b>");
        note.IsHtml.ShouldBeTrue();
    }

    [Fact]
    public void Equality_SameValues()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new PendingNote("text", time, false);
        var b = new PendingNote("text", time, false);
        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentText()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new PendingNote("text1", time, false);
        var b = new PendingNote("text2", time, false);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentTimestamp()
    {
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var a = new PendingNote("text", t1, false);
        var b = new PendingNote("text", t2, false);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentIsHtml()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new PendingNote("text", time, false);
        var b = new PendingNote("text", time, true);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void EmptyText_AllowedByStruct()
    {
        var note = new PendingNote("", DateTimeOffset.UtcNow, false);
        note.Text.ShouldBe("");
    }
}
