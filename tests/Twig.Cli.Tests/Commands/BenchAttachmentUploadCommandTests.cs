using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class BenchAttachmentUploadCommandTests
{
    [Fact]
    public async Task PrepareCommandEmitsParseableJsonErrorWithoutResolvingWorkspaceServices()
    {
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var command = Command("{", stdout, stderr);

        var exitCode = await command.ExecutePrepareAsync(readStdin: true, output: "json");

        exitCode.ShouldBe(2);
        using var result = JsonDocument.Parse(stdout.ToString());
        result.RootElement.GetProperty("status").GetString().ShouldBe("refused");
        result.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid-json");
        stderr.ToString().ShouldContain("invalid-json");
    }

    [Fact]
    public async Task UploadCommandRejectsTruncatedFrameBeforeResolvingWriter()
    {
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var command = Command("TWIGUPL1", stdout, stderr);

        var exitCode = await command.ExecuteUploadAsync(
            readStdin: true, framed: true, output: "json");

        exitCode.ShouldBe(2);
        using var result = JsonDocument.Parse(stdout.ToString());
        result.RootElement.GetProperty("status").GetString().ShouldBe("refused");
        result.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid-frame");
    }

    [Fact]
    public async Task UploadFrameBindsTheExactByteCountAndSha256()
    {
        var request = Request();
        var content = new byte[] { 1, 2, 3 };
        using var frame = new MemoryStream();
        await AttachmentUploadFrame.WriteAsync(frame, request, content);
        frame.Position = 0;

        var parsed = await AttachmentUploadFrame.ReadAsync(frame);

        parsed.Request.ShouldBe(request);
        parsed.Content.SequenceEqual(content).ShouldBeTrue();

        var corrupted = frame.ToArray();
        corrupted[^1] ^= 0xff;
        using var tampered = new MemoryStream(corrupted);
        await Should.ThrowAsync<InvalidDataException>(() => AttachmentUploadFrame.ReadAsync(tampered));
    }

    [Fact]
    public async Task PrepareCommandRefusesUnsupportedProviderBeforeResolvingNativeWriter()
    {
        var upload = Request();
        var prepare = new AttachmentUploadPrepareRequest(1, upload.ItemKey, upload.Bench,
            upload.ExpectBench, upload.ExpectIdentity, upload.ExpectedRevision, upload.FileName,
            upload.ContentType, upload.ContentLength, upload.Sha256);
        var stdin = JsonSerializer.Serialize(prepare, TwigJsonContext.Default.AttachmentUploadPrepareRequest);
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var command = Command(stdin, stdout, stderr, new TwigConfiguration { Provider = "github" });

        var exitCode = await command.ExecutePrepareAsync(readStdin: true, output: "json");

        exitCode.ShouldBe(1);
        using var result = JsonDocument.Parse(stdout.ToString());
        result.RootElement.GetProperty("status").GetString().ShouldBe("refused");
        result.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("provider-unsupported");
    }

    private static BenchAttachmentUploadCommand Command(
        string stdin, TextWriter stdout, TextWriter stderr, TwigConfiguration? configuration = null)
    {
        var context = new CommandContext(null!, null!, null!, configuration ?? new TwigConfiguration(), Stderr: stderr);
        return new(context, () => throw new InvalidOperationException("The writer must not be resolved for invalid input."),
            new MemoryStream(Encoding.UTF8.GetBytes(stdin)), stdout);
    }

    private static AttachmentUploadRequest Request()
    {
        var content = new byte[] { 1, 2, 3 };
        return new(1, new QualifiedWorkItemKey("C:\\repo", "connection", "binding", 42),
            "Sprint", "7", "identity", 9, "report.txt", "text/plain", content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)), new string('a', 64));
    }
}
