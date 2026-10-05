using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>Private PAT material keyed only by the registry's opaque credential reference.</summary>
internal sealed class PatCredentialStore(string path)
{
    internal string Read()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var entry = JsonSerializer.Deserialize(stream, TwigJsonContext.Default.PatCredentialEntry);
            if (!string.IsNullOrWhiteSpace(entry?.Pat)) return entry.Pat;
        }
        catch (JsonException)
        {
            throw new AdoAuthenticationException("Selected PAT credential file contains invalid JSON. Repair that identity with 'twig auth pat'; no work request was admitted.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new AdoAuthenticationException("Access to the selected PAT credential file was denied. Restore its owner permissions before retrying; no work request was admitted.");
        }
        catch (IOException)
        {
            throw new AdoAuthenticationException("Selected PAT credential file could not be read. Check that it exists and is accessible, or repair that identity with 'twig auth pat'.");
        }
        throw new AdoAuthenticationException("Selected PAT credential is missing or unreadable. Repair that identity with 'twig auth pat'; no alternate account is selected.");
    }

    internal void Write(string pat) => WritePrivateAtomic(path,
        JsonSerializer.Serialize(new PatCredentialEntry { Pat = pat }, TwigJsonContext.Default.PatCredentialEntry));

    // This is an invalidation signal, not principal proof or selection authority.
    // The random stamp also forces already-open runtimes to attest after a named clear.
    internal string ReadResetStamp() => File.Exists(path + ".admission-reset")
        ? File.ReadAllText(path + ".admission-reset") : string.Empty;

    internal void ResetAdmission() => WritePrivateAtomic(path + ".admission-reset", Guid.NewGuid().ToString("N"));

    private static void WritePrivateAtomic(string target, string content)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreatePrivateFile(temp))
            using (var writer = new StreamWriter(stream))
                writer.Write(content);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static FileStream CreatePrivateFile(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new InvalidOperationException("Cannot establish the current credential-file owner.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(file).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}

internal sealed class PatCredentialEntry
{
    [JsonPropertyName("pat")]
    public string? Pat { get; set; }
}
