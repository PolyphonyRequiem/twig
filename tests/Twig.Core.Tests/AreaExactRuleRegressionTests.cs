using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Core.Tests;

public sealed class AreaExactRuleRegressionTests
{
    [Fact]
    public void PersistedExactAreaRuleMatchesCaseInsensitivelyWithoutIncludingDescendants()
    {
        var connection = new Connection(new("ado"), "collection", "project", "identity");
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        // The persisted operator number is part of the compatibility contract.
        var query = new Query((QueryKind)8, "System.AreaPath", FieldValue.String("Project\\Area"));
        WorkItem Item(string area) => new(key, "Native.Type",
            FieldValues.Empty.Set("System.AreaPath", FieldValue.String(area)), new(key, "1"), true);
        Twig.Core.Core.Matches(query, Item("project\\AREA")).Value.ShouldBeTrue();
        Twig.Core.Core.Matches(query, Item("Project\\Area\\Child")).Value.ShouldBeFalse();
        Twig.Core.Core.Matches(query, Item("Project\\AreaOther")).Value.ShouldBeFalse();
    }
}
