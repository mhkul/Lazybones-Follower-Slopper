using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoFollower;

// This version owns individual attempts, never the configured parent or legacy jobs.
public static class WorkingStorage
{
    public const string Kind = "Lazybones/working-storage-v1";
    public const int MaximumFiles = 256;
    public const long FileLimit = 4 * 1024 * 1024, TotalLimit = 32 * 1024 * 1024;
    public const int HistoryCount = 8, HistoryDays = 7;
    public const long HistoryBytes = 128 * 1024 * 1024;
    private static readonly Regex AttemptName = new("^build-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex StageName = new("^(prebuilt-[0-9]{1,3}|plugin-(generation|validation)-(prebuilt|obody|as-is))$", RegexOptions.CultureInvariant);
    public sealed record Owner(string Kind, string Directory, string Root, Guid Id, int Pid, long Start, DateTime CreatedUtc, string Request);
    public sealed record State(string Status, DateTime UpdatedUtc, bool SnapshotReady, bool PendingDelivery, string? SnapshotSha256 = null);
    public sealed record Item(string Entry, string Source, string Disposition, long Bytes, long IncludedBytes);
    public sealed record Outcome(bool SnapshotReady, bool Reclaimed, string Notice);
    public sealed record PackageReceipt(string Kind, Guid OwnerId, string Path, string Sha256, long Bytes);
    public sealed record DeliveryReceipt(string Kind, Guid OwnerId, string Path, string Sha256, long Bytes, string Destination);
    public static FileStream Begin(string attempt, string request)
    {
        var path = Path.GetFullPath(attempt); SafePaths.NoLinks(path);
        using var process = Process.GetCurrentProcess();
        var owner = new Owner(Kind, path, Path.GetDirectoryName(path)!, Guid.NewGuid(), process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), DateTime.UtcNow, Path.GetFullPath(request));
        WriteNew(Path.Combine(path, "storage-owner.json"), owner);
        using (new FileStream(Path.Combine(path, "storage.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        WriteState(path, new("active", DateTime.UtcNow, false, false));
        return Lease(path);
    }
    public static FileStream Lease(string attempt)
    {
        RequireOwner(attempt);
        return new FileStream(SafePaths.Below(attempt, "storage.lock"), FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static FileStream Exclusive(string attempt)
    {
        RequireOwner(attempt);
        return new FileStream(SafePaths.Below(attempt, "storage.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    public static Owner RequireOwner(string attempt)
    {
        attempt = Path.GetFullPath(attempt); SafePaths.NoLinks(attempt);
        var owner = Read<Owner>(SafePaths.Below(attempt, "storage-owner.json"));
        if (owner.Kind != Kind || owner.Id == Guid.Empty || !Same(owner.Directory, attempt) || !Same(owner.Root, Path.GetDirectoryName(attempt)!) || !AttemptName.IsMatch(Path.GetFileName(attempt)) || !File.Exists(SafePaths.Below(attempt, "owned-output.json")))
            throw new IOException("Working storage ownership unavailable.");
        using var original = JsonDocument.Parse(ReadBytes(SafePaths.Below(attempt, "owned-output.json"), 16384));
        if (original.RootElement.GetProperty("kind").GetString() != "T09B lean export build" || original.RootElement.GetProperty("process").GetInt32() != owner.Pid || original.RootElement.GetProperty("createdFileTime").GetInt64() != owner.Start)
            throw new IOException("Working storage ownership disagrees.");
        return owner;
    }
    public static Outcome Finish(string attempt, bool pendingDelivery, string status)
    {
        bool snapshotReady = false;
        try
        {
            using var gate = Exclusive(attempt);
            if (!ChildrenStopped(attempt)) return new(false, false, "Working files retained: child activity could not be ruled out.");
            Snapshot(attempt);
            snapshotReady = true;
            // Memory/flag absence is never delivery proof. A ZIP or completion
            // receipt can precede the GUI finally block and even result.json.
            bool pending = RetainPackage(attempt, GetState(attempt), pendingDelivery);
            WriteState(attempt, new(pending ? "delivery-unconfirmed" : status is "cancelled" ? "cancelled" : status is "success" ? "success" : "failed", DateTime.UtcNow, true, pending, Hashing.File(SafePaths.Below(attempt, "diagnostics/private.zip"))));
            if (pending) return new(true, false, "Compact diagnostics kept; package and retry inputs retained because saving is unconfirmed. See Recent diagnostics.");
            bool cleaned = Reclaim(attempt);
            RemoveRequest(RequireOwner(attempt));
            return new(true, cleaned, cleaned ? "Compact diagnostics kept for up to 7 days; bulky working copies released." : "Compact diagnostics kept; some working files could not be released.");
        }
        catch (Exception e) when (Recoverable(e)) { return new(snapshotReady, false, snapshotReady ? "Compact diagnostics kept; some working files could not be released." : "Working files retained: compact diagnostic cleanup unavailable."); }
    }
    // Called while the producer still holds its lifetime lease, before the
    // builder publishes its top-level result. The Python producer also emits
    // this receipt before publishing its package-stage result.
    public static void PackageReady(string attempt, JsonElement distribution)
    {
        using var lease = Lease(attempt);
        var owner = RequireOwner(attempt);
        var package = ValidatePackage(attempt, distribution.GetProperty("path").GetString()!, distribution.GetProperty("sha256").GetString()!);
        WriteAtomic(attempt, "package-ready.json", new PackageReceipt("Lazybones/package-ready-v1", owner.Id, package.Path, package.Sha256, package.Bytes));
        var state = GetState(attempt);
        WriteState(attempt, state with { PendingDelivery = true, UpdatedUtc = DateTime.UtcNow });
    }
    public static PackageReceipt PendingPackage(string attempt)
    {
        var owner = RequireOwner(attempt);
        // A stage-only/ambiguous ZIP is protected, but cannot be presented as
        // a completed follower without the builder's structural success.
        using var result = JsonDocument.Parse(ReadBytes(SafePaths.Below(attempt, "result.json"), (int)FileLimit));
        if (result.RootElement.GetProperty("status").GetString() != "structural-build-passed") throw new IOException("Package retained; final build success is unconfirmed.");
        var distribution = result.RootElement.GetProperty("distribution");
        var package = ValidatePackage(attempt, distribution.GetProperty("path").GetString()!, distribution.GetProperty("sha256").GetString()!);
        string ready = SafePaths.Below(attempt, "package-ready.json");
        if (File.Exists(ready))
        {
            var receipt = Read<PackageReceipt>(ready);
            if (receipt.Kind != "Lazybones/package-ready-v1" || receipt.OwnerId != owner.Id || !Same(receipt.Path, package.Path) || !string.Equals(receipt.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase) || receipt.Bytes != package.Bytes)
                throw new IOException("Package completion evidence disagrees.");
        }
        return package with { OwnerId = owner.Id };
    }
    public static void Delivered(string attempt, string source, string destination)
    {
        using var lease = Lease(attempt);
        var owner = RequireOwner(attempt); var package = PendingPackage(attempt);
        if (!Same(source, package.Path)) throw new IOException("Saved package source disagrees.");
        destination = Path.GetFullPath(destination); SafePaths.NoLinks(destination);
        if (Same(destination, owner.Root) || destination.StartsWith(Path.TrimEndingDirectorySeparator(owner.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Saved package must be outside working storage.");
        if (new FileInfo(destination).Length != package.Bytes || !Hashing.File(destination).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Saved package differs.");
        // Persist readback proof before either memory or Finish acknowledges
        // delivery. A failure here retains the owned package for recovery.
        WriteAtomic(attempt, "package-delivery.json", new DeliveryReceipt("Lazybones/package-delivered-v1", owner.Id, package.Path, package.Sha256, package.Bytes, destination));
        var state = GetState(attempt); WriteState(attempt, state with { PendingDelivery = false, UpdatedUtc = DateTime.UtcNow });
    }
    private static PackageReceipt ValidatePackage(string attempt, string path, string sha256)
    {
        path = Path.GetFullPath(path); SafePaths.NoLinks(path);
        if (!Same(Path.GetDirectoryName(path)!, SafePaths.Below(attempt, "packages")) || !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)) throw new IOException("Package ownership unavailable.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        if (zip.Entries.Count is 0 or > 50000 || zip.Entries.Any(e => e.FullName.StartsWith('/') || e.FullName.StartsWith('\\') || e.FullName.Replace('\\', '/').Split('/').Any(p => p is "." or ".." || p.Contains(':'))) || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count) throw new IOException("Package archive structure disagrees.");
        if (!Hashing.File(path).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package changed after completion.");
        return new("Lazybones/package-ready-v1", Guid.Empty, path, sha256.ToUpperInvariant(), input.Length);
    }
    private static bool ConfirmedDelivery(string attempt)
    {
        string path = SafePaths.Below(attempt, "package-delivery.json");
        if (!File.Exists(path)) return false;
        try
        {
            var owner = RequireOwner(attempt); var receipt = Read<DeliveryReceipt>(path);
            if (receipt.Kind != "Lazybones/package-delivered-v1" || receipt.OwnerId != owner.Id || receipt.Bytes <= 0 || !Same(Path.GetDirectoryName(receipt.Path)!, SafePaths.Below(attempt, "packages")) || !Path.GetExtension(receipt.Path).Equals(".zip", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(receipt.Sha256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)) return false;
            string ready = SafePaths.Below(attempt, "package-ready.json");
            if (File.Exists(ready))
            {
                var completed = Read<PackageReceipt>(ready);
                if (completed.Kind != "Lazybones/package-ready-v1" || completed.OwnerId != owner.Id || !Same(completed.Path, receipt.Path) || !string.Equals(completed.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase) || completed.Bytes != receipt.Bytes) return false;
            }
            string resultPath = SafePaths.Below(attempt, "result.json");
            if (File.Exists(resultPath))
            {
                using var result = JsonDocument.Parse(ReadBytes(resultPath, (int)FileLimit));
                if (result.RootElement.GetProperty("status").GetString() != "structural-build-passed") return false;
                var distribution = result.RootElement.GetProperty("distribution");
                if (!Same(distribution.GetProperty("path").GetString()!, receipt.Path) || !string.Equals(distribution.GetProperty("sha256").GetString(), receipt.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            string destination = Path.GetFullPath(receipt.Destination); SafePaths.NoLinks(destination);
            if (Same(destination, owner.Root) || destination.StartsWith(Path.TrimEndingDirectorySeparator(owner.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(destination) || new FileInfo(destination).Length != receipt.Bytes || !Hashing.File(destination).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            SafePaths.NoLinks(receipt.Path);
            if (File.Exists(receipt.Path) && (new FileInfo(receipt.Path).Length != receipt.Bytes || !Hashing.File(receipt.Path).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase))) return false;
            // An unrelated second ZIP must not be erased under the first's
            // delivery receipt. Enumeration is bounded and direct only.
            string packages = SafePaths.Below(attempt, "packages");
            if (Directory.Exists(packages))
            {
                var entries = Directory.EnumerateFileSystemEntries(packages).Take(4097).ToArray();
                if (entries.Length > 4096) return false;
                foreach (string entry in entries) { SafePaths.NoLinks(entry); if (Path.GetExtension(entry).Equals(".zip", StringComparison.OrdinalIgnoreCase) && !Same(entry, receipt.Path)) return false; }
            }
            return true;
        }
        catch (Exception e) when (Recoverable(e)) { return false; }
    }
    private static bool RetainPackage(string attempt, State state, bool requested = false)
    {
        if (ConfirmedDelivery(attempt)) return false;
        if (requested || state.PendingDelivery || File.Exists(SafePaths.Below(attempt, "package-ready.json")) || File.Exists(SafePaths.Below(attempt, "package-delivery.json"))) return true;
        string packages = SafePaths.Below(attempt, "packages");
        if (Directory.Exists(packages))
        {
            var entries = Directory.EnumerateFileSystemEntries(packages).Take(4097).ToArray();
            if (entries.Length > 4096) throw new IOException("Package enumeration bound reached.");
            foreach (string entry in entries) { SafePaths.NoLinks(entry); if (Path.GetExtension(entry).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return true; }
        }
        foreach (string name in new[] { "result.json", "package-stage-result.json", "packages/packages.json" })
        {
            string path = SafePaths.Below(attempt, name);
            if (!File.Exists(path)) continue;
            try { using var result = JsonDocument.Parse(ReadBytes(path, (int)FileLimit)); if (result.RootElement.TryGetProperty("distribution", out var distribution) && distribution.ValueKind == JsonValueKind.Object) return true; }
            catch (JsonException) { } // No package/receipt was found; malformed failure prose alone is not completion.
        }
        return false;
    }
    public static bool ChildrenStopped(string attempt)
    {
        // Launch intent precedes process creation. Missing identity after a crash
        // is deliberately insufficient evidence; no age/PID-only recovery.
        var rootFiles = Directory.EnumerateFiles(attempt).Take(4097).ToArray();
        if (rootFiles.Length > 4096) return false;
        var intents = rootFiles.Select(Path.GetFileName).Where(x => x != null && x.EndsWith("-launch.json", StringComparison.Ordinal)).Select(x => x![..^12]).Where(x => x is "head-stage" or "package-stage" || StageName.IsMatch(x));
        foreach (string name in intents)
        {
            string intent = SafePaths.Below(attempt, name + "-launch.json");
            if (!File.Exists(intent)) continue;
            string receipt = SafePaths.Below(attempt, name + "-process.json");
            if (!File.Exists(receipt)) return false;
            using var doc = JsonDocument.Parse(ReadBytes(receipt, 65536));
            var p = doc.RootElement;
            int id = p.TryGetProperty("id", out var v) ? v.GetInt32() : p.GetProperty("pid").GetInt32();
            long start = p.GetProperty("createdFileTime").GetInt64();
            if (!Inactive(id, start)) return false;
        }
        return true;
    }
    public static bool Inactive(int pid, long start)
    {
        if (pid <= 0 || start <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return p.HasExited || p.StartTime.ToUniversalTime().ToFileTimeUtc() != start; }
        catch (ArgumentException) { return true; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
    private static IEnumerable<(string Source, string Entry)> Candidates(string attempt)
    {
        string[] roots = ["result.json", "package-ready.json", "package-delivery.json", "input-plan.json", "record-inputs.json", "master-paths.json", "compatibility.json", "body-work.json", "body-project-choices.json", "head-stage.log", "head-stage-process.json", "head-stage-exit.json", "package-stage.log", "package-stage-process.json", "package-stage-exit.json", "package-stage-progress.json", "package-stage-result.json", "plugin-validation.json", "private-head-result.json"];
        foreach (string s in roots) yield return (s, s);
        foreach (string s in new[] { "body-stage/native-paths.json", "body-stage/body-result.json", "body-stage/tool/Log_BS.txt", "body-stage/tool/BuildSelection.xml", "body-stage/tool/Config.xml", "private-assets/asset-manifest.json", "packages/packages.json" }) yield return (s, s);
        foreach (string v in new[] { "prebuilt", "obody", "as-is" })
        {
            yield return ("private-assets/" + v + "-manifest.json", "private-assets/" + v + "-manifest.json");
            yield return ("plugin-validation-" + v + ".json", "plugin-validation-" + v + ".json");
            foreach (string phase in new[] { "generation", "validation" }) foreach (string ext in new[] { ".log", "-process.json", "-launch.json" })
            {
                string name = "plugin-" + phase + "-" + v + ext;
                yield return (name, name);
            }
            yield return ("plugin/" + v + "/records.json", "plugin/" + v + "/records.json");
            string folder = SafePaths.Below(attempt, "plugin/" + v);
            if (Directory.Exists(folder))
            {
                var esp = Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).Equals(".esp", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (esp.Length == 1) yield return (Path.GetRelativePath(attempt, esp[0]).Replace('\\', '/'), "plugin/" + v + "/generated.esp");
            }
        }
        // Only the direct numeric overlay children emitted by LeanRecordIndex.
        string overlays = SafePaths.Below(attempt, "record-inputs");
        if (Directory.Exists(overlays))
        {
            int n = 0;
            foreach (string dir in Directory.EnumerateDirectories(overlays).Take(MaximumFiles))
            {
                if (!Regex.IsMatch(Path.GetFileName(dir), "^[0-9]{1,6}$", RegexOptions.CultureInvariant)) continue;
                SafePaths.NoLinks(dir);
                foreach (string file in Directory.EnumerateFiles(dir).Take(2))
                    if (Path.GetExtension(file).ToLowerInvariant() is ".esp" or ".esm" or ".esl") yield return (Path.GetRelativePath(attempt, file).Replace('\\', '/'), "record-inputs/overlay-" + (++n).ToString("D3") + Path.GetExtension(file).ToLowerInvariant());
            }
        }
        // Names come from a directory of this attempt, never a path in a log.
        foreach (string p in Directory.EnumerateFiles(attempt).Take(4096))
        {
            string file = Path.GetFileName(p), stem = Path.GetFileNameWithoutExtension(file);
            if (StageName.IsMatch(stem) && Path.GetExtension(file) == ".log") yield return (file, file);
            if (file.StartsWith("prebuilt-", StringComparison.Ordinal) && (file.EndsWith("-process.json", StringComparison.Ordinal) || file.EndsWith("-launch.json", StringComparison.Ordinal)))
            {
                string stage = file[..file.LastIndexOf('-')];
                if (StageName.IsMatch(stage)) yield return (file, file);
            }
        }
    }
    public static void Snapshot(string attempt)
    {
        RequireOwner(attempt);
        string folder = SafePaths.Below(attempt, "diagnostics"); Directory.CreateDirectory(folder);
        string finished = SafePaths.Below(folder, "private.zip");
        if (File.Exists(finished))
        {
            ValidateSnapshot(finished);
            var state = GetState(attempt);
            if (state.SnapshotReady && state.SnapshotSha256 != Hashing.File(finished)) throw new IOException("Diagnostic snapshot changed after capture.");
            return;
        }
        string partial = SafePaths.Below(folder, "snapshot-" + Guid.NewGuid().ToString("N") + ".partial");
        var items = new List<Item>(); long total = 0;
        try
        {
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                AddText(zip, "PRIVACY.txt", "PRIVATE DIAGNOSTICS: files can reveal paths, character/mod names and settings. Share privately. These files are not anonymized. No automatic upload.\n");
                foreach (var c in Candidates(attempt).DistinctBy(c => c.Entry).Take(MaximumFiles + 1))
                {
                    if (items.Count >= MaximumFiles) { items.Add(new("additional-files", "additional-files", "omitted-count-limit", 0, 0)); break; }
                    long length = 0, included = 0; string disposition;
                    try
                    {
                        string path = SafePaths.Below(attempt, c.Source);
                        if (!File.Exists(path)) disposition = "missing";
                        else
                        {
                            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            length = input.Length;
                            bool log = c.Entry.EndsWith(".log", StringComparison.Ordinal) || c.Entry.EndsWith("Log_BS.txt", StringComparison.Ordinal);
                            long limit = log ? 256 * 1024 : FileLimit;
                            if (!log && length > limit) disposition = "omitted-file-limit";
                            else if (total + Math.Min(length, limit) > TotalLimit) disposition = "omitted-total-limit";
                            else
                            {
                                included = Math.Min(length, limit);
                                input.Position = length - included;
                                var entry = zip.CreateEntry(c.Entry, CompressionLevel.Optimal);
                                using var output = entry.Open(); Copy(input, output, included);
                                total += included;
                                disposition = length == 0 ? "empty" : included < length ? "tail-only" : "complete";
                            }
                        }
                    }
                    catch (Exception e) when (Recoverable(e)) { disposition = "unreadable-or-linked"; }
                    items.Add(new(c.Entry, c.Source, disposition, length, included));
                }
                AddText(zip, "collection.json", JsonSerializer.Serialize(new { mode = "private", maximumFiles = MaximumFiles, perFileBytes = FileLimit, logTailBytes = 256 * 1024, totalBytes = TotalLimit, selectedAttemptOnly = true, items }, Wire.Options));
            }
            ValidateSnapshot(partial); File.Move(partial, finished);
        }
        catch { if (File.Exists(partial)) File.Delete(partial); throw; }
    }
    public static void ValidateSnapshot(string path)
    {
        SafePaths.NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > TotalLimit + 2 * 1024 * 1024) throw new IOException("Diagnostic snapshot exceeds its bound.");
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaximumFiles + 2 || zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count || zip.Entries.Sum(x => x.Length) > TotalLimit + 2 * 1024 * 1024 || zip.Entries.Any(x => x.Length > FileLimit || x.FullName.Split('/').Any(n => n is "." or ".." || n.Contains(':')))) throw new IOException("Diagnostic snapshot invalid.");
        foreach (var entry in zip.Entries) { using var input = entry.Open(); Copy(input, Stream.Null, entry.Length); if (input.ReadByte() != -1) throw new IOException("Diagnostic entry length disagrees."); }
    }
    public static byte[] ReadBytes(string path, int limit)
    {
        SafePaths.NoLinks(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new IOException("Diagnostic data exceeds its bound.");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static bool Reclaim(string attempt)
    {
        // Enumerate before deleting: a link anywhere cancels the entire sweep.
        var files = Tree(attempt, 50000);
        bool complete = true;
        foreach (string file in files.Where(x => !Same(Path.GetDirectoryName(x)!, attempt) && !x.StartsWith(SafePaths.Below(attempt, "diagnostics") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            try { SafePaths.NoLinks(file); File.Delete(file); } catch (Exception e) when (Recoverable(e)) { complete = false; }
        foreach (string file in files.Where(x => Same(Path.GetDirectoryName(x)!, attempt) && Path.GetFileName(x) is not ("storage.lock" or "storage-owner.json" or "storage-state.json" or "owned-output.json")))
            try { SafePaths.NoLinks(file); File.Delete(file); } catch (Exception e) when (Recoverable(e)) { complete = false; }
        // Empty directories only; never recursive deletion that could follow links.
        foreach (string dir in Directories(attempt).OrderByDescending(x => x.Length))
            if (!Same(dir, SafePaths.Below(attempt, "diagnostics"))) try { Directory.Delete(dir, false); } catch (Exception e) when (Recoverable(e)) { complete = false; }
        return complete;
    }
    private static List<string> Tree(string root, int bound)
    {
        var files = new List<string>(); var queue = new Queue<string>(); queue.Enqueue(root);
        int visited = 0;
        while (queue.TryDequeue(out string? dir))
        {
            SafePaths.NoLinks(dir);
            foreach (string p in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++visited > bound) throw new IOException("Working storage enumeration bound reached.");
                SafePaths.NoLinks(p);
                if (Directory.Exists(p)) queue.Enqueue(p); else files.Add(p);
            }
        }
        return files;
    }
    private static IEnumerable<string> Directories(string root)
    {
        var dirs = new List<string>(); var queue = new Queue<string>(); queue.Enqueue(root);
        while (queue.TryDequeue(out string? dir)) foreach (string p in Directory.EnumerateDirectories(dir))
        { SafePaths.NoLinks(p); if (dirs.Count >= 50000) throw new IOException("Working storage enumeration bound reached."); dirs.Add(p); queue.Enqueue(p); }
        return dirs;
    }
    private static void RemoveRequest(Owner owner)
    {
        string requests = SafePaths.Below(owner.Root, "requests");
        if (!Same(Path.GetDirectoryName(owner.Request)!, requests) || !Regex.IsMatch(Path.GetFileName(owner.Request), "^request-[a-f0-9]{32}\\.json$", RegexOptions.CultureInvariant)) return;
        foreach (string p in new[] { owner.Request, owner.Request + ".inputs.json" }) { SafePaths.NoLinks(p); if (File.Exists(p)) File.Delete(p); }
    }
    public static string[] Recent(string root)
    {
        SafePaths.NoLinks(root); if (!Directory.Exists(root)) return [];
        var found = new List<(string Path, DateTime Date, bool Pending)>();
        foreach (string dir in Directory.EnumerateDirectories(root).Take(4096))
            try { RequireOwner(dir); var state = Read<State>(SafePaths.Below(dir, "storage-state.json")); if (state.SnapshotReady) found.Add((dir, state.UpdatedUtc, state.PendingDelivery)); } catch (Exception e) when (Recoverable(e)) { }
        return found.Where(x => x.Pending).OrderByDescending(x => x.Date).Concat(found.Where(x => !x.Pending).OrderByDescending(x => x.Date).Take(HistoryCount)).Select(x => x.Path).ToArray();
    }
    public static void Sweep(string root, CancellationToken token = default)
    {
        // Only positively marked direct children. Unknown/legacy directories stay.
        try
        {
            SafePaths.NoLinks(root); if (!Directory.Exists(root)) return;
            var candidates = new List<(string Path, State State, long Bytes)>();
            foreach (string dir in Directory.EnumerateDirectories(root).Take(4096))
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var owner = RequireOwner(dir);
                    var state = Read<State>(SafePaths.Below(dir, "storage-state.json"));
                    if (state.Status == "active" && !Inactive(owner.Pid, owner.Start)) continue;
                    using var gate = Exclusive(dir);
                    state = GetState(dir);
                    if (!ChildrenStopped(dir)) continue;
                    if (RetainPackage(dir, state))
                    {
                        Snapshot(dir);
                        WriteState(dir, new("delivery-unconfirmed", DateTime.UtcNow, true, true, Hashing.File(SafePaths.Below(dir, "diagnostics/private.zip"))));
                        continue;
                    }
                    if (!state.SnapshotReady) { Snapshot(dir); state = new("interrupted", DateTime.UtcNow, true, false, Hashing.File(SafePaths.Below(dir, "diagnostics/private.zip"))); WriteState(dir, state); }
                    else if (state.PendingDelivery) { state = state with { Status = "success", PendingDelivery = false }; WriteState(dir, state); }
                    Reclaim(dir); RemoveRequest(owner);
                    long bytes = new FileInfo(SafePaths.Below(dir, "diagnostics/private.zip")).Length;
                    candidates.Add((dir, state, bytes));
                }
                catch (Exception e) when (Recoverable(e)) { }
            }
            long total = 0; int count = 0;
            foreach (var c in candidates.OrderByDescending(x => x.State.UpdatedUtc))
            {
                if (token.IsCancellationRequested) return;
                total += c.Bytes; count++;
                if (count <= HistoryCount && total <= HistoryBytes && DateTime.UtcNow - c.State.UpdatedUtc < TimeSpan.FromDays(HistoryDays)) continue;
                try
                {
                    using (var gate = Exclusive(c.Path))
                    {
                        var owner = RequireOwner(c.Path); var latest = Read<State>(SafePaths.Below(c.Path, "storage-state.json"));
                        if (latest.Status == "active" && !Inactive(owner.Pid, owner.Start) || RetainPackage(c.Path, latest) || !ChildrenStopped(c.Path)) continue;
                        // Locked/exported snapshots cause safe partial retention.
                        foreach (string p in Tree(c.Path, 50000).Where(p => Path.GetFileName(p) is not ("storage.lock" or "storage-owner.json" or "storage-state.json" or "owned-output.json"))) File.Delete(p);
                        foreach (string d in Directories(c.Path).OrderByDescending(x => x.Length)) Directory.Delete(d, false);
                        foreach (string name in new[] { "storage-state.json", "owned-output.json", "storage-owner.json" }) File.Delete(SafePaths.Below(c.Path, name));
                    }
                    File.Delete(Path.Combine(c.Path, "storage.lock")); Directory.Delete(c.Path, false);
                }
                catch (Exception e) when (Recoverable(e)) { }
            }
        }
        catch (Exception e) when (Recoverable(e)) { }
    }
    public static State GetState(string attempt) => Read<State>(SafePaths.Below(attempt, "storage-state.json"));
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(ReadBytes(path, 16384), Wire.Options) ?? throw new IOException("Working storage marker invalid.");
    private static void WriteNew(string path, object value) { SafePaths.NoLinks(path); using var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); JsonSerializer.Serialize(s, value, Wire.Options); }
    private static void WriteState(string path, State state)
    {
        WriteAtomic(path, "storage-state.json", state);
    }
    private static void WriteAtomic(string path, string name, object value)
    {
        string pending = SafePaths.Below(path, "state-" + Guid.NewGuid().ToString("N") + ".partial");
        SafePaths.NoLinks(pending);
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value, Wire.Options); stream.Flush(true); }
        File.Move(pending, SafePaths.Below(path, name), true);
    }
    public static bool Same(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Recoverable(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException;
    public static void AddText(ZipArchive zip, string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
    public static void Copy(Stream input, Stream output, long bytes)
    {
        byte[] buffer = new byte[65536];
        while (bytes > 0) { int n = input.Read(buffer, 0, (int)Math.Min(bytes, buffer.Length)); if (n == 0) throw new EndOfStreamException(); output.Write(buffer, 0, n); bytes -= n; }
    }
}
