using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>Private per-selection OAuth state; callers hold the cross-process lease for mutations.</summary>
internal sealed class GitHubAppCredentialStore
{
    private readonly string _path;
    private readonly string _directory;
    internal string ClientId { get; }
    internal string Alias { get; }

    internal GitHubAppCredentialStore(string directory, string clientId, string alias)
    {
        ValidateSelection(clientId, alias);
        ClientId = clientId;
        Alias = alias;
        _directory = directory;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"github.com\n{clientId}\n{alias}")));
        _path = Path.Combine(directory, key + ".json");
    }

    internal static string DefaultDirectory
    {
        get
        {
            try { return Path.Combine(ConnectionBindingService.ResolveUserHome(), "github-oauth"); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                throw new GitHubDeviceFlowException("invalid Twig system-state home; no alternate storage was used");
            }
        }
    }

    internal static void ValidateSelection(string clientId, string alias)
    {
        GitHubDeviceFlowClient.ValidateAppClientId(clientId);
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 100
            || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new GitHubDeviceFlowException("an explicit identity alias of ASCII letters, digits, hyphens or underscores is required");
    }

    internal FileStream Acquire()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            return OpenPrivate(_path + ".lock", FileMode.OpenOrCreate);
        }
        catch (IOException)
        {
            throw new GitHubDeviceFlowException("selected identity storage is busy or unavailable; no credential request was made");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GitHubDeviceFlowException("selected identity storage is unavailable");
        }
    }

    internal GitHubAppCredential? Read()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 16384)
                throw new GitHubDeviceFlowException("selected identity storage is invalid; sign in again");
            var entry = JsonSerializer.Deserialize(stream, TwigJsonContext.Default.GitHubAppCredential);
            if (entry is null || entry.Host != "github.com" || entry.ClientId != ClientId
                || entry.Alias != Alias || string.IsNullOrWhiteSpace(entry.ActorId)
                || string.IsNullOrWhiteSpace(entry.Login)
                || !GitHubDeviceFlowClient.AppToken(entry.AccessToken, "ghu_")
                || !GitHubDeviceFlowClient.AppToken(entry.RefreshToken, "ghr_")
                || entry.AccessExpiresAt == default || entry.RefreshExpiresAt == default)
                throw new GitHubDeviceFlowException("selected identity storage is invalid; sign in again");
            return entry;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new GitHubDeviceFlowException("selected identity storage cannot be read; no alternate source was used");
        }
    }

    internal void Write(GitHubAppCredential entry)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = OpenPrivate(temp, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, entry, TwigJsonContext.Default.GitHubAppCredential);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GitHubDeviceFlowException("selected identity could not be saved; sign in again");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GitHubDeviceFlowException("private temporary credential cleanup failed");
            }
        }
    }

    internal void Delete()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GitHubDeviceFlowException("selected identity could not be removed");
        }
    }

    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new GitHubDeviceFlowException("cannot establish credential-file owner");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(mode, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}

internal sealed record GitHubAppCredential
{
    public string Host { get; init; } = "github.com";
    public string ClientId { get; init; } = "";
    public string Alias { get; init; } = "";
    public string ActorId { get; init; } = "";
    public string Login { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset AccessExpiresAt { get; init; }
    public DateTimeOffset RefreshExpiresAt { get; init; }
    public bool RefreshPending { get; init; }
    public override string ToString() => nameof(GitHubAppCredential);
}
