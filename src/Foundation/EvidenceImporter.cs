using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace AutoFollower;

public sealed class EvidenceImporter
{
    readonly string root;
    readonly Dictionary<string, EvidenceRef> refs;
    public List<EvidenceRef> Evidence => refs.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    public EvidenceImporter(string projectRoot, List<EvidenceRef> evidence)
    {
        root = Path.GetFullPath(projectRoot);
        refs = evidence.ToDictionary(e => e.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
    }
    public void Verify(CancellationToken token)
    {
        foreach (var e in Evidence)
        {
            token.ThrowIfCancellationRequested();
            var path = SafePaths.Below(root, e.Path);
            if (new FileInfo(path).Length != e.Bytes || Hashing.File(path) != e.Sha256.ToUpperInvariant())
                throw new InvalidDataException("Evidence fingerprint changed: " + e.Path);
        }
    }
    EvidenceRef Ref(string path) => refs[path];
    string Text(string path)
    {
        var reference = Ref(path);
        var bytes = File.ReadAllBytes(SafePaths.Below(root, reference.Path));
        if (bytes.LongLength != reference.Bytes || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != reference.Sha256)
            throw new InvalidDataException("Evidence changed during import: " + reference.Path);
        return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }
    JsonElement Json(string path) => JsonDocument.Parse(Text(path)).RootElement.Clone();
    static double[] Vector(JsonElement element) => element.EnumerateArray().Select(x => x.GetDouble()).ToArray();
    static List<KeyedMorph> SavedMorphs(JsonElement data) => data.GetProperty("morphs").EnumerateArray()
        .SelectMany(m => m.GetProperty("keys").EnumerateObject().Select(k => new KeyedMorph(m.GetProperty("name").GetString()!, k.Name, k.Value.GetDouble()))).ToList();
    static List<KeyedMorph> LiveMorphs(JsonElement data) => data.GetProperty("bodyMorphs").EnumerateArray()
        .SelectMany(m => m.GetProperty("keys").EnumerateArray().Select(k => new KeyedMorph(m.GetProperty("name").GetString()!, k.GetProperty("key").GetString()!, k.GetProperty("value").GetDouble()))).ToList();
    public Bundle Import(Guid follower, Guid attempt, CancellationToken token)
    {
        Verify(token);
        const string t02 = "reports/T02-evidence/";
        const string t02a = "reports/T02A-evidence/";
        const string output = t02a + "isolation/overwrite/SKSE/Plugins/";
        var manifest = Json("reports/T02A-input-manifest.json");
        var comparison = Json(t02a + "capture-comparison.json");
        var saved = Json(t02 + "player-saved-morphs.json");
        var placement = Json(t02 + "saved-placement.json");
        var compatibility = Json(t02 + "save-compatibility.json");
        var savedEntries = Text(t02 + "save-reader/save-plugins.tsv").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t')).Select(p => new PluginEntry(p[2], p[0], int.Parse(p[1], CultureInfo.InvariantCulture))).ToList();
        var currentEntries = savedEntries.Select(p =>
        {
            var remaps = compatibility.GetProperty("remappings").EnumerateArray().Where(r => string.Equals(r.GetProperty("plugin").GetString(), p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            return remaps.Count == 0 ? p : new PluginEntry(p.Name, remaps.Single().GetProperty("currentKind").GetString()!, remaps.Single().GetProperty("currentIndex").GetInt32());
        }).ToList();
        // Retain the new name without inventing a header-derived index absent from this compact adapter.
        currentEntries.AddRange(compatibility.GetProperty("addedSinceSave").EnumerateArray().Select(p => new PluginEntry(p.GetString()!, "unknown", -1)));
        List<PluginTable> runtimeTables = [];
        List<CaptureRun> runs = [];
        List<CanonicalForm> forms = [];
        foreach (var id in new[] { "a01", "a02" })
        {
            token.ThrowIfCancellationRequested();
            var samplePath = output + $"StorageUtilData/T02A_{id}_sample_0.json";
            var sample = Json(samplePath);
            var entries = sample.GetProperty("string").EnumerateObject().Where(p => p.Name.StartsWith("fullplugin/", StringComparison.Ordinal) || p.Name.StartsWith("lightplugin/", StringComparison.Ordinal))
                .Select(p => new PluginEntry(p.Value.GetString()!, p.Name.StartsWith("full", StringComparison.Ordinal) ? "full" : "light", int.Parse(p.Name.Split('/')[1], CultureInfo.InvariantCulture))).ToList();
            var table = new PluginTable("runtime/" + id, entries, Status.Observed, samplePath);
            runtimeTables.Add(table);
            var presetPath = output + $"CharGen/Exported/T02A_{id}.jslot";
            var preset = Json(presetPath);
            foreach (var part in preset.GetProperty("headParts").EnumerateArray())
                forms.Add(Decisions.Resolve(part.GetProperty("formIdentifier").GetString()!, part.GetProperty("formId").GetUInt32(), table));
            var logPath = t02a + $"native-{id}.log";
            var log = Text(logPath);
            var reviewAttempt = manifest.GetProperty("attempts").EnumerateArray().Single(a => a.GetProperty("id").GetString() == id);
            var validationPath = t02a + $"{id}-export-validation.json";
            var export = Json(validationPath);
            bool exportPresent = new[] { "head", "tint", "preset" }.All(k => export.GetProperty(k).GetProperty("sha256").GetString()!.Length == 64);
            var samples = Enumerable.Range(0, 4).Select(i => Json(output + $"StorageUtilData/T02A_{id}_sample_{i}.json")).ToList();
            var transforms = samples.Select(s =>
            {
                var f = s.GetProperty("float");
                return new Transform(placement.GetProperty("cell").GetString()!, null,
                    new[] { f.GetProperty("x").GetDouble(), f.GetProperty("y").GetDouble(), f.GetProperty("z").GetDouble() },
                    new[] { f.GetProperty("anglex").GetDouble(), f.GetProperty("angley").GetDouble(), f.GetProperty("anglez").GetDouble() },
                    "game-units", "degrees", "live actor/view; cell label from saved placement, not independently resolved here", samplePath, Status.Unknown);
            }).ToList();
            runs.Add(new(id,
                new(log.Contains("OBodyIsReady callback", StringComparison.Ordinal) ? Status.Observed : Status.Unknown, log.Contains("OBodyIsReady callback", StringComparison.Ordinal), logPath, "Readiness for two assisted runs only"),
                new(log.Contains("PostLoadGame success=1", StringComparison.Ordinal) ? Status.Observed : Status.Unknown, log.Contains("PostLoadGame success=1", StringComparison.Ordinal), logPath, "Menu filename not instrumented; copied-content association"),
                new(Status.Observed, true, "reports/T02A-input-manifest.json", reviewAttempt.GetProperty("loadMethod").GetString()!),
                new(exportPresent ? Status.Observed : Status.Unknown, exportPresent, validationPath, "Prior structural export report; no visual or full actor validation"),
                new(log.Contains("apiVersion=1", StringComparison.Ordinal) ? Status.Observed : Status.Unknown, log.Contains("apiVersion=1", StringComparison.Ordinal) ? 1 : null, logPath, "Native API v1; actor-specific ORefit unknown"),
                LiveMorphs(preset), transforms, JsonSerializer.SerializeToElement(samples)));
        }
        var originalXmlPath = t02 + "capture-inputs/preset/Sen Abersha.xml";
        var xml = XDocument.Parse(Text(originalXmlPath));
        var presetXml = xml.Descendants("Preset").Single(e => (string?)e.Attribute("name") == "SenAbersha");
        var xmlValues = presetXml.Elements("SetSlider").Select(e => new KeyedMorph((string)e.Attribute("name")!, (string)e.Attribute("size")!,
            double.Parse((string)e.Attribute("value")!, CultureInfo.InvariantCulture) / 100)).ToList();
        var incidentPath = t02a + "fallback-profile-audit.json";
        var incident = Json(incidentPath);
        var inputRefs = manifest.GetProperty("sourceInputs").EnumerateArray().Select(p => Ref(Path.GetRelativePath(root, p.GetProperty("immutable").GetString()!).Replace('\\', '/'))).ToList();
        var env = new EnvironmentSnapshot
        {
            SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = Evidence,
            Profile = manifest.GetProperty("originalProfile").GetString()!, RuntimeVersion = "1.5.97.0",
            ToolVersions = manifest.GetProperty("tools").EnumerateArray().Select(x => x.Clone()).ToList(),
            CaptureRequirements = ["Historical T02A: SKSE 2.0.20, RaceMenu/CharGen, OBody native API v1, task helper, protected copied ESS/SKSE, verified MO2 context"],
            Inputs = new(inputRefs.Single(p => p.Path.EndsWith(".ess", StringComparison.OrdinalIgnoreCase)), inputRefs.Single(p => p.Path.EndsWith(".skse", StringComparison.OrdinalIgnoreCase)),
                manifest.GetProperty("association").GetProperty("pluginIndexNameSetsMatch").GetBoolean() ? Status.Observed : Status.Unknown,
                "reports/T02A-input-manifest.json: association", "Filename 389/date label; not embedded save number or authoritative date", manifest.GetProperty("saveHeader").Clone(),
                Status.Partial, [Ref(t02 + "save-reader/player-00000014-ACHR.bin"), Ref(t02 + "cosave-structure.json")]),
            Saved = new("saved", savedEntries, Status.Observed, t02 + "save-reader/save-plugins.tsv"),
            CurrentProfile = new("current-profile", currentEntries, Status.Partial, t02 + "save-compatibility.json"), RuntimeTables = runtimeTables,
            FutureLaunch = new() { KnownWriteCoverage = Status.Partial, Recovery = Status.Unknown, UnknownWriters = ["Future adapter must verify writer surfaces and recoverable shared/fallback configuration before startup"] },
            PreservationExceptions = [new("11-14 Debug", incident.GetProperty("startupTouchedFiles").EnumerateArray().Select(p => p.GetString()!).ToList(), Status.Failed,
                "Six files unrestored; no pre-task baseline. User considers profile stale; that is not restoration. No speculative recovery.", Ref(incidentPath))],
            Extra = new() { ["rawCompatibility"] = compatibility, ["rawIsolationEvidence"] = Json(t02a + "isolation-gate.json") }
        };
        List<Coverage> coverage = [
            new("head-tint-preset", Status.Observed, "Two assisted exports; structural reports retained; seven matching shapes do not prove full actor or visual equivalence", [t02a + "capture-comparison.json"]),
            new("saved-live-keyed-morphs", Status.Observed, "191 independent key/value pairs; eight finite samples at float32 precision; JSON sample precision limited", [t02a + "capture-comparison.json"]),
            new("preset-drift", Status.Observed, "Saved/live NavelEven 1.0, current XML small/big 0.4; current compared copies agree. August 6 timestamps consistent with user account, causality unproven", [originalXmlPath, t02a + "navel-provenance.json"]),
            new("overlays", Status.Unknown, "Empty body getters do not prove absence; head tint references include overlays; complete visibility/enumeration unknown", [output + "CharGen/Exported/T02A_a01.jslot"]),
            new("node-flags", Status.Partial, "NPC Spine2: 0408000E versus 0008000E unexplained; retained without normalization", [t02a + "capture-comparison.json"]),
            new("winning-providers", Status.Unknown, "Owner candidates are not all winning overrides; loose/BSA precedence unresolved", []),
            new("IED-extras", Status.Unknown, "Attachments/visibility not enumerated", []),
            new("body-ORefit-geometry", Status.Unavailable, "No effective body/outfit geometry export or ORefit flattening", []),
            new("SMP-motion", Status.Unknown, "Backpack observed; six lantern bones/motion unresolved. Supplemental Amor hair is separate from Sen Soledad", []),
            new("unattended-capture", Status.Unavailable, "Both loads and quest activation required user assistance", ["reports/T02A-input-manifest.json"])
        ];
        var firstPreset = Json(output + "CharGen/Exported/T02A_a01.jslot");
        var firstSample = Json(output + "StorageUtilData/T02A_a01_sample_0.json");
        var capture = new CharacterCapture
        {
            SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = Evidence,
            SavedPlacement = new(placement.GetProperty("cell").GetString()!, null, Vector(placement.GetProperty("position")), Vector(placement.GetProperty("rotationRadians")),
                "game-units", "radians", "saved spawn target; partial ACHR", t02 + "saved-placement.json", Status.Partial),
            Weight = new(Status.Observed, firstPreset.GetProperty("actor").GetProperty("weight").GetDouble(), output + "CharGen/Exported/T02A_a01.jslot", "Actual exported player, not reference NPC"),
            Scale = new(Status.Observed, firstSample.GetProperty("float").GetProperty("scale").GetDouble(), output + "StorageUtilData/T02A_a01_sample_0.json", "Live observation"),
            SavedMorphs = SavedMorphs(saved), Preset = new("SenAbersha", (string)presetXml.Attribute("set")!, Ref(originalXmlPath), xmlValues, null, "No derived preset; prebuilt shape authority is actual resolved XML; captured morphs are diagnostics"),
            Runs = runs, Forms = forms, NodeDifferences = comparison.GetProperty("repeatability").GetProperty("nodeDifferences").Clone(),
            Repeatability = comparison.Clone(), Coverage = coverage
        };
        var assets = new AssetManifest { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = Evidence, Closure = Status.Unknown,
            Assets = [new("SenAbersha original resolved preset", Ref(originalXmlPath), manifest.GetProperty("assignedPreset").GetProperty("activeXML").GetProperty("source").GetString(),
                Status.Observed, "None; read-only reference", null, [], ["T06 compatible XML-driven build sources unresolved; saved/live differences are diagnostics"], Status.Unknown)] };
        var records = new RecordMap { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = Evidence, Allocation = Status.Unavailable,
            Records = forms.Select(f => new RecordEntry(f, null, null, "None; canonical identity only", ["Winning override and private output record graph unresolved"])).ToList() };
        var variants = new VariantManifest { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, PluginIdentity = $"AutoFollower_{follower:N}.esp", Evidence = Evidence,
            Variants = [
                new("prebuilt", Status.Unavailable, ["Skyrim 1.5.97; other output dependencies await closure"], false,
                    ["XML-driven body/hands/feet/outfits from compatible BodySlide sources", "Private copy of resolved original preset XML"], Status.Unavailable, "Switching used actors requires supported reset/migration; no procedure validated") { GeometrySourcePolicy = "resolved-bodyslide-xml/v1" },
                new("obody", Status.Unavailable, ["Compatible OBody runtime; follower-specific assignment adapter; remaining dependencies unresolved"], true,
                    ["Genuine zeroed baseline body/outfits", "Compatible TRI and rewritten morph references", "Private original XML; any namespacing preserves actual preset slider values", "Follower-specific assignment without random leakage"],
                    Status.Unavailable, "Runtime/saved morph reset, migration and double-morph checks required; mesh replacement alone insufficient") { GeometrySourcePolicy = "zeroed-compatible-meshes-and-actual-xml/v1" }]
        };
        Verify(token);
        return new(env, capture, assets, records, variants, Decisions.Assess(env, capture, variants));
    }
}
