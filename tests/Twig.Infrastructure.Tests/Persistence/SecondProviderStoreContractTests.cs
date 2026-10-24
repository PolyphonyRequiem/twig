using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Epic #1168 prove-store-contract: a second provider shape (GitHub-like node IDs, API-host scope,
/// ETag tokens) uses the same durable tables as ADO, with no schema change and no collisions.
/// This is a test-level provider only; it does not claim a shipped GitHub adapter.
/// </summary>
public sealed class SecondProviderStoreContractTests : IDisposable
{
    private const string AdoScope = "8a1b6c3e-0f4d-4c5a-9e2b-7d6f5a4b3c21";
    private const string HubScope = "https://api.github.com";

    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteLocalWorkItemRepository _repo;

    public SecondProviderStoreContractTests() => _repo = new SqliteLocalWorkItemRepository(_store);

    public void Dispose() => _store.Dispose();

    private static ExternalWorkItemIdentity Ado(string id) => new("ado", AdoScope, id);
    private static ExternalWorkItemIdentity Hub(string id, string scope = HubScope) => new("github", scope, id);

    [Fact]
    public async Task SecondProvider_UsesTheSameTables_WithoutAnySchemaChange()
    {
        var before = SchemaSnapshot();

        await _repo.ResolveAsync(Ado("42"), new ProviderRevisionToken("7"));
        await _repo.ResolveAsync(Hub("I_kwDOAbCd12"), new ProviderRevisionToken("W/\"9f2c\""));
        await _repo.ResolveAsync(Hub("I_kwDOEfGh34"), observedToken: null);

        SchemaSnapshot().ShouldBe(before);
        CountRows("pending.external_identities").ShouldBe(3);
        CountRows("pending.local_work_items").ShouldBe(3);
    }

    [Fact]
    public async Task SameNativeId_AcrossProviders_AndHosts_NeverCollides()
    {
        var ado = (await _repo.ResolveAsync(Ado("42"), null)).Id;
        var hub = (await _repo.ResolveAsync(Hub("42"), null)).Id;
        var enterprise = (await _repo.ResolveAsync(Hub("42", "https://ghe.example.com/api/v3"), null)).Id;

        new[] { ado, hub, enterprise }.Distinct().Count().ShouldBe(3);
        (await _repo.GetExternalIdentitiesAsync(hub)).ShouldBe([Hub("42")]);
    }

    [Fact]
    public async Task NativeIds_AreOpaqueAndCasePreserving()
    {
        var upper = (await _repo.ResolveAsync(Hub("I_kwDOABC"), null)).Id;
        var lower = (await _repo.ResolveAsync(Hub("I_kwDOabc"), null)).Id;

        upper.ShouldNotBe(lower);
        (await _repo.GetExternalIdentitiesAsync(lower)).Single().NativeId.ShouldBe("I_kwDOabc");
    }

    [Fact]
    public async Task OpaqueTokens_RoundTripVerbatim_AndNullMeansNoToken()
    {
        const string etag = "W/\"5b1f-Ab\"";
        await _repo.ResolveAsync(Hub("I_1"), null);
        (await _repo.GetObservedTokenAsync(Hub("I_1")))!.Token.ShouldBeNull();

        (await _repo.RecordObservedTokenAsync(Hub("I_1"), new ProviderRevisionToken(etag))).ShouldBeTrue();
        (await _repo.GetObservedTokenAsync(Hub("I_1")))!.Token!.Value.ShouldBe(etag);

        (await _repo.RecordObservedTokenAsync(Hub("I_unknown"), new ProviderRevisionToken(etag))).ShouldBeFalse();
    }

    [Fact]
    public async Task LocalRecordsAndCas_AreProviderNeutral()
    {
        var id = (await _repo.ResolveAsync(Hub("I_kwDOAbCd12"), new ProviderRevisionToken("\"e1\""))).Id;

        var fields = FieldValues.Empty.Set("System.Title", new TextValue("from a second provider"));
        (await _repo.UpdateAsync(id, 1, fields)).Value.ShouldBeOfType<LocalWriteApplied>().Record.Revision.ShouldBe(2);
        (await _repo.UpdateAsync(id, 1, fields)).Value.ShouldBeOfType<LocalRevisionConflict>();

        (await _repo.RecordObservedTokenAsync(Hub("I_kwDOAbCd12"), new ProviderRevisionToken("\"e2\""))).ShouldBeTrue();
        (await _repo.GetAsync(id))!.Revision.ShouldBe(2, "a provider token observation never advances the local CAS revision");
    }

    private string SchemaSnapshot()
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT group_concat(sql, char(10)) FROM (
                SELECT 'main' AS s, sql FROM main.sqlite_master WHERE sql IS NOT NULL
                UNION ALL SELECT 'pending', sql FROM pending.sqlite_master WHERE sql IS NOT NULL
                ORDER BY s, sql);
            """;
        return (string)cmd.ExecuteScalar()!;
    }

    private long CountRows(string table)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
