using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeAuthoringContractTests
{
    [Fact]
    public async Task RemovalProposalPreservesCanonicalBytesThroughNativeFileParserAndActualSqlite()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-authoring-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var connection = new Connection(new("ado"), "collection", "project", "actor");
            var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
            var other = ItemKey.Remote(connection.Provider, connection.ResourceScope, "43");
            var proposal = new Proposal(connection, "schema", 0,
                [new("compound", OpKind.Batch, key, new(key, "revision"),
                    FieldValues.Empty.Set("title", FieldValue.String("edited")),
                    links: [new(other, "related", true, true)])]);
            var bytes = Model.ProposalBytes(proposal);
            var file = Path.Combine(root, "review.json");
            await File.WriteAllBytesAsync(file, bytes);
            var parsed = await CoreProposalFiles.ReadAsync(root, file, default);
            parsed.IsSuccess.ShouldBeTrue(parsed.IsSuccess ? "" : parsed.Error.Message);
            Model.ProposalBytes(parsed.Value).ShouldBe(bytes);
            var store = (await SqliteStore.CreateAsync(Path.Combine(root, "core.db"))).Value;
            (await store.CommitAsync(connection, 0, new(1, proposals: [parsed.Value],
                journal: [JournalEntry.Planned(proposal.Digest, 0)]), default)).IsSuccess.ShouldBeTrue();
            var reopened = (await SqliteStore.OpenAsync(store.DatabasePath, expectedInstance: store.InstanceId)).Value;
            Model.ProposalBytes((await reopened.ReadAsync(connection, default)).Value.Proposals.Single()).ShouldBe(bytes);
            var noncanonical = JsonNode.Parse(bytes)!;
            noncanonical["operations"]![0]!["links"]![0]![3] = false;
            await File.WriteAllTextAsync(file, noncanonical.ToJsonString());
            (await CoreProposalFiles.ReadAsync(root, file, default)).IsSuccess.ShouldBeFalse("the legacy default add flag remains omitted");
        }
        finally { Directory.Delete(root, true); }
    }
}
