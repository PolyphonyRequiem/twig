namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Abstracts file and directory operations so <see cref="SelfUpdater"/> can be unit tested.
/// </summary>
internal interface IFileSystem
{
    bool FileExists(string path);
    void FileDelete(string path);
    void FileMove(string source, string destination);
    void FileCopy(string source, string destination, bool overwrite);
    Stream FileCreate(string path);
    Stream FileOpenRead(string path);
    void SetUnixFileMode(string path, UnixFileMode mode);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption);
    void ExtractZipToDirectory(string sourceArchive, string destinationDirectory);
}

/// <summary>
/// Default implementation that delegates to <see cref="System.IO"/> and <see cref="System.IO.Compression.ZipFile"/>.
/// </summary>
internal sealed class DefaultFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public void FileDelete(string path) => File.Delete(path);
    public void FileMove(string source, string destination) => File.Move(source, destination);
    public void FileCopy(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public Stream FileCreate(string path) => File.Create(path);
    public Stream FileOpenRead(string path) => File.OpenRead(path);
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void SetUnixFileMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption searchOption)
        => Directory.EnumerateFiles(directory, searchPattern, searchOption);
    public void ExtractZipToDirectory(string sourceArchive, string destinationDirectory)
        => System.IO.Compression.ZipFile.ExtractToDirectory(sourceArchive, destinationDirectory);
}
