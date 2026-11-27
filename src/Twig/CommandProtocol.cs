using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Serialization;

namespace Twig;

internal sealed record CommandInvocation(int Version, string[] Arguments, string Directory, string MetadataHome,
    bool OutputRedirected);

[JsonSerializable(typeof(CommandInvocation))]
internal sealed partial class CommandWireJson : JsonSerializerContext;

internal enum FrameKind : byte { Invocation, Stdout, Stderr, Exit, ReadLine, ReadBody, Edit, Text, End, ReadPat, Negotiate, Bound }

internal static class CommandProtocol
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaximumBytes = 8 * 1024 * 1024;

    internal static async Task WriteAsync(Stream stream, FrameKind kind, string text, CancellationToken ct = default)
    {
        int length = Utf8.GetByteCount(text);
        if (length > MaximumBytes) throw new InvalidDataException("Command frame exceeds 8 MiB.");
        var buffer = new byte[5 + length];
        buffer[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1), length);
        Utf8.GetBytes(text, buffer.AsSpan(5));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<(FrameKind Kind, string Text)> ReadAsync(Stream stream, CancellationToken ct = default,
        int maximum = MaximumBytes)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > maximum || header[0] > (byte)FrameKind.Bound)
            throw new InvalidDataException("Invalid command frame.");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return ((FrameKind)header[0], Utf8.GetString(buffer));
    }
}
