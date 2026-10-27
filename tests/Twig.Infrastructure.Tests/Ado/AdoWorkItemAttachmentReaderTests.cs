using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoWorkItemAttachmentReaderTests
{
    private static readonly Guid AttachmentId = new("4dab0171-49dc-4da5-8f18-35c923fbdb0a");
    private static readonly byte[] Body = [0, 255, 13, 10, 128];

    [Fact]
    public async Task InventoryRetainsExactItemRevisionAndUnknownSize()
    {
        using var handler = new Handler(_ => Json(Inventory(size: null)));
        using var reader = Reader(handler);
        var result = await reader.ReadAsync(42);
        result.WorkItemId.ShouldBe(42);
        result.Revision.ShouldBe(7);
        result.Attachments.ShouldHaveSingleItem().ShouldBe(new(AttachmentId, "report.bin", null));
        handler.Requests.ShouldHaveSingleItem().AbsolutePath.ShouldBe("/fixture/Project%20Space/_apis/wit/workitems/42");
        handler.Requests[0].Query.ShouldBe("?$expand=relations&api-version=7.1");
    }

    [Fact]
    public async Task DownloadProvesMembershipBeforeAndAfterAndPreservesExactBytes()
    {
        using var handler = new Handler(index => index == 2 ? Binary(Body) : Json(Inventory()));
        using var reader = Reader(handler);
        var result = await reader.DownloadAsync(42, 7, AttachmentId);
        result.Content.ShouldBe(Body);
        result.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Body)));
        result.ContentType.ShouldBe("application/octet-stream");
        result.WorkItemId.ShouldBe(42);
        result.Revision.ShouldBe(7);
        result.Attachment.FileName.ShouldBe("report.bin");
        handler.Requests.Count.ShouldBe(3);
        handler.Requests[1].AbsolutePath.ShouldBe($"/fixture/Project%20Space/_apis/wit/attachments/{AttachmentId:D}");
        handler.Requests[1].Query.ShouldBe("?download=true&api-version=7.1");
        handler.Methods.ShouldAllBe(method => method == HttpMethod.Get);
        handler.Authorizations.ShouldAllBe(value => value == "Bearer fixture-token");
    }

    [Theory]
    [InlineData("https://evil.invalid/fixture/_apis/wit/attachments/")]
    [InlineData("https://dev.azure.com/other/_apis/wit/attachments/")]
    [InlineData("https://dev.azure.com/fixture/OtherProject/_apis/wit/attachments/")]
    [InlineData("http://dev.azure.com/fixture/_apis/wit/attachments/")]
    [InlineData("https://user@dev.azure.com/fixture/_apis/wit/attachments/")]
    public async Task ForeignAttachmentUrlsAreRejectedWithoutFetchingThem(string prefix)
    {
        using var handler = new Handler(_ => Json(Inventory(url: prefix + AttachmentId)));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-response-invalid:");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Host.ShouldBe("dev.azure.com");
    }

    [Theory]
    [InlineData("""{"id":99,"rev":7}""")]
    [InlineData("""{"id":42,"rev":0}""")]
    [InlineData("""{"id":"42","rev":7}""")]
    [InlineData("""{"id":42,"rev":"7"}""")]
    [InlineData("""{"id":42,"id":99,"rev":7}""")]
    [InlineData("""{"id":42,"rev":7,"relations":null}""")]
    [InlineData("""{"id":42,"rev":7,"relations":[null]}""")]
    [InlineData("not json")]
    public async Task MalformedInventoryRefusesInsteadOfReportingNoAttachments(string json)
    {
        using var handler = new Handler(_ => Json(json));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.ReadAsync(42)))
            .Message.ShouldStartWith("attachment-response-invalid:");
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder\\file")]
    [InlineData("bad\r\nheader")]
    [InlineData("")]
    [InlineData("..")]
    public async Task UnsafeFilenameIsNotAdmittedToResponseHeaders(string name)
    {
        var json = Inventory().Replace("report.bin", System.Text.Json.JsonEncodedText.Encode(name).ToString(),
            StringComparison.Ordinal);
        using var handler = new Handler(_ => Json(json));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.ReadAsync(42)))
            .Message.ShouldStartWith("attachment-response-invalid:");
    }

    [Fact]
    public async Task StaleInventoryNeverFetchesBinary()
    {
        using var handler = new Handler(_ => Json(Inventory(revision: 8)));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-stale:");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task UnlinkedAttachmentNeverFetchesBinary()
    {
        using var handler = new Handler(_ => Json("""{"id":42,"rev":7,"relations":[]}"""));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-not-linked:");
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevisionOrMembershipChangeDuringDownloadWithholdsBytes(bool revisionChanged)
    {
        using var handler = new Handler(index => index == 2 ? Binary(Body)
            : Json(index == 3 ? revisionChanged ? Inventory(revision: 8) : """{"id":42,"rev":7,"relations":[]}"""
                : Inventory()));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-stale:");
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RedirectDoesNotFollowProviderLocation()
    {
        using var handler = new Handler(index => index == 1 ? Json(Inventory())
            : new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.invalid/steal") },
            });
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-redirect-refused:");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests.ShouldAllBe(uri => uri.Host == "dev.azure.com");
    }

    [Fact]
    public async Task ReportedOversizeRefusesBeforeBinaryRequest()
    {
        using var handler = new Handler(_ => Json(Inventory(size: AttachmentReadLimits.MaximumDownloadBytes + 1L)));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-too-large:");
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BinaryLimitIsEnforcedWithAndWithoutDeclaredLength(bool declared)
    {
        using var handler = new Handler(index =>
        {
            if (index != 2) return Json(Inventory(size: null));
            if (!declared)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new UnspecifiedLengthContent(new byte[AttachmentReadLimits.MaximumDownloadBytes + 1]),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[AttachmentReadLimits.MaximumDownloadBytes + 1]),
            };
        });
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-too-large:");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TruncatedBodyIsNotAccepted()
    {
        using var handler = new Handler(index =>
        {
            if (index != 2) return Json(Inventory());
            var response = Binary(Body);
            response.Content.Headers.ContentLength = Body.Length + 1;
            return response;
        });
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-size-mismatch:");
    }

    [Fact]
    public async Task KnownResourceSizeMustMatchDownloadedBytes()
    {
        using var handler = new Handler(index => index == 2 ? Binary(Body) : Json(Inventory(size: 6)));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.DownloadAsync(42, 7, AttachmentId)))
            .Message.ShouldStartWith("attachment-size-mismatch:");
    }

    [Fact]
    public async Task ExactDownloadLimitIsAcceptedRatherThanSilentlyReduced()
    {
        var bytes = new byte[AttachmentReadLimits.MaximumDownloadBytes];
        using var handler = new Handler(index => index == 2 ? Binary(bytes) : Json(Inventory(size: bytes.Length)));
        using var reader = Reader(handler);
        var result = await reader.DownloadAsync(42, 7, AttachmentId);
        result.Content.Length.ShouldBe(AttachmentReadLimits.MaximumDownloadBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task InvalidItemIdsAreRejectedWithoutHttp(int id)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("No HTTP expected."));
        using var reader = Reader(handler);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(id));
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationIsNotConvertedToAnOfflineOrEmptySuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new Handler(_ => throw new InvalidOperationException("No HTTP expected."));
        using var reader = Reader(handler);
        await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadAsync(42, cancellation.Token));
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NonJsonInventoryCannotMasqueradeAsEmpty()
    {
        using var handler = new Handler(_ => Binary(Encoding.UTF8.GetBytes("""{"id":42,"rev":7}""")));
        using var reader = Reader(handler);
        (await Should.ThrowAsync<AdoException>(() => reader.ReadAsync(42)))
            .Message.ShouldStartWith("attachment-response-invalid:");
    }

    private static AdoWorkItemAttachmentReader Reader(HttpMessageHandler handler)
    {
        var authentication = Substitute.For<IAuthenticationProvider>();
        authentication.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("fixture-token");
        return new(authentication, "fixture", "Project Space", handler: handler);
    }

    private static string Inventory(int revision = 7, long? size = 5, string? url = null) =>
        $$"""{"id":42,"rev":{{revision}},"relations":[{"rel":"AttachedFile","url":"{{url ?? $"https://dev.azure.com/fixture/_apis/wit/attachments/{AttachmentId:D}"}}","attributes":{ "name":"report.bin"{{(size is null ? "" : $",\"resourceSize\":{size}")}} } } ] }""";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Binary(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal List<HttpMethod> Methods { get; } = [];
        internal List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            Methods.Add(request.Method);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(Requests.Count));
        }
    }

    private sealed class UnspecifiedLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
