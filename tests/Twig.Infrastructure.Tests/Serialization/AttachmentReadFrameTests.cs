using System.Buffers.Binary;
using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Serialization;

public sealed class AttachmentReadFrameTests
{
    [Fact]
    public async Task DownloadFrameCarriesGeneratedMetadataAndExactBinaryBytes()
    {
        var content = new byte[] { 0, 255, 13, 10, 128 };
        var attachment = new AttachmentReadEntry("4dab0171-49dc-4da5-8f18-35c923fbdb0a", "report.bin", "5");
        var result = new AttachmentReadResult(1, "download", new("native", "available"),
            new(new("C:\\repo", "connection", "binding", 42), "7", "Sprint", "digest", "identity",
                "2", "bench", "4", "3", "5", 7), [], attachment, "application/octet-stream",
            content.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), null);
        using var output = new MemoryStream();

        await AttachmentReadFrame.WriteAsync(output, result, content);

        var frame = output.ToArray();
        frame.AsSpan(0, 8).SequenceEqual("TWIGATT1"u8).ShouldBeTrue();
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(8, 4));
        using var header = JsonDocument.Parse(frame.AsMemory(12, headerLength));
        header.RootElement.GetProperty("status").GetString().ShouldBe("download");
        var contentOffset = 16 + headerLength;
        BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(12 + headerLength, 4)).ShouldBe(content.Length);
        frame.AsSpan(contentOffset).SequenceEqual(content).ShouldBeTrue();
    }

    [Fact]
    public async Task FrameRefusesMismatchedMetadataInsteadOfWritingAmbiguousBytes()
    {
        var result = AttachmentReadFrame.Refused("attachment-offline");
        using var output = new MemoryStream();
        await Should.ThrowAsync<InvalidDataException>(() =>
            AttachmentReadFrame.WriteAsync(output, result with { Status = "download", ContentLength = 6 }, [1, 2, 3]));
        output.Length.ShouldBe(0);
    }
}
