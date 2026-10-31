using System.Text;
using Shouldly;
using Xunit;

namespace Twig.Core.Tests;

public sealed class QueryOperatorInvariantTests
{
    [Fact]
    public void CanonicalQueryBytesFreezeOperandsAndBindTheirOrder()
    {
        var equal = new Query(QueryKind.Equal, "field", FieldValue.String("x"));
        const string expected = """[1,"field",[1,"x"],[]]""";
        Encoding.UTF8.GetString(Core.QueryBytes(equal)).ShouldBe(expected);
        var bytes = Core.QueryBytes(equal);
        bytes[0] = 0;
        Encoding.UTF8.GetString(Core.QueryBytes(equal)).ShouldBe(expected);
        var children = new List<Query> { equal, new(QueryKind.All) };
        var query = new Query(QueryKind.Or, children: children);
        children.Clear();
        query.Children.Length.ShouldBe(2);
        Core.QueryBytes(query).ShouldNotBe(Core.QueryBytes(
            new(QueryKind.Or, children: query.Children.Reverse())));
    }

    [Fact]
    public void TextPathsAndThresholdsHaveExplicitTypedSemantics()
    {
        var key = ItemKey.Remote(new("ado"), "scope", "1");
        var item = new WorkItem(key, "native", new([
            KeyValuePair.Create("text", FieldValue.String("Mixed CASE title")),
            KeyValuePair.Create("path", FieldValue.String("Root\\Area\\Child")),
            KeyValuePair.Create("number", FieldValue.Decimal(2.5m)),
            KeyValuePair.Create("empty", FieldValue.Null),
        ]), null);
        Core.Matches(new(QueryKind.Contains, "text", FieldValue.String("case")), item).Value.ShouldBeTrue();
        Core.Matches(new(QueryKind.Equal, "text", FieldValue.String("mixed case title")), item).Value.ShouldBeFalse();
        Core.Matches(new(QueryKind.Under, "path", FieldValue.String("root\\area")), item).Value.ShouldBeTrue();
        Core.Matches(new(QueryKind.Under, "path", FieldValue.String("Root\\Are")), item).Value.ShouldBeFalse();
        Core.Matches(new(QueryKind.GreaterOrEqual, "number", FieldValue.Decimal(2.5m)), item).Value.ShouldBeTrue();
        Core.Matches(new(QueryKind.GreaterOrEqual, "number", FieldValue.Decimal(2.6m)), item).Value.ShouldBeFalse();
        Core.Matches(new(QueryKind.Contains, "empty", FieldValue.String("")), item).Value.ShouldBeFalse();
        Core.Matches(new(QueryKind.Contains, "missing", FieldValue.String("")), item).Error.Code.ShouldBe(ErrorCode.Unavailable);
        Core.Matches(new(QueryKind.GreaterOrEqual, "number", FieldValue.Integer(2)), item).Error.Code.ShouldBe(ErrorCode.Unavailable);
    }

    [Fact]
    public void OperatorsRejectOperandsWithoutTheirDeclaredTypes()
    {
        Should.Throw<ArgumentException>(() => new Query(QueryKind.Contains, "field", FieldValue.Null));
        Should.Throw<ArgumentException>(() => new Query(QueryKind.GreaterOrEqual, "field", FieldValue.String("2")));
        Should.Throw<ArgumentException>(() => new Query(QueryKind.Under, "field", FieldValue.Integer(2)));
    }
}
