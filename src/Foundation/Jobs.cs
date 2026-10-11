using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoFollower;

public static class Hashing
{
    public static string File(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static string Inputs(IEnumerable<EvidenceRef> evidence) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', evidence.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => $"{e.Path}|{e.Sha256}|{e.Bytes}")))));
}
public static class SafePaths
{
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((System.IO.File.Exists(current) || Directory.Exists(current)) && (System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse point rejected: " + current);
    }
    public static string Below(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(s => s is "." or ".." || s.Contains(':')))
            throw new IOException("Relative non-traversing path required");
        var absolute = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes owned root");
        NoLinks(absolute);
        return absolute;
    }
}
public sealed class OwnedJob
{
    public const string Owner = "AutoFollower/T03/offline-v1";
    public string DirectoryPath { get; }
    public JobEnvelope Envelope { get; private set; }
    OwnedJob(string path, JobEnvelope envelope) { DirectoryPath = path; Envelope = envelope; }
    public static OwnedJob Create(string projectRoot, Guid follower, List<EvidenceRef> evidence, string? retryPath = null, string task = "T03", bool liveCapture = false)
    {
        if (follower == Guid.Empty) throw new ArgumentException("Stable follower identity required");
        if (task is not ("T03" or "T04" or "T05" or "T09")) throw new ArgumentException("Unsupported task owner");
        string owner = task == "T03" ? Owner : task == "T04" ? "AutoFollower/T04/resolution-v1" : "AutoFollower/T05/capture-assessment-v1";
        if (liveCapture) { if (task != "T05") throw new ArgumentException("Live owner requires T05"); owner = "AutoFollower/T05/live-capture-v1"; }
        var root = SafePaths.Below(projectRoot, "runs/" + task);
        SafePaths.NoLinks(root);
        Directory.CreateDirectory(root);
        Guid? retry = null;
        var fingerprint = Hashing.Inputs(evidence);
        if (retryPath != null)
        {
            var priorPath = Path.GetFullPath(retryPath);
            if (!string.Equals(Path.GetDirectoryName(priorPath), root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Retry must be a direct owned T03 job");
            SafePaths.NoLinks(priorPath);
            var prior = Wire.Read<JobEnvelope>(System.IO.File.ReadAllText(SafePaths.Below(priorPath, "job.json")));
            if (prior.Owner != owner || prior.Directory != priorPath || Path.GetFileName(priorPath) != "job-" + prior.AttemptId.ToString("N")) throw new IOException("Retry ownership mismatch");
            if (prior.FollowerId != follower || prior.InputFingerprint != fingerprint) throw new InvalidDataException("Retry identity or input fingerprints changed; start a new import");
            retry = prior.AttemptId;
        }
        var attempt = Guid.NewGuid();
        var path = SafePaths.Below(root, "job-" + attempt.ToString("N"));
        if (Directory.Exists(path)) throw new IOException("Attempt already exists");
        Directory.CreateDirectory(path);
        var envelope = new JobEnvelope { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt,
            Evidence = evidence, Owner = owner, Directory = path, RetryOf = retry, Status = Status.Unknown, InputFingerprint = fingerprint };
        var job = new OwnedJob(path, envelope);
        job.SaveEnvelope(); job.Journal("created", Status.Observed, "Unique attempt; no cache reuse or cleanup");
        return job;
    }
    void CheckOwnership()
    {
        SafePaths.NoLinks(DirectoryPath);
        var persisted = Wire.Read<JobEnvelope>(System.IO.File.ReadAllText(SafePaths.Below(DirectoryPath, "job.json")));
        if (persisted.Owner != Envelope.Owner || persisted.AttemptId != Envelope.AttemptId || persisted.Directory != DirectoryPath)
            throw new IOException("Owned job marker changed");
    }
    void SaveEnvelope()
    {
        var temporary = SafePaths.Below(DirectoryPath, "job.json.pending");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) writer.Write(Wire.Write(Envelope));
        System.IO.File.Move(temporary, SafePaths.Below(DirectoryPath, "job.json"), overwrite: true);
    }
    public void Journal(string stage, Status status, string message)
    {
        CheckOwnership();
        System.IO.File.AppendAllText(SafePaths.Below(DirectoryPath, "journal.jsonl"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, stage, status = status.ToString(), message, attemptId = Envelope.AttemptId }) + "\n");
    }
    public void Write<T>(string filename, T contract)
    {
        CheckOwnership();
        var destination = SafePaths.Below(DirectoryPath, filename);
        var temporary = SafePaths.Below(DirectoryPath, filename + ".pending");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) writer.Write(Wire.Write(contract));
        System.IO.File.Move(temporary, destination, overwrite: false);
    }
    public Status Run(Func<Guid, CancellationToken, Bundle> import, CancellationToken token, bool simulateFailure = false)
    {
        try
        {
            Journal("evidence-import", Status.Observed, "Started");
            token.ThrowIfCancellationRequested();
            var bundle = import(Envelope.AttemptId, token);
            Contract[] contracts = [bundle.Environment, bundle.Capture, bundle.Assets, bundle.Records, bundle.Variants, bundle.Validation];
            if (contracts.Any(c => c.SchemaVersion != 1 || c.FollowerId != Envelope.FollowerId || c.AttemptId != Envelope.AttemptId || Hashing.Inputs(c.Evidence) != Envelope.InputFingerprint))
                throw new InvalidDataException("Imported contract identity/version/evidence does not match job");
            if (simulateFailure) throw new InvalidOperationException("Deliberate offline stage failure");
            token.ThrowIfCancellationRequested();
            Write("environment.json", bundle.Environment);
            Write("capture.json", bundle.Capture);
            Write("assets.json", bundle.Assets);
            Write("records.json", bundle.Records);
            Write("variants.json", bundle.Variants);
            Write("validation.json", bundle.Validation);
            foreach (var stage in new[] { "save-resolution-production", "live-capture", "geometry-transform", "plugin-generation", "packaging" })
                Journal(stage, Status.Unavailable, "Not implemented in T03");
            Finish(Status.Passed, "Offline import completed; complete follower unavailable");
        }
        catch (OperationCanceledException) { Finish(Status.Cancelled, "Cancelled; retained owned artifacts; originals never deleted"); }
        catch (Exception ex) { Finish(Status.Failed, ex.GetType().Name + ": " + ex.Message); }
        return Envelope.Status;
    }
    public Status Resolve(Action<CancellationToken> operation, CancellationToken token)
    {
        try { Journal("selected-input-resolution", Status.Observed, "Started; no game launcher"); token.ThrowIfCancellationRequested(); operation(token); token.ThrowIfCancellationRequested(); Finish(Status.Passed, "Input resolution completed; capture/follower unavailable"); }
        catch (OperationCanceledException) { Finish(Status.Cancelled, "Cancelled; partial owned evidence retained"); }
        catch (Exception ex) { Finish(Status.Failed, ex.GetType().Name + ": " + ex.Message); }
        return Envelope.Status;
    }
    public Status AssessCapture(Action<CancellationToken> operation, CancellationToken token)
    {
        try { Journal("retained-capture-assessment", Status.Observed, "Started; no game launch"); token.ThrowIfCancellationRequested(); operation(token); token.ThrowIfCancellationRequested(); Finish(Status.Partial, "Validated partial capture imported; required appearance/provider coverage remains open"); }
        catch (OperationCanceledException) { Finish(Status.Cancelled, "Cancelled; retained owned assessment outputs"); }
        catch (Exception ex) { Finish(Status.Failed, ex.GetType().Name + ": " + ex.Message); }
        return Envelope.Status;
    }
    public Status Capture(Action<CancellationToken> operation, CancellationToken token)
    {
        try { Journal("selected-input-live-capture", Status.Observed, "Started; protected pinned prototype"); token.ThrowIfCancellationRequested(); operation(token); token.ThrowIfCancellationRequested(); Finish(Status.Partial, "Live capture lifecycle completed; inspect six contracts and unsupported mechanisms"); }
        catch (OperationCanceledException) { Finish(Status.Cancelled, "Owned capture cancelled and drained; retained partial evidence"); }
        catch (Exception ex) { Finish(Status.Failed, ex.GetType().Name + ": " + ex.Message); }
        return Envelope.Status;
    }
    void Finish(Status status, string message)
    {
        Journal("terminal", status, message);
        Envelope = Envelope with { Status = status };
        SaveEnvelope();
    }
    public static List<EvidenceRef> ReadSelection(string projectRoot)
    {
        var path = SafePaths.Below(projectRoot, "tests/fixtures/t02a-selection.json");
        var json = JsonDocument.Parse(System.IO.File.ReadAllText(path)).RootElement;
        if (json.GetProperty("adapter").GetString() != "t02a-reviewed-v1") throw new InvalidDataException("Unsupported evidence adapter");
        return JsonSerializer.Deserialize<List<EvidenceRef>>(json.GetProperty("evidence"), Wire.Options) ?? throw new InvalidDataException("Missing evidence selection");
    }
}
