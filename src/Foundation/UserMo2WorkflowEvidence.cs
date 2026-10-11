using System.Text.Json;

namespace AutoFollower;

public static class UserMo2WorkflowEvidence
{
    public static void ValidateRouting(string output)
    {
        JsonElement Read(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(output, name))).RootElement.Clone();
        var route = Read("private-route.json"); var proof = Read("private-route-probe.json");
        var preservation = Read("selected-mapping-preservation.json");
        var child = Read("restricted-child-creation.json"); var context = Read("worker-context.json");
        if (!preservation.GetProperty("sameActualUserMapping").GetBoolean()
            || preservation.GetProperty("beforeSha256").GetString() != context.GetProperty("mappingSha256").GetString()
            || preservation.GetProperty("afterSha256").GetString() != Hashing.File(Path.Combine(output, "selected-vfs-after-route.txt"))
            || !route.GetProperty("taskVfsName").GetString()!.StartsWith("t09b_", StringComparison.Ordinal)
            || route.GetProperty("userNamespaceModified").GetBoolean()
            || route.GetProperty("selectedMappingSha256").GetString() != context.GetProperty("mappingSha256").GetString()
            || route.GetProperty("mappedFiles").GetInt64() < 1
            || proof.GetProperty("pid").GetInt32() != child.GetProperty("pid").GetInt32()
            || proof.GetProperty("creationFileTime").GetInt64() != child.GetProperty("creationFileTime").GetInt64())
            throw new InvalidDataException("User-MO2 private routing identity/mapping differs");
        foreach (string key in new[] { "copiedEssAndCosaveRead", "privateIniRead", "privateSaveAndLogWrites", "privateLocalAndExportWrites", "originalProfileWriteDenied" })
            if (!proof.GetProperty(key).GetBoolean()) throw new InvalidDataException("User-MO2 private routing incomplete: " + key);
        foreach (var backing in proof.GetProperty("actualBackingPaths").EnumerateArray())
            if (!Path.GetFullPath(backing.GetString()!).StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("User-MO2 routed backing escapes owned output");
        if (proof.GetProperty("actualBackingPaths").GetArrayLength() != 7 || proof.GetProperty("skyrimLaunched").GetBoolean()
            || proof.GetProperty("skyrimSaveAPIsObserved").GetBoolean()) throw new InvalidDataException("User-MO2 route probe scope differs");
    }
    public static void ValidateStopped(string output, int workerPid, long workerCreation, bool successfulProbe)
    {
        JsonElement Read(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(output, name))).RootElement.Clone();
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException("User-MO2 lifecycle: " + message);
        }
        bool Truth(JsonElement j, string key) => j.GetProperty(key).ValueKind == JsonValueKind.True
            || j.GetProperty(key).ValueKind == JsonValueKind.Number && j.GetProperty(key).GetInt64() == 1;
        var owner = Read("worker-ownership.json");
        Require(owner.GetProperty("supervisorPid").GetInt32() == workerPid
            && owner.GetProperty("supervisorCreationFileTime").GetInt64() == workerCreation
            && !owner.GetProperty("userMo2Owned").GetBoolean(), "actual supervisor binding differs");
        Require(owner.GetProperty("configSha256").GetString() == Hashing.File(Path.Combine(output, "workflow.tsv"))
            && owner.GetProperty("sourceManifestSha256").GetString() == Hashing.File(Path.Combine(output, "source-locks.tsv")), "configuration/manifest differs");
        var active = Read("active-source-guards.json");
        var accounting = Read("owned-job-accounting.json");
        var guardian = Read("guardian-stop.json");
        var release = Read("source-guards-release.json");
        foreach (var receipt in new[] { active, accounting, guardian, release })
            foreach (string key in new[] { "supervisorPid", "supervisorCreationFileTime", "configSha256", "sourceManifestSha256", "userMo2Pid", "userMo2Owned" })
                Require(receipt.GetProperty(key).GetRawText() == owner.GetProperty(key).GetRawText(), "receipt identity differs: " + key);
        foreach (string key in new[] { "guardianPid", "guardianCreationFileTime" })
        {
            Require(guardian.GetProperty(key).GetInt64() > 0, "missing independent guardian identity");
            Require(active.GetProperty(key).GetRawText() == guardian.GetProperty(key).GetRawText()
                && release.GetProperty(key).GetRawText() == guardian.GetProperty(key).GetRawText(), "guardian identity differs");
        }
        Require(guardian.GetProperty("guardianPid").GetInt32() != workerPid, "guardian is supervisor");
        foreach (var receipt in new[] { accounting, guardian })
        {
            Require(receipt.GetProperty("ownedJobConfirmedStopped").GetBoolean(), "owned job zero unconfirmed");
            foreach (string key in new[] { "queryFailures", "terminationFailures", "waitFailures", "lastError" })
                Require(receipt.GetProperty(key).GetInt64() == 0, "owned shutdown error: " + key);
        }
        var counts = File.ReadAllLines(Path.Combine(output, "source-guard-config.txt"));
        long expected = long.Parse(counts[0]);
        Require(active.GetProperty("entries").GetInt64() == expected
            && active.GetProperty("files").GetInt64() == long.Parse(counts[1])
            && active.GetProperty("directories").GetInt64() == long.Parse(counts[2])
            && active.GetProperty("manifestSha256").GetString() == counts[3]
            && active.GetProperty("deleteChecks").GetInt64() == expected
            && active.GetProperty("acquiredBeforeChildCreation").GetBoolean(), "incomplete source acquisition");
        if (active.TryGetProperty("sourceSecurityReceiptVersion", out var securityVersion))
        {
            Require(securityVersion.GetInt32() == 1
                && active.GetProperty("sourceSecurityIdsSha256").GetString() == Hashing.File(Path.Combine(output, "source-security-ids.tsv"))
                && active.GetProperty("sourceSecurityCatalogSha256").GetString() == Hashing.File(Path.Combine(output, "source-security-catalog.tsv"))
                && File.ReadLines(Path.Combine(output, "source-security-ids.tsv")).LongCount() == expected,
                "source security recovery metadata differs");
        }
        Require(release.GetProperty("releasedHandles").GetInt64() == expected
            && Truth(release, "ownedJobConfirmedStoppedBeforeRelease"), "incomplete or premature release");
        if (!successfulProbe) return;
        var child = Read("restricted-child-creation.json");
        var probe = Read("protected-probe.json");
        var context = Read("worker-context.json");
        Require(child.GetProperty("assignedAtCreation").GetBoolean() && child.GetProperty("exactJobVerified").GetBoolean()
            && !child.GetProperty("userMo2Owned").GetBoolean()
            && child.GetProperty("pid").GetInt32() == probe.GetProperty("pid").GetInt32()
            && child.GetProperty("creationFileTime").GetInt64() == probe.GetProperty("creationFileTime").GetInt64(), "protected child identity differs");
        Require(probe.GetProperty("restrictedSidMatches").GetBoolean() && probe.GetProperty("inOwnedJob").GetBoolean()
            && probe.GetProperty("deniedOriginalWriteHandles").GetInt32() >= 4
            && probe.GetProperty("allowedPrivateSaveWrite").GetBoolean()
            && probe.GetProperty("allowedExistingAndAtomicWrites").GetBoolean()
            && !probe.GetProperty("skyrimLaunched").GetBoolean(), "protected read/write proof incomplete");
        Require(probe.GetProperty("actualBacking").GetString() == context.GetProperty("backingPath").GetString()
            && probe.GetProperty("referenceSha256").GetString() == context.GetProperty("referenceSha256").GetString(), "protected merged source differs");
    }
}
