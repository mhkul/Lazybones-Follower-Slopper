using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AutoFollower;

public sealed record ResolveRequest(string Save, string Cosave, string InstanceIni, string Profile,
    string Mods, string Overwrite, string Game, string[] Assets, string? PresetXml, string? PresetName);

public sealed class EnvironmentResolver(InputStager stager, CancellationToken token)
{
    public List<ProviderRoot> Roots { get; } = [];
    public List<PluginHeader> Headers { get; } = [];
    public List<LoadedArchive> Archives { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public List<System.Text.Json.JsonElement> Tools { get; } = [];
    readonly Dictionary<string, (ProviderRoot Root, string Path)> top = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, BsaReader> archiveReaders = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> lockedArchiveHashes = new(StringComparer.OrdinalIgnoreCase);
    public string Profile { get; private set; } = "";
    public string RuntimeVersion { get; private set; } = "";
    int ordinal;
    static List<string> Lines(string path) => File.ReadAllLines(path, Encoding.UTF8).Select(s => s.Trim().TrimStart('\uFEFF')).Where(s => s.Length > 0 && !s.StartsWith('#') && !s.StartsWith(';')).ToList();
    public static Dictionary<string, string> Ini(string path)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase); string section = "";
        foreach (var s in Lines(path))
        {
            if (s.StartsWith('[') && s.EndsWith(']')) { section = s[1..^1]; continue; }
            int p = s.IndexOf('='); if (p < 0) continue;
            string value = s[(p + 1)..].Trim(); if (value.StartsWith("@ByteArray(") && value.EndsWith(')')) value = value[11..^1];
            result[section + "/" + s[..p].Trim()] = value.Replace("\\\\", "\\");
        }
        return result;
    }
    string StageConfig(string path) => stager.Stage(path, "config/" + ordinal++ + "-" + Path.GetFileName(path), 4 * 1024 * 1024).Path;
    void Root(string identity, string path)
    {
        SafePaths.NoLinks(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Enabled provider missing: " + path);
        var root = new ProviderRoot(identity, Path.GetFullPath(path), Roots.Count); Roots.Add(root);
        foreach (var file in Directory.EnumerateFiles(path))
        {
            string extension = Path.GetExtension(file);
            if (extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".esl", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bsa", StringComparison.OrdinalIgnoreCase))
                top.TryAdd(Path.GetFileName(file), (root, file));
        }
    }
    public PluginTable Discover(ResolveRequest request)
    {
        var ini = Ini(StageConfig(request.InstanceIni));
        string selected = ini.GetValueOrDefault("General/selected_profile") ?? throw new InvalidDataException("Missing selected MO2 profile");
        if (!string.Equals(selected, request.Profile, StringComparison.Ordinal)) throw new InvalidDataException("Selected MO2 profile differs; no automatic profile selection");
        string game = ini.GetValueOrDefault("General/gamePath") ?? throw new InvalidDataException("Missing game path");
        if (!string.Equals(Path.GetFullPath(game), Path.GetFullPath(request.Game), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Game path does not match instance");
        string baseDirectory = ini.GetValueOrDefault("Settings/base_directory") ?? throw new InvalidDataException("Missing explicit MO2 base_directory");
        string Expected(string key, string defaultPath) => Path.GetFullPath(ini.GetValueOrDefault("Settings/" + key) ?? defaultPath);
        if (!string.Equals(Expected("mod_directory", Path.Combine(baseDirectory, "mods")), Path.GetFullPath(request.Mods), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Expected("overwrite_directory", Path.Combine(baseDirectory, "overwrite")), Path.GetFullPath(request.Overwrite), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Provider paths do not match instance configuration");
        Profile = Path.GetFullPath(Path.Combine(Expected("profiles_directory", Path.Combine(baseDirectory, "profiles")), VirtualPath.Normalize(request.Profile)));
        SafePaths.NoLinks(Profile);
        if (Ini(Path.Combine(Profile, "settings.ini")).GetValueOrDefault("General/LocalSettings") != "true") throw new InvalidDataException("Supported environment requires explicit profile-local INIs; global INI adapter unavailable");
        var modlist = Lines(StageConfig(Path.Combine(Profile, "modlist.txt")));
        var plugins = Lines(StageConfig(Path.Combine(Profile, "plugins.txt")));
        var loadorder = Lines(StageConfig(Path.Combine(Profile, "loadorder.txt")));
        Dictionary<string, string> effectiveIni = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "settings.ini", "Skyrim.ini", "SkyrimPrefs.ini", "SkyrimCustom.ini", "initweaks.ini", "lockedorder.txt", "archives.txt" })
        {
            string path = Path.Combine(Profile, name); if (!File.Exists(path)) continue;
            string staged = StageConfig(path); if (name.StartsWith("Skyrim", StringComparison.OrdinalIgnoreCase)) foreach (var v in Ini(staged)) effectiveIni[v.Key] = v.Value;
            if (name == "archives.txt" && Lines(staged).Count > 0) Diagnostics.Add("archives.txt retained; nonempty custom archive selection is not admitted as automatic activation evidence");
        }
        if (effectiveIni.TryGetValue("Archive/bInvalidateOlderFiles", out string? invalidation) && invalidation != "1") Diagnostics.Add("Loose/archive invalidation setting requires merged-engine confirmation");
        var enabled = modlist.Where(s => s.StartsWith('+')).Select(s => s[1..]).ToList();
        if (enabled.Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabled.Count) throw new InvalidDataException("Duplicate enabled mod");
        Root("MO2/Overwrite", request.Overwrite);
        foreach (var mod in enabled) Root("mod/" + mod, SafePaths.Below(request.Mods, VirtualPath.Normalize(mod)));
        Root("physical/Data", Path.Combine(request.Game, "Data"));
        var active = plugins.Where(s => s.StartsWith('*')).Select(s => s[1..]).ToList();
        if (active.Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Count) throw new InvalidDataException("Duplicate active plugin");
        var implicitNames = new List<string> { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        string ccc = Path.Combine(request.Game, "Skyrim.ccc"); if (File.Exists(ccc)) implicitNames.AddRange(Lines(StageConfig(ccc)));
        HashSet<string> all = new(active, StringComparer.OrdinalIgnoreCase); all.UnionWith(implicitNames);
        if (loadorder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != loadorder.Count || all.Except(loadorder, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException("Duplicate loadorder or enabled plugin has no order");
        int full = 0, light = 0;
        foreach (var name in loadorder.Where(all.Contains))
        {
            token.ThrowIfCancellationRequested(); VirtualPath.Normalize(name);
            if (!top.TryGetValue(name, out var provider)) throw new FileNotFoundException("Enabled/implicit plugin missing", name);
            var header = ReadHeader(provider.Path); string kind = (header.Flags & 0x200) != 0 || Path.GetExtension(name).Equals(".esl", StringComparison.OrdinalIgnoreCase) ? "light" : "full";
            var identity = new PluginEntry(name, kind, kind == "light" ? light++ : full++);
            Headers.Add(new(identity, provider.Root.Identity + ":" + provider.Path, header.Flags, header.Masters,
                new(provider.Path + "#TES4-header", BoundedBinary.Digest(header.Bytes), header.Bytes.Length, "TES4 header bytes only; whole plugin content not fingerprinted"), new FileInfo(provider.Path).Length));
        }
        var table = new PluginTable("current-profile/headers", Headers.Select(h => h.Identity).ToList(), Status.Observed, Profile); Semantics.Table(table);
        foreach (var h in Headers)
            foreach (var master in h.Masters)
            {
                var owner = Headers.SingleOrDefault(x => string.Equals(x.Identity.Name, master, StringComparison.OrdinalIgnoreCase));
                if (owner == null) Diagnostics.Add("Missing/disabled master: " + h.Identity.Name + " -> " + master);
                else if (Headers.IndexOf(owner) >= Headers.IndexOf(h)) Diagnostics.Add("Master loads after dependent: " + h.Identity.Name + " -> " + master);
            }
        void Archive(string name, string activation)
        {
            name = VirtualPath.Normalize(name);
            if (!top.TryGetValue(name, out var provider)) { Diagnostics.Add("Activated archive missing: " + name); return; }
            if (Archives.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return;
            Archives.Add(new(name, provider.Path, provider.Root.Identity, Archives.Count, activation));
        }
        foreach (string key in new[] { "Archive/sResourceArchiveList", "Archive/sResourceArchiveList2" })
            if (effectiveIni.TryGetValue(key, out var value)) foreach (var name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) Archive(name, "effective profile INI " + key);
        foreach (var p in Headers)
        {
            string stem = Path.GetFileNameWithoutExtension(p.Identity.Name);
            foreach (var name in top.Keys.Where(n => n.Equals(stem + ".bsa", StringComparison.OrdinalIgnoreCase) || n.Equals(stem + " - Textures.bsa", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.EndsWith(" - Textures.bsa", StringComparison.OrdinalIgnoreCase))) Archive(name, "active plugin " + p.Identity.Name);
            foreach (var name in top.Keys.Where(n => n.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) && n.StartsWith(stem + " - ", StringComparison.OrdinalIgnoreCase) && !n.Equals(stem + " - Textures.bsa", StringComparison.OrdinalIgnoreCase))) Diagnostics.Add("Unsupported plugin archive suffix activation: " + name);
        }
        const string suffixDiagnostic = "Unsupported plugin archive suffix activation: ";
        var unknownArchives = Diagnostics.Where(d => d.StartsWith(suffixDiagnostic, StringComparison.Ordinal)).Select(d => d[suffixDiagnostic.Length..]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Diagnostics.RemoveAll(d => d.StartsWith("Unsupported plugin archive suffix activation: ", StringComparison.Ordinal));
        foreach (var name in unknownArchives)
        {
            if (Archives.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            if (!top.TryGetValue(name, out var provider)) continue;
            Archives.Add(new(name, provider.Path, provider.Root.Identity, Archives.Count, "uncertain suffix activation; conservatively considered above known archives"));
            Diagnostics.Add("Uncertain plugin archive suffix activation: " + name + "; only a matching requested member can block its archive winner");
        }
        var toolPaths = new List<string> { Path.Combine(request.Game, "SkyrimSE.exe"), Path.Combine(request.Game, "skse64_loader.exe"), "C:/Modding/MO2/ModOrganizer.exe" };
        toolPaths.AddRange(ini.Where(p => p.Key.EndsWith("\\binary", StringComparison.OrdinalIgnoreCase) && p.Value.Contains("BodySlide", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value));
        foreach (var path in toolPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var v = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            if (Path.GetFileName(path) == "SkyrimSE.exe") RuntimeVersion = v;
            Tools.Add(System.Text.Json.JsonSerializer.SerializeToElement(new { path, version = v, observed = "file version metadata; no process launched" }));
        }
        Diagnostics.Add("Observed runtime " + RuntimeVersion + "; version metadata does not gate compatibility testing. Required input/source structures remain checked.");
        return table;
    }
    public static (uint Flags, List<string> Masters, byte[] Bytes) ReadHeader(string path)
    {
        using var f = File.OpenRead(path); byte[] prefix = new byte[24]; f.ReadExactly(prefix);
        var h = new BoundedBinary(prefix); if (Encoding.ASCII.GetString(h.Take(4)) != "TES4") throw new InvalidDataException("Plugin lacks TES4 header");
        int size = h.Count(4 * 1024 * 1024); uint flags = h.U32();
        if ((flags & 0x40000) != 0) throw new InvalidDataException("Compressed TES4 header unsupported");
        byte[] data = new byte[size]; f.ReadExactly(data); var s = new BoundedBinary(data); List<string> masters = []; bool hedr = false;
        int? extended = null;
        while (s.Remaining > 0)
        {
            string tag = Encoding.ASCII.GetString(s.Take(4)); int n = s.U16();
            if (tag == "XXXX") { if (extended != null || n != 4) throw new InvalidDataException("Invalid XXXX header field"); extended = s.Count(4 * 1024 * 1024); continue; }
            if (extended != null) { n = extended.Value; extended = null; }
            var field = new BoundedBinary(s.Take(n));
            if (tag == "HEDR") { if (hedr || n != 12) throw new InvalidDataException("Missing/duplicate HEDR"); float version = field.Float(); if (!new[] { 0.94f, 1.7f, 1.71f }.Contains(version)) throw new InvalidDataException("Unsupported plugin header version " + version + ": " + path); hedr = true; }
            if (tag == "MAST") { byte[] b = field.Take(n); if (n == 0 || b[^1] != 0) throw new InvalidDataException("Invalid master name"); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); masters.Add(Encoding.GetEncoding(1252).GetString(b[..^1])); }
        }
        if (!hedr || extended != null || masters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != masters.Count) throw new InvalidDataException("Missing HEDR, dangling XXXX or duplicate master");
        return (flags, masters, [.. prefix, .. data]);
    }
    public AssetResolution Asset(string relative, string ownedRoot, bool includeArchives = true)
    {
        relative = VirtualPath.Normalize(relative); List<ProviderCandidate> candidates = [];
        if (includeArchives) BsaReader.RequireAsciiLookup(relative);
        foreach (var root in Roots)
        {
            token.ThrowIfCancellationRequested(); string path = SafePaths.Below(root.Path, relative);
            if (!File.Exists(path)) continue;
            candidates.Add(new(root.Identity + ":" + relative, "loose", path, null, root.Priority, "Overwrite, descending enabled modlist, physical Data; mixed loose/BSA engine precedence requires confirmation", null,
                new(path, Hashing.File(path), new FileInfo(path).Length, "original loose candidate fingerprint")));
        }
        List<string> errors = []; List<int> errorPriority = [];
        if (includeArchives)
        {
            foreach (var archive in Archives.AsEnumerable().Reverse())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!archiveReaders.TryGetValue(archive.Path, out var reader)) { SafePaths.NoLinks(archive.Path); reader = new(archive.Path); archiveReaders.Add(archive.Path, reader); }
                    if (!reader.Members.ContainsKey(relative)) continue;
                    if (archive.Activation.StartsWith("uncertain", StringComparison.Ordinal)) { errors.Add(archive.Name + ": Requested member exists but activation/order is uncertain"); errorPriority.Add(100000 + Archives.Count - 1 - archive.Order); continue; }
                    candidates.Add(new(archive.Provider + ":" + archive.Name + "::" + relative, "BSA105", archive.Path, relative, 100000 + Archives.Count - 1 - archive.Order,
                        "Later activated archive wins; " + archive.Activation, null, null));
                }
                catch (IOException e) { errors.Add(archive.Name + ": " + e.Message); errorPriority.Add(100000 + Archives.Count - 1 - archive.Order); }
                catch (InvalidDataException e) { errors.Add(archive.Name + ": " + e.Message); errorPriority.Add(100000 + Archives.Count - 1 - archive.Order); }
                catch (DecoderFallbackException e) { errors.Add(archive.Name + ": Unsupported archive filename encoding; " + e.Message); errorPriority.Add(100000 + Archives.Count - 1 - archive.Order); }
            }
        }
        if (includeArchives && candidates.Any(c => c.Kind == "loose") && (candidates.Any(c => c.Kind == "BSA105") || errors.Count > 0))
            return new(relative, Status.Unknown, null, candidates, "Mixed loose/archive winner requires engine evidence. T05's authored 1.5.97 fixture returned the archive member despite a loose candidate; offline loose-first ordering is not proof. " + string.Join("; ", errors), null);
        if (candidates.Count == 0 || (errorPriority.Any(p => p < candidates[0].Priority))) return new(relative, errors.Count > 0 ? Status.Unknown : Status.Unavailable, null, candidates, "No proven winner. " + string.Join("; ", errors), null);
        var winner = candidates[0]; string destination;
        if (winner.Kind == "loose")
        {
            var staged = stager.Stage(winner.Path, "assets/" + relative);
            if (staged.Sha256 != winner.Content!.Sha256 || staged.Bytes != winner.Content.Bytes) throw new InvalidDataException("Loose candidate changed before staging");
            destination = staged.Path; candidates[0] = winner with { Content = staged };
        }
        else
        {
            var before = Hashing.File(winner.Path); var data = archiveReaders[winner.Path].Extract(relative);
            var after = Hashing.File(winner.Path); if (before != after) throw new InvalidDataException("Archive changed during extraction");
            destination = SafePaths.Below(ownedRoot, "assets/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var f = new FileStream(destination, FileMode.CreateNew)) f.Write(data); File.SetAttributes(destination, FileAttributes.ReadOnly);
            candidates[0] = winner with { Container = new(winner.Path, before, new FileInfo(winner.Path).Length, "winning archive"), Content = new(destination, BoundedBinary.Digest(data), data.Length, "selected bounded BSA member") };
        }
        return new(relative, Status.Observed, winner.Identity, candidates, "Offline ordered-provider resolution; content hash corroborates bytes, not provider identity. Live VFS/engine cross-check recorded separately. " + string.Join("; ", errors), destination);
    }
    public (List<ProviderCandidate> Candidates, List<string> Errors) StreamCandidates(string relative, string ownedRoot)
    {
        relative = VirtualPath.Normalize(relative); List<ProviderCandidate> candidates = []; List<string> errors = []; int n = 0;
        BsaReader.RequireAsciiLookup(relative);
        foreach (var root in Roots)
        {
            string path = SafePaths.Below(root.Path, relative); if (!File.Exists(path)) continue;
            try { var pin = stager.Stage(path, "stream-candidates/" + n++ + "/" + relative); candidates.Add(new(root.Identity + ":" + relative, "loose", path, null, root.Priority, "Candidate only; generic engine bytes compared separately", null, pin)); }
            catch (Exception e) when (e is IOException or InvalidDataException) { errors.Add(path + ": " + e.Message); }
        }
        foreach (var archive in Archives)
        {
            try
            {
                if (!archiveReaders.TryGetValue(archive.Path, out var reader)) { SafePaths.NoLinks(archive.Path); reader = new(archive.Path); archiveReaders.Add(archive.Path, reader); }
                if (!reader.Members.ContainsKey(relative)) continue;
                if (!lockedArchiveHashes.TryGetValue(archive.Path, out var hash)) lockedArchiveHashes[archive.Path] = hash = Hashing.File(archive.Path);
                var data = reader.Extract(relative); string path = SafePaths.Below(ownedRoot, "stream-candidates/" + n++ + "/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using (var file = new FileStream(path, FileMode.CreateNew)) file.Write(data); File.SetAttributes(path, FileAttributes.ReadOnly);
                candidates.Add(new(archive.Provider + ":" + archive.Name + "::" + relative, "BSA105", archive.Path, relative, archive.Order, archive.Activation + "; candidate only", new(archive.Path, hash, new FileInfo(archive.Path).Length, "Read/share locked archive container"), new(path, BoundedBinary.Digest(data), data.Length, "Exact candidate member bytes")));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or DecoderFallbackException) { errors.Add(archive.Path + ": " + e.Message); }
        }
        return (candidates, errors);
    }
    readonly Dictionary<string, ProviderCandidate> observedWinners = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string Source, string Hash, long Bytes)> privateCopies = new(StringComparer.OrdinalIgnoreCase);
    EvidenceRef? copyManifest;
    readonly Dictionary<string,EvidenceRef> usedCopySources = new(StringComparer.OrdinalIgnoreCase);
    public void VerifyPrivateCopies()
    {
        if(usedCopySources.Count==0)return;
        CaptureRawEvidence.Require(copyManifest!=null&&Hashing.File(copyManifest.Path)==copyManifest.Sha256,"Private mapping changed during source staging");
        foreach(var e in usedCopySources.Values)CaptureRawEvidence.Require(new FileInfo(e.Path).Length==e.Bytes&&Hashing.File(e.Path)==e.Sha256,"Private mapped original changed during staging");
    }
    public void RegisterPrivateCopies(string manifest, string mutableRoot)
    {
        SafePaths.NoLinks(manifest); SafePaths.NoLinks(mutableRoot);
        byte[] raw=File.ReadAllBytes(manifest); copyManifest=new(manifest,BoundedBinary.Digest(raw),raw.Length,"Explicit protected mutable-provider source/copy mapping");
        foreach(var entry in System.Text.Json.JsonDocument.Parse(raw).RootElement.EnumerateArray().Where(e=>e.GetProperty("role").GetString()=="mutable-provider"))
        {
            string copy=BackingKey(entry.GetProperty("copy").GetString()!);
            CaptureRawEvidence.Require(copy.StartsWith(Path.GetFullPath(mutableRoot).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"Private mapping escaped mutable provider");
            CaptureRawEvidence.Require(privateCopies.TryAdd(copy,(BackingKey(entry.GetProperty("source").GetString()!),entry.GetProperty("sha256").GetString()!,entry.GetProperty("bytes").GetInt64())),"Ambiguous private-copy mapping");
        }
    }
    static string BackingKey(string path) => Path.GetFullPath(path.Replace("\\\\?\\", "", StringComparison.Ordinal)).TrimEnd('\\', '/');
    public ProviderCandidate StageObserved(System.Text.Json.JsonElement observed, string ownedRoot)
    {
        string relative = VirtualPath.Normalize(observed.GetProperty("path").GetString()!);
        CaptureRawEvidence.Require(observed.GetProperty("valid").GetBoolean() && observed.GetProperty("bounded").GetBoolean() && !observed.GetProperty("engineReadTruncated").GetBoolean(), "Invalid/truncated source observation: " + relative);
        string digest = observed.GetProperty("sha256").GetString()!; long bytes = observed.GetProperty("bytes").GetInt64();
        var direct = observed.GetProperty("engineReadBackingPaths").EnumerateArray().Select(p => BackingKey(p.GetString()!)).ToArray();
        string backing;
        if (direct.Length == 1) backing = direct[0];
        else if (direct.Length == 0)
        {
            CaptureRawEvidence.Require(observed.GetProperty("streamType").GetString()!.Contains("LooseFileStream", StringComparison.Ordinal) && observed.GetProperty("streamNameReturned").GetBoolean(), "No definitive backing identity: " + relative);
            string name = observed.GetProperty("streamName").GetString()!;
            CaptureRawEvidence.Require(LiveCaptureRunner.ResourcePath(name, relative.Split('/')[0]).Equals(relative, StringComparison.OrdinalIgnoreCase), "Loose stream name/member mismatch");
            backing = BackingKey(observed.GetProperty("mergedLooseBackingPath").GetString()!);
        }
        else throw new InvalidDataException("Multiple backing handles; no winner: " + relative);
        if (observedWinners.TryGetValue(relative, out var cached))
        {
            CaptureRawEvidence.Require(BackingKey(cached.Path).Equals(backing, StringComparison.OrdinalIgnoreCase) && cached.Content!.Sha256 == digest && cached.Content.Bytes == bytes, "Conflicting repeated source observation");
            if(privateCopies.ContainsKey(backing))VerifyPrivateCopies();
            return cached;
        }
        string selectedBacking=backing; bool privateCopy=privateCopies.TryGetValue(backing,out var binding);
        if(privateCopy)
        {
            SafePaths.NoLinks(binding.Source);
            CaptureRawEvidence.Require(copyManifest!=null && Hashing.File(copyManifest.Path)==copyManifest.Sha256 && binding.Hash==digest && binding.Bytes==bytes && new FileInfo(binding.Source).Length==bytes && Hashing.File(binding.Source)==digest,"Private-copy source/mapping drift");
            selectedBacking=binding.Source;
            usedCopySources[binding.Source]=new(binding.Source,digest,bytes,"Selected original corroborated by explicit private-copy mapping");
        }
        // The normalized reference and already validated root form a bounded
        // candidate name. Check the actual backing's links once, below; avoid
        // filesystem probes on thousands of unrelated provider candidates.
        var loose = Roots.Select(r => (Root: r, Path: Path.GetFullPath(Path.Combine(r.Path, relative)))).Where(p => p.Path.Equals(selectedBacking, StringComparison.OrdinalIgnoreCase)).ToArray();
        var archives = Archives.Where(a => BackingKey(a.Path).Equals(backing, StringComparison.OrdinalIgnoreCase)).ToArray();
        CaptureRawEvidence.Require(loose.Length + archives.Length == 1, "Observed backing is not one selected provider: " + relative);
        SafePaths.NoLinks(backing); ProviderCandidate winner;
        if (loose.Length == 1)
        {
            // Verify the named source before staging. Stager retains its immutable-boundary checks.
            CaptureRawEvidence.Require(new FileInfo(backing).Length == bytes && Hashing.File(backing) == digest, "Named loose source content mismatch");
            var content = stager.Stage(backing, "observed-winners/" + relative);
            CaptureRawEvidence.Require(content.Sha256 == digest && content.Bytes == bytes, "Named loose source changed during staging");
            winner = new((privateCopy?"private-copy/":"")+loose[0].Root.Identity + ":" + relative, "loose", backing, null, loose[0].Root.Priority, privateCopy?"Engine backing identity; explicit mapping "+copyManifest!.Path+" sha256="+copyManifest.Sha256+" original="+binding.Source:"Engine backing identity; content corroborates source", null, content);
        }
        else
        {
            var archive = archives[0]; BsaReader.RequireAsciiLookup(relative);
            CaptureRawEvidence.Require(!archive.Activation.StartsWith("uncertain", StringComparison.Ordinal), "Observed archive lacks selected activation proof");
            if (!archiveReaders.TryGetValue(backing, out var reader)) { reader = new(backing); archiveReaders.Add(backing, reader); }
            CaptureRawEvidence.Require(reader.Members.TryGetValue(relative, out var member), "Named archive lacks exact member: " + relative);
            if (!lockedArchiveHashes.TryGetValue(backing, out var containerHash)) lockedArchiveHashes[backing] = containerHash = Hashing.File(backing);
            byte[] data = reader.Extract(relative);
            CaptureRawEvidence.Require(data.Length == bytes && BoundedBinary.Digest(data) == digest, "Named archive/member content mismatch");
            string target = SafePaths.Below(ownedRoot, "observed-winners/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var f = new FileStream(target, FileMode.CreateNew)) f.Write(data); File.SetAttributes(target, FileAttributes.ReadOnly);
            winner = new(archive.Provider + ":" + archive.Name + "::" + member!.Path, "BSA105", backing, member.Path, archive.Order, archive.Activation + "; engine handle and exact indexed member", new(backing, containerHash, new FileInfo(backing).Length, "Read/share locked selected archive"), new(target, digest, bytes, "Verified observed member staged once"));
        }
        observedWinners.Add(relative, winner); return winner;
    }
    public static BodyBuildSource Xml(AssetResolution asset, string name)
    {
        if (asset.Status != Status.Observed || asset.StagedPath == null) return new("resolved-bodyslide-xml/v1", Status.Unavailable, name, null, null, null, [], false, false, "Missing winning XML; no runtime fallback");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        using var reader = XmlReader.Create(asset.StagedPath, settings); var xml = XDocument.Load(reader);
        var presets = xml.Descendants("Preset").Where(p => (string?)p.Attribute("name") == name).ToArray();
        if (presets.Length != 1 || string.IsNullOrWhiteSpace((string?)presets[0].Attribute("set"))) throw new InvalidDataException("Missing/ambiguous preset or slider-set identity");
        List<XmlSlider> sliders = []; HashSet<(string, string)> seen = [];
        foreach (var s in presets[0].Elements("SetSlider"))
        {
            string slider = (string?)s.Attribute("name") ?? "", endpoint = (string?)s.Attribute("size") ?? "";
            if (string.IsNullOrWhiteSpace(slider) || endpoint is not ("small" or "big") || !seen.Add((slider, endpoint)) ||
                !double.TryParse((string?)s.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || Math.Abs(value) > 100000)
                throw new InvalidDataException("Invalid/duplicate BodySlide slider endpoint");
            sliders.Add(new(slider, endpoint, value));
        }
        if (sliders.Count == 0) throw new InvalidDataException("Preset has no slider values");
        return new("resolved-bodyslide-xml/v1", Status.Observed, name, (string?)presets[0].Attribute("set"), asset.Candidates[0].Content, asset.Winner, sliders, false, false,
            "Values preserved in native XML percent units. T06 must validate compatible projects/ShapeData and weight interpolation; no geometry built. OBody variant namespacing must preserve these values.");
    }
    public string? FindPreset(string name, string? selectedPath)
    {
        const string directory = "CalienteTools/BodySlide/SliderPresets";
        Dictionary<string, string> winners = new(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
        {
            string folder = SafePaths.Below(root.Path, directory); if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.xml")) winners.TryAdd(directory + "/" + Path.GetFileName(file), file);
        }
        List<string> matches = [];
        foreach (var file in winners)
        {
            token.ThrowIfCancellationRequested(); SafePaths.NoLinks(file.Value);
            try
            {
                var data = BoundedBinary.Read(file.Value, 4 * 1024 * 1024); string before = BoundedBinary.Digest(data);
                using var stream = new MemoryStream(data);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var presets = XDocument.Load(reader).Descendants("Preset").Where(p => (string?)p.Attribute("name") == name).ToArray();
                if (Hashing.File(file.Value) != before) throw new InvalidDataException("Preset file changed during identity lookup");
                if (presets.Length > 1) throw new InvalidDataException("Duplicate internal preset identity: " + file.Key);
                if (presets.Length == 1) matches.Add(file.Key);
            }
            catch (XmlException ex) { throw new InvalidDataException("Malformed enabled preset prevents unambiguous lookup: " + file.Key, ex); }
        }
        Diagnostics.Add("Preset identity lookup: " + winners.Count + " winning loose XML files; " + matches.Count + " matching virtual paths. Disabled mods excluded. BodySlide loose resource namespace only.");
        if (matches.Count > 1) throw new InvalidDataException("Ambiguous preset name across winning XML files: " + string.Join(", ", matches));
        if (selectedPath != null && matches.Count == 1 && !VirtualPath.Normalize(selectedPath).Equals(matches[0], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Requested XML path differs from unique assigned preset provider");
        return matches.SingleOrDefault();
    }
    public void VerifyHeaders()
    {
        foreach (var h in Headers) { var path = h.HeaderBytes.Path[..h.HeaderBytes.Path.IndexOf("#TES4", StringComparison.Ordinal)]; if (new FileInfo(path).Length != h.FileBytes || BoundedBinary.Digest(ReadHeader(path).Bytes) != h.HeaderBytes.Sha256) throw new InvalidDataException("Plugin header changed during resolution: " + path); }
    }
    public void Close() { foreach (var reader in archiveReaders.Values) reader.Dispose(); }
}
