using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;
using Rules = Twig.Core.Core;

namespace Twig.Infrastructure.Tests.Services;

public sealed class CoreCreationLinkFileTests
{
    private static Proposal Proposal(bool includeLinks)
    {
        var connection = new Connection(new("test"), "scope", "destination", "actor");
        var seed = ItemKey.Local(Guid.Parse("01900000-0000-7000-8000-000000000001"));
        var target = ItemKey.Remote(connection.Provider, connection.ResourceScope, "1");
        var fields = new FieldValues([KeyValuePair.Create("phase", FieldValue.String("open"))]);
        return new(connection, "schema", 0,
            [new("create", OpKind.Create, seed, fields: fields, type: "record",
                concurrency: ConcurrencyMode.ObservedNonAtomic,
                links: includeLinks ? [new(target, "dependency", true)] : [])]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalCreationLinksRoundTripWithoutChangingIntent(bool includeLinks)
    {
        var original = Proposal(includeLinks);
        using var json = JsonDocument.Parse(Rules.ProposalBytes(original));
        var parsed = CoreProposalFiles.Parse(json.RootElement);
        parsed.Digest.ShouldBe(original.Digest);
        parsed.Operations[0].Links.ShouldBe(original.Operations[0].Links);
        Rules.ProposalBytes(parsed).ShouldBe(Rules.ProposalBytes(original));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("""[[["remote","test","scope","1"],"dependency"]]""")]
    [InlineData("""[[["remote","test","scope","1"],"dependency","true"]]""")]
    [InlineData("""[[["remote","test","scope","1"],"dependency",true,"unknown"]]""")]
    [InlineData("""[[["remote","test","scope","1"],"",true]]""")]
    [InlineData("""[[["remote","test","scope","1"],"dependency",true],[["remote","test","scope","1"],"dependency",true]]""")]
    public async Task MalformedCreationLinksFailExplicitFileValidation(string payload)
    {
        var canonical = Encoding.UTF8.GetString(Rules.ProposalBytes(Proposal(true)));
        using var original = JsonDocument.Parse(canonical);
        var links = original.RootElement.GetProperty("operations")[0].GetProperty("links").GetRawText();
        var marker = "\"links\":" + links;
        canonical.ShouldContain(marker);
        var malformed = canonical.Replace(marker, "\"links\":" + payload, StringComparison.Ordinal);
        malformed.ShouldNotBe(canonical);
        var root = Path.Combine(Path.GetTempPath(), "twig-creation-link-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "proposal.json");
        try
        {
            await File.WriteAllTextAsync(file, malformed);
            var result = await CoreProposalFiles.ReadAsync(root, file, CancellationToken.None);
            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
            result.Error.Message.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(root);
        }
    }
}
