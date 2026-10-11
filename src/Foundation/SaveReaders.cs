namespace AutoFollower;

public enum CosaveParsePolicy { FullDiagnostics, MinimumCapture }
// Optional offline measurement; null on the ordinary production path.
public sealed class CosaveParseMetrics
{
    public long SkeePayloadBytesCopied { get; internal set; }
    public int SkeeChunksDecodedOrBuffered { get; internal set; }
    public int StringsDecoded { get; internal set; }
    public int PlayerMorphBlocksDecoded { get; internal set; }
    public int MorphValuesDecoded { get; internal set; }
}
public static class SaveReaders
{
    public static SaveRead Ess(byte[] bytes, string source, string payloadRoot, CancellationToken token, SkyrimInputFormats? formats = null)
    {
        formats=SkyrimInputFormats.Resolve(formats);
        var r = new BoundedBinary(bytes);
        if (System.Text.Encoding.ASCII.GetString(r.Take(13)) != "TESV_SAVEGAME") throw new InvalidDataException("Unsupported ESS signature");
        int headerBytes = r.Count(255), headerStart = r.Position;
        uint format = r.U32(); if (format != formats.EssVersion) throw new InvalidDataException("Required ESS header layout is version12; observed header is unsupported");
        uint saveNumber = r.U32(); string player = r.String16(); uint level = r.U32();
        string location = r.String16(), date = r.String16(), race = r.String16(); ushort sex = r.U16();
        if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(race) || level == 0 || level > 100000 || sex > 1)
            throw new InvalidDataException("Missing/invalid embedded ESS metadata");
        r.Float(); r.Float(); r.U64(); int width = r.Count(8192), height = r.Count(8192); ushort compression = r.U16();
        if (r.Position - headerStart != headerBytes) throw new InvalidDataException("ESS header length mismatch");
        r.Take(checked(width * height * 4)); int bodyBase = r.Position;
        byte[] body;
        if (compression == 0) body = r.Take(r.Remaining);
        else
        {
            int expected = r.Count(), compressed = r.Count(); var data = r.Take(compressed); r.End();
            body = compression switch { 1 => BoundedBinary.Inflate(data, expected), 2 => BoundedBinary.Lz4(data, expected),
                _ => throw new InvalidDataException("Unsupported ESS compression") };
        }
        var b = new BoundedBinary(body); byte formVersion = b.U8();
        if (formVersion != formats.FormVersion) throw new InvalidDataException("Required saved form layout is version78; observed layout is unsupported");
        int pluginSize = b.Count(1024 * 1024), pluginStart = b.Position;
        List<PluginEntry> entries = [];
        int full = b.U8(); for (int i = 0; i < full; i++) entries.Add(new(b.String16(), "full", i));
        int light = b.U16(); if (light > 4096) throw new InvalidDataException("Light plugin overflow");
        for (int i = 0; i < light; i++) entries.Add(new(b.String16(), "light", i));
        if (b.Position - pluginStart != pluginSize) throw new InvalidDataException("ESS plugin block size mismatch");
        var table = new PluginTable("saved/ess", entries, Status.Observed, source); Semantics.Table(table);
        int fltStart = b.Position;
        var locations = Enumerable.Range(0, 6).Select(_ => checked((int)b.U32() - bodyBase)).ToArray();
        int t1 = b.Count(100000), t2 = b.Count(100000), t3 = b.Count(100000) + 1, changes = b.Count(2000000);
        b.Take(60);
        if (locations.Any(p => p < b.Position || p > body.Length) || !(locations[2] <= locations[3] && locations[3] <= locations[4] && locations[4] <= locations[5] && locations[5] <= locations[0] && locations[0] <= locations[1]))
            throw new InvalidDataException("ESS location table bounds/order invalid");
        b.Seek(locations[0]); int nids = b.Count(4000000);
        uint[] ids = new uint[nids]; for (int i = 0; i < nids; i++) ids[i] = b.U32();
        uint Ref(uint v)
        {
            uint value = v & 0x3FFFFF;
            if (value == 0) return 0;
            return (v >> 22) switch { 0 when value <= ids.Length => ids[value - 1], 1 => value, 2 => 0xFF000000 | value,
                _ => throw new InvalidDataException("Invalid ESS reference index/type") };
        }
        List<PayloadSlice> payloads = [];
        void Opaque(string tag, int offset, int length, uint version, string limit)
        {
            var data = body.AsSpan(offset, length).ToArray();
            payloads.Add(new(source + "#decoded-body", "ESS", tag, version, offset, length, BoundedBinary.Digest(data), limit));
        }
        Opaque("body", 0, body.Length, formVersion, "Header/tables/ACHR prefix only; all other state opaque");
        foreach (var (start, end, count) in new[] { (locations[2], locations[3], t1), (locations[3], locations[4], t2), (locations[5], locations[0], t3) })
        {
            b.Seek(start);
            for (int i = 0; i < count; i++) { uint type = b.U32(); int size = b.Count(); int offset = b.Position; b.Take(size); if (b.Position > end) throw new InvalidDataException("ESS global block escapes table"); Opaque("global/" + type, offset, size, formVersion, "Opaque global payload"); }
            if (b.Position != end) throw new InvalidDataException("ESS global table framing mismatch");
        }
        Transform? placement = null; CanonicalForm? cell = null; int players = 0; Status achr = Status.Unavailable;
        string limits = "Player ACHR absent";
        b.Seek(locations[4]); HashSet<uint> seen = [];
        for (int i = 0; i < changes; i++)
        {
            token.ThrowIfCancellationRequested(); uint ref24 = b.Ref24(), id = Ref(ref24), flags = b.U32(); byte type = b.U8(), version = b.U8();
            if (!seen.Add(ref24)) throw new InvalidDataException("Duplicate ESS changeform reference");
            int lengthBits = type >> 6;
            int Length() => lengthBits switch { 0 => b.U8(), 1 => b.U16(), 2 => b.Count(), _ => throw new InvalidDataException("Unsupported changeform size encoding") };
            int size = Length(), decoded = Length(), offset = b.Position; byte[] data = b.Take(size);
            if (b.Position > locations[5]) throw new InvalidDataException("Changeform escapes table");
            if (id != 0x14) continue;
            if ((type & 63) != 1 || ++players != 1) throw new InvalidDataException("Invalid/duplicate player ACHR");
            Opaque("player-ACHR-stored", offset, size, version, "Compressed payload retained; no full ACHR decode");
            if (decoded > 0) data = BoundedBinary.Inflate(data, decoded);
            string rawPath = SafePaths.Below(payloadRoot, "player-achr.bin"); Directory.CreateDirectory(payloadRoot); File.WriteAllBytes(rawPath, data);
            var a = new BoundedBinary(data);
            bool moved = (flags & ((1u << 1) | (1u << 2) | (1u << 3) | (1u << 25))) != 0;
            achr = Status.Partial; limits = "Only supported initial placement prefix decoded; entire remaining ACHR retained opaque, including animation tail. No inventory/full player decode claimed.";
            if (!moved) { limits += " No moved/cell-changed prefix; placement unavailable."; continue; }
            uint cellId = Ref(a.Ref24()); cell = Semantics.Saved(cellId, table);
            var position = Enumerable.Range(0, 3).Select(_ => (double)a.Float()).ToArray();
            var rotation = Enumerable.Range(0, 3).Select(_ => (double)a.Float()).ToArray();
            placement = new(cell.Plugin == null ? "unknown/" + cellId.ToString("X8") : cell.Plugin + "|" + cell.LocalId!.Value.ToString("X6"), null,
                position, rotation, "game-units", "radians", "saved/ACHR-initial", rawPath, Status.Unknown);
            Semantics.Placement(placement);
            payloads.Add(new(rawPath, "ESS", "player-ACHR-decoded-tail", version, a.Position, a.Remaining,
                BoundedBinary.Digest(data.AsSpan(a.Position).ToArray()), "Opaque tail; earlier FallrimTools animation-tail failure is historical comparison, not this prefix reader's error"));
        }
        if (b.Position != locations[5]) throw new InvalidDataException("ESS changeform count/length mismatch");
        _ = fltStart;
        return new(new(format, formVersion, compression, saveNumber, player, level, location, date, race, sex), table, placement, cell, achr, limits, payloads, changes);
    }

    public static CosaveRead Cosave(byte[] bytes, string source, ushort? playerSex = null, CosaveParsePolicy policy = CosaveParsePolicy.FullDiagnostics, CosaveParseMetrics? metrics = null, SkyrimInputFormats? formats = null)
    {
        formats=SkyrimInputFormats.Resolve(formats);
        if(policy is not (CosaveParsePolicy.FullDiagnostics or CosaveParsePolicy.MinimumCapture))throw new InvalidDataException("Unknown co-save parsing policy");
        var r = new BoundedBinary(bytes);
        if (System.Text.Encoding.ASCII.GetString(r.Take(4)) != "SKSE") throw new InvalidDataException("Unsupported co-save signature");
        uint format = r.U32(), skse = r.U32(), runtime = r.U32(); int plugins = r.Count(4096);
        formats.RequireCosave(format,skse,runtime);
        List<PayloadSlice> slices = []; List<PluginEntry>? entries = null; HashSet<uint> uids = [];
        List<(uint Tag, uint Version, byte[] Data)>? skee = policy==CosaveParsePolicy.FullDiagnostics?[]:null;
        List<(uint Tag, uint Version, byte[] Data)> obody = [];
        for (int i = 0; i < plugins; i++)
        {
            uint uid = r.U32(); int chunks = r.Count(100000), length = r.Count(); int end = checked(r.Position + length);
            if (end > bytes.Length || !uids.Add(uid)) throw new InvalidDataException("Co-save section bounds/duplicate UID");
            for (int j = 0; j < chunks; j++)
            {
                uint tag = r.U32(), version = r.U32(); int size = r.Count(), offset = r.Position;
                if(size>r.Remaining||size>end-offset)throw new InvalidDataException("Co-save chunk escapes section");
                var span=bytes.AsSpan(offset,size);r.Seek(offset+size);
                // Validate framing/hash opaque bytes without materializing or
                // semantically interpreting unused SKEE diagnostics.
                byte[]? data=(uid==0&&tag==0x504C474E)||uid==0xA0B0D9EE||(uid==0x534B4545&&skee!=null)?span.ToArray():null;
                string interpretation = "Opaque plugin payload";
                if (uid == 0 && tag == 0x504C474E)
                {
                    if (version != 0 || entries != null) throw new InvalidDataException("Unsupported/duplicate PLGN");
                    var p = new BoundedBinary(data!); int count = p.U16(); entries = [];
                    for (int k = 0; k < count; k++) { byte index = p.U8(); int slot = index == 0xFE ? p.U16() : index; entries.Add(new(p.String16(), index == 0xFE ? "light" : "full", slot)); }
                    p.End(); interpretation = "SKSE PLGN v0 decoded";
                }
                if(uid==0x534B4545&&skee!=null) { skee.Add((tag,version,data!));if(metrics!=null){metrics.SkeePayloadBytesCopied+=size;metrics.SkeeChunksDecodedOrBuffered++;} }
                if (uid == 0xA0B0D9EE) obody.Add((tag, version, data!));
                slices.Add(new(source, uid.ToString("X8"), tag.ToString("X8"), version, offset, size, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(span)), interpretation));
            }
            if (r.Position != end) throw new InvalidDataException("Co-save section length mismatch");
        }
        r.End(); if (entries == null) throw new InvalidDataException("Missing embedded PLGN association table");
        var table = new PluginTable("saved/cosave", entries, Status.Observed, source); Semantics.Table(table);
        List<KeyedMorph> morphs = []; List<string> diagnostics = [];
        if(skee==null)diagnostics.Add("Minimum capture: SKEE strings/morph semantics not decoded; bounded opaque chunk hashes retained");
        else
        {
        var stringChunks = skee.Where(c => c.Tag == 0x53545442).ToArray();
        if (stringChunks.Length > 1) throw new InvalidDataException("Duplicate critical SKEE string table");
        if (stringChunks.Length == 1 && stringChunks[0].Version == 3)
        {
            var s = new BoundedBinary(stringChunks[0].Data); int count = s.Count(1000000);
            string[] strings = Enumerable.Range(0, count).Select(_ => s.String16()).ToArray(); s.End();
            if(metrics!=null)metrics.StringsDecoded+=strings.Length;
            string String(BoundedBinary d) { int index = d.Count(); if (index >= strings.Length) throw new InvalidDataException("SKEE string index out of range"); return strings[index]; }
            int playerCount = 0;
            for (int i = 0; i < skee.Count; i++)
            {
                if (skee[i].Tag != 0x4D525048) continue;
                if (skee[i].Version != 3 || i + 1 >= skee.Count || skee[i + 1].Tag != 0x4D524454 || skee[i + 1].Version != 3) { diagnostics.Add("Unsupported SKEE morph layout; retained opaque"); continue; }
                var actor = new BoundedBinary(skee[i].Data); uint id = actor.U32(); actor.End(); if (id != 0x14) continue;
                if (++playerCount != 1) throw new InvalidDataException("Duplicate SKEE player morph block");
                if(metrics!=null)metrics.PlayerMorphBlocksDecoded++;
                var d = new BoundedBinary(skee[i + 1].Data); int n = d.Count(10000); HashSet<(string, string)> seen = [];
                for (int j = 0; j < n; j++) { string name = String(d); int keys = d.Count(1000); for (int k = 0; k < keys; k++) { string key = String(d); double value = d.Float(); if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key) || !seen.Add((name, key))) throw new InvalidDataException("Duplicate/empty keyed morph"); morphs.Add(new(name, key, value));if(metrics!=null)metrics.MorphValuesDecoded++; } }
                d.End();
            }
        }
        else diagnostics.Add("SKEE STTB v3 unavailable/unsupported; morph payloads opaque");
        }
        string? assigned = null;
        var actorChunks = obody.Where(c => c.Tag == 0xA0B0D9EA).ToArray(); var nameChunks = obody.Where(c => c.Tag == 0xA0B0D9E0).ToArray();
        if (actorChunks.Length > 1 || nameChunks.Length > 1) throw new InvalidDataException("Duplicate critical OBody registry chunk");
        if (actorChunks.Length == 1 && nameChunks.Length == 1 && actorChunks[0].Version == 0 && nameChunks[0].Version == 0)
        {
            var a = new BoundedBinary(actorChunks[0].Data); Dictionary<uint, uint> actors = [];
            while (a.Remaining > 0) { uint id = a.U32(), state = a.U32(); if (!actors.TryAdd(id, state)) throw new InvalidDataException("Duplicate OBody actor"); }
            var n = new BoundedBinary(nameChunks[0].Data); Dictionary<uint, string>[] names = [[], []];
            for (int sex = 0; sex < 2; sex++) { n.U32(); while (true) { int length = n.Count(65536); if (length == 0) break; uint index = n.U32(); string name = new System.Text.UTF8Encoding(false, true).GetString(n.Take(length)); n.Take((4 - length % 4) % 4); if (!names[sex].TryAdd(index, name) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Invalid OBody preset identity"); } }
            n.End();
            if (playerSex is 0 or 1 && actors.TryGetValue(0x14, out uint stateValue) && stateValue >> 12 > 0) assigned = names[playerSex == 1 ? 0 : 1].GetValueOrDefault((stateValue >> 12) - 1);
            diagnostics.Add("OBody 4.4.3 V0 registry decoded; preset map requires ESS sex (female=1, male=0); XML remains separate shape authority");
        }
        else diagnostics.Add("OBody V0 assignment unsupported/unavailable; no inferred preset");
        return new(format, skse, runtime, table, slices, assigned, morphs, diagnostics);
    }
    public static string Associate(string save, string cosave, SaveRead ess, CosaveRead skse)
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(save), Path.GetFileNameWithoutExtension(cosave), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Co-save stem mismatch; select the associated pair");
        if (!ess.Plugins.Entries.OrderBy(p => p.Kind).ThenBy(p => p.Index).SequenceEqual(skse.Plugins.Entries.OrderBy(p => p.Kind).ThenBy(p => p.Index)))
            throw new InvalidDataException("Embedded ESS/PLGN identity mismatch");
        return "Same stem plus exact saved full/light index/name table and supported embedded runtime. Co-save contains no unique ESS checksum; matching tables cannot prove unique pair ownership.";
    }
}
