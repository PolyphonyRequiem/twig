namespace Twig.Core;

/// <summary>Ephemeral identity presentation, never a field value or write-admission input.</summary>
public sealed record IdentityProfile(string Id, string? DisplayName, string? ImageDataUrl, string? Error);

public static class ProfileImage
{
    public const int MaximumBytes = 256 * 1024;

    public static Result<string> Encode(ReadOnlySpan<byte> bytes)
    {
        var format = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "png"
            : bytes.StartsWith(new byte[] { 255, 216, 255 }) ? "jpeg"
            : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? "webp" : null;
        return format is not null && bytes.Length <= MaximumBytes
            ? Result<string>.Ok($"data:image/{format};base64,{Convert.ToBase64String(bytes)}")
            : Result<string>.Fail(ErrorCode.Unavailable, "Profile photo is not a bounded PNG, JPEG or WebP image.");
    }
}
