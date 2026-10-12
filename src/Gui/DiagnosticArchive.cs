using System.IO.Compression;
using System.Text.Json;
using AutoFollower;

namespace AutoFollower.Gui;

internal static class DiagnosticArchive
{
    // An anonymous archive can only be created from the immutable safe projection.
    internal static void Save(string destination, DiagnosticReport report, bool includePrivate, string? attempt, Action<string> protect)
    {
        destination = Path.GetFullPath(destination); SafePaths.NoLinks(destination); protect(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Choose an unused diagnostic ZIP filename.");
        FileStream? lease = null; bool created = false;
        try
        {
            if (includePrivate && !string.IsNullOrWhiteSpace(attempt))
            {
                lease = WorkingStorage.Lease(attempt);
                if (!WorkingStorage.GetState(attempt).SnapshotReady) throw new IOException("Private diagnostics are not ready. Working files have been retained.");
            }
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, true);
                WorkingStorage.AddText(zip, "report.txt", report.Text);
                WorkingStorage.AddText(zip, "manifest.json", JsonSerializer.Serialize(new { format = "Lazybones diagnostic ZIP v1", mode = includePrivate ? "private" : "anonymous", noUpload = true, privateEvidence = includePrivate && lease != null ? "selected-attempt-snapshot" : "none", report = "report.txt" }));
                if (includePrivate)
                {
                    WorkingStorage.AddText(zip, "PRIVACY.txt", "PRIVATE DIAGNOSTICS. Files can reveal paths, character/mod names and settings. Share privately. Private files are not anonymized. No automatic upload.\n");
                    if (lease != null)
                    {
                        string snapshot = SafePaths.Below(attempt!, "diagnostics/private.zip");
                        if (Hashing.File(snapshot) != WorkingStorage.GetState(attempt!).SnapshotSha256) throw new IOException("Private snapshot changed after capture.");
                        WorkingStorage.ValidateSnapshot(snapshot);
                        using var input = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var privateZip = new ZipArchive(input, ZipArchiveMode.Read);
                        foreach (var entry in privateZip.Entries)
                        {
                            using var source = entry.Open(); using var target = zip.CreateEntry("private/" + entry.FullName, CompressionLevel.Optimal).Open();
                            WorkingStorage.Copy(source, target, entry.Length);
                        }
                    }
                }
            }
            // Full readback before reporting success. Never repair with raw data.
            using var saved = ZipFile.OpenRead(destination);
            foreach (var entry in saved.Entries) { using var input = entry.Open(); WorkingStorage.Copy(input, Stream.Null, entry.Length); if (input.ReadByte() != -1) throw new IOException("Saved diagnostic entry changed."); }
        }
        catch
        {
            if (created) try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally { lease?.Dispose(); }
    }
    internal static DiagnosticReport FromHistory(string attempt)
    {
        using var lease = WorkingStorage.Lease(attempt);
        string path = SafePaths.Below(attempt, "diagnostics/private.zip"); WorkingStorage.ValidateSnapshot(path);
        if (Hashing.File(path) != WorkingStorage.GetState(attempt).SnapshotSha256) throw new IOException("Private snapshot changed after capture.");
        using var zip = ZipFile.OpenRead(path);
        var result = zip.GetEntry("result.json");
        if (result != null && result.Length <= WorkingStorage.FileLimit)
        {
            try { using var input = result.Open(); using var doc = JsonDocument.Parse(input); return DiagnosticReport.Create("Build follower", new InvalidDataException("Saved build diagnostics"), doc.RootElement); }
            catch (JsonException) { }
        }
        return DiagnosticReport.Create("Build follower", new InvalidDataException("Saved build diagnostics unavailable"));
    }
}
