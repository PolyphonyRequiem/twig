using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

public sealed class AdoBindingTransactionTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "twig-binding-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(home, "system.db");

    [Fact]
    public async Task PortableBindUsesPriorNormalizedDeclarationButKeepsExactEndpoint()
    {
        await SeedIdentity("one");
        await SeedIdentity("two");
        using var service = new ConnectionBindingService(home);
        await Should.ThrowAsync<NativeSetupException>(() => service.BindRegisteredAdoAsync("example", "Work", "one"));
        Counts().ShouldBe("0/0/0/0");

        await new DeviceConnectionCatalog(Database).AddAsync(
            ProviderConnectionRequirement.Create("ado", "https://example.visualstudio.com/", "WORK"));
        var one = await service.BindRegisteredAdoAsync("example", "Work", "one");
        var two = await service.BindRegisteredAdoAsync("example", "Work", "two");
        (await service.BindRegisteredAdoAsync("example", "Work", "ONE")).ShouldBe(one);
        two.BindingId.ShouldNotBe(one.BindingId);
        var alias = await service.BindRegisteredAdoAsync("https://dev.azure.com/example", "WORK", "one");
        alias.ConnectionRef.ShouldBe(ConnectionRefResolver.Compute("https://dev.azure.com/example", "WORK"));
        alias.ConnectionRef.ShouldNotBe(one.ConnectionRef);
        Counts().ShouldBe("1/2/3/0");
    }

    [Fact]
    public async Task UnknownIdentityAndCrossAuthorityPatCannotCreateDeviceDeclaration()
    {
        await SeedIdentity("pat", "https://dev.azure.com/elsewhere");
        using var service = new ConnectionBindingService(home);
        await Should.ThrowAsync<NativeSetupException>(() => service.CreateBindingAsync("example", "Work", "missing", true));
        await Should.ThrowAsync<InvalidOperationException>(() => service.CreateBindingAsync("example", "Work", "pat", true));
        Counts().ShouldBe("0/0/0/0");
    }

    [Fact]
    public async Task ExistingEndpointWithWrongCoordinatesIsNotRewrittenOrBound()
    {
        await SeedIdentity("one");
        using var service = new ConnectionBindingService(home);
        var reference = ConnectionRefResolver.Compute("example", "Work");
        Sql($"""
            INSERT INTO connections(connection_ref,organization,project,first_seen_at,last_seen_at)
            VALUES('{reference}','example','work','before','before');
            """);
        await Should.ThrowAsync<NativeSetupException>(() => service.CreateBindingAsync("example", "Work", "one", false));
        Counts().ShouldBe("0/1/0/0");
        Sql("SELECT project FROM connections;").ShouldBe("work");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqlFailureRollsBackAllNewRowsButNotAPriorDeclaration(bool portable)
    {
        await SeedIdentity("one");
        using var service = new ConnectionBindingService(home);
        if (portable)
            await new DeviceConnectionCatalog(Database).AddAsync(ProviderConnectionRequirement.Create("ado", "example", "Work"));
        var before = Counts();
        Sql(portable
            ? "CREATE TRIGGER stop BEFORE INSERT ON connection_bindings BEGIN SELECT RAISE(ABORT,'test failure'); END;"
            : "CREATE TRIGGER stop BEFORE INSERT ON connection_defaults BEGIN SELECT RAISE(ABORT,'test failure'); END;");

        await Should.ThrowAsync<NativeSetupException>(() => portable
            ? service.BindRegisteredAdoAsync("example", "Work", "one")
            : service.CreateBindingAsync("example", "Work", "one", true));
        Counts().ShouldBe(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RacingInitialDefaultsCommitExactlyOneSelection(bool sameIdentity)
    {
        await SeedIdentity("first");
        await SeedIdentity("other");
        using var left = new ConnectionBindingService(home);
        using var right = new ConnectionBindingService(home);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IdentityBinding?> Attempt(ConnectionBindingService service, string alias)
        {
            await start.Task;
            try { return await service.CreateBindingAsync("example", "Work", alias, true); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("different default", StringComparison.Ordinal)) { return null; }
        }

        var first = Task.Run(() => Attempt(left, "first"));
        var second = Task.Run(() => Attempt(right, sameIdentity ? "first" : "other"));
        start.SetResult();
        var winners = (await Task.WhenAll(first, second)).OfType<IdentityBinding>().ToArray();
        winners.Length.ShouldBe(sameIdentity ? 2 : 1);
        winners.Distinct().Count().ShouldBe(1);
        Counts().ShouldBe("1/1/1/1");
        Sql("SELECT binding_id FROM connection_defaults;").ShouldBe(winners[0].BindingId);
    }

    [Fact]
    public async Task ConcurrentNativeAndPortableBindsPreserveBothExplicitCandidates()
    {
        await SeedIdentity("first");
        await SeedIdentity("second");
        await new DeviceConnectionCatalog(Database).AddAsync(ProviderConnectionRequirement.Create("ado", "example", "Work"));
        using var native = new ConnectionBindingService(home);
        using var portable = new ConnectionBindingService(home);
        var candidates = await Task.WhenAll(
            Task.Run(() => native.CreateBindingAsync("example", "Work", "first", true)),
            Task.Run(() => portable.BindRegisteredAdoAsync("example", "Work", "second")));
        Counts().ShouldBe("1/1/2/1");
        Sql("SELECT binding_id FROM connection_defaults;").ShouldBe(candidates[0].BindingId);
        candidates[1].BindingId.ShouldNotBe(candidates[0].BindingId);
    }

    private async Task SeedIdentity(string alias, string? patAuthority = null)
    {
        using var registry = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var row = new IdentityRow(alias, alias, "tenant", alias, "issuer", "host", $"ref-{alias}", null, now, now,
            patAuthority is null ? "aad" : "pat", patAuthority is null ? null : alias, patAuthority);
        (await registry.InsertIdentityAsync(row)).IsSuccess.ShouldBeTrue();
    }

    private string? Sql(string statement)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = statement;
        return command.ExecuteScalar()?.ToString();
    }

    private string? Counts() => Sql("""
        SELECT (SELECT COUNT(*) FROM device_connections) || '/' || (SELECT COUNT(*) FROM connections)
            || '/' || (SELECT COUNT(*) FROM connection_bindings) || '/' || (SELECT COUNT(*) FROM connection_defaults);
        """);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(home)) Directory.Delete(home, true);
    }
}
