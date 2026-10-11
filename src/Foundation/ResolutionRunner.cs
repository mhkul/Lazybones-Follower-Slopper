using System.Text.Json;

namespace AutoFollower;

public static class ResolutionRunner
{
    public static List<EvidenceRef> Fingerprints(string requestPath, ResolveRequest request)
    {
        if (new[] { request.Save, request.Cosave, request.InstanceIni, request.Profile, request.Mods, request.Overwrite, request.Game }.Any(string.IsNullOrWhiteSpace) || request.Assets == null || request.Assets.Length > 1000 || request.Assets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Assets.Length)
            throw new InvalidDataException("Missing/duplicate/oversized selected-input request fields");
        foreach (var path in request.Assets) VirtualPath.Normalize(path);
        if (request.PresetXml != null && request.Assets.Any(p => p.Equals(request.PresetXml, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("PresetXml is resolved separately; do not repeat it in assets");
        var ini = EnvironmentResolver.Ini(request.InstanceIni);
        string basePath = ini.GetValueOrDefault("Settings/profiles_directory") ?? Path.Combine(ini["Settings/base_directory"], "profiles");
        var paths = new List<string> { requestPath, request.Save, request.Cosave, request.InstanceIni, Path.Combine(request.Game, "Skyrim.ccc") };
        foreach (var name in new[] { "modlist.txt", "plugins.txt", "loadorder.txt", "settings.ini", "Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini", "archives.txt", "initweaks.ini", "lockedorder.txt" }) paths.Add(Path.Combine(basePath, request.Profile, name));
        return paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => { SafePaths.NoLinks(path); return new EvidenceRef(Path.GetFullPath(path), Hashing.File(path), new FileInfo(path).Length, "selected input/config fingerprint"); }).ToList();
    }
    public static void Execute(OwnedJob job, ResolveRequest request, CancellationToken token, bool captureMetadataOnly = false, List<EvidenceRef>? inactivityEvidence = null)
    {
        var evidence = job.Envelope.Evidence; var follower = job.Envelope.FollowerId; var attempt = job.Envelope.AttemptId;
        foreach (var e in evidence) if (Hashing.File(e.Path) != e.Sha256 || new FileInfo(e.Path).Length != e.Bytes) throw new InvalidDataException("Pinned selection changed before staging: " + e.Path);
        var stager = new InputStager(job.DirectoryPath, token);
        var save = stager.Stage(request.Save, "inputs/" + Path.GetFileName(request.Save));
        var cosave = stager.Stage(request.Cosave, "inputs/" + Path.GetFileName(request.Cosave));
        var ess = SaveReaders.Ess(BoundedBinary.Read(save.Path), save.Path, SafePaths.Below(job.DirectoryPath, "payloads"), token);
        var skse = SaveReaders.Cosave(BoundedBinary.Read(cosave.Path), cosave.Path, ess.Metadata.Sex,captureMetadataOnly?CosaveParsePolicy.MinimumCapture:CosaveParsePolicy.FullDiagnostics);
        string association = SaveReaders.Associate(request.Save, request.Cosave, ess, skse);
        var resolver = new EnvironmentResolver(stager, token);
        try
        {
            var current = resolver.Discover(request);
            List<PluginComparison> compatibility = [];
            foreach (var saved in ess.Plugins.Entries)
            {
                var now = current.Entries.SingleOrDefault(p => string.Equals(p.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
                compatibility.Add(new(saved.Name, now == null ? "missing-or-disabled" : now.Kind != saved.Kind ? "kind-changed" : now.Index != saved.Index ? "remapped" : "same-slot", saved, now,
                    "ESS has no original plugin content hash; current TES4-header fingerprints do not prove historical content equivalence"));
            }
            foreach (var added in current.Entries.Where(p => !ess.Plugins.Entries.Any(s => s.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))) compatibility.Add(new(added.Name, "added-since-save", null, added, "Current header-derived identity"));
            List<AssetResolution> providers = [];
            if(!captureMetadataOnly) foreach (var path in request.Assets) providers.Add(resolver.Asset(path, job.DirectoryPath));
            var bodyEnvironment = BodyEnvironmentPolicy.Detect(resolver.Roots);
            if(inactivityEvidence!=null)bodyEnvironment=BodyEnvironmentPolicy.ConfirmInactive(bodyEnvironment,inactivityEvidence);
            string? presetName = request.PresetName ?? skse.AssignedPreset;
            var body = BodyEnvironmentPolicy.ResolveSource(bodyEnvironment, () =>
            {
                if(captureMetadataOnly)return new("resolved-bodyslide-xml/v1",Status.Unknown,presetName,null,null,null,[],false,false,"Applied-mesh XML comes from the explicit verified output plan after runtime assignment; no pre-start preset scan/candidate payload staging");
                string? presetPath = presetName == null ? null : resolver.FindPreset(presetName, request.PresetXml);
                if (presetPath == null || presetName == null)
                    return new("resolved-bodyslide-xml/v1", Status.Unavailable, presetName, null, null, null, [], false, false, "Explicit intended XML virtual path/name required; no runtime-morph fallback");
                if (skse.AssignedPreset != null && skse.AssignedPreset != presetName) throw new InvalidDataException("Requested preset conflicts with supported saved assignment");
                var asset = resolver.Asset(presetPath, job.DirectoryPath, false); providers.Add(asset);
                return EnvironmentResolver.Xml(asset, presetName);
            });
            resolver.VerifyHeaders(); stager.Verify();
            foreach (var candidate in providers.SelectMany(p => p.Candidates).Where(c => c.Kind == "loose" && c.Content != null))
                if (Hashing.File(candidate.Content!.Path) != candidate.Content.Sha256 || new FileInfo(candidate.Content.Path).Length != candidate.Content.Bytes) throw new InvalidDataException("Loose provider candidate changed during resolution");
            foreach (var e in evidence) if (Hashing.File(e.Path) != e.Sha256) throw new InvalidDataException("Selected configuration changed during resolution");
            var env = new EnvironmentSnapshot { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = evidence,
                Inputs = new(evidence.Single(e => e.Path.Equals(Path.GetFullPath(request.Save), StringComparison.OrdinalIgnoreCase)), evidence.Single(e => e.Path.Equals(Path.GetFullPath(request.Cosave), StringComparison.OrdinalIgnoreCase)),
                    Status.Observed, association, Path.GetFileName(request.Save), JsonSerializer.SerializeToElement(ess.Metadata, Wire.Options), Status.Partial,
                    [new(save.Path, save.Sha256, save.Bytes, "Original opaque save state retained in immutable staging"), new(cosave.Path, cosave.Sha256, cosave.Bytes, "All opaque plugin chunks retained with offset/length/hash")]),
                Profile = resolver.Profile, RuntimeVersion = resolver.RuntimeVersion, ToolVersions = resolver.Tools,
                Saved = ess.Plugins, CurrentProfile = current, FutureLaunch = new(),
                CaptureRequirements = ["T05 controlled runtime/capture adapters; no launch gate passed in this offline resolution"] };
            var details = new ResolutionDetails { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = evidence,
                Save = ess, Cosave = skse, Staging = stager.Inputs, Headers = resolver.Headers, Compatibility = compatibility, Archives = resolver.Archives,
                Providers = providers, PrebuiltSource = body, BodyEnvironment = bodyEnvironment, Diagnostics = [.. resolver.Diagnostics, .. skse.Diagnostics,
                    "Current profile is independently header-derived; no current runtime table is fabricated.", "Known T02A six-file 11-14 Debug exception remains historical and unrestored; no new MO2/game process in this resolution."] };
            var manifest = new AssetManifest { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = evidence, Closure = Status.Partial,
                Assets = providers.Select(p => new AssetEntry(p.VirtualPath, p.Candidates.FirstOrDefault()?.Content, p.Winner, p.Status, "Immutable selected member only; no transformation", p.StagedPath, [],
                    ["Complete appearance dependency closure belongs to T05-T07"], Status.Unknown)).ToList() };
            var variants = new VariantManifest { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = evidence, PluginIdentity = "AutoFollower_" + follower.ToString("N") + ".esp", PrebuiltSource = body, BodyEnvironment = bodyEnvironment,
                Variants = ExtractionPolicy.Variants(bodyEnvironment.RequiresAdapter ? OBodyState.Active : bodyEnvironment.State) };
            var validation = new ValidationReport { SchemaVersion = 1, FollowerId = follower, AttemptId = attempt, Evidence = evidence,
                Assessment = "Selected-input resolution; partial appearance; no follower generated", FullCapture = Status.Partial, CompleteFollower = Status.Unavailable, SourcePreservation = Status.Passed,
                Checks = [new("association", Status.Observed, association, [save.Path, cosave.Path]), new("current-classification", Status.Observed, "TES4 headers; not loaded-runtime evidence", [resolver.Profile]),
                    new("compatibility", compatibility.Any(c => c.Result is "missing-or-disabled" or "kind-changed") || resolver.Diagnostics.Any(d => d.StartsWith("Missing/disabled master") || d.StartsWith("Master loads after")) ? Status.Failed : Status.Observed, "Canonical saved identities retained; original plugin content versions cannot be recovered from ESS", []),
                    new("placement", ess.Placement == null ? Status.Unavailable : Status.Partial, ess.AchrLimits, [save.Path]), new("preset-authority", body.Status, body.Limits, body.Xml == null ? [] : [body.Xml.Path]),
                    new("providers", providers.All(p => p.Status == Status.Observed) ? Status.Observed : Status.Partial, "Offline ordered candidates; live merged-data/engine comparison is separate", []),
                    new("live-vfs", Status.Unavailable, "No MO2 process launched by the resolver; requires independent startup preflight and cross-check", []),
                    new("source-preservation", Status.Passed, "Selected originals/configs/loose assets and current plugin headers before/after; no claim of complete historical A02 recovery", stager.Inputs.Select(i => i.Original.Path).ToList())] };
            job.Write("environment.json", env); job.Write("resolution.json", details); job.Write("assets.json", manifest); job.Write("variants.json", variants); job.Write("validation.json", validation);
            job.Write("input-manifest.json", new { pinned = evidence, staged = stager.Inputs, headers = resolver.Headers });
            job.Write("output-manifest.json", Directory.EnumerateFiles(job.DirectoryPath, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("journal.jsonl") && !p.EndsWith("job.json")).Select(p => new EvidenceRef(p, Hashing.File(p), new FileInfo(p).Length, "T04 retained output")).ToList());
        }
        finally { resolver.Close(); }
    }
}
