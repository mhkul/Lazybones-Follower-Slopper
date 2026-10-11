using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace AutoFollower;

public static class RuntimeProviderEvidence
{
    public static bool NeedsPluginSource(JsonElement form) => !(form.GetProperty("runtimeId").GetUInt32()==20&&form.GetProperty("formType").GetInt32()==62&&form.GetProperty("requestedBy").GetString()=="selected-sample");
    public static CanonicalForm Canonical(uint id, PluginTable runtime)
    {
        if (id == 0 || id >> 24 == 255) return new(id.ToString("X8"), id, null, null, Status.Unknown, "Null/dynamic runtime identity");
        bool light = id >> 24 == 254; int slot = light ? (int)((id >> 12) & 4095) : (int)(id >> 24);
        var plugin = runtime.Entries.SingleOrDefault(p => p.Kind == (light ? "light" : "full") && p.Index == slot);
        return new(id.ToString("X8"), id, plugin?.Name, plugin == null ? null : id & (light ? 4095u : 0xFFFFFFu), plugin == null ? Status.Unknown : Status.Observed, runtime.EvidencePath);
    }
    public static List<JsonElement> RecordSlices(string path, PluginHeader plugin, Dictionary<uint, CanonicalForm> requested, string output)
    {
        SafePaths.NoLinks(path); Directory.CreateDirectory(output); string before = Hashing.File(path); List<JsonElement> result = [];
        var target = new Dictionary<uint, CanonicalForm>();
        foreach (var form in requested.Values)
        {
            int master = form.Plugin?.Equals(plugin.Identity.Name, StringComparison.OrdinalIgnoreCase) == true ? plugin.Masters.Count : plugin.Masters.FindIndex(p => p.Equals(form.Plugin, StringComparison.OrdinalIgnoreCase));
            if (master >= 0 && master < 254 && form.LocalId != null) target.Add((uint)master << 24 | form.LocalId.Value, form);
        }
        using var stream = File.OpenRead(path); List<(long End, uint Label, int Type)> groups = []; int records = 0;
        byte[] header = new byte[24]; var seenTargets = new HashSet<uint>();
        while (stream.Position < stream.Length)
        {
            while (groups.Count > 0 && groups[^1].End == stream.Position) groups.RemoveAt(groups.Count - 1);
            long offset = stream.Position, limit = groups.Count == 0 ? stream.Length : groups[^1].End;
            CaptureRawEvidence.Require(limit - offset >= 24 && ++records <= 4000000, "Plugin record/group bounds"); stream.ReadExactly(header);
            string tag = Encoding.ASCII.GetString(header, 0, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (tag == "GRUP")
            {
                CaptureRawEvidence.Require(size >= 24 && offset + size <= limit && groups.Count < 64, "Invalid plugin group extent"); groups.Add((offset + size, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)))); continue;
            }
            CaptureRawEvidence.Require(size <= 64 * 1024 * 1024 && stream.Position + size <= limit, "Invalid plugin record extent");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (!target.TryGetValue(id, out var form)) { stream.Position += size; continue; }
            CaptureRawEvidence.Require(seenTargets.Add(id), "Duplicate requested record ID in source plugin");
            byte[] packed = new byte[size]; stream.ReadExactly(packed); byte[] payload = packed;
            if ((flags & 0x40000) != 0)
            {
                CaptureRawEvidence.Require(packed.Length >= 4, "Compressed record size missing"); int expected = BinaryPrimitives.ReadInt32LittleEndian(packed); CaptureRawEvidence.Require(expected is > 0 and <= 64 * 1024 * 1024, "Compressed record output bound");
                using var input = new MemoryStream(packed, 4, packed.Length - 4); using var inflater = new ZLibStream(input, CompressionMode.Decompress); payload = new byte[expected]; inflater.ReadExactly(payload); CaptureRawEvidence.Require(inflater.ReadByte() == -1, "Compressed record exceeds declared extent");
            }
            var fields = new List<object>(); int at = 0; int? extended = null; byte? cellFlags = null;
            while (at < payload.Length)
            {
                CaptureRawEvidence.Require(payload.Length - at >= 6, "Truncated record subfield"); string field = Encoding.ASCII.GetString(payload, at, 4); int n = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(at + 4)); at += 6;
                if (field == "XXXX") { CaptureRawEvidence.Require(n == 4 && extended == null && payload.Length - at >= 4, "Bad record XXXX field"); extended = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at)); at += 4; continue; }
                if (extended != null) { n = extended.Value; extended = null; } CaptureRawEvidence.Require(n >= 0 && payload.Length - at >= n && fields.Count < 100000, "Record subfield extent");
                if (tag == "CELL" && field == "DATA" && n >= 1) cellFlags = payload[at];
                fields.Add(new { tag = field, offset = at, bytes = n, sha256 = BoundedBinary.Digest(payload.AsSpan(at, n).ToArray()) }); at += n;
            }
            CaptureRawEvidence.Require(extended == null, "Dangling record XXXX");
            string retained = Path.Combine(output, plugin.Identity.Name + "-" + id.ToString("X8") + ".record"); byte[] raw = [.. header, .. packed]; File.WriteAllBytes(retained, raw);
            result.Add(JsonSerializer.SerializeToElement(new { canonical = form, sourcePlugin = plugin.Identity.Name, provider = plugin.Provider, sourcePath = path, sourceFileSha256 = before, recordOffset = offset, rawFormId = id, recordTag = tag, flags, packed = new EvidenceRef(retained, BoundedBinary.Digest(raw), raw.Length, "Exact current source record range"), payloadSha256 = BoundedBinary.Digest(payload), fields, cellFlags, interior = cellFlags == null ? (bool?)null : (cellFlags & 1) != 0, containingWorldspaceRaw = groups.Where(g => g.Type == 1).Select(g => (uint?)g.Label).LastOrDefault(), status = "Observed source record; runtime fields may include save or plugin mutations" }, Wire.Options));
        }
        CaptureRawEvidence.Require(stream.Position == stream.Length && Hashing.File(path) == before, "Source plugin changed during record inspection"); return result;
    }
    public static void Write(OwnedJob job, string root, ResolveRequest selection, ResolutionDetails details, string attempt, JsonElement validation)
    {
        string folder = Path.Combine(root, "runs/T05/sandbox-small-01/attempts", attempt);
        var native = CaptureRawEvidence.Json(File.ReadAllBytes(Path.Combine(folder, "runtime-records.json"))); var resource = CaptureRawEvidence.Json(File.ReadAllBytes(Path.Combine(folder, "engine-resource-inputs.json")));
        var entries = new List<PluginEntry>(); foreach (string kind in new[] { "full", "light" }) entries.AddRange(validation.GetProperty("runtimePluginTables").GetProperty(kind + "plugin").EnumerateArray().Select((p, i) => new PluginEntry(p.GetString()!, kind, i)));
        var table = new PluginTable("runtime/" + attempt, entries, Status.Observed, Path.Combine(folder, "runtime-records.json")); Semantics.Table(table);
        List<JsonElement> slices = []; List<object> recordLimits = []; int requiredRecordFailures=0;
        foreach (var group in native.GetProperty("forms").EnumerateArray().Where(f => f.GetProperty("available").GetBoolean()).GroupBy(f => f.GetProperty("finalSource").GetString(), StringComparer.OrdinalIgnoreCase))
        {
            var plugin = details.Headers.SingleOrDefault(h => h.Identity.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (plugin == null) { foreach(var f in group) { bool required=NeedsPluginSource(f);if(required)requiredRecordFailures++;recordLimits.Add(new {source=group.Key,runtimeId=f.GetProperty("runtimeId").GetUInt32(),required,reason=required?"No final source/current header; unsupported required record":"Loaded player lifecycle root; saved ACHR placement/identity and native base/parent links are authority, not a plugin override"}); } continue; }
            string source = plugin.HeaderBytes.Path[..plugin.HeaderBytes.Path.LastIndexOf("#TES4-header", StringComparison.Ordinal)];
            try { var found = RecordSlices(source, plugin, group.Select(f => Canonical(f.GetProperty("runtimeId").GetUInt32(), table)).ToDictionary(f => f.RuntimeId), Path.Combine(job.DirectoryPath, "record-inputs")); slices.AddRange(found); var foundIds = found.Select(f => f.GetProperty("canonical").GetProperty("runtimeId").GetUInt32()).ToHashSet(); foreach (var f in group.Where(f => !foundIds.Contains(f.GetProperty("runtimeId").GetUInt32()))) { if(NeedsPluginSource(f))requiredRecordFailures++;recordLimits.Add(new { source = group.Key, runtimeId = f.GetProperty("runtimeId").GetUInt32(), reason = "No exact record identified in final source; owner/master or dynamic mutation remains unsupported" }); } }
            catch (Exception e) when (e is IOException or InvalidDataException) { requiredRecordFailures+=group.Count(NeedsPluginSource);recordLimits.Add(new { source = group.Key, reason = e.Message }); }
        }
        job.Write("runtime-winning-records.json", new { attempt, native, slices, recordLimits, limits = "Final loaded source corroborated with exact current records where supported; owner, save-time records, composed fields and generated follower allocation remain distinct" });
        if(MinimumCaptureContract.IsMinimum(CaptureRawEvidence.Json(File.ReadAllBytes(Path.Combine(folder,"capture-stage.json")))))CaptureRawEvidence.Require(requiredRecordFailures==0,"Missing required selected plugin record/source closure; inspect retained runtime-winning-records.json");
        var stager = new InputStager(Path.Combine(job.DirectoryPath, "provider-inputs"), CancellationToken.None); var resolver = new EnvironmentResolver(stager, CancellationToken.None); List<object> resources = []; List<string> unresolved = [];
        try
        {
            resolver.Discover(selection);
            resolver.RegisterPrivateCopies(Path.Combine(root,"runs/T05/sandbox-small-01/copies.json"),Path.Combine(root,"runs/T05/sandbox-small-01/mods/T05 private mutable"));
            foreach (var observed in resource.GetProperty("resources").EnumerateArray())
            {
                string path = VirtualPath.Normalize(observed.GetProperty("path").GetString()!); Console.WriteLine("Inspecting observed resource " + path);
                try
                {
                    var winner = resolver.StageObserved(observed, Path.Combine(job.DirectoryPath, "provider-inputs"));
                    resources.Add(new { observed, candidates = new[] { winner }, errors = Array.Empty<string>(), byteMatches = new[] { winner.Identity }, genericStreamStatus = Status.Observed, specializedLoaderStatus = Status.Unknown, limits = "Observed absolute backing identity selects the provider; indexed exact member and full bounded content corroborate it. No competitor payload copies." });
                }
                catch (Exception e) when (e is IOException or InvalidDataException) { resources.Add(new { observed, status = Status.Unavailable, error = e.Message }); unresolved.Add(path + ": " + e.Message); }
            }
            resolver.VerifyHeaders(); resolver.VerifyPrivateCopies(); stager.Verify();
        }
        finally { resolver.Close(); }
        job.Write("runtime-resource-providers.json", new { attempt, resources, limits = "Winner-first source proof; unbound/multiple handles or content/member failures remain explicit. No byte-only selection or universal precedence rule." });
        if (MinimumCaptureContract.IsMinimum(CaptureRawEvidence.Json(File.ReadAllBytes(Path.Combine(folder, "capture-stage.json"))))) CaptureRawEvidence.Require(unresolved.Count == 0, "Required definitive source closure failed: " + string.Join("; ", unresolved));
    }
}
