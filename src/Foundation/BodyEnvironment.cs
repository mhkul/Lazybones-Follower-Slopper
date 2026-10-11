using System.Text.Json;
namespace AutoFollower;

// Available means an effective loader input exists, not that its API works.
public record BodyEnvironment(string Detection, OBodyState State, bool RequiresAdapter,
    string Basis, List<EvidenceRef> Evidence, List<string> EffectiveRoots, List<EvidenceRef>? InactivityEvidence = null);

public static class BodyEnvironmentPolicy
{
    public static BodyEnvironment ConfirmInactive(BodyEnvironment detected,List<EvidenceRef> evidence)
    {
        CaptureRawEvidence.Require(detected.Detection=="available"&&evidence.Count>0,"Explicit inactivity requires an installed selected OBody and confirmation");
        var route=detected with {Detection="inactive",State=OBodyState.Inactive,RequiresAdapter=false,InactivityEvidence=evidence,Basis="Explicit pinned selected-environment inactivity; presence alone never proves inactivity"};
        RequiresAdapter(JsonSerializer.SerializeToElement(new {bodyEnvironment=route},Wire.Options));return route;
    }
    public static BodyBuildSource ResolveSource(BodyEnvironment environment, Func<BodyBuildSource> resolvePreset)
        => environment.RequiresAdapter ? resolvePreset() : new("extracted-as-is/v1", Status.Observed, null, null, null, null, [], false, false, environment.Basis);
    public static BodyEnvironment Detect(IEnumerable<ProviderRoot> roots)
    {
        var selected = roots.OrderBy(r => r.Priority).ToList();
        if (selected.Count == 0) throw new InvalidDataException("Missing selected provider context");
        foreach (var root in selected)
        {
            SafePaths.NoLinks(root.Path);
            if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException(root.Path);
            string path = Path.Combine(root.Path, "SKSE", "Plugins", "OBody.dll");
            if (!File.Exists(path)) continue;
            SafePaths.NoLinks(path);
            if (new FileInfo(path).Length == 0) throw new InvalidDataException("Empty installed OBody adapter: " + path);
            return new("available", OBodyState.Unknown, true, "Effective loose SKSE loader input " + root.Identity + "; runtime API/readiness still required",
                [new(Path.GetFullPath(path), Hashing.File(path), new FileInfo(path).Length, "effective OBody loader input")], selected.Select(r => r.Path).ToList());
        }
        return new("absent", OBodyState.Absent, false, "OBody.dll absent from every enabled MO2 provider, Overwrite and physical Data; disabled/unrelated providers excluded",
            [], selected.Select(r => r.Path).ToList());
    }
    public static bool RequiresAdapter(JsonElement stage)
    {
        // Frozen pre-D44 evidence remains the active-adapter route.
        if (!stage.TryGetProperty("bodyEnvironment", out var route)) return true;
        string detection = route.GetProperty("detection").GetString()!;
        bool required = route.GetProperty("requiresAdapter").GetBoolean();
        if (detection is not ("available" or "absent" or "inactive") || required != (detection == "available"))
            throw new InvalidDataException("Unknown/inconsistent body environment route");
        if (detection == "inactive")
        {
            if (!route.TryGetProperty("inactivityEvidence", out var proof) || proof.ValueKind != JsonValueKind.Array || proof.GetArrayLength() == 0)
                throw new InvalidDataException("Inactive OBody requires explicit confirming evidence");
            foreach (var item in proof.EnumerateArray())
            {
                var pin = item.Deserialize<EvidenceRef>(Wire.Options) ?? throw new InvalidDataException("Missing inactivity pin");
                SafePaths.NoLinks(pin.Path);
                if (pin.Role != "confirmed selected-environment OBody inactivity" || !File.Exists(pin.Path) || new FileInfo(pin.Path).Length != pin.Bytes || Hashing.File(pin.Path) != pin.Sha256)
                    throw new InvalidDataException("Invalid confirming inactivity evidence");
            }
        }
        return required;
    }
    public static void ValidateNative(JsonElement stage, string native)
    {
        if (RequiresAdapter(stage))
            CaptureRawEvidence.Require(native.Contains("OBodyIsReady callback apiVersion=1", StringComparison.Ordinal), "Installed OBody API/readiness unavailable; absence is not established");
        else
            CaptureRawEvidence.Require(native.Contains("bodyRoute=as-is adapterRequired=0", StringComparison.Ordinal) && !native.Contains("OBodyIsReady callback", StringComparison.Ordinal), "As-is native route missing or active OBody contradicts route");
    }
}
