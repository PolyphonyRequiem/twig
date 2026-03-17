using Microsoft.Data.Sqlite;
using Shouldly;
using System.Text.Json;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests;

/// <summary>
/// Validates SQLite JSON1 extension availability and source-generated serialization.
/// Corresponds to ITEM-005 and ITEM-006 in twig.prd.md (EPIC-001).
/// </summary>
public class SqliteAndSerializationTests
{
    [Fact]
    public void Sqlite_JsonExtract_ReturnsCorrectValue()
    {
        SQLitePCL.Batteries.Init();

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var createCmd = connection.CreateCommand();
        createCmd.CommandText =
            "CREATE TABLE items (id INTEGER PRIMARY KEY, data TEXT NOT NULL)";
        createCmd.ExecuteNonQuery();

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText =
            "INSERT INTO items (id, data) VALUES (1, '{\"value\":\"hello-twig\",\"id\":42}')";
        insertCmd.ExecuteNonQuery();

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText =
            "SELECT json_extract(data, '$.value'), CAST(json_extract(data, '$.id') AS INTEGER) FROM items WHERE id = 1";

        using var reader = selectCmd.ExecuteReader();
        reader.Read().ShouldBeTrue("Expected one row");

        reader.GetString(0).ShouldBe("hello-twig");
        reader.GetInt32(1).ShouldBe(42);
    }

    [Fact]
    public void TwigJsonContext_SourceGenerated_SerializesAndDeserializesCorrectly()
    {
        // Verify that source-generated AOT serialization works with a real DTO.
        var config = new TwigConfiguration
        {
            Organization = "https://dev.azure.com/testorg",
            Project = "TestProject",
        };

        var json = JsonSerializer.Serialize(config, TwigJsonContext.Default.TwigConfiguration);
        json.ShouldNotBeNullOrEmpty();
        json.ShouldContain("testorg");

        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TwigConfiguration);
        deserialized.ShouldNotBeNull();
        deserialized!.Organization.ShouldBe("https://dev.azure.com/testorg");
        deserialized.Project.ShouldBe("TestProject");
    }

    [Fact]
    public void AdoWorkItemTypeResponse_Deserializes_ColorIconAndIsDisabled()
    {
        const string json = """
            {
                "count": 2,
                "value": [
                    {
                        "name": "Bug",
                        "description": "A defect",
                        "referenceName": "System.Bug",
                        "color": "CC293D",
                        "icon": { "id": "icon_insect", "url": "https://vstf.visualstudio.com/_icon/insect" },
                        "isDisabled": false
                    },
                    {
                        "name": "Hidden Type",
                        "description": "Disabled",
                        "referenceName": "Custom.Hidden",
                        "isDisabled": true
                    }
                ]
            }
            """;

        var result = JsonSerializer.Deserialize(json, TwigJsonContext.Default.AdoWorkItemTypeListResponse);

        result.ShouldNotBeNull();
        result!.Value.ShouldNotBeNull();
        result.Value!.Count.ShouldBe(2);

        // First type — all fields populated
        var bug = result.Value[0];
        bug.Name.ShouldBe("Bug");
        bug.Color.ShouldBe("CC293D");
        bug.IsDisabled.ShouldBeFalse();
        bug.Icon.ShouldNotBeNull();
        bug.Icon!.Id.ShouldBe("icon_insect");
        bug.Icon.Url.ShouldBe("https://vstf.visualstudio.com/_icon/insect");

        // Second type — color and icon omitted, isDisabled true
        var hidden = result.Value[1];
        hidden.Name.ShouldBe("Hidden Type");
        hidden.Color.ShouldBeNull();
        hidden.Icon.ShouldBeNull();
        hidden.IsDisabled.ShouldBeTrue();
    }
}
