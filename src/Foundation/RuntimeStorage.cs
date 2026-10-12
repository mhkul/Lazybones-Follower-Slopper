using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoFollower;

public static class RuntimeStorage
{
    private const string Kind = "Lazybones/runtime-storage-v1";
    private sealed record Owner(string Kind, string Root, string Hash, int Pid, long Start, DateTime CreatedUtc);
    private sealed record Child(int Launcher, long LauncherStart, int Pid, long Start);
    public sealed class TrackedProcess : IDisposable
    {
        public Process Process { get; }
        private readonly string? marker;
        internal TrackedProcess(Process process, string? path) { Process = process; marker = path; }
        public void Dispose()
        {
            try { if (marker != null && Process.HasExited) { SafePaths.NoLinks(marker); File.Delete(marker); } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            Process.Dispose();
        }
    }
    public static TrackedProcess Launch(ProcessStartInfo info)
    {
        string? root = BundledRuntime.CurrentRoot, marker = null;
        using var launcher = Process.GetCurrentProcess();
        if (root != null && Path.GetFullPath(info.FileName).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            marker = SafePaths.Below(root, "runtime-child-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(marker, JsonSerializer.Serialize(new Child(launcher.Id, launcher.StartTime.ToUniversalTime().ToFileTimeUtc(), 0, 0), Wire.Options));
        }
        var process = Process.Start(info) ?? throw new IOException("Owned child did not start.");
        try
        {
            if (marker != null) File.WriteAllText(marker, JsonSerializer.Serialize(new Child(launcher.Id, launcher.StartTime.ToUniversalTime().ToFileTimeUtc(), process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc()), Wire.Options));
            return new(process, marker);
        }
        catch { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } process.Dispose(); throw; }
    }
    public static void MarkNew(string folder, string parent, string hash, string[] payloadMembers)
    {
        using var p = Process.GetCurrentProcess(); SafePaths.NoLinks(folder);
        File.WriteAllText(SafePaths.Below(folder, "runtime-storage.json"), JsonSerializer.Serialize(new Owner(Kind, Path.GetFullPath(parent), hash, p.Id, p.StartTime.ToUniversalTime().ToFileTimeUtc(), DateTime.UtcNow), Wire.Options));
        File.WriteAllText(SafePaths.Below(folder, "runtime-members.json"), JsonSerializer.Serialize(payloadMembers));
        using var file = new FileStream(SafePaths.Below(folder, "runtime-use.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    public static FileStream Use(string folder)
    {
        SafePaths.NoLinks(folder);
        // Legacy runtimes get a reader lease but no new ownership stamp. An old
        // app could still be using one without a lease, so it remains unsweepable.
        string file = SafePaths.Below(folder, "runtime-use.lock");
        if (!File.Exists(file)) try { using var created = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read); } catch (IOException) when (File.Exists(file)) { }
        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public static void Sweep(string parent, string current)
    {
        try
        {
            SafePaths.NoLinks(parent);
            DateTime Created(string dir)
            {
                try { return JsonSerializer.Deserialize<Owner>(WorkingStorage.ReadBytes(SafePaths.Below(dir, "runtime-storage.json"), 16384), Wire.Options)?.CreatedUtc ?? DateTime.MinValue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return DateTime.MinValue; }
            }
            int recentInactive = 0; long retainedBytes = 0;
            foreach (string folder in Directory.EnumerateDirectories(parent).Take(256).OrderByDescending(Created))
            {
                if (WorkingStorage.Same(folder, current)) continue;
                try
                {
                    SafePaths.NoLinks(folder);
                    var owner = JsonSerializer.Deserialize<Owner>(WorkingStorage.ReadBytes(SafePaths.Below(folder, "runtime-storage.json"), 16384), Wire.Options) ?? throw new IOException("Runtime owner invalid.");
                    if (owner.Kind != Kind || !WorkingStorage.Same(owner.Root, parent) || !Regex.IsMatch(owner.Hash, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) || File.ReadAllText(SafePaths.Below(folder, "owned-runtime.json")) != owner.Hash) continue;
                    string name = Path.GetFileName(folder);
                    bool partial = Regex.IsMatch(name, "^" + owner.Hash + "\\.partial-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
                    if (name != owner.Hash && !partial) continue;
                    string[] members = JsonSerializer.Deserialize<string[]>(WorkingStorage.ReadBytes(SafePaths.Below(folder, "runtime-members.json"), 65536)) ?? throw new IOException("Runtime member ownership missing.");
                    if (members.Length > 256 || members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length) continue;
                    var allowed = members.Select(p => SafePaths.Below(folder, p)).Concat(new[] { "owned-runtime.json", "runtime-storage.json", "runtime-members.json", "runtime-use.lock" }.Select(p => SafePaths.Below(folder, p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    bool childrenStopped = true;
                    foreach (string childFile in Directory.EnumerateFiles(folder).Where(p => Regex.IsMatch(Path.GetFileName(p), "^runtime-child-[a-f0-9]{32}\\.json$", RegexOptions.CultureInvariant)).Take(256))
                    {
                        var child = JsonSerializer.Deserialize<Child>(WorkingStorage.ReadBytes(childFile, 16384), Wire.Options);
                        // A launcher dying between creation and identity publication
                        // leaves ambiguity. Keep the cache rather than infer shutdown.
                        if (child == null || !WorkingStorage.Inactive(child.Pid, child.Start)) { childrenStopped = false; break; }
                        allowed.Add(childFile);
                    }
                    if (!childrenStopped) continue;
                    if (partial && DateTime.UtcNow - owner.CreatedUtc < TimeSpan.FromDays(1) || !WorkingStorage.Inactive(owner.Pid, owner.Start)) continue;
                    using var mutex = new Mutex(false, "Local\\LazybonesRuntime_" + owner.Hash);
                    bool held = false;
                    try
                    {
                        try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                        if (!held) continue;
                        var files = new List<string>(); var dirs = new List<string>(); var queue = new Queue<string>(); queue.Enqueue(folder);
                        while (queue.TryDequeue(out string? dir)) foreach (string path in Directory.EnumerateFileSystemEntries(dir))
                        {
                            SafePaths.NoLinks(path);
                            if (files.Count + dirs.Count >= 2048) throw new IOException("Runtime cleanup bound reached.");
                            if (Directory.Exists(path)) { dirs.Add(path); queue.Enqueue(path); } else files.Add(path);
                        }
                        if (files.Any(p => !allowed.Contains(p))) continue;
                        using (var lease = new FileStream(SafePaths.Below(folder, "runtime-use.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            if (!partial)
                            {
                                recentInactive++; retainedBytes += files.Sum(p => new FileInfo(p).Length);
                                if (recentInactive <= 2 && retainedBytes <= 512 * 1024 * 1024 && DateTime.UtcNow - owner.CreatedUtc < TimeSpan.FromDays(7)) continue;
                            }
                            // Preserve ownership until all payload deletion succeeded.
                            foreach (string path in files.Where(x => Path.GetFileName(x) is not ("runtime-use.lock" or "runtime-storage.json" or "runtime-members.json" or "owned-runtime.json"))) File.Delete(path);
                            foreach (string dir in dirs.OrderByDescending(x => x.Length)) Directory.Delete(dir, false);
                            File.Delete(SafePaths.Below(folder, "runtime-members.json")); File.Delete(SafePaths.Below(folder, "runtime-storage.json")); File.Delete(SafePaths.Below(folder, "owned-runtime.json"));
                        }
                        File.Delete(SafePaths.Below(folder, "runtime-use.lock")); Directory.Delete(folder, false);
                    }
                    finally { if (held) mutex.ReleaseMutex(); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
