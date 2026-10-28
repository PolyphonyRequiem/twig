using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class BenchAttachmentRequestTests
{
    [Fact]
    public async Task RequestParserRequiresAllExplicitQualifiedFields()
    {
        const string valid = """
            {"version":1,"action":"inventory","itemKey":{"checkoutRoot":"C:\\repo","connectionId":"connection","bindingId":"binding","workItemId":42},"bench":"Sprint","expectBench":"7","expectIdentity":"identity","expectedRevision":null,"attachmentId":null}
            """;
        var request = await Parse(valid);
        request.Action.ShouldBe("inventory");
        request.ItemKey.WorkItemId.ShouldBe(42);

        foreach (var invalid in new[]
        {
            valid.Replace("\"attachmentId\":null", "\"attachmentId\":null,\"unexpected\":true", StringComparison.Ordinal),
            valid.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            valid.Replace("\"expectIdentity\":\"identity\",", "", StringComparison.Ordinal),
        })
            await Should.ThrowAsync<JsonException>(() => Parse(invalid));
    }

    [Fact]
    public async Task RequestParserEnforcesTheInputByteBound()
    {
        var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', BenchAttachmentCommand.MaximumInputBytes + 1)));
        var error = await Should.ThrowAsync<ArgumentException>(() =>
            BenchAttachmentCommand.ReadRequestAsync(oversized, CancellationToken.None));
        error.Message.ShouldBe("input-too-large");
    }

    private static Task<AttachmentReadRequest> Parse(string json) =>
        BenchAttachmentCommand.ReadRequestAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), CancellationToken.None);
}
