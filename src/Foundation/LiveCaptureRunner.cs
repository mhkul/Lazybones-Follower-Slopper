using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace AutoFollower;

public record LiveCaptureRequest(string Selection, string Sandbox, string Launcher, string Adapter,
    string Python, string PowerShell, string? ExpectedStopSha256, int TimeoutSeconds = 720, bool RuntimeInputs = true, List<string>? RequiredResources = null, string? PapyrusAdapter = null, string? HelperCapabilities = null, string? InputPlan = null, bool Diagnostics = false);

// Live orchestration is deliberately restricted to the revalidated compact Sen
// environment. Other selected environments need their own protected staging.
public static class LiveCaptureRunner
{
    public static string? PapyrusPath(LiveCaptureRequest request)
    {
        if (request.PapyrusAdapter != null) return request.PapyrusAdapter;
        string companion = Path.ChangeExtension(request.Adapter, ".pex");
        return File.Exists(companion) ? companion : null;
    }
    public static string TexturePath(string path)
        => ResourcePath(path, "textures");
    public static string ResourcePath(string path, string category)
    {
        path = path.Replace('\\', '/');
        if (path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) path = path[5..];
        if (!path.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase)) path = category + "/" + path;
        return VirtualPath.Normalize(path).ToLowerInvariant();
    }
    public static HashSet<string> SceneResourcePaths(JsonElement node)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node.TryGetProperty("model", out var model) && !string.IsNullOrEmpty(model.GetString()))
        {
            string p = ResourcePath(model.GetString()!, "meshes"); paths.Add(p);
            // A weight endpoint is an extraction input, never a zeroed baseline.
            if (p.EndsWith("_1.nif")) paths.Add(p[..^6] + "_0.nif");
        }
        if (node.TryGetProperty("extras", out var extras)) foreach (var e in extras.EnumerateArray())
            if (e.GetProperty("name").GetString() is "BODYTRI" or "HDT Skinned Mesh Physics Object" && e.TryGetProperty("value", out var value) && !string.IsNullOrEmpty(value.GetString())) paths.Add(ResourcePath(value.GetString()!, "meshes"));
        if (node.TryGetProperty("textures", out var textures)) foreach (var value in textures.EnumerateArray())
            if (!string.IsNullOrEmpty(value.GetString())) paths.Add(TexturePath(value.GetString()!));
        return paths;
    }
    public static HashSet<string> HeadResourcePaths(byte[] bytes)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); CaptureRawEvidence.Head(bytes, refs);
        return refs.Where(p => !p.Replace('\\', '/').StartsWith("Data/SKSE/Plugins/CharGen/", StringComparison.OrdinalIgnoreCase))
            .Select(TexturePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    public static HashSet<uint> RuntimeFormIds(JsonElement sample) => sample.GetProperty("int").EnumerateObject()
        .Where(p => p.Name is "actor" or "race" or "parentcell" or "worldspace" || p.Name.EndsWith("/form", StringComparison.Ordinal) && (p.Name.StartsWith("head/", StringComparison.Ordinal) || p.Name.StartsWith("worn/", StringComparison.Ordinal) || p.Name.StartsWith("equipped/", StringComparison.Ordinal)))
        .Select(p => unchecked((uint)p.Value.GetInt32())).Where(id => id != 0).ToHashSet();
    public static List<EvidenceRef> Fingerprints(string root, string requestPath, LiveCaptureRequest request)
    {
        if (Path.GetFullPath(root) != @"C:\Skyrim Auto Follower" || Path.GetFullPath(request.Sandbox) != Path.Combine(root, @"runs\T05\sandbox-small-01") || request.TimeoutSeconds is < 300 or > 900) throw new InvalidDataException("Unsupported live prototype root or deadline");
        foreach (string path in new[] { requestPath, request.Selection, request.Launcher, request.Adapter, request.Python, request.PowerShell }) SafePaths.NoLinks(path);
        string? papyrus = PapyrusPath(request);
        if (papyrus != null) SafePaths.NoLinks(papyrus);
        foreach (string path in new[] { request.Launcher, request.Adapter }.Concat(papyrus == null ? [] : new[] { papyrus })) CaptureRawEvidence.Require(new[] { "T05", "T06", "T05D", "T09" }.Any(task => Path.GetFullPath(path).StartsWith(Path.Combine(root, "runs", task) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Capture tool escapes task output");
        CaptureRawEvidence.Require(request.HelperCapabilities != null && papyrus != null && request.InputPlan != null && request.RuntimeInputs, "New captures require a matched helper manifest, explicit PEX and output input plan");
        MinimumCaptureContract.ValidateHelpers(request.HelperCapabilities!, request.Adapter, papyrus!, root, request.Launcher);
        MinimumCaptureInputs.Read(request.InputPlan!, request.Selection);
        var selection = JsonSerializer.Deserialize<ResolveRequest>(File.ReadAllText(request.Selection), Wire.Options) ?? throw new InvalidDataException("Missing explicit selection");
        var pins = ResolutionRunner.Fingerprints(request.Selection, selection);
        var helpers = new List<string> { "stage_capture.py", "body_environment.py", "Preflight.ps1", "collect_attempt.py", "validate_runtime_capture.py", "canonical_runtime_forms.py", "helper_capabilities.py", "tint_structure.py" };
        if (request.Diagnostics) helpers.Add("verify_engine_fixtures.py");
        var files = new[] { requestPath, request.Launcher, request.Adapter, request.Python, request.PowerShell, request.HelperCapabilities!, request.InputPlan! }.Concat(helpers.Select(n => Path.Combine(root, "prototypes/T05", n)));
        pins.AddRange(files.Concat(papyrus == null ? [] : new[] { papyrus }).Select(p => new EvidenceRef(Path.GetFullPath(p), Hashing.File(p), new FileInfo(p).Length, "live operation/tool input"))); return pins;
    }
    static void Verify(IEnumerable<EvidenceRef> evidence) { foreach (var e in evidence) CaptureRawEvidence.Require(new FileInfo(e.Path).Length == e.Bytes && Hashing.File(e.Path) == e.Sha256, "Live selected input/tool changed: " + e.Path); }
    static JsonElement Json(string path) => CaptureRawEvidence.Json(File.ReadAllBytes(path));
    static Process Start(string executable, IEnumerable<string> arguments, string root, string log, Action<string>? output = null)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true }; var writer = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Line(string? line) { if (line != null) { lock (writer) writer.WriteLine(line); output?.Invoke(line); } }
        process.OutputDataReceived += (_, e) => Line(e.Data); process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Exited += (_, _) => { }; // Writer closes after WaitForExit flushes both readers.
        try { process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
        catch { writer.Dispose(); process.Dispose(); throw; }
        Writers.Add(process, writer); return process;
    }
    static readonly Dictionary<Process, StreamWriter> Writers = [];
    static void Wait(Process process) { process.WaitForExit(); Writers[process].Dispose(); Writers.Remove(process); }
    static void Run(string executable, IEnumerable<string> args, string root, string log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var process = Start(executable, args, root, log);
        Wait(process); token.ThrowIfCancellationRequested(); if (process.ExitCode != 0) throw new InvalidDataException("Capture helper failed; retained log: " + log);
    }
    public record OptionalDiagnosticResult(string Name,string Status,int? ExitCode,string? Error,bool AffectsCoreCapture=false);
    public static OptionalDiagnosticResult RunOptionalDiagnostic(string name,string executable,IEnumerable<string> args,string root,string log,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var process=Start(executable,args,root,log);Wait(process);token.ThrowIfCancellationRequested();
            return new(name,process.ExitCode==0?"passed":"failed",process.ExitCode,process.ExitCode==0?null:"Optional checker returned nonzero; see retained log");
        }
        catch(Exception e) when(e is not OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();return new(name,"failed",null,e.GetType().Name+": "+e.Message);
        }
    }
    public static void Execute(OwnedJob job, string root, LiveCaptureRequest request, CancellationToken token, bool cancelAfterPostLoad = false)
    {
        Verify(job.Envelope.Evidence);
        MinimumCaptureContract.ValidateHelpers(request.HelperCapabilities ?? throw new InvalidDataException("Missing helper capabilities"), request.Adapter, PapyrusPath(request) ?? throw new InvalidDataException("Missing PEX"), root, request.Launcher);
        var inputPlan = MinimumCaptureInputs.Read(request.InputPlan ?? throw new InvalidDataException("Missing input plan"), request.Selection);
        string sandbox = request.Sandbox; SafePaths.NoLinks(sandbox);
        string claim = Path.Combine(sandbox, "live-runner.lock"); using var claimHandle = new FileStream(claim, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var selection = JsonSerializer.Deserialize<ResolveRequest>(File.ReadAllText(request.Selection), Wire.Options)!;
        var resolutionPins = ResolutionRunner.Fingerprints(request.Selection, selection); var resolutionJob = OwnedJob.Create(root, job.Envelope.FollowerId, resolutionPins, task: "T04");
        if (resolutionJob.Resolve(t => ResolutionRunner.Execute(resolutionJob, selection, t, captureMetadataOnly:true,inactivityEvidence:inputPlan.ConfirmedInactivityEvidence), token) != Status.Passed) throw new InvalidDataException("Selected-input resolution did not pass: " + resolutionJob.DirectoryPath);
        job.Write("selected-resolution.json", new { resolutionJob = resolutionJob.DirectoryPath, identity = resolutionJob.Envelope.AttemptId, inputs = resolutionPins });
        var details = Wire.Read<ResolutionDetails>(File.ReadAllText(Path.Combine(resolutionJob.DirectoryPath, "resolution.json")));
        var environment = Wire.Read<EnvironmentSnapshot>(File.ReadAllText(Path.Combine(resolutionJob.DirectoryPath, "environment.json")));
        var variants = Wire.Read<VariantManifest>(File.ReadAllText(Path.Combine(resolutionJob.DirectoryPath, "variants.json")));
        job.Journal("staging", Status.Observed, "Fresh explicit selection, no merged resource copy");
        var stagingArguments = new List<string> { Path.Combine(root, "prototypes/T05/stage_capture.py"), "--adapter", request.Adapter, "--selection", request.Selection, "--launcher", request.Launcher, "--helper-capabilities", request.HelperCapabilities!, "--input-plan", request.InputPlan! };
        if (request.Diagnostics) stagingArguments.Add("--diagnostics");
        job.Write("body-environment.json", details.BodyEnvironment ?? throw new InvalidDataException("Missing selected body environment"));
        stagingArguments.AddRange(["--body-environment", Path.Combine(job.DirectoryPath, "body-environment.json")]);
        if (PapyrusPath(request) is string papyrus) stagingArguments.AddRange(["--papyrus-adapter", papyrus]);
        if (request.RuntimeInputs) stagingArguments.Add("--runtime-inputs");
        Run(request.Python, stagingArguments, root, Path.Combine(job.DirectoryPath, "staging.log"), token);
        var stage = Json(Path.Combine(sandbox, "capture-stage.json")); string attempt = stage.GetProperty("attempt").GetString()!;
        string attemptFolder = SafePaths.Below(sandbox, "attempts/" + attempt); job.Write("live-attempt.json", new { attempt, attemptFolder, stageHash = Hashing.File(Path.Combine(sandbox, "capture-stage.json")) });
        foreach (string name in new[] { "capture-completion.txt", "capture-completion-exports.tsv" }) if (File.Exists(Path.Combine(sandbox, name))) { File.Copy(Path.Combine(sandbox, name), Path.Combine(job.DirectoryPath, "previous-" + name)); File.Delete(Path.Combine(sandbox, name)); }
        Run(request.PowerShell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "prototypes/T05/Preflight.ps1"), "-Sandbox", sandbox, "-Purpose", "capture", "-LauncherPath", request.Launcher], root, Path.Combine(job.DirectoryPath, "preflight.log"), token);
        Verify(job.Envelope.Evidence);
        string stop = Path.Combine(sandbox, "stop");
        string? expectedStop = request.ExpectedStopSha256;
        if (job.Envelope.RetryOf is Guid retry) { string prior = Path.Combine(Path.GetDirectoryName(job.DirectoryPath)!, "job-" + retry.ToString("N")); var receipt = Json(Path.Combine(prior, "owned-stop-pin.json")); expectedStop = receipt.GetProperty("sha256").GetString(); }
        if (File.Exists(stop)) { CaptureRawEvidence.Require(expectedStop == Hashing.File(stop), "Existing stop marker requires its exact retained pin"); File.Copy(stop, Path.Combine(job.DirectoryPath, "resumed-stop-marker.txt")); File.Delete(stop); job.Journal("resume-owned-stop", Status.Observed, "Pinned prior stop marker retained after fresh preflight"); }
        else CaptureRawEvidence.Require(request.ExpectedStopSha256 == null, "Expected stop marker is missing; no launch");
        string nativePath = Path.Combine(attemptFolder, "native.log"); bool cancellation = false, complete = false; string? failure = null;
        using var supervisor = Start(request.Launcher, ["launch", sandbox, stage.GetProperty("sid").GetString()!, Path.Combine(sandbox, "mo2/ModOrganizer.exe"), Path.Combine(sandbox, "game"), "-p", "T05", "run", Path.Combine(sandbox, "game/skse64_loader.exe")], root, Path.Combine(job.DirectoryPath, "supervisor.log"), Console.WriteLine);
        long supervisorCreated = supervisor.StartTime.ToUniversalTime().ToFileTimeUtc();
        var timer = Stopwatch.StartNew(); string? progress = null;
        try
        {
            while (!supervisor.HasExited)
            {
                string native = File.Exists(nativePath) ? File.ReadAllText(nativePath) : "";
                string phase = native.Contains("PostLoadGame success=1") ? "Selected save loaded; waiting for stable exports" : native.Contains("automaticLoad request=") ? "Automatic selected save load requested" : native.Contains("query runtime=") ? "Protected game startup" : "Protected source acquisition/startup";
                if (phase != progress) { Console.WriteLine(phase); job.Journal("live-progress", Status.Observed, phase); progress = phase; }
                if (token.IsCancellationRequested || cancelAfterPostLoad && native.Contains("PostLoadGame success=1")) { cancellation = true; File.WriteAllText(stop, "Owned live capture cancellation " + attempt); break; }
                if (timer.Elapsed.TotalSeconds > request.TimeoutSeconds) { failure = "Live deadline expired before complete stable exports"; File.WriteAllText(stop, failure + " " + attempt); break; }
                bool outputReady = false;
                if (native.Contains("bodyRouteError=")) { failure = "Installed OBody adapter unavailable; absence is not established"; File.WriteAllText(stop, failure + " " + attempt); break; }
                if (native.Contains("PostLoadGame success=1") && (BodyEnvironmentPolicy.RequiresAdapter(stage) ? native.Contains("OBodyIsReady callback apiVersion=1") : native.Contains("bodyRoute=as-is adapterRequired=0")))
                {
                    try { outputReady = Completion(sandbox, attempt, native, request.RuntimeInputs, inputPlan.Resources.Select(r => r.Path).ToList()); }
                    catch (Exception e) when (e is IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException) { /* Exports may still be flushing. The deadline retains incomplete outputs. */ }
                }
                if (outputReady) { complete = true; job.Journal("normal-completion-request", Status.Observed, "Settled output inputs and export structures validated before shutdown"); break; }
                Thread.Sleep(500);
            }
        }
        catch (Exception e) { failure = e.Message; File.WriteAllText(stop, "Owned capture failure " + attempt); }
        finally { Wait(supervisor); }
        bool confirmed = false;
        CaptureLifecycleBinding.Finalize(job, sandbox, attempt, () => confirmed, cancellation ? "cancelled" : complete ? "normal completion; retained even if finalization failed" : failure ?? "supervisor exited", () =>
        {
        job.Write("supervisor-result.json", new { attempt, supervisorPid = supervisor.Id, supervisorCreationFileTime = supervisorCreated, exitCode = supervisor.ExitCode, completionRequested = complete, cancellation, failure, seconds = timer.Elapsed.TotalSeconds });
        var identity = Json(Path.Combine(sandbox, "lifecycle-identity.json"));
        string[] receiptNames = ["restricted-submission.json", "restricted-exit.json", "owned-job-accounting.json", "guardian-stop.json"];
        var receipts = receiptNames.ToDictionary(n => n, n => Json(Path.Combine(sandbox, n)));
        var submission = receipts["restricted-submission.json"]; bool early = submission.TryGetProperty("noChildCreated", out var noChild) && noChild.GetBoolean();
        string? releaseName = File.Exists(Path.Combine(sandbox, "source-guards-release.json")) ? "source-guards-release.json" : null;
        string? earlyName = File.Exists(Path.Combine(sandbox, "source-guards-cancelled.json")) ? "source-guards-cancelled.json" : null;
        foreach (string name in new[] { releaseName, earlyName }.OfType<string>()) receipts.Add(name, Json(Path.Combine(sandbox, name)));
        CaptureLifecycleBinding.Bind(identity, stage, Hashing.File(Path.Combine(attemptFolder, "capture-stage.json")), receipts.Values, supervisor.Id, supervisorCreated);
        CaptureRawEvidence.Require(identity.GetProperty("launchPinsSha256").GetString() == Hashing.File(Path.Combine(sandbox, "capture-pins.tsv")), "Owned shutdown pin manifest changed");
        CaptureLifecycleBinding.Stopped(identity, submission, receipts["restricted-exit.json"], receipts["owned-job-accounting.json"], receipts["guardian-stop.json"], releaseName == null ? null : receipts[releaseName], earlyName == null ? null : receipts[earlyName]); confirmed = true;
        job.Write("confirmed-shutdown.json", new { identity, receipts });
        var launchRecords = Directory.EnumerateDirectories(Path.Combine(sandbox, "launch-records")).Where(p => File.Exists(Path.Combine(p, "capture-stage.json")) && Json(Path.Combine(p, "capture-stage.json")).GetProperty("attempt").GetString() == attempt).ToArray();
        CaptureRawEvidence.Require(launchRecords.Length == 1, "Unique owned launch record for runner receipt required");
        File.Copy(Path.Combine(job.DirectoryPath, "supervisor-result.json"), Path.Combine(launchRecords[0], "runner-supervisor-result.json"));
        if (early)
        {
            job.Write("capture-lifecycle.json", new { attempt, noChildCreated = true, ownedShutdownConfirmed = true, receipts });
            WriteIncomplete(job, environment, variants, details, attempt, "Cancelled during source acquisition; no game child created");
            throw new OperationCanceledException();
        }
        // Preserve diagnostics even for cancellation/failure. Only completed
        // valid bundles receive Observed export and lifecycle contract checks.
        Run(request.Python, [Path.Combine(root, "prototypes/T05/collect_attempt.py"), attempt], root, Path.Combine(job.DirectoryPath, "collect-first.log"), CancellationToken.None);
        if (cancellation || failure != null || !complete || supervisor.ExitCode != 0) CompleteInterruptedCapture(job, environment, variants, details, attempt, Json(Path.Combine(attemptFolder, "observation.json")), cancellation, failure);
        Run(request.Python, [Path.Combine(root, "prototypes/T05/validate_runtime_capture.py"), attempt], root, Path.Combine(job.DirectoryPath, "runtime-validation.log"), CancellationToken.None);
        Run(request.Python, [Path.Combine(root, "prototypes/T05/canonical_runtime_forms.py"), attempt], root, Path.Combine(job.DirectoryPath, "canonical-forms.log"), CancellationToken.None);
        if (request.Diagnostics) job.Write("engine-fixtures-diagnostic.json",RunOptionalDiagnostic("engine-fixtures",request.Python,[Path.Combine(root,"prototypes/T05/verify_engine_fixtures.py"),attempt],root,Path.Combine(job.DirectoryPath,"engine-fixtures.log"),CancellationToken.None));
        // Derived validation is admitted directly; no second broad retention pass.
        var admitted = CaptureAssessmentRunner.Read(root, attempt); Verify(job.Envelope.Evidence);
        if (request.RuntimeInputs)
        {
            RuntimeProviderEvidence.Write(job, root, selection, details, attempt, admitted.Validation);
            var records = Json(Path.Combine(attemptFolder, "runtime-records.json"));
            var models = records.GetProperty("forms").EnumerateArray().Where(f => f.TryGetProperty("models", out _)).SelectMany(f => f.GetProperty("models").EnumerateArray()).Where(m => m.GetProperty("context").GetString() == "third-person-female").Select(m => m.GetProperty("path").GetString()).OfType<string>().Where(s => s.Length > 0).Select(s => s.Replace('\\', '/')).Select(s => s.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? s : "meshes/" + s).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            job.Write("runtime-outfit-models.json", models);
            // Build-source analysis is selected after actual per-mesh application
            // is established. BodySlide file presence is not application evidence.
        }
        if(details.BodyEnvironment!.RequiresAdapter)
        {
            var verifiedPlan=MinimumCaptureInputs.Read(request.InputPlan!,request.Selection);
            string assigned=admitted.Validation.GetProperty("samples")[0].GetProperty("string").GetProperty("assignedpreset").GetString()!;
            var body=verifiedPlan.ResolveAppliedPreset(assigned);
            details=details with {PrebuiltSource=body};variants=variants with {PrebuiltSource=body};
        }
        WriteContracts(job, environment, variants, details, attempt, admitted.Validation, admitted.Observation, admitted.Evidence);
        Console.WriteLine("Partial capture contracts written; owned shutdown confirmed");
        });
    }
    static bool Completion(string sandbox, string attempt, string native, bool runtimeInputs, List<string>? requiredResources)
    {
        var roots = new[] { "mods/T05 private mutable", "mods/T05 adapter", "overwrite" }.Select(p => Path.Combine(sandbox, p)).ToArray();
        string? Find(string filename) { var paths = roots.SelectMany(p => Directory.EnumerateFiles(Path.Combine(p, "SKSE/Plugins"), filename, SearchOption.AllDirectories)).Select(Path.GetFullPath).ToArray(); return paths.Length == 1 ? paths[0] : null; }
        List<string> paths = []; List<JsonElement> samples = [];
        for (int i = 0; i < 1; i++) { string? path = Find($"T05_{attempt}_sample_{i}.json"); if (path == null) return false; var j = Json(path); if (j.GetProperty("int").GetProperty("sample").GetInt32() != i || j.GetProperty("int").GetProperty("loaded3d").GetInt32() != 1) return false; paths.Add(path); samples.Add(j); }
        foreach (string extension in new[] { "nif", "dds", "jslot" }) { string? path = Find($"T05_{attempt}.{extension}"); if (path == null) return false; byte[] raw = File.ReadAllBytes(path); if (extension == "nif") CaptureRawEvidence.Head(raw); else if (extension == "dds") CaptureRawEvidence.Tint(raw); else CaptureRawEvidence.Json(raw); paths.Add(path); }
        string? terminal = Find($"T05_{attempt}_terminal.json"); if (terminal == null || Json(terminal).GetProperty("string").GetProperty("state").GetString() != "settled-exports-requested") return false; paths.Add(terminal);
        MinimumCaptureContract.ValidateSample(samples[0], BodyEnvironmentPolicy.RequiresAdapter(Json(Path.Combine(sandbox, "capture-stage.json"))));
        for (int i = 0; i < 1; i++) for (int perspective = 0; perspective < 2; perspective++) if (!native.Contains($"scene sample={i} perspective={perspective}")) return false;
        if (runtimeInputs)
        {
            string folder = Path.Combine(sandbox, "attempts", attempt), request = Path.Combine(folder, "runtime-input-request.tsv");
            if (!File.Exists(request))
            {
                var ids = RuntimeFormIds(samples[0]).Order().Select(id => "F\t" + id.ToString("X8"));
                var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scripts/chargen.pex" };
                if (requiredResources != null) resources.UnionWith(requiredResources.Select(VirtualPath.Normalize));
                resources.UnionWith(HeadResourcePaths(File.ReadAllBytes(Find($"T05_{attempt}.nif")!)));
                for (int perspective = 0; perspective < 2; perspective++) foreach (string line in File.ReadLines(Path.Combine(folder, $"scene-0-{perspective}.jsonl")))
                {
                    var node = CaptureRawEvidence.Json(Encoding.UTF8.GetBytes(line));
                    resources.UnionWith(SceneResourcePaths(node));
                }
                File.WriteAllText(request + ".pending", attempt + "\n" + string.Join('\n', ids.Concat(resources.Order().Select(s => "R\t" + s))) + "\n", new UTF8Encoding(false)); File.Move(request + ".pending", request);
            }
            if (!native.Contains("runtimeInputs completed=1") || !File.Exists(Path.Combine(folder, "runtime-records.json")) || !File.Exists(Path.Combine(folder, "engine-resource-inputs.json"))) return false;
            var records = Json(Path.Combine(folder, "runtime-records.json")); var inputs = Json(Path.Combine(folder, "engine-resource-inputs.json"));
            CaptureRawEvidence.Require(records.GetProperty("attempt").GetString() == attempt && inputs.GetProperty("attempt").GetString() == attempt && records.GetProperty("requestSha256").GetString() == Hashing.File(request) && !records.GetProperty("truncated").GetBoolean() && !inputs.GetProperty("truncated").GetBoolean(), "Runtime input snapshot incomplete/mismatched");
        }
        string manifest = Path.Combine(sandbox, "capture-completion-exports.tsv"); File.WriteAllText(manifest, string.Concat(paths.Select(p => p + "\t" + Hashing.File(p) + "\n")), new UTF8Encoding(false));
        string marker = Path.Combine(sandbox, "capture-completion.txt"); File.WriteAllText(marker + ".pending", attempt + "\n" + Hashing.File(Path.Combine(sandbox, "capture-stage.json")) + "\n" + Hashing.File(manifest) + "\n", new UTF8Encoding(false)); File.Move(marker + ".pending", marker); return true;
    }
    static void WriteIncomplete(OwnedJob job, EnvironmentSnapshot environment, VariantManifest variants, ResolutionDetails details, string attempt, string reason)
        => WriteContracts(job, environment, variants, details, attempt, null, null, [], reason);
    public static void CompleteInterruptedCapture(OwnedJob job, EnvironmentSnapshot environment, VariantManifest variants, ResolutionDetails details, string attempt, JsonElement observation, bool cancellation, string? failure)
    {
        // The shared finalizer alone pins the final stopped marker. A preliminary
        // receipt here would both stale that hash and collide with immutable Write.
        job.Write("capture-lifecycle.json", observation);
        WriteIncomplete(job, environment, variants, details, attempt, cancellation ? "cancelled" : failure ?? "No validated normal completion");
        if (cancellation) throw new OperationCanceledException();
        throw new InvalidDataException(failure ?? "Owned capture did not complete normally");
    }
    static void WriteContracts(OwnedJob job, EnvironmentSnapshot environment, VariantManifest variants, ResolutionDetails details, string attempt, JsonElement? validation, JsonElement? observation, List<EvidenceRef> raw, string? incomplete = null)
    {
        var evidence = job.Envelope.Evidence.Concat(raw).ToList(); var body = details.PrebuiltSource;
        var obodyState = details.BodyEnvironment is { RequiresAdapter: false } route ? route.State : validation != null && observation is JsonElement observed && observed.TryGetProperty("obodyReadyObserved", out var ready) && ready.GetBoolean() ? OBodyState.Active : OBodyState.Unknown;
        var tables = new List<PluginTable>(); var forms = new List<CanonicalForm>(); var runs = new List<CaptureRun>();
        double weight = 0, scale = 0; string samplePath = "";
        if (validation is JsonElement v && observation is JsonElement o)
        {
            var entries = new List<PluginEntry>(); foreach (string kind in new[] { "full", "light" }) entries.AddRange(v.GetProperty("runtimePluginTables").GetProperty(kind + "plugin").EnumerateArray().Select((p, i) => new PluginEntry(p.GetString()!, kind, i)));
            tables.Add(new("runtime/T05/" + attempt, entries, Status.Observed, Path.Combine(job.DirectoryPath, "runtime-observations.json"))); Semantics.Table(tables[0]);
            var last = v.GetProperty("samples").EnumerateArray().Last(); weight = last.GetProperty("float").GetProperty("weight").GetDouble(); scale = last.GetProperty("float").GetProperty("scale").GetDouble(); samplePath = last.GetProperty("evidence").GetProperty("path").GetString()!;
            var morphs = last.GetProperty("float").EnumerateObject().Where(p => p.Name.StartsWith("morph/")).Select(p => { string name = p.Name[6..]; int split = name.IndexOf('/'); CaptureRawEvidence.Require(split > 0, "Unkeyed runtime morph"); return new KeyedMorph(name[..split], name[(split + 1)..], p.Value.GetDouble()); }).ToList();
            var canonical = Json(Path.Combine(Path.GetDirectoryName(samplePath)!, "../runtime-canonical-forms.json"));
            foreach (var f in canonical.GetProperty("forms").EnumerateArray().Where(f => f.GetProperty("sample").GetInt32() == last.GetProperty("index").GetInt32())) forms.Add(new(f.GetProperty("field").GetString()! + ":" + f.GetProperty("rawSigned").GetRawText(), Convert.ToUInt32(f.GetProperty("runtimeId").GetString(), 16), f.GetProperty("plugin").GetString(), f.GetProperty("localId").ValueKind == JsonValueKind.Null ? null : Convert.ToUInt32(f.GetProperty("localId").GetString(), 16), f.GetProperty("status").GetString() == "runtime-owner-observed" ? Status.Observed : Status.Unknown, "Actual runtime tables; owner only, winning overrides separate"));
            runs.Add(new(attempt, new(Status.Observed, true, samplePath, obodyState == OBodyState.Active ? "Native OBody API readiness" : "As-is capture; OBody readiness not applicable"), new(Status.Observed, true, samplePath, "Raw exact ESS filename and input pins"), new(Status.Observed, false, samplePath, "No menu/console/quest steering"), new(Status.Partial, true, samplePath, "Bounded head/tint/preset exports only"), new(obodyState == OBodyState.Active ? Status.Observed : Status.Unavailable, obodyState == OBodyState.Active ? 1 : 0, samplePath, "OBody API only on applicable route"), morphs, [], v.GetProperty("samples")));
            job.Write("runtime-observations.json", v); job.Write("capture-lifecycle.json", o);
        }
        Dictionary<string, JsonElement> extra = [];
        foreach (string name in new[] { "runtime-winning-records.json", "runtime-resource-providers.json", "body-build-inputs/body-build-inputs.json", "confirmed-shutdown.json" })
        {
            string path = Path.Combine(job.DirectoryPath, name); if (!File.Exists(path)) continue; extra[Path.GetFileNameWithoutExtension(path)] = Json(path);
            evidence.Add(new(path, Hashing.File(path), new FileInfo(path).Length, "Bounded live capture/provider/build-source evidence"));
        }
        var coverage = new List<Coverage> { new("unattended-head-state-exports", validation == null ? Status.Unavailable : Status.Observed, incomplete ?? "Settled output inputs and head/tint/export metadata; no full visual/skin proof", raw.Select(e => e.Path).ToList()), new("ordinary-model-material-overlay-paths", Status.Partial, "Ordinary biped model/material references and overlay values; resolve required sources, no visual-inclusion gate", []), new("applicable-body-authority", body.Status, "XML authority only for actual OBody-morphed meshes; unaffected/non-OBody meshes as-is", body.Xml == null ? [] : [body.Xml.Path]), new("body-outfit-build-inputs", extra.ContainsKey("body-build-inputs") ? Status.Partial : Status.Unavailable, "Only actual OBody-morphed targets need compatible build sources; per-mesh application still requires evidence", []), new("current-record-source", extra.ContainsKey("runtime-winning-records") ? Status.Partial : Status.Unavailable, "Native current final-source names and exact current record ranges where supported; saved/runtime mutations and composed fields remain separate", []), new("mixed-loose-archive", Status.Unknown, "Exact generic resource stream comparison is bounded to observed paths; specialized loaders and a universal precedence rule remain unproved", []), new("Spine2-and-physics", Status.Partial, "Historical discrepancy and motion/constraints are not normalized or proved", []), new("owned-lifecycle", extra.ContainsKey("confirmed-shutdown") ? Status.Observed : Status.Unknown, "Attempt, process creation times, launcher and manifests bound; guardian/power-loss recovery remains unsupported", []) };
        job.Write("environment.json", environment with { AttemptId = job.Envelope.AttemptId, Evidence = evidence, RuntimeTables = tables, Extra = extra, CaptureRequirements = ["Future launches require fresh private marker/configuration/source checks", "Private-only AccuratePenetration exclusion", "Pinned private startup workarounds; ordinary gameplay support not established"] });
        var capture = new CharacterCapture { SchemaVersion = 1, FollowerId = job.Envelope.FollowerId, AttemptId = job.Envelope.AttemptId, Evidence = evidence, SavedPlacement = details.Save.Placement ?? throw new InvalidDataException("No saved placement"), Weight = new(validation == null ? Status.Unknown : Status.Observed, weight, samplePath, "Settled tail actor metadata, separate from XML"), Scale = new(validation == null ? Status.Unknown : Status.Observed, scale, samplePath, "Settled tail actor metadata"), SavedMorphs = [], OBody = obodyState, Preset = body.PresetName == null || body.SliderSet == null || body.Xml == null ? null : new(body.PresetName, body.SliderSet, body.Xml, body.Sliders.Select(s => new KeyedMorph(s.Name, s.Endpoint, s.Value)).ToList(), null, "Original XML authority; no runtime-morph baking"), Forms = forms, Runs = runs, NodeDifferences = JsonSerializer.SerializeToElement(new { status = "historical Spine2 consequence unresolved" }), Repeatability = JsonSerializer.SerializeToElement(new { status = "single fresh integrated run; comparison separate" }), Coverage = coverage };
        job.Write("capture.json", capture);
        job.Write("capture-minimum-schema.json", new { schemaVersion = 4, consumers = MinimumCaptureContract.Consumers, diagnostics = "opt-in; not admission gates", outputPolicy = "D49/D50/D54/D57/D58", sourceRoute = "captured installed OBody body/outfits; XML prebuilt only on applied targets; unaffected/absent as-is" });
        job.Write("assets.json", new AssetManifest { SchemaVersion = 1, FollowerId = job.Envelope.FollowerId, AttemptId = job.Envelope.AttemptId, Evidence = evidence, Closure = Status.Partial, Assets = details.Providers.Select(p => new AssetEntry(p.VirtualPath, p.Candidates.FirstOrDefault(c => c.Identity == p.Winner)?.Content, p.Winner, p.Status, "Resolution only; no final asset rewrite", p.StagedPath, [], p.Status == Status.Observed ? [] : [p.Evidence], Status.Unknown)).Concat(raw.Where(e => e.Role == "attempt-specific runtime output").Select(e => new AssetEntry(e.Path, e, "runtime-export/" + attempt, Status.Observed, "Private retained export; body XML authority unchanged", null, [], ["Complete provider/asset/skin closure remains open"], Status.Partial))).ToList() });
        job.Write("records.json", new RecordMap { SchemaVersion = 1, FollowerId = job.Envelope.FollowerId, AttemptId = job.Envelope.AttemptId, Evidence = evidence, Extra = extra, Allocation = Status.Unavailable, Records = forms.Select(f => new RecordEntry(f, null, null, "Canonical sampled owner; retained current source-record evidence is separate; no generated follower records", ["Composed/save-mutated fields and complete dependency closure remain open"])).ToList() });
        job.Write("variants.json", variants with { AttemptId = job.Envelope.AttemptId, Evidence = evidence, BodyEnvironment = details.BodyEnvironment, Variants = ExtractionPolicy.Variants(obodyState) });
        job.Write("validation.json", new ValidationReport { SchemaVersion = 1, FollowerId = job.Envelope.FollowerId, AttemptId = job.Envelope.AttemptId, Evidence = evidence, FullCapture = Status.Partial, CompleteFollower = Status.Unavailable, SourcePreservation = Status.Partial, Assessment = incomplete ?? "Integrated unattended partial capture; normal owned shutdown; required appearance/provider gates explicit", Checks = coverage });
    }
}
