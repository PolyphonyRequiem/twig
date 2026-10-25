using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoRestClientDiscussionTests
{
    private const string Empty = """{"count":0,"totalCount":0,"comments":[]}""";

    [Fact]
    public async Task FixedGetEscapesOnlyTheTokenAndNeverFollowsProviderLinks()
    {
        const string token = "https://evil.invalid/?x=1&workItemId=99#fragment +%";
        using var handler = new RecordingHandler((_, _) => Reply("""
            {"count":1,"totalCount":3,"continuationToken":"opaque-next","nextPage":"https://evil.invalid/steal",
             "comments":[{"id":45,"workItemId":42,"version":1,"createdBy":{"id":"actor","displayName":"Writer",
             "uniqueName":"private@example.invalid","url":"https://evil.invalid/actor"},
             "createdDate":"2026-01-01T12:00:00+02:00","text":"<script>untrusted</script>","format":"html"}]}
            """));
        using var http = new HttpClient(handler);
        var page = await Reader(http).ReadDiscussionAsync(42, token);
        handler.Uris.Count.ShouldBe(1);
        var uri = handler.Uris[0];
        uri.Host.ShouldBe("dev.azure.com");
        uri.AbsolutePath.ShouldBe("/fixture/Project%20Space/_apis/wit/workItems/42/comments");
        uri.Query.ShouldBe("?$top=50&order=asc&includeDeleted=true&$expand=none&api-version=7.1-preview.4&continuationToken="
            + Uri.EscapeDataString(token));
        handler.Methods.ShouldBe([HttpMethod.Get]);
        handler.Authorizations.ShouldBe(["Bearer fixture-token"]);
        page.ContinuationToken.ShouldBe("opaque-next");
        var comment = page.Comments.ShouldHaveSingleItem();
        comment.Id.ShouldBe("45");
        comment.Version.ShouldBe("1");
        comment.Author.ShouldBe(new DiscussionAuthor("actor", "Writer"));
        comment.CreatedAt.ShouldBe(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        comment.Body.ShouldBe("<script>untrusted</script>");
        comment.Format.ShouldBe("html");
        comment.IsDeleted.ShouldBeNull();
    }

    [Fact]
    public async Task PreservesUnknownDeletedMissingAndEmptyMetadataWithoutGuessing()
    {
        using var handler = new RecordingHandler((_, _) => Reply("""
            {"count":3,"comments":[
              {"id":1,"isDeleted":true,"text":null,"format":null},
              {"id":2,"isDeleted":false,"text":"","format":"future-format"},
              {"id":3,"text":"# not proof of Markdown"}]}
            """));
        using var http = new HttpClient(handler);
        var page = await Reader(http).ReadDiscussionAsync(42, null);
        page.ReportedTotalCount.ShouldBeNull();
        page.Comments[0].IsDeleted.ShouldBe(true);
        page.Comments[0].Body.ShouldBeNull();
        page.Comments[0].Format.ShouldBeNull();
        page.Comments[1].IsDeleted.ShouldBe(false);
        page.Comments[1].Body.ShouldBe("");
        page.Comments[1].Format.ShouldBe("unknown");
        page.Comments[2].IsDeleted.ShouldBeNull();
        page.Comments[2].Format.ShouldBeNull();
        page.Comments[2].Author.ShouldBe(new DiscussionAuthor(null, null));
        page.ContinuationToken.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"comments":[]}""")]
    [InlineData("""{"count":1,"comments":[]}""")]
    [InlineData("""{"count":0,"totalCount":-1,"comments":[]}""")]
    [InlineData("""{"count":1,"totalCount":0,"comments":[{"id":1}]}""")]
    [InlineData("""{"count":1,"comments":[{"commentId":1}]}""")]
    [InlineData("""{"count":1,"comments":[{"id":0}]}""")]
    [InlineData("""{"count":1,"comments":[{"id":2147483648}]}""")]
    [InlineData("""{"count":1,"comments":[{"id":1,"version":0}]}""")]
    [InlineData("""{"count":1,"comments":[{"id":1,"workItemId":99}]}""")]
    [InlineData("""{"count":0,"workItemId":99,"comments":[]}""")]
    [InlineData("""{"count":2,"comments":[{"id":1},{"id":1}]}""")]
    [InlineData("""{"count":1,"comments":[null]}""")]
    [InlineData("""{"count":0,"comments":[],"nextPage":"https://evil.invalid/"}""")]
    [InlineData("""{"count":0,"comments":[],"continuationToken":"same"}""")]
    [InlineData("""{"count":0,"comments":[],"continuationToken":"bad\nvalue"}""")]
    [InlineData("""{"count":1,"comments":[{"id":1,"isDeleted":"false"}]}""")]
    [InlineData("""{"count":1,"comments":[{"id":1,"format":{}}]}""")]
    [InlineData("not json")]
    public async Task MalformedPageIsAnExplicitFailureNotEmptySuccess(string json)
    {
        using var handler = new RecordingHandler((_, _) => Reply(json));
        using var http = new HttpClient(handler);
        (await Should.ThrowAsync<AdoException>(() => Reader(http).ReadDiscussionAsync(42, "same")))
            .Message.ShouldStartWith("discussion-response-invalid:");
        handler.Uris.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(51)]
    public async Task RequestedEntryLimitRefusesRatherThanTruncates(int count)
    {
        var comments = string.Join(',', Enumerable.Range(1, count).Select(id => $$"""{"id":{{id}}}"""));
        using var handler = new RecordingHandler((_, _) => Reply($$"""{"count":{{count}},"comments":[{{comments}}]}"""));
        using var http = new HttpClient(handler);
        if (count == 50)
            (await Reader(http).ReadDiscussionAsync(42, null)).Comments.Count.ShouldBe(50);
        else
            (await Should.ThrowAsync<AdoException>(() => Reader(http).ReadDiscussionAsync(42, null)))
                .Message.ShouldStartWith("discussion-response-invalid:");
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(4097)]
    public async Task TokenLimitsCountUtf8BytesOnBothInputAndOutput(int characters)
    {
        var token = new string('\u00e9', characters);
        using var handler = new RecordingHandler((_, _) => Reply(
            $$"""{"count":0,"comments":[],"continuationToken":"{{token}}"}"""));
        using var http = new HttpClient(handler);
        if (characters == 4096)
        {
            (await Reader(http).ReadDiscussionAsync(42, "input")).ContinuationToken.ShouldBe(token);
            using var acceptedHandler = new RecordingHandler((_, _) => Reply(Empty));
            using var acceptedHttp = new HttpClient(acceptedHandler);
            (await Reader(acceptedHttp).ReadDiscussionAsync(42, token)).Comments.ShouldBeEmpty();
            acceptedHandler.Uris.Count.ShouldBe(1);
        }
        else
            (await Should.ThrowAsync<AdoException>(() => Reader(http).ReadDiscussionAsync(42, "input")))
                .Message.ShouldStartWith("discussion-response-invalid:");
    }

    [Fact]
    public async Task ExactResponseByteLimitIsAccepted()
    {
        var json = Empty + new string(' ', DiscussionReadLimits.MaximumResponseBytes - Encoding.UTF8.GetByteCount(Empty));
        Encoding.UTF8.GetByteCount(json).ShouldBe(DiscussionReadLimits.MaximumResponseBytes);
        using var handler = new RecordingHandler((_, _) => Reply(json));
        using var http = new HttpClient(handler);
        (await Reader(http).ReadDiscussionAsync(42, null)).Comments.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundsResponseBeforeMaterializationEvenWithoutContentLength(bool declaredLength)
    {
        var content = new CountingContent(DiscussionReadLimits.MaximumResponseBytes + 1024 * 1024, declaredLength);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        (await Should.ThrowAsync<AdoException>(() => Reader(http).ReadDiscussionAsync(42, null)))
            .Message.ShouldStartWith("discussion-response-too-large:");
        content.BytesRead.ShouldBeLessThanOrEqualTo(DiscussionReadLimits.MaximumResponseBytes + 8192);
        content.Serialized.ShouldBeFalse("ResponseContentRead would materialize the entire content before the bounded reader.");
        content.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("too-long")]
    [InlineData("control")]
    public async Task InvalidInputTokenMakesNoHttpAttempt(string kind)
    {
        var token = kind == "control" ? "bad\rvalue" : new string('\u00e9', 4097);
        using var handler = new RecordingHandler((_, _) => Reply(Empty));
        using var http = new HttpClient(handler);
        await Should.ThrowAsync<ArgumentException>(() => Reader(http).ReadDiscussionAsync(42, token));
        handler.Uris.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistingSafeGetAuthenticationRenewalIsRetained()
    {
        var auth = new FakeAuthentication();
        using var handler = new RecordingHandler((attempt, _) =>
            attempt == 1 ? Reply("""{"message":"denied"}""", HttpStatusCode.Unauthorized) : Reply(Empty));
        using var http = new HttpClient(handler);
        var page = await Reader(http, auth).ReadDiscussionAsync(42, null);
        page.Comments.ShouldBeEmpty();
        handler.Uris.Count.ShouldBe(2);
        handler.Uris[0].ShouldBe(handler.Uris[1]);
        handler.Methods.ShouldAllBe(method => method == HttpMethod.Get);
        auth.Invalidations.ShouldBe(1);
    }

    [Theory]
    [InlineData(403, typeof(AdoAuthenticationException), 2)]
    [InlineData(404, typeof(AdoNotFoundException), 1)]
    [InlineData(429, typeof(AdoRateLimitException), 1)]
    [InlineData(500, typeof(AdoServerException), 1)]
    public async Task ExistingTypedHttpErrorsAreNotSuccessfulPages(int status, Type expected, int attempts)
    {
        using var handler = new RecordingHandler((_, _) => Reply("""{"message":"private provider message"}""", (HttpStatusCode)status));
        using var http = new HttpClient(handler);
        var error = await Record.ExceptionAsync(() => Reader(http).ReadDiscussionAsync(42, null));
        error.ShouldNotBeNull().GetType().ShouldBe(expected);
        handler.Uris.Count.ShouldBe(attempts);
    }

    [Fact]
    public async Task OfflineAndCancellationDoNotBecomeExhaustedPages()
    {
        using var offline = new RecordingHandler((_, _) => throw new HttpRequestException("private"));
        using var http = new HttpClient(offline);
        await Should.ThrowAsync<AdoOfflineException>(() => Reader(http).ReadDiscussionAsync(42, null));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Reader(http).ReadDiscussionAsync(42, null, canceled.Token));
        offline.Uris.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("caller-cancel")]
    public async Task StreamFailuresDisposeTheResponseAndPreserveTypedOfflineOrCallerCancellation(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        Exception error = kind switch
        {
            "io" => new IOException("SECRET https://private.invalid/token"),
            "http" => new HttpRequestException("SECRET https://private.invalid/token"),
            _ => new TaskCanceledException("SECRET https://private.invalid/token"),
        };
        var content = new CountingContent(1, false, error,
            kind == "caller-cancel" ? () => cancellation.Cancel() : null);
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        if (kind == "caller-cancel")
            await Should.ThrowAsync<OperationCanceledException>(() => Reader(http).ReadDiscussionAsync(42, null, cancellation.Token));
        else
        {
            var failure = await Should.ThrowAsync<AdoOfflineException>(() => Reader(http).ReadDiscussionAsync(42, null, cancellation.Token));
            failure.Message.ShouldNotContain("SECRET");
            failure.Message.ShouldNotContain("private.invalid");
            failure.InnerException.ShouldBeSameAs(error);
        }
        handler.Uris.Count.ShouldBe(1);
        content.Serialized.ShouldBeFalse();
        content.Disposed.ShouldBeTrue();
    }

    private static IWorkItemDiscussionReader Reader(HttpClient http, FakeAuthentication? auth = null)
        => new AdoRestClient(http, auth ?? new(), "https://dev.azure.com/fixture", "Project Space", new WorkItemMapper());

    private sealed class FakeAuthentication : IAuthenticationProvider
    {
        internal int Invalidations { get; private set; }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("fixture-token");
        }
        public void InvalidateToken() => Invalidations++;
    }

    private static Task<HttpResponseMessage> Reply(string json, HttpStatusCode status = HttpStatusCode.OK)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    private sealed class RecordingHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal List<Uri> Uris { get; } = [];
        internal List<HttpMethod> Methods { get; } = [];
        internal List<string> Authorizations { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            Methods.Add(request.Method);
            Authorizations.Add(request.Headers.Authorization!.ToString());
            return reply(Uris.Count, ct);
        }
    }

    private sealed class CountingContent(int length, bool declaredLength, Exception? failure = null, Action? beforeRead = null) : HttpContent
    {
        private readonly CountingStream _stream = new(length, failure, beforeRead);
        internal int BytesRead => _stream.BytesRead;
        internal bool Serialized { get; private set; }
        internal bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long size) { size = length; return declaredLength; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Serialized = true; throw new InvalidOperationException("Content must not be eagerly materialized."); }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => Task.FromResult<Stream>(_stream);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CountingStream(int length, Exception? failure = null, Action? beforeRead = null) : Stream
    {
        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, length - BytesRead);
            Array.Fill(buffer, (byte)' ', offset, read);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            beforeRead?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
            var read = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
