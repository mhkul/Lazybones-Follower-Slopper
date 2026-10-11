using System.Text.Json;
using System.Text.Json.Serialization;
namespace AutoFollower;
public record CaptureResourceInput(string Path, string Consumer);
public record MinimumCaptureInputs(int SchemaVersion, string SelectionSha256, List<EvidenceRef> Evidence, List<CaptureResourceInput> Resources, List<string> MorphNames)
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? Extra { get; init; }
    [JsonIgnore] public BodyBuildSource? AppliedBuildSource => Extra!=null&&Extra.TryGetValue("appliedPreset",out var value)?value.Deserialize<BodyBuildSource>(Wire.Options):null;
    [JsonIgnore] public List<EvidenceRef>? ConfirmedInactivityEvidence => Extra!=null&&Extra.TryGetValue("obodyInactivityEvidence",out var value)?value.Deserialize<List<EvidenceRef>>(Wire.Options):null;
    public BodyBuildSource ResolveAppliedPreset(string assigned)
    {
        var body=AppliedBuildSource??throw new InvalidDataException("Missing explicit applied-mesh XML source in output plan");
        CaptureRawEvidence.Require(body.Status==Status.Observed&&body.Xml!=null&&body.PresetName==assigned&&body.Sliders.Count>0&&Evidence.Any(e=>e.Path.Equals(body.Xml.Path,StringComparison.OrdinalIgnoreCase)&&e.Sha256==body.Xml.Sha256),"Applied preset/selected XML source mismatch");
        var asset=new AssetResolution("selected-applied-xml",Status.Observed,body.Provider,[new(body.Provider??"selected-applied-xml","selected-authority",body.Xml!.Path,null,0,"Pinned reviewed selected XML authority",null,body.Xml)],"Output plan authority",body.Xml.Path);
        var parsed=EnvironmentResolver.Xml(asset,assigned);
        CaptureRawEvidence.Require(parsed.SliderSet==body.SliderSet&&parsed.Sliders.Count==body.Sliders.Count&&parsed.Sliders.OrderBy(s=>s.Name).ThenBy(s=>s.Endpoint).SequenceEqual(body.Sliders.OrderBy(s=>s.Name).ThenBy(s=>s.Endpoint)),"Applied XML values/source metadata mismatch");
        return parsed;
    }
    public static MinimumCaptureInputs Read(string path, string selection)
    {
        SafePaths.NoLinks(path);
        var plan = JsonSerializer.Deserialize<MinimumCaptureInputs>(File.ReadAllText(path), Wire.Options) ?? throw new InvalidDataException("Missing capture input plan");
        CaptureRawEvidence.Require(plan.SchemaVersion == 4 && plan.SelectionSha256 == Hashing.File(selection) && plan.Evidence.Count > 0 && plan.Resources.Count is > 0 and <= 256 && plan.Resources.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Resources.Count, "Missing/stale selected output input plan");
        // Empty selected configuration files can prove an empty override/order
        // list. Payload admission remains separate from these identity pins.
        foreach(var e in plan.Evidence) { SafePaths.NoLinks(e.Path);CaptureRawEvidence.Require(e.Bytes>=0&&File.Exists(e.Path)&&new FileInfo(e.Path).Length==e.Bytes&&Hashing.File(e.Path)==e.Sha256,"Capture input plan evidence changed: "+e.Path); }
        if(plan.ConfirmedInactivityEvidence is {} inactive)CaptureRawEvidence.Require(inactive.Count>0&&inactive.All(e=>plan.Evidence.Contains(e)),"Inactivity proof is not bound to selected output plan evidence");
        foreach (var r in plan.Resources) CaptureRawEvidence.Require(VirtualPath.Normalize(r.Path) == r.Path && !string.IsNullOrWhiteSpace(r.Consumer), "Unconsumed/invalid input reference");
        CaptureRawEvidence.Require(plan.MorphNames.Count <= 4096 && plan.MorphNames.All(n => !string.IsNullOrWhiteSpace(n) && !n.Contains('/')), "Invalid scoped applicability names");
        return plan;
    }
}
