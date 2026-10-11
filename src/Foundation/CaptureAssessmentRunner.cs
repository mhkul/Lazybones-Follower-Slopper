using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
namespace AutoFollower;

// Import only completed, retained prototype evidence. Live launch orchestration
// and full geometry/provider contracts remain separate unfinished adapters.
public static class CaptureAssessmentRunner
{
    public static (JsonElement Validation, JsonElement Observation, List<EvidenceRef> Evidence) Read(string root, string attempt)
    {
        try { return ReadBound(root, attempt); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { throw new InvalidDataException("Retained capture admission failed: " + e.Message, e); }
    }
    static (JsonElement Validation, JsonElement Observation, List<EvidenceRef> Evidence) ReadBound(string root, string attempt)
    {
        if (!Regex.IsMatch(attempt, "^[a-f0-9]{32}$")) throw new InvalidDataException("Invalid retained attempt identity");
        string folder = SafePaths.Below(root, "runs/T05/sandbox-small-01/attempts/" + attempt);
        List<EvidenceRef> evidence = [];
        JsonElement Json(string name)
        {
            string path = SafePaths.Below(folder, name);
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Assessment JSON bound");
            byte[] bytes = File.ReadAllBytes(path);
            evidence.Add(new(path, BoundedBinary.Digest(bytes), bytes.Length, "T05 retained assessment input"));
            return CaptureRawEvidence.Json(bytes);
        }
        var validation = Json("runtime-validation.json"); var observation = Json("observation.json"); var stage = Json("capture-stage.json");
        bool minimum = MinimumCaptureContract.IsMinimum(stage); int count = minimum ? 1 : 6; int last = count - 1;
        if (stage.TryGetProperty("captureSchemaVersion", out var schema)) CaptureRawEvidence.Require(schema.GetInt32() == 4, "Unknown capture admission schema");
        if (validation.GetProperty("attempt").GetString() != attempt || observation.GetProperty("attempt").GetString() != attempt || stage.GetProperty("attempt").GetString() != attempt || validation.GetProperty("validation").GetString() != "structural-partial-capture" || validation.GetProperty("errors").GetArrayLength() != 0 || validation.GetProperty("sampleCount").GetInt32() != count || (!minimum && !validation.GetProperty("finalThreeActorEquipmentMorphSamplesStable").GetBoolean())) throw new InvalidDataException("Capture validation is incomplete or mismatched");
        foreach (string field in new[] { "exactPreLoadObserved", "adapterPreLoadGateAccepted", "postLoadObserved", "ownedProcessesStopped", "guardsReleasedAfterOwnedExit" })
            if (!observation.GetProperty(field).GetBoolean()) throw new InvalidDataException("Missing capture lifecycle gate: " + field);
        var refs = observation.GetProperty("evidence").EnumerateArray().ToArray();
        CaptureRawEvidence.Require(refs.Length >= 1 && refs.Length <= 2048, "Missing/bounded raw evidence bundle");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in refs)
        {
            string path = Path.GetFullPath(item.GetProperty("path").GetString()!);
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Retained evidence escapes attempt");
            SafePaths.NoLinks(path); long size = new FileInfo(path).Length;
            CaptureRawEvidence.Require(paths.Add(path) && identities.Add(item.GetProperty("role").GetString() + "|" + Path.GetFullPath(item.GetProperty("source").GetString()!)), "Duplicate evidence path or role/source identity");
            string digest = Hashing.File(path);
            if (size > 64 * 1024 * 1024 || size != item.GetProperty("bytes").GetInt64() || digest != item.GetProperty("sha256").GetString()) throw new InvalidDataException("Retained capture evidence changed");
            evidence.Add(new(path, digest, size, item.GetProperty("role").GetString()!));
        }
        JsonElement Ref(string name, string role)
        {
            var found = refs.Where(e => Path.GetFileName(e.GetProperty("source").GetString()) == name && e.GetProperty("role").GetString() == role).ToArray();
            CaptureRawEvidence.Require(found.Length == 1, "Required unique evidence role: " + role + "/" + name); return found[0];
        }
        byte[] Raw(JsonElement reference) => File.ReadAllBytes(reference.GetProperty("path").GetString()!);
        JsonElement Receipt(string name) => CaptureRawEvidence.Json(Raw(Ref(name, "owned launch receipt")));
        void Bound(JsonElement declared, JsonElement actual) { foreach (string key in new[] { "path", "source", "sha256", "bytes", "role" }) CaptureRawEvidence.Require(CaptureRawEvidence.Equal(declared.GetProperty(key), actual.GetProperty(key)), "Summary refers to swapped evidence: " + key); }
        var retainedStageRef = Ref("capture-stage.json", "owned launch receipt");
        CaptureRawEvidence.Require(CaptureRawEvidence.Equal(stage, CaptureRawEvidence.Json(Raw(retainedStageRef))), "Retained launch stage differs");
        var launch = Receipt("capture-launch-validation.json"); var submission = Receipt("restricted-submission.json"); var exit = Receipt("restricted-exit.json"); var active = Receipt("active-source-guards.json"); var released = Receipt("source-guards-release.json"); var preflight = Receipt("preflight-capture.json");
        bool Truth(JsonElement j, string name) { var v = j.GetProperty(name); return v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1; }
        CaptureRawEvidence.Require(Truth(launch, "checkedBeforeChildCreation") && launch.GetProperty("pins").GetInt32() == stage.GetProperty("pins").GetArrayLength() && launch.GetProperty("captureStageSha256").GetString() == retainedStageRef.GetProperty("sha256").GetString(), "Launch/stage pin receipt disagreement");
        var gate = Encoding.UTF8.GetString(Raw(Ref("capture-gate.txt", "owned launch receipt"))).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        CaptureRawEvidence.Require(gate.Length == 3 && gate[0] == launch.GetProperty("captureStageSha256").GetString() && gate[1] == launch.GetProperty("pinsSha256").GetString() && gate[2] == launch.GetProperty("launcherSha256").GetString(), "Gate/launch identity disagreement");
        CaptureRawEvidence.Require(Truth(preflight, "verifiedBeforeProcessCreation") && preflight.GetProperty("profile").GetString() == "T05" && preflight.GetProperty("privateRoot").GetString() == stage.GetProperty("root").GetString(), "Preflight/stage disagreement");
        CaptureRawEvidence.Require(submission.GetProperty("rootPid").GetInt32() > 0 && submission.GetProperty("rootPid").GetInt32() == exit.GetProperty("rootPid").GetInt32() && Truth(submission, "restrictedToken") && exit.GetProperty("activeOwnedProcessesBeforeJobClose").GetInt32() == 0 && exit.GetProperty("terminalReason").GetString() is "cancelled" or "completed" or "exited" && !Truth(exit, "diagnosticFailure"), "Unconfirmed owned shutdown");
        CaptureRawEvidence.Require(Truth(active, "active") && Truth(active, "acquiredBeforeChildCreation") && Truth(released, "ownedJobConfirmedStoppedBeforeRelease") && active.GetProperty("entries").GetInt64() > 0 && active.GetProperty("entries").GetInt64() == released.GetProperty("releasedHandles").GetInt64() && active.GetProperty("manifestSha256").GetString() == stage.GetProperty("guardEvidence").GetProperty("sourceManifestSha256").GetString(), "Guard lifetime/identity disagreement");
        if (stage.TryGetProperty("lifecycleVersion", out var version))
        {
            CaptureRawEvidence.Require(version.GetInt32() is 2 or 3, "Unsupported lifecycle evidence version");
            CaptureRawEvidence.Require(Truth(Receipt("owned-job-accounting.json"), "ownedJobConfirmedStopped") && Truth(Receipt("guardian-stop.json"), "ownedJobConfirmedStopped") && stage.GetProperty("launcherBuild").GetProperty("sha256").GetString() == launch.GetProperty("launcherSha256").GetString(), "New guard-owner accounting identity disagreement");
            var pinRef = Ref("capture-pins.tsv", "owned launch receipt");
            if (version.GetInt32() == 3)
            {
                var identity = Receipt("lifecycle-identity.json"); var accounting = Receipt("owned-job-accounting.json"); var guardian = Receipt("guardian-stop.json"); var children = Receipt("owned-child-exits.json");
                CaptureLifecycleBinding.Bind(identity, stage, retainedStageRef.GetProperty("sha256").GetString()!, [launch, submission, exit, active, released, accounting, guardian, children]);
                CaptureRawEvidence.Require(identity.GetProperty("launchPinsSha256").GetString() == pinRef.GetProperty("sha256").GetString(), "Lifecycle pin manifest disagreement");
                if (stage.TryGetProperty("runnerSupervisionRequired", out var hostRequired) && hostRequired.GetBoolean())
                {
                    var host = Receipt("runner-supervisor-result.json");
                    CaptureRawEvidence.Require(host.GetProperty("attempt").GetString() == attempt && host.GetProperty("supervisorPid").GetInt32() == identity.GetProperty("supervisorPid").GetInt32() && host.GetProperty("supervisorCreationFileTime").GetInt64() == identity.GetProperty("supervisorCreationFileTime").GetInt64(), "Actual runner/supervisor creation identity disagreement");
                    CaptureRawEvidence.Require(exit.GetProperty("terminalReason").GetString() != "completed" || Truth(host, "completionRequested") && host.GetProperty("exitCode").GetInt32() == 0 && !Truth(host, "cancellation"), "Actual runner completion disagrees with native receipts");
                }
                CaptureLifecycleBinding.Stopped(identity, submission, exit, accounting, guardian, released, null);
                var roots = children.GetProperty("processes").EnumerateArray().Where(p => p.GetProperty("pid").GetInt32() == submission.GetProperty("rootPid").GetInt32()).ToArray();
                CaptureRawEvidence.Require(roots.Length == 1 && CaptureRawEvidence.Equal(roots[0].GetProperty("creationFileTime"), submission.GetProperty("rootCreationFileTime")) && Truth(roots[0], "exitCodeAvailable") && Truth(roots[0], "exactOwnedJob"), "Owned root creation/exit corroboration missing");
            }
            CaptureRawEvidence.Require(pinRef.GetProperty("sha256").GetString() == launch.GetProperty("pinsSha256").GetString(), "Raw launch pin manifest changed");
            var lines = Encoding.UTF8.GetString(Raw(pinRef)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\t')).ToArray();
            CaptureRawEvidence.Require(lines.Length == stage.GetProperty("pins").GetArrayLength() && lines.All(l => l.Length == 2) && lines.Select(l => l[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == lines.Length, "Raw pin manifest cardinality");
            foreach (var pin in stage.GetProperty("pins").EnumerateArray()) CaptureRawEvidence.Require(lines.Any(l => l[0] == pin.GetProperty("path").GetString() && l[1] == pin.GetProperty("sha256").GetString()), "Stage/raw pin manifest mismatch");
            if (exit.GetProperty("terminalReason").GetString() == "completed")
            {
                var completion = Encoding.UTF8.GetString(Raw(Ref("capture-completion.txt", "owned launch receipt"))).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var manifestRef = Ref("capture-completion-exports.tsv", "owned launch receipt");
                CaptureRawEvidence.Require(completion.Length == 3 && completion[0] == attempt && completion[1] == retainedStageRef.GetProperty("sha256").GetString() && completion[2] == manifestRef.GetProperty("sha256").GetString(), "Normal completion identity disagreement");
                var outputs = Encoding.UTF8.GetString(Raw(manifestRef)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToArray();
                CaptureRawEvidence.Require(outputs.Length == count + 4 && outputs.All(l => l.Length == 2) && outputs.Select(l => l[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == count + 4, "Normal completion output cardinality");
                foreach (var item in refs.Where(e => e.GetProperty("role").GetString() == "attempt-specific runtime output")) CaptureRawEvidence.Require(outputs.Any(l => Path.GetFullPath(l[0]).Equals(Path.GetFullPath(item.GetProperty("source").GetString()!), StringComparison.OrdinalIgnoreCase) && l[1] == item.GetProperty("sha256").GetString()), "Completed output/raw evidence mismatch");
            }
        }
        string save = stage.GetProperty("saveStem").GetString()!;
        CaptureRawEvidence.Require(!string.IsNullOrEmpty(save) && Path.GetFileName(save) == save, "Invalid selected save stem");
        var inputs = stage.GetProperty("selectedInputs").EnumerateArray().ToArray();
        CaptureRawEvidence.Require(inputs.Length == 2, "ESS/co-save input pair required");
        foreach (string extension in new[] { ".ess", ".skse" })
        {
            var matches = inputs.Where(i => Path.GetFileName(i.GetProperty("loadedCopy").GetString()) == save + extension).ToArray(); CaptureRawEvidence.Require(matches.Length == 1, "Unique loaded input required"); var input = matches[0];
            string loaded = Path.GetFullPath(input.GetProperty("loadedCopy").GetString()!); string copy = Path.GetFullPath(input.GetProperty("copy").GetString()!); string sandbox = Path.GetDirectoryName(Path.GetDirectoryName(folder))!;
            foreach (string inputPath in new[] { loaded, copy })
            {
                CaptureRawEvidence.Require(inputPath.StartsWith(sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Copied input escapes sandbox"); SafePaths.NoLinks(inputPath);
                string hash = Hashing.File(inputPath); long size = new FileInfo(inputPath).Length;
                CaptureRawEvidence.Require(size > 0 && size == input.GetProperty("bytes").GetInt64() && hash == input.GetProperty("sha256").GetString() && hash == input.GetProperty("loadedCopySha256").GetString(), "Loaded ESS/co-save identity changed");
                evidence.Add(new(inputPath, hash, size, "admitted loaded-input identity"));
            }
            var pins = stage.GetProperty("pins").EnumerateArray().Where(i => i.GetProperty("path").GetString() == loaded).ToArray();
            CaptureRawEvidence.Require(pins.Length == 1 && pins[0].GetProperty("sha256").GetString() == input.GetProperty("sha256").GetString() && pins[0].GetProperty("bytes").GetInt64() == input.GetProperty("bytes").GetInt64(), "Loaded input absent/swapped in launch pins");
        }
        string native = Encoding.UTF8.GetString(Raw(Ref("native.log", "native capture handshake")));
        var nativePids = Regex.Matches(native, @"\bpid=(\d+) ").Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        var gamePids = observation.GetProperty("gamePidsFromOwnedVfsLog").EnumerateArray().Select(p => p.GetInt32()).Distinct().ToArray();
        CaptureRawEvidence.Require(nativePids.Length == 1 && gamePids.Length == 1 && nativePids[0] == gamePids[0], "Native game identity mismatch");
        var childExits = Receipt("owned-child-exits.json");
        CaptureRawEvidence.Require(childExits.GetProperty("processes").EnumerateArray().Any(p => p.GetProperty("pid").GetInt32() == nativePids[0] && p.GetProperty("imageName").GetString() == "SkyrimSE.exe" && Truth(p, "exitCodeAvailable") && Truth(p, "exactOwnedJob")), "Game absent from owned exit receipts");
        foreach (string marker in new[] { "automaticLoad request=" + save, "PreLoadGame exactFilename=" + save + ".ess expected=" + save + ".ess accepted=1", "PostLoadGame success=1" }) CaptureRawEvidence.Require(native.Contains(marker, StringComparison.Ordinal), "Missing raw load/readiness marker: " + marker);
        BodyEnvironmentPolicy.ValidateNative(stage, native);
        var samples = new List<JsonElement>();
        string Stable(JsonElement sample)
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in sample.GetProperty("int").EnumerateObject()) if (p.Name.StartsWith("worn/") || p.Name.StartsWith("equipped/") || p.Name.StartsWith("head/") || p.Name is "actor" or "race" or "sex" or "level" or "parentcell" or "worldspace") values[p.Name] = p.Value.GetRawText();
            foreach (var p in sample.GetProperty("float").EnumerateObject()) if (p.Name.StartsWith("morph/") || p.Name is "weight" or "scale") values[p.Name] = p.Value.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            values["assignedpreset"] = sample.GetProperty("string").GetProperty("assignedpreset").GetString()!; return JsonSerializer.Serialize(values);
        }
        var runtimeRefs = refs.Where(e => e.GetProperty("role").GetString() == "attempt-specific runtime output").ToArray();
        CaptureRawEvidence.Require(runtimeRefs.Length == count + 4 && validation.GetProperty("samples").GetArrayLength() == count, "Runtime evidence cardinality");
        for (int index = 0; index < count; index++)
        {
            var reference = Ref($"T05_{attempt}_sample_{index}.json", "attempt-specific runtime output"); var sample = CaptureRawEvidence.Json(Raw(reference)); var declared = validation.GetProperty("samples")[index]; Bound(declared.GetProperty("evidence"), reference);
            CaptureRawEvidence.Require(declared.GetProperty("index").GetInt32() == index && sample.GetProperty("int").GetProperty("sample").GetInt32() == index && sample.GetProperty("int").GetProperty("actor").GetInt32() == 20 && sample.GetProperty("int").GetProperty("loaded3d").GetInt32() == 1, "Raw sample identity/readiness");
            foreach (string section in new[] { "int", "float", "string" }) CaptureRawEvidence.Require(CaptureRawEvidence.Equal(sample.GetProperty(section), declared.GetProperty(section)), "Raw sample/validation disagreement");
            if (minimum) MinimumCaptureContract.ValidateSample(sample, BodyEnvironmentPolicy.RequiresAdapter(stage));
            foreach (string scalar in minimum ? new[] { "weight", "scale" } : new[] { "weight", "scale", "x", "y", "z", "anglex", "angley", "anglez", "time" }) CaptureRawEvidence.Require(double.IsFinite(sample.GetProperty("float").GetProperty(scalar).GetDouble()), "Nonfinite sample scalar");
            CaptureRawEvidence.Require(sample.GetProperty("float").GetProperty("weight").GetDouble() is >= 0 and <= 100 && sample.GetProperty("float").GetProperty("scale").GetDouble() > 0, "Actor scalar range"); samples.Add(sample);
        }
        if (!minimum) CaptureRawEvidence.Require(Stable(samples[3]) == Stable(samples[4]) && Stable(samples[4]) == Stable(samples[5]), "Raw final samples unstable");
        if (stage.TryGetProperty("runtimeInputsRequired", out var runtimeRequired) && runtimeRequired.GetBoolean())
        {
            const string role = "scene/engine result or explicit process/exception observation";
            var requestRef = Ref("runtime-input-request.tsv", role); var records = CaptureRawEvidence.Json(Raw(Ref("runtime-records.json", role))); var resources = CaptureRawEvidence.Json(Raw(Ref("engine-resource-inputs.json", role)));
            CaptureRawEvidence.Require(native.Contains("runtimeInputs completed=1") && records.GetProperty("attempt").GetString() == attempt && resources.GetProperty("attempt").GetString() == attempt && records.GetProperty("pid").GetInt32() == nativePids[0] && resources.GetProperty("pid").GetInt32() == nativePids[0] && records.GetProperty("requestSha256").GetString() == requestRef.GetProperty("sha256").GetString() && !records.GetProperty("truncated").GetBoolean() && !resources.GetProperty("truncated").GetBoolean(), "Runtime input request/attempt/process disagreement");
            var rows = Encoding.UTF8.GetString(Raw(requestRef)).Split('\n', StringSplitOptions.RemoveEmptyEntries); CaptureRawEvidence.Require(rows.Length is > 1 and <= 1025 && rows[0] == attempt && rows.Distinct(StringComparer.OrdinalIgnoreCase).Count() == rows.Length, "Runtime input request cardinality/identity");
            var requestedIds = rows.Skip(1).Where(r => r.StartsWith("F\t")).Select(r => Convert.ToUInt32(r[2..], 16)).ToHashSet();
            int fieldVersion = stage.TryGetProperty("runtimeInputFieldsVersion", out var fv) ? fv.GetInt32() : 1;
            CaptureRawEvidence.Require(fieldVersion is 1 or 2 or 3, "Unsupported runtime input field version");
            // Older frozen probes also queried numeric metadata. Keep their
            // diagnostic admission separate from the corrected future request.
            var sampleIds = fieldVersion >= 2 ? LiveCaptureRunner.RuntimeFormIds(samples[last]) : samples[last].GetProperty("int").EnumerateObject().Where(p => p.Name is "actor" or "race" or "parentcell" or "worldspace" || p.Name.StartsWith("head/") || p.Name.StartsWith("worn/") || p.Name.StartsWith("equipped/")).Select(p => unchecked((uint)p.Value.GetInt32())).Where(id => id != 0).ToHashSet();
            CaptureRawEvidence.Require(requestedIds.SetEquals(sampleIds) && rows.Skip(1).All(r => r.StartsWith("F\t") || r.StartsWith("R\t") && VirtualPath.Normalize(r[2..]) == r[2..]), "Runtime request differs from selected sample");
            var formIds = records.GetProperty("forms").EnumerateArray().Select(f => f.GetProperty("runtimeId").GetUInt32()).ToArray(); var resourcePaths = resources.GetProperty("resources").EnumerateArray().Select(r => VirtualPath.Normalize(r.GetProperty("path").GetString()!)).ToArray();
            CaptureRawEvidence.Require(formIds.Length is > 0 and <= 256 && formIds.Distinct().Count() == formIds.Length && requestedIds.All(id => formIds.Contains(id)) && resourcePaths.Length is > 0 and <= 256 && resourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == resourcePaths.Length && rows.Skip(1).Where(r => r.StartsWith("R\t")).All(r => resourcePaths.Contains(r[2..], StringComparer.OrdinalIgnoreCase)), "Runtime record/resource closure missing or duplicated");
            long streamLimit = stage.TryGetProperty("resourceStreamLimitBytes", out var sl) ? sl.GetInt64() : 64 * 1024 * 1024;
            CaptureRawEvidence.Require(streamLimit is 64 * 1024 * 1024 or 128 * 1024 * 1024, "Unsupported resource stream bound");
            foreach (var resource in resources.GetProperty("resources").EnumerateArray()) if (resource.GetProperty("valid").GetBoolean() && resource.GetProperty("bounded").GetBoolean()) CaptureRawEvidence.Require(resource.GetProperty("bytes").GetInt64() >= 0 && resource.GetProperty("bytes").GetInt64() <= streamLimit && Regex.IsMatch(resource.GetProperty("sha256").GetString()!, "^[A-F0-9]{64}$"), "Invalid bounded resource digest");
        }
        foreach (string prefix in new[] { "fullplugin", "lightplugin" })
        {
            var entries = samples[0].GetProperty("string").EnumerateObject().Where(p => p.Name.StartsWith(prefix + "/", StringComparison.Ordinal)).ToDictionary(p => int.Parse(p.Name[(prefix.Length + 1)..]), p => p.Value.GetString()!);
            var table = validation.GetProperty("runtimePluginTables").GetProperty(prefix).EnumerateArray().Select(p => p.GetString()!).ToArray();
            CaptureRawEvidence.Require(entries.Count > 0 && entries.Count == table.Length && entries.Keys.Order().SequenceEqual(Enumerable.Range(0, entries.Count)) && entries.Values.All(v => !string.IsNullOrWhiteSpace(v)) && entries.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Count && table.Select((v, i) => v == entries[i]).All(v => v), "Runtime table/raw sample disagreement");
        }
        var terminal = CaptureRawEvidence.Json(Raw(Ref($"T05_{attempt}_terminal.json", "attempt-specific runtime output"))); CaptureRawEvidence.Require(terminal.GetProperty("string").GetProperty("state").GetString() == (minimum ? "settled-exports-requested" : "samples-and-exports-requested"), "Missing raw terminal marker");
        foreach (string extension in new[] { "nif", "dds", "jslot" })
        {
            var reference = Ref($"T05_{attempt}.{extension}", "attempt-specific runtime output"); Bound(validation.GetProperty("exports").GetProperty(extension), reference); byte[] raw = Raw(reference);
            if (extension == "nif") { int shapes = CaptureRawEvidence.Head(raw); CaptureRawEvidence.Require(shapes == validation.GetProperty("headStructure").GetProperty("shapes").GetArrayLength() && validation.GetProperty("headStructure").GetProperty("sha256").GetString() == reference.GetProperty("sha256").GetString(), "Head structure identity/count mismatch"); }
            else if (extension == "dds") CaptureRawEvidence.Tint(raw);
            else { var slot = CaptureRawEvidence.Json(raw); CaptureRawEvidence.Require(slot.GetProperty("headParts").GetArrayLength() > 0 && slot.GetProperty("actor").ValueKind == JsonValueKind.Object, "Preset structure incomplete"); }
        }
        if (minimum)
        {
            CaptureRawEvidence.Require(stage.GetProperty("runtimeInputsRequired").GetBoolean() && stage.GetProperty("runtimeInputFieldsVersion").GetInt32() == 3, "Minimum source closure must be requested");
            var records = CaptureRawEvidence.Json(Raw(Ref("runtime-records.json", "scene/engine result or explicit process/exception observation")));
            var resources = CaptureRawEvidence.Json(Raw(Ref("engine-resource-inputs.json", "scene/engine result or explicit process/exception observation")));
            CaptureRawEvidence.Require(resources.GetProperty("sourceProbeRestored").GetBoolean(), "Resource source probe not restored");
            foreach (var r in resources.GetProperty("resources").EnumerateArray()) CaptureRawEvidence.Require(r.GetProperty("valid").GetBoolean() && r.GetProperty("bounded").GetBoolean(), "Missing required resource: " + r.GetProperty("path").GetString());
            foreach (var f in records.GetProperty("forms").EnumerateArray()) CaptureRawEvidence.Require(f.GetProperty("available").GetBoolean(), "Missing required record");
            foreach (int view in new[] { 0, 1 }) CaptureRawEvidence.Require(native.Contains($"scene sample=0 perspective={view}"), "Missing settled biped view");
            var helperRef = Ref("helper-capabilities.json", "owned launch receipt");
            var helper = CaptureRawEvidence.Json(Raw(helperRef));
            CaptureRawEvidence.Require(CaptureRawEvidence.Equal(helper, stage.GetProperty("helperCapabilities")) && helper.GetProperty("capability").GetString() == MinimumCaptureContract.Capability && helper.GetProperty("controlLines").GetInt32() == 7, "Selected helper/config mismatch");
            foreach (string kind in new[] { "native", "papyrus" })
            {
                string filename = kind == "native" ? "T05Capture.dll" : "T05Capture.pex";
                var pins = stage.GetProperty("pins").EnumerateArray().Where(p => Path.GetFileName(p.GetProperty("path").GetString()) == filename).ToArray();
                CaptureRawEvidence.Require(pins.Length == 1 && pins[0].GetProperty("sha256").GetString() == helper.GetProperty(kind).GetProperty("sha256").GetString(), "Staged helper capability pin mismatch");
            }
            CaptureRawEvidence.Require(helper.GetProperty("launcher").GetProperty("sha256").GetString() == stage.GetProperty("launcherBuild").GetProperty("sha256").GetString(), "Minimum launcher mismatch");
            var planRef = Ref("capture-input-plan.json", "owned launch receipt"); var plan = CaptureRawEvidence.Json(Raw(planRef));
            CaptureRawEvidence.Require(plan.GetProperty("schemaVersion").GetInt32() == 4 && plan.GetProperty("resources").GetArrayLength() > 0, "Missing output consumer/input plan");
            var resourcePaths = resources.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("path").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in plan.GetProperty("resources").EnumerateArray()) CaptureRawEvidence.Require(!string.IsNullOrWhiteSpace(r.GetProperty("consumer").GetString()) && resourcePaths.Contains(r.GetProperty("path").GetString()), "Missing required output/ground input: " + r.GetProperty("path").GetString());
            foreach (var f in records.GetProperty("forms").EnumerateArray().Where(f => f.GetProperty("formType").GetInt32() == 26))
                CaptureRawEvidence.Require(f.GetProperty("models").EnumerateArray().Any(m => m.GetProperty("context").GetString()!.StartsWith("ground-", StringComparison.Ordinal)), "Armor ground model applicability was not collected");
        }
        return (validation, observation, evidence);
    }
    public static void Write(OwnedJob job, JsonElement validation, JsonElement observation, CancellationToken token)
    {
        var admitted = Read(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(job.DirectoryPath)))!, observation.GetProperty("attempt").GetString()!);
        if (!CaptureRawEvidence.Equal(validation, admitted.Validation) || !CaptureRawEvidence.Equal(observation, admitted.Observation) || Hashing.Inputs(admitted.Evidence) != Hashing.Inputs(job.Envelope.Evidence)) throw new InvalidDataException("Assessment writer requires the exact admitted raw evidence bundle");
        foreach (var input in job.Envelope.Evidence) { token.ThrowIfCancellationRequested(); if (new FileInfo(input.Path).Length != input.Bytes || Hashing.File(input.Path) != input.Sha256) throw new InvalidDataException("Assessment evidence changed after admission"); }
        job.Write("runtime-observations.json", validation);
        job.Write("capture-lifecycle.json", observation);
        job.Write("validation.json", new ValidationReport { SchemaVersion = 1, FollowerId = job.Envelope.FollowerId, AttemptId = job.Envelope.AttemptId, Evidence = job.Envelope.Evidence,
            Assessment = "Retained unattended partial capture; XML remains prebuilt authority", FullCapture = Status.Partial, CompleteFollower = Status.Unavailable, SourcePreservation = Status.Partial,
            Checks = [new("structural-runtime-exports", Status.Observed, "Schema-selected sample count, head/tint/preset structure; no complete graph or visual validation", []), new("owned-load-and-cancellation", Status.Observed, "Exact .ess, PostLoad/OBody readiness and job-zero guard release", []), new("remaining-capture", Status.Unavailable, "Winning records/providers, full attachments/geometry and required appearance adapters remain open; original XML drives prebuilt", []), new("preservation-scope", Status.Partial, "Source checkpoint is separate; historical six T02A files remain unrestored", [])] });
    }
}
