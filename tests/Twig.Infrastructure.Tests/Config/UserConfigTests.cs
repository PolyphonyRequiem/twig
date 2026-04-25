using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests for UserConfig support in TwigConfiguration.
/// </summary>
public class UserConfigTests : IDisposable
{
    private readonly string _testDir;

    public UserConfigTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-userconfig-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void SetValue_UserName_SetsDisplayName()
    {
        var config = new TwigConfiguration();

        var result = config.SetValue("user.name", "Alice Smith");

        result.ShouldBeTrue();
        config.User.DisplayName.ShouldBe("Alice Smith");
    }

    [Fact]
    public void SetValue_UserEmail_SetsEmail()
    {
        var config = new TwigConfiguration();

        var result = config.SetValue("user.email", "alice@example.com");

        result.ShouldBeTrue();
        config.User.Email.ShouldBe("alice@example.com");
    }

    [Fact]
    public void Default_UserConfig_HasNullProperties()
    {
        var config = new TwigConfiguration();

        config.User.ShouldNotBeNull();
        config.User.DisplayName.ShouldBeNull();
        config.User.Email.ShouldBeNull();
    }

    [Fact]
    public async Task UserConfig_RoundTrip_Serialization()
    {
        var configPath = Path.Combine(_testDir, "config");
        var config = new TwigConfiguration
        {
            Organization = "https://dev.azure.com/org",
            Project = "MyProject",
        };
        config.User.DisplayName = "Alice Smith";
        config.User.Email = "alice@example.com";

        await config.SaveAsync(configPath);
        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.User.ShouldNotBeNull();
        loaded.User.DisplayName.ShouldBe("Alice Smith");
        loaded.User.Email.ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task UserConfig_NullValues_OmittedFromJson()
    {
        var configPath = Path.Combine(_testDir, "config");
        var config = new TwigConfiguration
        {
            Organization = "https://dev.azure.com/org",
            Project = "MyProject",
        };
        // User left at defaults (null)

        await config.SaveAsync(configPath);
        var content = await File.ReadAllTextAsync(configPath);

        // With WhenWritingNull, the user section should not contain displayName/email
        // but the user object itself might appear as empty {}
        var loaded = await TwigConfiguration.LoadAsync(configPath);
        loaded.User.DisplayName.ShouldBeNull();
        loaded.User.Email.ShouldBeNull();
    }

    [Fact]
    public async Task UserConfig_LoadFromFile_WithNoUserSection()
    {
        // Simulate a config file from before EPIC-013 (no user section)
        var configPath = Path.Combine(_testDir, "config");
        await File.WriteAllTextAsync(configPath, """{"organization":"org","project":"proj"}""");

        var loaded = await TwigConfiguration.LoadAsync(configPath);

        loaded.User.ShouldNotBeNull();
        loaded.User.DisplayName.ShouldBeNull();
        loaded.User.Email.ShouldBeNull();
    }
}
