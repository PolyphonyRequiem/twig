using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests for <see cref="FileSeedPublishRulesProvider"/>
/// (file exists, file missing, malformed JSON, partial JSON).
/// </summary>
public class FileSeedPublishRulesProviderTests : IDisposable
{
    private readonly string _tempDir;

    public FileSeedPublishRulesProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twig_seed_rules_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsDefaults_WhenFileMissing()
    {
        var provider = new FileSeedPublishRulesProvider(_tempDir);

        var rules = await provider.GetRulesAsync();

        rules.RequiredFields.ShouldBe(new[] { "System.Title" });
        rules.RequireParent.ShouldBeFalse();
    }

    [Fact]
    public async Task GetRulesAsync_LoadsFromJson_WhenFileExists()
    {
        var json = """
            {
              "requiredFields": ["System.Title", "System.Description"],
              "requireParent": true
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "seed-rules.json"), json);

        var provider = new FileSeedPublishRulesProvider(_tempDir);
        var rules = await provider.GetRulesAsync();

        rules.RequiredFields.ShouldBe(new[] { "System.Title", "System.Description" });
        rules.RequireParent.ShouldBeTrue();
    }

    [Fact]
    public async Task GetRulesAsync_ThrowsTwigConfigurationException_OnMalformedJson()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "seed-rules.json"), "{ not valid json }");

        var provider = new FileSeedPublishRulesProvider(_tempDir);

        var ex = await Should.ThrowAsync<TwigConfigurationException>(() => provider.GetRulesAsync());
        ex.Message.ShouldContain("invalid JSON");
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsDefaults_WhenJsonIsNull()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "seed-rules.json"), "null");

        var provider = new FileSeedPublishRulesProvider(_tempDir);
        var rules = await provider.GetRulesAsync();

        rules.RequiredFields.ShouldBe(new[] { "System.Title" });
        rules.RequireParent.ShouldBeFalse();
    }

    [Fact]
    public async Task GetRulesAsync_HandlesPartialJson_WithDefaults()
    {
        var json = """
            {
              "requireParent": true
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "seed-rules.json"), json);

        var provider = new FileSeedPublishRulesProvider(_tempDir);
        var rules = await provider.GetRulesAsync();

        // Omitted fields should fall back to defaults
        rules.RequiredFields.ShouldBe(new[] { "System.Title" });
        rules.RequireParent.ShouldBeTrue();
    }
}
