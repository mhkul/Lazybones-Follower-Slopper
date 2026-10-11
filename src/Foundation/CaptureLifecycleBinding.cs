using System.Text.Json;
namespace AutoFollower;

// Receipts corroborate a retained, bounded owned session. They are not signed
// attestation and do not prove recovery after guardian failure or power loss.
public static class CaptureLifecycleBinding
{
    public static readonly string[] Fields = ["attempt", "supervisorPid", "supervisorCreationFileTime", "guardianPid", "guardianCreationFileTime", "stageSha256", "launchPinsSha256", "sourceManifestSha256", "launcherSha256"];
    static bool Truth(JsonElement j, string key) => j.GetProperty(key).ValueKind == JsonValueKind.True || j.GetProperty(key).ValueKind == JsonValueKind.Number && j.GetProperty(key).GetInt32() == 1;
    public static void Bind(JsonElement identity, JsonElement stage, string stageHash, IEnumerable<JsonElement> receipts, int? supervisorPid = null, long? supervisorCreated = null)
    {
        CaptureRawEvidence.Require(identity.GetProperty("attempt").GetString() == stage.GetProperty("attempt").GetString() && identity.GetProperty("stageSha256").GetString() == stageHash && identity.GetProperty("sourceManifestSha256").GetString() == stage.GetProperty("guardEvidence").GetProperty("sourceManifestSha256").GetString() && identity.GetProperty("launcherSha256").GetString() == stage.GetProperty("launcherBuild").GetProperty("sha256").GetString(), "Lifecycle attempt/stage/tool/manifest disagreement");
        foreach (string key in new[] { "supervisorPid", "supervisorCreationFileTime", "guardianPid", "guardianCreationFileTime" }) CaptureRawEvidence.Require(identity.GetProperty(key).GetInt64() > 0, "Missing owned process identity: " + key);
        CaptureRawEvidence.Require(identity.GetProperty("supervisorPid").GetInt32() != identity.GetProperty("guardianPid").GetInt32(), "Guardian must be independently owned");
        if (supervisorPid != null) CaptureRawEvidence.Require(identity.GetProperty("supervisorPid").GetInt32() == supervisorPid && identity.GetProperty("supervisorCreationFileTime").GetInt64() == supervisorCreated, "Receipt does not belong to the actual launched supervisor");
        foreach (var receipt in receipts) foreach (string key in Fields) CaptureRawEvidence.Require(CaptureRawEvidence.Equal(identity.GetProperty(key), receipt.GetProperty(key)), "Lifecycle receipt identity disagreement: " + key);
    }
    public static void Stopped(JsonElement identity, JsonElement submission, JsonElement exit, JsonElement accounting, JsonElement guardian, JsonElement? release, JsonElement? early)
    {
        CaptureRawEvidence.Require(Truth(accounting, "ownedJobConfirmedStopped") && Truth(guardian, "ownedJobConfirmedStopped") && exit.GetProperty("activeOwnedProcessesBeforeJobClose").GetInt32() == 0, "Owned shutdown remains unconfirmed");
        if (submission.TryGetProperty("noChildCreated", out var noChild) && noChild.GetBoolean())
        {
            CaptureRawEvidence.Require(submission.GetProperty("rootPid").GetInt32() == 0 && exit.GetProperty("rootPid").GetInt32() == 0 && Truth(exit, "noChildCreated") && exit.GetProperty("terminalReason").GetString() == "cancelled-before-child" && (early != null && Truth(early.Value, "noChildCreated") || release != null && Truth(release.Value, "ownedJobConfirmedStoppedBeforeRelease")), "Early cancellation lacks acquisition-release evidence");
        }
        else
        {
            CaptureRawEvidence.Require(submission.GetProperty("rootPid").GetInt32() > 0 && CaptureRawEvidence.Equal(submission.GetProperty("rootPid"), exit.GetProperty("rootPid")) && submission.GetProperty("rootCreationFileTime").GetInt64() > 0 && CaptureRawEvidence.Equal(submission.GetProperty("rootCreationFileTime"), exit.GetProperty("rootCreationFileTime")) && release != null && Truth(release.Value, "ownedJobConfirmedStoppedBeforeRelease"), "Owned root/guard-release disagreement");
        }
    }
    public static void RetainStop(OwnedJob job, string sandbox, string attempt, bool confirmed, string reason)
    {
        if (!confirmed) { job.Write("shutdown-uncertain.json", new { attempt, ownedShutdownConfirmed = false, reason }); return; }
        string stop = SafePaths.Below(sandbox, "stop"), pending = stop + ".pending-" + job.Envelope.AttemptId.ToString("N"); SafePaths.NoLinks(stop);
        File.WriteAllText(pending, "Confirmed owned shutdown " + attempt + "; " + reason); File.Move(pending, stop, true);
        job.Write("owned-stop-pin.json", new { attempt, ownedShutdownConfirmed = true, sha256 = Hashing.File(stop), bytes = new FileInfo(stop).Length, reason });
    }
    public static void Finalize(OwnedJob job, string sandbox, string attempt, Func<bool> confirmed, string reason, Action action)
    {
        try { action(); } finally { RetainStop(job, sandbox, attempt, confirmed(), reason); }
    }
}
