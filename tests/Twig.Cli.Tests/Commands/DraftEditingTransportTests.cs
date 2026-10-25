using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Twig.Browser;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class DraftEditingTransportTests
{
    [Fact]
    public async Task RetainInputIsStrictLosslessAndAcceptsNoEffectsOrAuthorizationFields()
    {
        var staged = Request([]);
        var expected = new DraftRetainRequest(1, staged.NativeContext with
        {
            BindingRevision = "9007199254740993", AttachmentRevision = "9223372036854775807",
            StorageGeneration = null,
        }, staged.Generation, staged.BaselineRevision, staged.ObservedRevision);
        var json = JsonSerializer.Serialize(expected, TwigJsonContext.Default.DraftRetainRequest);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var actual = await DraftEditingCommand.ReadRetainRequestAsync(input, default);
        actual.ShouldBe(expected);
        RecoverableDraftEditingModule.IsValidRequest(actual).ShouldBeTrue();
        json.ShouldContain("\"storageGeneration\":null");
        foreach (var addition in new[]
        {
            "\"changes\":[]", "\"authorizer\":\"browser\"", "\"authorized\":true",
            "\"digest\":\"supplied\"", "\"proposalPath\":\"supplied\"", "\"version\":1",
        })
        {
            var malformed = json.Insert(1, addition + ",");
            using var rejected = new MemoryStream(Encoding.UTF8.GetBytes(malformed));
            await Should.ThrowAsync<JsonException>(() => DraftEditingCommand.ReadRetainRequestAsync(rejected, default));
        }
        foreach (var (from, to) in new[]
        {
            ("\"checkoutRoot\":", "\"extra\":true,\"checkoutRoot\":"),
            ("\"providerKind\":", "\"extra\":true,\"providerKind\":"),
            ("\"generation\":\"9007199254740993\"", "\"generation\":9007199254740993"),
            ("\"version\":1,", ""),
            ("\"observedRevision\":\"3\"", "\"observedRevision\":true"),
        })
        {
            var malformed = json.Replace(from, to, StringComparison.Ordinal);
            malformed.ShouldNotBe(json);
            using var rejected = new MemoryStream(Encoding.UTF8.GetBytes(malformed));
            await Should.ThrowAsync<JsonException>(() => DraftEditingCommand.ReadRetainRequestAsync(rejected, default));
        }
        foreach (var malformed in new[]
        {
            expected with { NativeContext = null! }, expected with { Generation = null! },
            expected with { NativeContext = expected.NativeContext with { ItemKey = null! } },
            expected with { NativeContext = expected.NativeContext with { ExternalIdentity = null! } },
            expected with { Generation = "01" }, expected with { Generation = "-1" },
            expected with { Generation = "9223372036854775808" },
            expected with { BaselineRevision = "0" }, expected with { ObservedRevision = "+3" },
        })
            RecoverableDraftEditingModule.IsValidRequest(malformed).ShouldBeFalse();
    }

    [Fact]
    public async Task RetainReaderRequiresEofBoundsUtf8AndHonorsCancellation()
    {
        var staged = Request([]);
        var request = new DraftRetainRequest(1, staged.NativeContext, staged.Generation, "3", "3");
        var json = JsonSerializer.Serialize(request, TwigJsonContext.Default.DraftRetainRequest);
        var exact = json + new string(' ', DraftEditingCommand.MaximumInputBytes - Encoding.UTF8.GetByteCount(json));
        using var limit = new MemoryStream(Encoding.UTF8.GetBytes(exact));
        (await DraftEditingCommand.ReadRetainRequestAsync(limit, default)).ShouldBe(request);
        foreach (var oversized in new[] { exact + " ", new string('\u00e9', 140000) })
        {
            using var rejected = new MemoryStream(Encoding.UTF8.GetBytes(oversized));
            (await Should.ThrowAsync<ArgumentException>(() =>
                DraftEditingCommand.ReadRetainRequestAsync(rejected, default))).Message.ShouldContain("input-too-large");
        }
        using var held = new OpenInput(Encoding.UTF8.GetBytes(json));
        var reading = DraftEditingCommand.ReadRetainRequestAsync(held, default);
        await held.Waiting.Task;
        reading.IsCompleted.ShouldBeFalse();
        held.Eof.SetResult();
        (await reading).ShouldBe(request);
        using var canceled = new CancellationTokenSource();
        using var canceledInput = new OpenInput(Encoding.UTF8.GetBytes(json));
        var canceledRead = DraftEditingCommand.ReadRetainRequestAsync(canceledInput, canceled.Token);
        await canceledInput.Waiting.Task;
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => canceledRead);
        using var tooDeep = new MemoryStream(Encoding.UTF8.GetBytes(new string('[', 33) + new string(']', 33)));
        await Should.ThrowAsync<JsonException>(() => DraftEditingCommand.ReadRetainRequestAsync(tooDeep, default));
    }

    [Fact]
    public async Task StdinPreservesNullableEmptyAndLosslessGeneration()
    {
        var expected = Request([
            new("System.Title", null), new("System.AssignedTo", ""), new("System.State", "Doing")]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(expected, TwigJsonContext.Default.DraftStageRequest);
        using var input = new MemoryStream(bytes);
        var actual = await DraftEditingCommand.ReadRequestAsync(input, default);
        actual.Generation.ShouldBe("9007199254740993");
        actual.Changes.Select(change => change.ProposedValue).ShouldBe([null, "", "Doing"]);
        actual.NativeContext.ShouldBe(expected.NativeContext);
        Encoding.UTF8.GetString(bytes).ShouldContain("\"proposedValue\":null");
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"nativeContext\":null}")]
    [InlineData("{\"version\":1,\"changes\":[{\"fieldRef\":\"System.Title\"}]}")]
    [InlineData("{\"version\":1,\"changes\":[{\"fieldRef\":\"System.Title\",\"proposedValue\":1}]}")]
    public async Task InvalidOrAmbiguousJsonIsRejected(string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Should.ThrowAsync<JsonException>(() => DraftEditingCommand.ReadRequestAsync(input, default));
    }

    [Fact]
    public async Task LimitCountsUtf8BytesAndDoesNotTruncate()
    {
        var prefix = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            Request([new("System.Title", "")]), TwigJsonContext.Default.DraftStageRequest));
        var baseBytes = Encoding.UTF8.GetByteCount(prefix);
        var exact = prefix.Replace("\"proposedValue\":\"\"",
            "\"proposedValue\":\"" + new string('x', DraftEditingCommand.MaximumInputBytes - baseBytes) + "\"",
            StringComparison.Ordinal);
        Encoding.UTF8.GetByteCount(exact).ShouldBe(DraftEditingCommand.MaximumInputBytes);
        using var accepted = new MemoryStream(Encoding.UTF8.GetBytes(exact));
        (await DraftEditingCommand.ReadRequestAsync(accepted, default)).Changes.Single().ProposedValue!
            .Length.ShouldBe(DraftEditingCommand.MaximumInputBytes - baseBytes);
        using var rejected = new MemoryStream(Encoding.UTF8.GetBytes(exact + " "));
        (await Should.ThrowAsync<ArgumentException>(() =>
            DraftEditingCommand.ReadRequestAsync(rejected, default))).Message.ShouldContain("input-too-large");
        using var multibyte = new MemoryStream(Encoding.UTF8.GetBytes(new string('\u00e9', 140000)));
        (await Should.ThrowAsync<ArgumentException>(() =>
            DraftEditingCommand.ReadRequestAsync(multibyte, default))).Message.ShouldContain("input-too-large");
    }

    [Fact]
    public async Task CanceledStdinIsNotParsedOrStaged()
    {
        using var input = new MemoryStream([123]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            DraftEditingCommand.ReadRequestAsync(input, canceled.Token));
    }

    [Fact]
    public async Task CompleteJsonWaitsForEofAndCancellationInterruptsAnOpenPipe()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Request([new("System.Title", "intent")]),
            TwigJsonContext.Default.DraftStageRequest);
        using var input = new OpenInput(bytes);
        var reading = DraftEditingCommand.ReadRequestAsync(input, default);
        await input.Waiting.Task;
        reading.IsCompleted.ShouldBeFalse("A valid JSON prefix is not permission to stage before EOF.");
        input.Eof.SetResult();
        (await reading).Changes.Single().ProposedValue.ShouldBe("intent");

        using var canceled = new CancellationTokenSource();
        using var held = new OpenInput(bytes);
        var canceledRead = DraftEditingCommand.ReadRequestAsync(held, canceled.Token);
        await held.Waiting.Task;
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => canceledRead);
    }

    private sealed class OpenInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream _prefix = new(bytes);
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Eof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await _prefix.ReadAsync(buffer, ct);
            if (count > 0) return count;
            Waiting.TrySetResult();
            await Eof.Task.WaitAsync(ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _prefix.Dispose();
            base.Dispose(disposing);
        }
    }

    private static DraftStageRequest Request(IReadOnlyList<DraftEditingChange> changes)
        => new(1, new(new QualifiedWorkItemKey(@"C:\fixture", "connection", "binding", 42),
            new ExternalWorkItemIdentity("ado", "resource", "42"), "7", "Sprint", "bench-digest",
            "identity", "1", "checkout-binding-pin", "1", "1", "storage", "origin-digest", "evidence-digest"),
            "9007199254740993", "3", "3", changes);
}
