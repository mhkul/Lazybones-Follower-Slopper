using System.Globalization;

namespace AutoFollower;

public static class Decisions
{
    // This adapter accepts a runtime numeric form AND a raw exporter string. Neither alone
    // proves the embedded light index is local. Tables must be from that loaded runtime.
    public static CanonicalForm Resolve(string raw, uint runtime, PluginTable table)
    {
        var light = (runtime >> 24) == 0xFE;
        var index = light ? (int)((runtime >> 12) & 0xFFF) : (int)(runtime >> 24);
        var local = runtime & (light ? 0xFFFu : 0xFFFFFFu);
        var matches = table.Entries.Where(p => p.Kind == (light ? "light" : "full") && p.Index == index).ToList();
        var parts = raw.Split('|');
        if (table.Status != Status.Observed || !table.Context.StartsWith("runtime/", StringComparison.Ordinal) || matches.Count != 1 ||
            parts.Length != 2 || !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var suffix) ||
            !string.Equals(matches[0].Name, parts[0], StringComparison.OrdinalIgnoreCase) ||
            table.Entries.Count(p => string.Equals(p.Name, parts[0], StringComparison.OrdinalIgnoreCase)) != 1 ||
            (suffix != local && (!light || suffix != (runtime & 0xFFFFFF))))
            return new(raw, runtime, null, null, Status.Unknown, "Insufficient, conflicting or ambiguous runtime table evidence");
        return new(raw, runtime, matches[0].Name, local, Status.Observed, table.EvidencePath);
    }
    public static List<string> LaunchFailures(LaunchPreconditions p)
    {
        List<string> errors = [];
        if (!p.MarkerVerified || p.MarkerName != "portable.txt") errors.Add("Verified portable.txt marker required before process creation");
        if (!p.ConfigVerified || string.IsNullOrWhiteSpace(p.ExpectedInstance) || p.ExpectedInstance != p.ObservedInstance) errors.Add("Intended instance configuration must match");
        if (!p.ProfileVerified || !p.PathsVerified || !p.PrivateWritableDestinations) errors.Add("Verified profile and private destinations required");
        if (!p.ExternalBackupsVerified || !p.SharedConfigSnapshotsVerified || !p.SharedFallbackProfilesSnapshotsVerified || p.Snapshots.Count == 0) errors.Add("Recoverable external/shared configuration and fallback-profile snapshots required");
        if (p.Recovery != Status.Passed || p.KnownWriteCoverage != Status.Passed || p.UnknownWriters.Count != 0) errors.Add("Recovery and scoped writer coverage must pass");
        return errors;
    }
    public static ValidationReport Assess(EnvironmentSnapshot environment, CharacterCapture capture, VariantManifest variants)
    {
        // An offline import can NEVER certify a generated product. Other stages own those checks.
        return new()
        {
            SchemaVersion = 1, FollowerId = capture.FollowerId, AttemptId = capture.AttemptId,
            Evidence = capture.Evidence, FullCapture = Status.Partial,
            SourcePreservation = environment.PreservationExceptions.Any(e => e.Recovery != Status.Passed) ? Status.Failed : Status.Unknown,
            CompleteFollower = Status.Unavailable,
            Checks = [.. capture.Coverage,
                new("future-launch", LaunchFailures(environment.FutureLaunch).Count == 0 ? Status.Passed : Status.Unavailable,
                    "Contract-only gate; not authorization or evidence of an actual launch", []),
                new("variants", Status.Unavailable, string.Join(", ", variants.Variants.Select(v => v.Name)) + "; no meshes/plugin/migration produced", [])]
        };
    }
}
