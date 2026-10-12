using System.IO.Compression;
using System.Text.Json;

namespace AutoFollower;

// Installed by the GUI's embedded resource. Source harnesses can still supply
// explicit tools; the packaged application always uses its owned payload.
public static class BundledRuntime
{
    public static Func<Stream>? Payload { get; set; }
    private static string? _root;
    private static FileStream? _useLease;
    private static readonly object Gate = new();
    public static string? CurrentRoot { get { lock (Gate) return _root; } }
    public static string? Resolve()
    {
        if (Payload == null) return null;
        lock (Gate)
        {
            if (_root != null && _useLease != null) return _root;
            using var stream = Payload();
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            stream.Position = 0;
            string parent = Path.Combine(Path.GetTempPath(), "Lazybones Follower Slopper", "runtime");
            string target = Path.Combine(parent, hash);
            SafePaths.NoLinks(parent); Directory.CreateDirectory(parent);
            using var mutex = new Mutex(false, "Local\\LazybonesRuntime_" + hash);
            bool held = false;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(60)); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("Application runtime is busy. Retry shortly.");
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var entry = archive.GetEntry("runtime-files.json") ?? throw new InvalidDataException("Bundled runtime manifest missing.");
                using var manifest = entry.Open();
                var pins = JsonSerializer.Deserialize<RuntimePin[]>(manifest, Wire.Options) ?? throw new InvalidDataException("Bundled runtime manifest invalid.");
                if (pins.Length > 256 || pins.Length != archive.Entries.Count - 1 || pins.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Length)
                    throw new InvalidDataException("Bundled runtime manifest ambiguous.");
                void Validate(string root)
                {
                    SafePaths.NoLinks(root);
                    foreach (var pin in pins)
                    {
                        string path = SafePaths.Below(root, pin.Path); SafePaths.NoLinks(path);
                        if (!File.Exists(path) || new FileInfo(path).Length != pin.Bytes || Hashing.File(path) != pin.Sha256)
                            throw new InvalidDataException("Application runtime cache is incomplete or changed: " + pin.Path + ". Close the application and remove only this cache folder: " + root);
                    }
                    string owner = Path.Combine(root, "owned-runtime.json"); SafePaths.NoLinks(owner);
                    if (!File.Exists(owner) || File.ReadAllText(owner) != hash) throw new InvalidDataException("Application runtime cache ownership mismatch: " + root);
                }
                if (Directory.Exists(target)) Validate(target);
                else
                {
                    if (File.Exists(target)) throw new IOException("Application runtime cache collides with a file: " + target);
                    string staging = Path.Combine(parent, hash + ".partial-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    File.WriteAllText(Path.Combine(staging, "owned-runtime.json"), hash);
                    RuntimeStorage.MarkNew(staging, parent, hash, pins.Select(p => p.Path).ToArray());
                    foreach (var pin in pins)
                    {
                        string path = SafePaths.Below(staging, pin.Path); SafePaths.NoLinks(path);
                        var member = archive.GetEntry(pin.Path) ?? throw new InvalidDataException("Bundled runtime member missing.");
                        if (member.Length != pin.Bytes || pin.Bytes < 0 || pin.Bytes > 200_000_000) throw new InvalidDataException("Bundled runtime member bound invalid.");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using var input = member.Open(); using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output);
                    }
                    Validate(staging); Directory.Move(staging, target);
                }
                _useLease = RuntimeStorage.Use(target);
                _root = target;
                RuntimeStorage.Sweep(parent, target);
                return target;
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }
    private sealed record RuntimePin(string Path, string Sha256, long Bytes);
}
