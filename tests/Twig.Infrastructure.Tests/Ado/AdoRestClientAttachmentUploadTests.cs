using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoRestClientAttachmentUploadTests
{
    private static readonly byte[] Content = [1, 2, 3];
    private static readonly string Sha256 = Convert.ToHexStringLower(SHA256.HashData(Content));
    private const string IntentDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AttachmentId = "8a814c6e-e1c8-4efb-8cf1-7780b0ad2222";
    private const string AttachmentUrl = "https://dev.azure.com/fixture/_apis/wit/attachments/" + AttachmentId;

    [Fact]
    public void SeparateAttachmentEvidenceDoesNotChangeExistingWriteIntentDigests()
    {
        using var fixture = new UploadFixture();
        var request = new ConnectionRemoteWriteRequest("workitem-patch", "PATCH",
            "https://dev.azure.com/fixture/Project/_apis/wit/workitems/42", "[]", "7");
        var legacyCanonical = string.Join('\n', fixture.Binding.Binding.ConnectionRef,
            fixture.Binding.Identity.IdentityId, fixture.Binding.Identity.Method,
            fixture.Binding.Identity.CredentialRef, request.EffectKind, request.Method,
            request.Target, request.IfMatch, request.Payload);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyCanonical)));

        ConnectionRemoteWriteAdmission.RequestDigest(fixture.Binding, request).ShouldBe(expected);
        JsonSerializer.Serialize(request, TwigJsonContext.Default.ConnectionRemoteWriteRequest)
            .ShouldNotContain("\"evidence\"");
        var attachmentRequest = request with { Evidence = "{\"attachmentId\":\"captured\"}" };
        var attachmentDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(legacyCanonical + "\n" + attachmentRequest.Evidence)));
        ConnectionRemoteWriteAdmission.RequestDigest(fixture.Binding, attachmentRequest).ShouldBe(attachmentDigest);
        JsonSerializer.Serialize(attachmentRequest, TwigJsonContext.Default.ConnectionRemoteWriteRequest)
            .ShouldContain("\"evidence\"");
    }

    [Fact]
    public async Task ProductionWriterPostsBytesThenLinksExactReceiptAtCapturedRevision()
    {
        using var fixture = new UploadFixture();
        using var handler = new RecordingHandler((request, _, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? Reply($$"""{"id":"{{AttachmentId}}","url":"{{AttachmentUrl}}"}""", HttpStatusCode.Created)
                : Reply($$"""{"id":42,"rev":8,"relations":[{"rel":"AttachedFile","url":"{{AttachmentUrl}}"}]}""")));
        using var http = new HttpClient(handler);
        var client = fixture.Client(http);

        var result = await client.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", Content,
            Sha256, IntentDigest);

        result.WorkItemId.ShouldBe(42);
        result.Revision.ShouldBe(8);
        result.AttachmentId.ToString("D").ShouldBe(AttachmentId);
        handler.Methods.ShouldBe([HttpMethod.Post, HttpMethod.Patch]);
        handler.Bodies[0].SequenceEqual(Content).ShouldBeTrue();
        Encoding.UTF8.GetString(handler.Bodies[1]).ShouldContain("\"path\":\"/rev\"");
        Encoding.UTF8.GetString(handler.Bodies[1]).ShouldContain(AttachmentUrl);
        handler.IfMatches.ShouldBe([null, "7"]);

        var histories = await fixture.ReadHistoryAsync();
        histories.Count.ShouldBe(2);
        histories.Select(history => history.Intent.Request.EffectKind)
            .ShouldBe(["attachment-upload", "attachment-link"]);
        histories.ShouldAllBe(history => history.Receipt != null && history.Receipt.Kind == "acknowledged");
        histories[0].Intent.Request.Payload.ShouldNotContain(Convert.ToBase64String(Content));
        fixture.Requests[1].Payload.ShouldContain("\"path\":\"/relations/-\"");
        fixture.Requests[1].Evidence!.ShouldContain("\"attachmentUrl\"");
    }

    [Fact]
    public async Task UnknownUploadSurvivesClientReopenAndFencesChangedIntentBeforeAnotherPost()
    {
        using var fixture = new UploadFixture();
        using (var firstHandler = new RecordingHandler((_, _, _) =>
            throw new HttpRequestException("simulated lost response")))
        using (var firstHttp = new HttpClient(firstHandler))
        {
            var firstClient = fixture.Client(firstHttp);
            await Should.ThrowAsync<AdoAttachmentUploadUnknownException>(() =>
                firstClient.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", Content,
                    Sha256, IntentDigest));
            firstHandler.Methods.ShouldBe([HttpMethod.Post]);
        }

        using var retryHandler = new RecordingHandler((_, _, _) =>
            Task.FromResult(Reply($$"""{"id":"{{AttachmentId}}","url":"{{AttachmentUrl}}"}""", HttpStatusCode.Created)));
        using var retryHttp = new HttpClient(retryHandler);
        var reopenedClient = fixture.Client(retryHttp);
        var replay = await Should.ThrowAsync<InvalidOperationException>(() =>
            reopenedClient.UploadAndLinkAsync(42, 8, "report.txt", "text/plain", Content,
                Sha256, new string('b', 64)));
        replay.Message.ShouldContain("remote-write-attachment-outcome-unknown");
        retryHandler.Methods.ShouldBeEmpty();

        var history = await fixture.ReadHistoryAsync();
        history.ShouldHaveSingleItem().Receipt.ShouldBeNull();
        history[0].Observations.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationAfterUploadDispatchIsUnknownAndFencesReplayAfterClientReopen()
    {
        using var fixture = new UploadFixture();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var firstHandler = new RecordingHandler(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply("{}", HttpStatusCode.OK);
        }))
        using (var firstHttp = new HttpClient(firstHandler))
        {
            var firstClient = fixture.Client(firstHttp);
            var upload = firstClient.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", Content,
                Sha256, IntentDigest, cancellation.Token);
            await started.Task;
            cancellation.Cancel();
            await Should.ThrowAsync<AdoAttachmentUploadUnknownException>(() => upload);
            firstHandler.Methods.ShouldBe([HttpMethod.Post]);
        }

        var history = await fixture.ReadHistoryAsync();
        history.ShouldHaveSingleItem().Receipt.ShouldBeNull();
        history[0].Observations.ShouldBeEmpty();

        using var retryHandler = new RecordingHandler((_, _, _) =>
            Task.FromResult(Reply($$"""{"id":"{{AttachmentId}}","url":"{{AttachmentUrl}}"}""", HttpStatusCode.Created)));
        using var retryHttp = new HttpClient(retryHandler);
        var reopenedClient = fixture.Client(retryHttp);
        var replay = await Should.ThrowAsync<InvalidOperationException>(() =>
            reopenedClient.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", Content,
                Sha256, IntentDigest));
        replay.Message.ShouldContain("remote-write-attachment-outcome-unknown");
        retryHandler.Methods.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationBeforeHttpDispatchIsDurableAndMayBeExplicitlyReconsidered()
    {
        using var fixture = new UploadFixture();
        using var cancellation = new CancellationTokenSource();
        using var canceledHandler = new RecordingHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run"));
        using var canceledHttp = new HttpClient(canceledHandler);
        var canceledClient = fixture.Client(canceledHttp, cancellation.Cancel);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            canceledClient.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", Content,
                Sha256, IntentDigest, cancellation.Token));
        canceledHandler.Methods.ShouldBeEmpty();
        (await fixture.ReadHistoryAsync()).ShouldHaveSingleItem().Receipt?.Kind.ShouldBe("not-dispatched");

        using var retryHandler = new RecordingHandler((request, _, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? Reply($$"""{"id":"{{AttachmentId}}","url":"{{AttachmentUrl}}"}""", HttpStatusCode.Created)
                : Reply($$"""{"id":42,"rev":8,"relations":[{"rel":"AttachedFile","url":"{{AttachmentUrl}}"}]}""")));
        using var retryHttp = new HttpClient(retryHandler);
        var retryClient = fixture.Client(retryHttp);
        (await retryClient.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", Content,
            Sha256, IntentDigest)).AttachmentId.ToString("D").ShouldBe(AttachmentId);
    }

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, byte[], CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal List<HttpMethod> Methods { get; } = [];
        internal List<byte[]> Bodies { get; } = [];
        internal List<string?> IfMatches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct));
            IfMatches.Add(request.Headers.TryGetValues("If-Match", out var values) ? values.SingleOrDefault() : null);
            return await reply(request, Bodies[^1], ct);
        }
    }

    private sealed class UploadFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "twig-attachment-upload-" + Guid.NewGuid().ToString("N"));
        private readonly string _registryPath;

        internal ResolvedConnectionBinding Binding { get; }
        internal List<ConnectionRemoteWriteRequest> Requests { get; } = [];

        internal UploadFixture()
        {
            Directory.CreateDirectory(_directory);
            _registryPath = Path.Combine(_directory, "system.db");
            var worktree = Path.Combine(_directory, "worktree");
            Directory.CreateDirectory(worktree);
            Binding = new(
                new IdentityBinding("upload-binding", "upload-connection", "upload-identity", 1),
                new AuthenticationIdentity("upload-identity", "fixture", "tenant", "object", "issuer",
                    "login.microsoftonline.com", "credential-ref", null),
                worktree, "checkout-binding-pin", 1,
                new ConnectionOperationSnapshot("https://dev.azure.com/fixture", "Project", "",
                    "upload-worktree-fingerprint", 1, "/fixture/config", "/fixture/preferences", "ascii", "default"));

            using var registry = new SqliteSystemWorktreeRegistry(_registryPath, TimeProvider.System);
            (registry.EnsureBindingConnectionAsync("upload-connection", "https://dev.azure.com/fixture", "Project",
                CancellationToken.None)
                .GetAwaiter().GetResult()).IsSuccess.ShouldBeTrue();
            (registry.UpsertWorktreeAsync(Binding.Operation.WorktreeFingerprint,
                Binding.Binding.ConnectionRef, Binding.WorktreeRoot).GetAwaiter().GetResult()).IsSuccess.ShouldBeTrue();
            var now = DateTimeOffset.UtcNow;
            (registry.InsertIdentityAsync(new IdentityRow("upload-identity", "fixture", "tenant", "object",
                "issuer", "login.microsoftonline.com", "credential-ref", null, now, now)).GetAwaiter().GetResult())
                .IsSuccess.ShouldBeTrue();
            (registry.InsertOrGetBindingAsync(new BindingRow("upload-binding", "upload-connection",
                "upload-identity", 1, now, now)).GetAwaiter().GetResult()).IsSuccess.ShouldBeTrue();
        }

        internal AdoRestClient Client(HttpClient http, Action? afterAdmission = null) =>
            new(http, new UploadAuthentication(_registryPath, Binding, afterAdmission, Requests.Add),
                "https://dev.azure.com/fixture", "Project", new WorkItemMapper());

        internal async Task<IReadOnlyList<ConnectionRemoteWriteHistory>> ReadHistoryAsync()
        {
            using var registry = new SqliteSystemWorktreeRegistry(_registryPath, TimeProvider.System);
            var result = await registry.ReadConnectionRemoteWritesAsync(Binding.Operation.WorktreeFingerprint);
            result.IsSuccess.ShouldBeTrue(result.Error);
            return result.Value;
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private sealed class UploadAuthentication(
        string registryPath, ResolvedConnectionBinding binding, Action? afterAdmission,
        Action<ConnectionRemoteWriteRequest> observe) :
        IAuthenticationProvider, IConnectionRemoteWriteGuard
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("fixture-token");
        }

        public void InvalidateToken() { }

        public async Task<IConnectionRemoteWriteAdmission> BeginRemoteWriteAsync(
            ConnectionRemoteWriteRequest request, CancellationToken ct = default)
        {
            observe(request);
            var admission = await ConnectionRemoteWriteAdmission.BeginAsync(registryPath, binding, request, ct);
            afterAdmission?.Invoke();
            return admission;
        }
    }
}
