using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.GitHub;

/// <summary>Reads one explicitly selected repository credential; never writes or enumerates credentials.</summary>
internal sealed class WindowsCredentialManagerTokenSource(
    IWindowsCredentialApi api, GitHubWorkspaceScope scope, string? account) : IGitHubTokenSource
{
    internal const string SourceKind = "windows-credential-manager";
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int MaxBlobBytes = 2560;
    private readonly object _gate = new();
    private string? _token;

    public ValueTask<string> GetTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!api.IsSupported)
                throw Unavailable("Windows Credential Manager is unavailable on this platform");
            if (string.IsNullOrWhiteSpace(account) || account.Any(char.IsWhiteSpace)
                || account.Any(char.IsControl) || account.StartsWith('-'))
                throw Unavailable("set github.account to the exact credential UserName");
            if (_token is not null)
                return ValueTask.FromResult(_token);

            var target = $"twig:github:{scope.Host}/{scope.Repository}";
            var pointer = api.Read(target, Generic);
            try
            {
                if (pointer == nint.Zero)
                    throw Unavailable("the selected generic credential was not returned");
                return ValueTask.FromResult(_token = ReadToken(pointer, target, account));
            }
            finally
            {
                if (pointer != nint.Zero)
                    api.Free(pointer);
            }
        }
    }

    private static unsafe string ReadToken(nint pointer, string target, string account)
    {
        var credential = *(NativeWindowsCredential*)pointer;
        if (credential.Type != Generic || credential.Persist != LocalMachine
            || !string.Equals(Marshal.PtrToStringUni(credential.TargetName), target, StringComparison.Ordinal)
            || !string.Equals(Marshal.PtrToStringUni(credential.UserName), account, StringComparison.Ordinal))
            throw Unavailable("credential type, persistence, target or UserName does not match the explicit selection");
        if (credential.CredentialBlobSize is 0 or > MaxBlobBytes || credential.CredentialBlob == nint.Zero)
            throw Unavailable("the selected credential has no usable token");

        var blob = new byte[credential.CredentialBlobSize];
        try
        {
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            string token;
            try
            {
                token = new UTF8Encoding(false, true).GetString(blob);
            }
            catch (DecoderFallbackException)
            {
                throw Unavailable("the credential blob is not valid UTF-8");
            }
            if (!token.StartsWith("github_pat_", StringComparison.Ordinal)
                || token.Length <= "github_pat_".Length
                || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw Unavailable("the selected credential is not a usable fine-grained token");
            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    private static GitHubCredentialUnavailableException Unavailable(string reason)
        => new($"GitHub credential unavailable: {reason}; no alternate credential source was used.");
}

internal interface IWindowsCredentialApi
{
    bool IsSupported { get; }
    nint Read(string target, uint type);
    void Free(nint credential);
}

internal sealed partial class WindowsCredentialApi : IWindowsCredentialApi
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public nint Read(string target, uint type)
    {
        if (!IsSupported)
            throw new GitHubCredentialUnavailableException("Windows Credential Manager is unavailable on this platform.");
        if (CredRead(target, type, 0, out var credential) == 0)
            throw new GitHubCredentialUnavailableException(
                $"GitHub credential unavailable: CredReadW failed (Win32 {Marshal.GetLastPInvokeError()}); no alternate credential source was used.");
        return credential;
    }

    public void Free(nint credential) => CredFree(credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CredRead(string target, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(nint credential);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWindowsCredential
{
    public uint Flags;
    public uint Type;
    public nint TargetName;
    public nint Comment;
    public uint LastWrittenLow;
    public uint LastWrittenHigh;
    public uint CredentialBlobSize;
    public nint CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public nint Attributes;
    public nint TargetAlias;
    public nint UserName;
}
