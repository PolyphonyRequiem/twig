namespace Bench.Server;

internal sealed class DaemonInstance(FileStream lease, Guid id) : IDisposable
{
    public Guid Id => id;
    public void Dispose() => lease.Dispose();

    public static DaemonInstance Acquire(string data)
    {
        var lease = new FileStream(Path.Combine(data, "daemon.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var path = Path.Combine(data, "daemon.id");
            Guid id;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 64 || !Guid.TryParseExact(File.ReadAllText(path), "D", out id)
                    || id == Guid.Empty)
                    throw new InvalidDataException("Invalid daemon identity; no replacement was generated.");
            }
            else
            {
                id = Guid.NewGuid();
                using var identity = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                identity.Write(System.Text.Encoding.ASCII.GetBytes(id.ToString("D")));
                identity.Flush(true);
            }
            return new(lease, id);
        }
        catch { lease.Dispose(); throw; }
    }
}
