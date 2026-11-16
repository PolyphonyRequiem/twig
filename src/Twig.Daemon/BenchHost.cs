using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Twig.Core;

namespace Bench.Server;

public sealed record ServerOptions(int Port, string DataDirectory, string? WebRoot = null,
    string DataMode = "Native user-wide Benches", X509Certificate2? Certificate = null,
    bool WindowsSignIn = false, string? CanvasInstallRoot = null, Guid DaemonId = default);

public static class BenchHost
{
    internal static string WindowsOwner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows user identity is unavailable.");
    }
    internal static bool IsWindowsOwner(ClaimsPrincipal principal, string owner)
        => OperatingSystem.IsWindows() && principal.Identity is WindowsIdentity identity && identity.User?.Value == owner;

    internal static string Bearer(HttpContext context) => context.Request.Headers.Authorization.ToString() is { } value
        && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : throw new HttpFailure(403, "CapabilityRejected", "Connect this view first.");
    internal static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new HttpFailure(result.Error.Code switch
    {
        ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404, ErrorCode.Conflict => 409, ErrorCode.PolicyViolation => 403, _ => 503,
    }, result.Error.Code.ToString(), $"Operation unavailable ({result.Error.Code}). No substitute data was returned; preserve the same operation UUID if outcome is unknown.");
    internal static async Task<T> Body<T>(HttpContext context, int maximumBytes = 16384)
    {
        if (!context.Request.HasJsonContentType()) throw new HttpFailure(415, "JsonRequired", "Send JSON.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var buffer = new MemoryStream();
        var bytes = new byte[2048];
        int read;
        while ((read = await context.Request.Body.ReadAsync(bytes, timeout.Token)) != 0)
        { if (buffer.Length + read > maximumBytes) throw new HttpFailure(413, "BodyLimit", "Request exceeds bound."); buffer.Write(bytes, 0, read); }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        Unique(doc.RootElement);
        return doc.Deserialize((JsonTypeInfo<T>)TransportJson.Default.GetTypeInfo(typeof(T))!) ?? throw new JsonException();
    }
    internal static async Task Write<T>(HttpContext context, T value, int status = 200)
    {
        using var buffer = new LimitedBuffer();
        JsonSerializer.Serialize(buffer, value, (JsonTypeInfo<T>)TransportJson.Default.GetTypeInfo(typeof(T))!);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), context.RequestAborted);
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in element.EnumerateObject())
            { if (!names.Add(member.Name)) throw new JsonException(); Unique(member.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
            { if (child.ValueKind == JsonValueKind.Null) throw new JsonException(); Unique(child); }
    }
    private sealed class LimitedBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
        private void Bound(int count)
        { if (Length + count > 8 * 1024 * 1024) throw new HttpFailure(503, "Partial", "Projection exceeds the output bound."); }
    }
}
