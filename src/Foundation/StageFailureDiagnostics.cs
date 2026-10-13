using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoFollower;

// Optional failure evidence from a fixed set of files in the current owned attempt.
// This remains internal operational data; DiagnosticReport projects it before sharing.
internal static class StageFailureDiagnostics
{
    private const int Limit = 64 * 1024;
    private sealed record LogEvidence(string Kind, string State, long Bytes, bool Truncated, string Text);
    internal static string PackageError(string output)
    {
        const string fallback = "No durable stage error; see owned package-stage.log";
        try
        {
            var result = Read(Path.GetFullPath(output), "package-stage-result.json", "Package result", false);
            using var doc = JsonDocument.Parse(result.Text);
            return doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() ?? fallback : fallback;
        }
        catch (Exception) { return fallback; }
    }
    internal static void Attach(Exception error, string output, string phase, int exitCode)
    {
        try { error.Data["StageDiagnostics"] = Collect(output, phase, exitCode); }
        catch (Exception) { error.Data["StageDiagnostics"] = "stage=" + (phase == "private-head" ? "private-head" : "package") + "; exitCode=" + exitCode + "; evidence unavailable"; }
    }
    internal static string Collect(string output, string phase, int exitCode)
    {
        phase = phase == "private-head" ? "private-head" : "package";
        var evidence = new List<LogEvidence>();
        string root = Path.GetFullPath(output);
        SafePaths.NoLinks(root);
        // Do not follow paths from an error, progress log, process command or receipt.
        var marker = Read(root, "owned-output.json", "Ownership", false);
        using (var owner = JsonDocument.Parse(marker.Text))
            if (!owner.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "T09B lean export build")
                throw new IOException("Current attempt ownership not proved");
        string stage = phase; long? childExit = null;
        if (phase == "package")
        {
            var progress = Read(root, "package-stage-progress.json", "Stage progress", false);
            try
            {
                using var doc = JsonDocument.Parse(progress.Text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var row in doc.RootElement.EnumerateArray())
                        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("stage", out var name) && name.ValueKind == JsonValueKind.String && row.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out long status) && status is >= int.MinValue and <= uint.MaxValue && status != 0 && KnownStage(name.GetString() ?? ""))
                        { stage = name.GetString()!; childExit = status; }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
            if (childExit.HasValue)
            {
                evidence.Add(Read(root, stage + ".log", "Child output", true));
                if (stage.StartsWith("prebuilt-", StringComparison.Ordinal))
                    evidence.Add(Read(root, "body-stage/tool/Log_BS.txt", "BodySlide tool log (may include earlier stages)", true));
            }
            evidence.Add(Read(root, "package-stage.log", "Package output", true));
        }
        else evidence.Add(Read(root, "head-stage.log", "Private head output", true));
        PluginValidationFailure? validationFailure = null;
        if (childExit == 1 && stage.StartsWith("plugin-validation-", StringComparison.Ordinal))
        {
            try
            {
                var result = Read(root, stage + ".json", "Plugin validation", false);
                using var doc = JsonDocument.Parse(result.Text);
                if (doc.RootElement.TryGetProperty("validationFailure", out var detail) && PluginValidationFailure.TryRead(detail, out var parsed)) validationFailure = parsed;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        }
        string crash = childExit is { } child ? unchecked((uint)child) switch
        {
            0xC0000005 => "access violation", 0xC0000409 => "fast-fail termination (reason unavailable)", 0xC00000FD => "stack overflow", 0xC0000135 => "DLL not found", 0xC0000139 => "entry point not found", _ => "not classified"
        } : "child exit unavailable";
        return JsonSerializer.Serialize(new { Stage = stage, PackageExitCode = exitCode, ExitCode = childExit, Crash = crash, ValidationFailure = validationFailure, Evidence = evidence }, Wire.Options);
    }
    private static bool KnownStage(string name) => Regex.IsMatch(name, "^prebuilt-[0-9]{1,4}$|^plugin-(?:generation|validation)-(?:prebuilt|obody|as-is)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static LogEvidence Read(string root, string relative, string kind, bool tail)
    {
        try
        {
            string path = SafePaths.Below(root, relative);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length; bool truncated = length > Limit;
            if (truncated && !tail) return new(kind, "oversize", length, true, "");
            int count = (int)Math.Min(length, Limit);
            if (truncated) stream.Seek(-count, SeekOrigin.End);
            byte[] bytes = new byte[count]; stream.ReadExactly(bytes);
            return new(kind, count == 0 ? "empty" : "present", length, truncated, Encoding.UTF8.GetString(bytes));
        }
        catch (FileNotFoundException) { return new(kind, "missing", 0, false, ""); }
        catch (DirectoryNotFoundException) { return new(kind, "missing", 0, false, ""); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(kind, "unreadable", 0, false, ""); }
    }
}
