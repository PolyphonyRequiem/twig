using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeCaptureRegistryPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-current-captures-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "system.db");

    [Fact]
    public async Task FreshCaptureNamespaceRequiresWitnessedBirth()
    {
        var path = Path.Combine(root, "user.db");
        var user = Guid.NewGuid();
        using var registry = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        (await registry.CaptureHeadsAsync(default)).Value.ShouldBeEmpty();
        (await registry.CaptureUserBirthAsync(path, user, true, default)).IsSuccess.ShouldBeFalse();
        (await registry.CaptureUserBirthAsync(path, user, false, default)).IsSuccess.ShouldBeTrue();
        var reserved = (await registry.CaptureHeadsAsync(default)).Value.Single();
        reserved.Birth.ShouldBe("reserved");
        reserved.Generation.ShouldBe(0);
        reserved.Head.ShouldBeEmpty();
        (await registry.CaptureUserBirthAsync(path, Guid.NewGuid(), true, default)).IsSuccess.ShouldBeFalse();
        (await registry.CaptureUserBirthAsync(path, user, true, default)).IsSuccess.ShouldBeTrue();
        (await registry.CaptureHeadsAsync(default)).Value.Single().Birth.ShouldBe("born");
        (await registry.CaptureUserBirthAsync(path, user, true, default)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task CurrentReopenRetainsCaptureSeal()
    {
        string seal;
        using (var registry = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System))
        {
            var fresh = await registry.CaptureInventoryAsync(default);
            fresh.IsSuccess.ShouldBeTrue();
            seal = fresh.Value;
        }
        using var reopened = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        (await reopened.CaptureInventoryAsync(default)).Value.ShouldBe(seal);
        (await reopened.CapturePublishingAsync(default)).IsSuccess.ShouldBeTrue();
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE editor_provenance_meta SET seal='changed' WHERE id=1;";
        await command.ExecuteNonQueryAsync();
        (await reopened.CaptureInventoryAsync(default)).IsSuccess.ShouldBeFalse();
        (await reopened.CapturePublishingAsync(default)).IsSuccess.ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
