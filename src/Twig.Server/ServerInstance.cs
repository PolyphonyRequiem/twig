namespace Twig.Server;

internal sealed class ServerInstance(FileStream lease, Guid id) : IDisposable
{
    public Guid Id => id;
    public void Dispose() => lease.Dispose();

    public static ServerInstance Acquire(string data, Guid? requiredId = null)
    {
        var lease = new FileStream(Path.Combine(data, "server.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(data, "server.id");
            Guid id;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 64 || !Guid.TryParseExact(File.ReadAllText(path), "D", out id)
                    || id == Guid.Empty || requiredId.HasValue && requiredId != id)
                    throw new InvalidDataException("Invalid server identity; no replacement was generated.");
            }
            else
            {
                id = requiredId ?? Guid.NewGuid();
                using var identity = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                identity.Write(System.Text.Encoding.ASCII.GetBytes(id.ToString("D")));
                identity.Flush(true);
            }
            return new(lease, id);
        }
        catch { lease.Dispose(); throw; }
    }
}
