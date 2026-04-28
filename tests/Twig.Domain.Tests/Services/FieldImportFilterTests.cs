using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="FieldImportFilter"/>.
/// </summary>
public class FieldImportFilterTests
{
    // ── Core field exclusion ────────────────────────────────────────

    [Theory]
    [InlineData("System.Id")]
    [InlineData("System.Rev")]
    [InlineData("System.WorkItemType")]
    [InlineData("System.Title")]
    [InlineData("System.State")]
    [InlineData("System.AssignedTo")]
    [InlineData("System.IterationPath")]
    [InlineData("System.AreaPath")]
    public void ShouldImport_CoreField_ReturnsFalse(string refName)
    {
        var fieldDef = new FieldDefinition(refName, refName, "string", false);
        FieldImportFilter.ShouldImport(refName, fieldDef).ShouldBeFalse();
    }

    [Theory]
    [InlineData("system.id")]
    [InlineData("SYSTEM.TITLE")]
    [InlineData("System.STATE")]
    public void ShouldImport_CoreField_CaseInsensitive(string refName)
    {
        var fieldDef = new FieldDefinition(refName, refName, "string", false);
        FieldImportFilter.ShouldImport(refName, fieldDef).ShouldBeFalse();
    }

    // ── Read-only exclusion ─────────────────────────────────────────

    [Fact]
    public void ShouldImport_ReadOnlyNonDisplayWorthy_ReturnsFalse()
    {
        var fieldDef = new FieldDefinition("System.Watermark", "Watermark", "integer", true);
        FieldImportFilter.ShouldImport("System.Watermark", fieldDef).ShouldBeFalse();
    }

    // ── Display-worthy readonly inclusion ────────────────────────────

    [Theory]
    [InlineData("System.CreatedDate")]
    [InlineData("System.ChangedDate")]
    [InlineData("System.CreatedBy")]
    [InlineData("System.ChangedBy")]
    [InlineData("System.Tags")]
    [InlineData("System.Description")]
    [InlineData("System.BoardColumn")]
    [InlineData("System.BoardColumnDone")]
    public void ShouldImport_DisplayWorthyReadOnly_ReturnsTrue(string refName)
    {
        var fieldDef = new FieldDefinition(refName, refName, "string", true);
        FieldImportFilter.ShouldImport(refName, fieldDef).ShouldBeTrue();
    }

    [Fact]
    public void ShouldImport_DisplayWorthyReadOnly_CaseInsensitive()
    {
        var fieldDef = new FieldDefinition("system.tags", "Tags", "plainText", true);
        FieldImportFilter.ShouldImport("system.tags", fieldDef).ShouldBeTrue();
    }

    // ── Data type filtering ─────────────────────────────────────────

    [Theory]
    [InlineData("string")]
    [InlineData("integer")]
    [InlineData("double")]
    [InlineData("dateTime")]
    [InlineData("html")]
    [InlineData("plainText")]
    public void ShouldImport_ImportableDataType_ReturnsTrue(string dataType)
    {
        var fieldDef = new FieldDefinition("Custom.MyField", "My Field", dataType, false);
        FieldImportFilter.ShouldImport("Custom.MyField", fieldDef).ShouldBeTrue();
    }

    [Theory]
    [InlineData("treePath")]
    [InlineData("history")]
    [InlineData("guid")]
    [InlineData("boolean")]
    public void ShouldImport_NonImportableDataType_ReturnsFalse(string dataType)
    {
        var fieldDef = new FieldDefinition("Custom.MyField", "My Field", dataType, false);
        FieldImportFilter.ShouldImport("Custom.MyField", fieldDef).ShouldBeFalse();
    }

    // ── Null fieldDef fallback ──────────────────────────────────────

    [Fact]
    public void ShouldImport_NullFieldDef_NonCoreField_ReturnsTrue()
    {
        FieldImportFilter.ShouldImport("Custom.Priority", null).ShouldBeTrue();
    }

    [Fact]
    public void ShouldImport_NullFieldDef_CoreField_ReturnsFalse()
    {
        FieldImportFilter.ShouldImport("System.Id", null).ShouldBeFalse();
    }

    // ── Standard editable field ─────────────────────────────────────

    [Fact]
    public void ShouldImport_EditableStringField_ReturnsTrue()
    {
        var fieldDef = new FieldDefinition("Microsoft.VSTS.Common.Priority", "Priority", "integer", false);
        FieldImportFilter.ShouldImport("Microsoft.VSTS.Common.Priority", fieldDef).ShouldBeTrue();
    }

    // ── Intentional boolean exclusion ───────────────────────────────

    [Fact]
    public void ShouldImport_EditableBooleanField_ReturnsFalse()
    {
        // Boolean is intentionally excluded: ADO returns true/false which
        // our string-only Fields dictionary cannot faithfully represent.
        var fieldDef = new FieldDefinition("Custom.IsBlocked", "Is Blocked", "boolean", false);
        FieldImportFilter.ShouldImport("Custom.IsBlocked", fieldDef).ShouldBeFalse();
    }
}
