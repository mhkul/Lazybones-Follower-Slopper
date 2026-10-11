using System.Text;
using System.Text.Json;
namespace AutoFollower;

// Version applies to new stage/admission, not the frozen CharacterCapture wire format.
public static class MinimumCaptureContract
{
    public const int Version = 4;
    public const string Capability = "minimum-capture/v4";
    public static bool IsMinimum(JsonElement stage) => stage.TryGetProperty("captureSchemaVersion", out var v) && v.GetInt32() == Version;
    public static readonly Dictionary<string, string> Consumers = new()
    {
        ["selectedInputs,profile,toolPins,runtimePluginTables"] = "reproducible input identity and canonical plugin/local IDs",
        ["raceMenuVersion,skseMajor,skseMinor,skseBeta,helper capability"] = "selected capture/export API compatibility",
        ["actor,race,sex,name,level,weight,scale,skin,head/*"] = "NPC appearance, actor identity and source record closure",
        ["worn/*,equipped/*,bipedSlot,armor,addon,model"] = "equipment inclusion, ring fallback, worn/first-person/ground calls",
        ["shapeType,propertyTypes,name,parent,textures,extras,swaps"] = "effective material dispatch/overrides, shape association, BODYTRI/physics and actual applicability",
        ["*Overlay/*,hasOverlays"] = "effective appearance texture/color/alpha inputs",
        ["assignedpreset,scoped morph/*/OBody and competing keys"] = "actual assigned XML identity and uncertain per-mesh attribution; never preset authority",
        ["head.nif,tint.dds,jslot,export provenance"] = "FaceGen conversion and structural/export validation",
        ["saved cell/worldspace origin,position,rotation,units"] = "saved placement; live camera/position is not readiness",
        ["resource backing path,member,hash,provider,private copy mapping,engine settings,selected environment"] = "definitive extraction source, activation/override context and integrity proof",
        ["load,completion,cancel,job accounting,guards,receipts"] = "protected copied load and zero-before-release lifecycle"
    };
    public static void ValidateSample(JsonElement sample, bool obody)
    {
        var i = sample.GetProperty("int"); var f = sample.GetProperty("float"); var s = sample.GetProperty("string");
        CaptureRawEvidence.Require(i.GetProperty("sample").GetInt32() == 0 && i.GetProperty("actor").GetInt32() == 20 && i.GetProperty("loaded3d").GetInt32() == 1 && i.GetProperty("race").GetInt32() != 0 && i.GetProperty("sex").GetInt32() is 0 or 1, "Minimum actor identity/readiness");
        CaptureRawEvidence.Require(f.GetProperty("weight").GetDouble() is >= 0 and <= 100 && double.IsFinite(f.GetProperty("scale").GetDouble()) && f.GetProperty("scale").GetDouble() > 0, "Minimum weight/scale");
        CaptureRawEvidence.Require(i.EnumerateObject().Any(p => p.Name.StartsWith("head/") && p.Name.EndsWith("/form") && p.Value.GetInt32() != 0), "Missing required head parts");
        CaptureRawEvidence.Require(!obody || !string.IsNullOrWhiteSpace(s.GetProperty("assignedpreset").GetString()), "Missing active assigned preset");
    }
    public static void ValidateHelpers(string manifestPath, string native, string papyrus, string root, string? launcher = null)
    {
        SafePaths.NoLinks(manifestPath); var m = CaptureRawEvidence.Json(File.ReadAllBytes(manifestPath));
        CaptureRawEvidence.Require(m.GetProperty("capability").GetString() == Capability && m.GetProperty("schemaVersion").GetInt32() == Version && m.GetProperty("controlLines").GetInt32() == 7 && m.GetProperty("privateWmiGuard").GetBoolean(), "Unsupported capture helper/config capabilities");
        var selected = new List<(string, string)> { ("native", native), ("papyrus", papyrus) };
        CaptureRawEvidence.Require(launcher != null, "Matched minimum launcher required"); selected.Add(("launcher", launcher!));
        foreach (var pair in selected)
        {
            SafePaths.NoLinks(pair.Item2); var pin = m.GetProperty(pair.Item1);
            CaptureRawEvidence.Require(Path.GetFullPath(pin.GetProperty("path").GetString()!).Equals(Path.GetFullPath(pair.Item2), StringComparison.OrdinalIgnoreCase) && Hashing.File(pair.Item2) == pin.GetProperty("sha256").GetString() && new FileInfo(pair.Item2).Length == pin.GetProperty("bytes").GetInt64() && Encoding.Latin1.GetString(File.ReadAllBytes(pair.Item2)).Contains(Capability, StringComparison.Ordinal), "Mismatched selected " + pair.Item1 + " helper");
        }
        string[] requiredSources = ["Capture.cpp", "T05Capture.psc", "RuntimeInputs.hpp", "CaptureIdentity.hpp", "ResourceSourceProbe.hpp", "WmiFailureGuard.hpp", "WmiEngineCodePin.hpp", "RestrictedLaunch.cpp", "SourceGuards.hpp", "OwnedJobSafety.hpp", "stage_capture.py", "helper_capabilities.py", "collect_attempt.py", "validate_runtime_capture.py", "canonical_runtime_forms.py", "body_environment.py", "Preflight.ps1", "tint_structure.py"];
        var sources = m.GetProperty("sources").EnumerateArray().ToArray();
        CaptureRawEvidence.Require(sources.Length == requiredSources.Length && sources.Select(p => p.GetProperty("relativePath").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(requiredSources.Select(p => "prototypes/T05/" + p)), "Incomplete/duplicate helper source pins");
        foreach (var pin in sources)
        {
            string path = SafePaths.Below(root, pin.GetProperty("relativePath").GetString()!);
            CaptureRawEvidence.Require(Hashing.File(path) == pin.GetProperty("sha256").GetString(), "Capture helper/source drift: " + path);
        }
    }
}
