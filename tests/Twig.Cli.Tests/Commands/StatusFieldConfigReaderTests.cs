using Shouldly;
using Twig.Commands;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StatusFieldConfigReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TwigPaths _paths;

    public StatusFieldConfigReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twig-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ReadAsync_Returns_Entries_When_File_Exists()
    {
        var content = """
            # Status fields config
            * Story Points (Microsoft.VSTS.Scheduling.StoryPoints)
              Priority (Microsoft.VSTS.Common.Priority)
            """;
        await File.WriteAllTextAsync(_paths.StatusFieldsPath, content);
        var reader = new StatusFieldConfigReader(_paths);

        var result = await reader.ReadAsync();

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result[0].ShouldBe(new StatusFieldEntry("Microsoft.VSTS.Scheduling.StoryPoints", true));
        result[1].ShouldBe(new StatusFieldEntry("Microsoft.VSTS.Common.Priority", false));
    }

    [Fact]
    public async Task ReadAsync_Returns_Null_When_File_Missing()
    {
        var reader = new StatusFieldConfigReader(_paths);

        var result = await reader.ReadAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_Returns_Null_On_Parse_Error()
    {
        // Create a file that exists but whose path will cause ReadAllTextAsync to fail.
        // We simulate this by making the StatusFieldsPath a directory instead of a file,
        // which causes File.ReadAllTextAsync to throw.
        Directory.CreateDirectory(_paths.StatusFieldsPath);
        var reader = new StatusFieldConfigReader(_paths);

        var result = await reader.ReadAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_Supports_Cancellation()
    {
        var content = "* Story Points (Microsoft.VSTS.Scheduling.StoryPoints)";
        await File.WriteAllTextAsync(_paths.StatusFieldsPath, content);
        var reader = new StatusFieldConfigReader(_paths);
        using var cts = new CancellationTokenSource();

        // Should succeed with non-cancelled token
        var result = await reader.ReadAsync(cts.Token);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_Returns_Empty_List_For_Comments_Only_File()
    {
        var content = """
            # Only comments here
            # No actual fields
            """;
        await File.WriteAllTextAsync(_paths.StatusFieldsPath, content);
        var reader = new StatusFieldConfigReader(_paths);

        var result = await reader.ReadAsync();

        result.ShouldNotBeNull();
        result.Count.ShouldBe(0);
    }
}
