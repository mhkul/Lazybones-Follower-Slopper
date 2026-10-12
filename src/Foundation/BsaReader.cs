using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace AutoFollower;

public sealed class BsaReader : IDisposable
{
    public record Member(string Path, long Offset, int Bytes, bool Compressed);
    readonly FileStream file;
    readonly uint flags;
    readonly Action<int>? bytesRead;
    public Dictionary<string, Member> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int OpaqueNameCount { get; private set; }
    // BSA names are byte strings, not guaranteed UTF-8. Latin1 is a reversible
    // index representation only; non-ASCII lookups require explicit encoding support.
    static string Name(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Any(b => b < 32 || b == 127)) throw new InvalidDataException("Invalid BSA name bytes");
        return Encoding.Latin1.GetString(bytes);
    }
    public static void RequireAsciiLookup(string path)
    {
        if (path.Any(c => c > 127)) throw new InvalidDataException("Non-ASCII BSA lookup requires verified name encoding; raw names retained losslessly");
    }
    byte[] At(long offset, int size)
    {
        if (offset < 0 || size < 0 || size > BoundedBinary.Limit || offset > file.Length - size) throw new InvalidDataException("BSA offset/size outside archive");
        file.Position = offset; byte[] b = new byte[size]; file.ReadExactly(b); bytesRead?.Invoke(size); return b;
    }
    public BsaReader(string path, Action<int>? bytesRead = null)
    {
        this.bytesRead = bytesRead;
        file = File.OpenRead(path);
        string operation = "read header";
        string metadata = "Archive bytes=" + file.Length;
        Func<string>? directoryDiagnostic = null;
        try
        {
            var h = new BoundedBinary(At(0, 36));
            uint magic = h.U32(), version = h.U32();
            metadata += "; magic=" + magic.ToString("X8") + "; version=" + version;
            if (magic != 0x00415342 || version != 105) throw new InvalidDataException("Supported archive format is BSA105");
            int folderOffset = h.Count(); flags = h.U32();
            metadata += "; folder offset=" + folderOffset + "; flags=" + flags.ToString("X8");
            if ((flags & 3) != 3 || (flags & 0x40) != 0) throw new InvalidDataException("BSA names required; Xbox layout unsupported");
            int folders = h.Count(100000), files = h.Count(2000000), folderBytes = h.Count(32 * 1024 * 1024), nameBytes = h.Count(32 * 1024 * 1024); uint fileFlags = h.U32();
            metadata += "; folders=" + folders + "; files=" + files + "; folder name bytes=" + folderBytes + "; file name bytes=" + nameBytes + "; file flags=" + fileFlags.ToString("X8");
            if (folderOffset != 36) throw new InvalidDataException("Unsupported BSA header offset");
            long namesOffset = checked((long)folderOffset + folders * 24L + folderBytes + folders + files * 16L);
            long metadataEnd = namesOffset + nameBytes;
            metadata += "; calculated filename offset=" + namesOffset + "; calculated metadata end=" + metadataEnd;
            directoryDiagnostic = () => DescribeDirectory(folderOffset, folders, files, nameBytes);
            operation = "read filename table at archive offset " + namesOffset + " (declared files=" + files + ", declared name bytes=" + nameBytes + ")";
            var nameData = new BoundedBinary(At(namesOffset, nameBytes)); List<string> names = [];
            for (int i = 0; i < files; i++) { List<byte> chars = []; byte c; while ((c = nameData.U8()) != 0) { if (chars.Count >= 4096) throw new InvalidDataException("BSA name exceeds bound"); chars.Add(c); } names.Add(Name(chars.ToArray())); }
            // FileCount defines the names, not exhaustion of the declared region.
            // Validate every name/directory mapping and the entire metadata bound.
            int q = 0, observedFolderBytes = 0; long nextFolder = folderOffset + folders * 24L;
            operation = "read folder directory";
            var records = new BoundedBinary(At(folderOffset, checked(folders * 24)));
            for (int i = 0; i < folders; i++)
            {
                operation = "read folder directory entry " + (i + 1);
                records.U64(); int count = records.Count(files); records.U32(); ulong stored = records.U64();
                long offset = checked((long)stored - nameBytes);
                if (offset != nextFolder) throw new InvalidDataException("BSA folder ordering/offset mismatch");
                int length = At(offset, 1)[0]; if (length == 0) throw new InvalidDataException("Empty BSA folder name");
                var dirnameBytes = At(offset + 1, length);
                if (dirnameBytes[^1] != 0) throw new InvalidDataException("BSA folder name lacks terminator");
                string dirname = Name(dirnameBytes[..^1]); observedFolderBytes += length;
                var f = new BoundedBinary(At(offset + 1 + length, checked(count * 16)));
                nextFolder = offset + 1 + length + count * 16L;
                if (nextFolder > namesOffset) throw new InvalidDataException("BSA folder records overlap filename table");
                for (int j = 0; j < count; j++)
                {
                    operation = "read member directory entry " + (q + 1) + " in folder " + dirname;
                    f.U64(); uint size = f.U32(), position = f.U32();
                    if ((size & 0x80000000) != 0 || q >= files) throw new InvalidDataException("Unsupported BSA size flags/count");
                    string rel = VirtualPath.Normalize(dirname + "/" + names[q++]);
                    if (rel.Any(c => c > 127)) OpaqueNameCount++;
                    int bytes = (int)(size & 0x3FFFFFFF); bool compressed = ((flags & 4) != 0) ^ ((size & 0x40000000) != 0);
                    if (bytes > BoundedBinary.Limit || position < metadataEnd || position > file.Length - bytes || !Members.TryAdd(rel, new(rel, position, bytes, compressed)))
                        throw new InvalidDataException("BSA member bounds/duplicate path");
                }
            }
            operation = "validate directory counts and member ranges";
            if (q != files || nextFolder != namesOffset || observedFolderBytes != folderBytes) throw new InvalidDataException("BSA header/count/name lengths disagree");
            // Mutagen 0.49 BsaFileNameBlock reads FileCount terminated names and
            // ignores unused bytes regardless of their values. They cannot name
            // another member: counts agree and payloads begin beyond the entire
            // declared region. No other bounded reader's exhaustion is relaxed.
            operation = "validate member overlap after complete filename mapping";
            long priorEnd = metadataEnd, priorOffset = -1; int priorBytes = -1;
            foreach (var m in Members.Values.OrderBy(m => m.Offset)) { if (m.Offset < priorEnd && (m.Offset != priorOffset || m.Bytes != priorBytes)) throw new InvalidDataException("Partially overlapping BSA members"); priorEnd = m.Offset + m.Bytes; priorOffset = m.Offset; priorBytes = m.Bytes; }
        }
        catch (InvalidDataException e)
        {
            try
            {
                // On failure only, measure the same archive's directory metadata.
                // This never admits members or changes the calculated table location.
                if (directoryDiagnostic != null) metadata += "\n" + directoryDiagnostic();
            }
            catch (Exception detail) when (detail is IOException or InvalidDataException) { metadata += "\nDirectory metadata inspection stopped: " + detail.Message; }
            finally { file.Dispose(); }
            var failure = new InvalidDataException("BSA archive '" + path + "': " + operation + ": " + e.Message, e);
            failure.Data["InputParseDetails"] = metadata;
            failure.Data["InputFile"] = path;
            throw failure;
        }
        catch { file.Dispose(); throw; }
    }
    string DescribeDirectory(int folderOffset, int folders, int files, int nameBytes)
    {
        long cursor = folderOffset + folders * 24L, observedNames = 0, observedFiles = 0;
        string firstDifference = "none";
        for (int i = 0; i < folders; i++)
        {
            var record = new BoundedBinary(At(folderOffset + i * 24L, 24));
            record.U64(); int count = record.Count(files); record.U32(); ulong stored = record.U64();
            int length = At(cursor, 1)[0];
            if (firstDifference == "none" && (stored < (ulong)nameBytes || stored - (ulong)nameBytes != (ulong)cursor))
                firstDifference = "entry " + (i + 1) + ", stored offset=" + stored + ", observed block offset=" + cursor;
            if (length == 0) throw new InvalidDataException("Empty folder name at directory entry " + (i + 1));
            if (At(cursor + length, 1)[0] != 0) throw new InvalidDataException("Folder terminator mismatch at directory entry " + (i + 1));
            observedNames += length; observedFiles += count; cursor += 1 + length + count * 16L;
        }
        return "Independent directory metadata: folders read=" + folders + "; files counted=" + observedFiles
            + "; folder name bytes counted=" + observedNames + "; observed filename table offset=" + cursor
            + "; first stored/physical folder offset disagreement=" + firstDifference;
    }
    public byte[] Extract(string relative)
    {
        RequireAsciiLookup(relative);
        if (!Members.TryGetValue(VirtualPath.Normalize(relative), out var member)) throw new FileNotFoundException("Missing BSA member", relative);
        string metadata = "Archive flags=" + flags.ToString("X8") + "; member compressed=" + member.Compressed;
        try
        {
            var r = new BoundedBinary(At(member.Offset, member.Bytes));
            if ((flags & 0x100) != 0) { int n = r.U8(); r.Take(n); }
            if (!member.Compressed) return r.Take(r.Remaining);
            int expected = r.Count(); byte[] frame = r.Take(r.Remaining);
            metadata += "; expected decoded bytes=" + expected + "; frame bytes=" + frame.Length;
            if (frame.Length >= 6) metadata += "; frame magic=" + BitConverter.ToUInt32(frame).ToString("X8") + "; FLG=" + frame[4].ToString("X2") + "; BD=" + frame[5].ToString("X2");
            return Lz4Frame.Decode(frame, expected);
        }
        catch (InvalidDataException e) { var failure = new InvalidDataException("Required BSA member " + file.Name + " :: " + member.Path + " (archive offset=" + member.Offset + ", stored bytes=" + member.Bytes + "): " + e.Message, e); failure.Data["InputParseDetails"] = metadata; failure.Data["InputFile"] = file.Name; throw failure; }
    }
    public void Dispose() => file.Dispose();
}

public static class VirtualPath
{
    public static string Normalize(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 || Path.IsPathRooted(relative) || relative.IndexOfAny([':', '\0', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new InvalidDataException("Unsafe virtual path");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))) throw new InvalidDataException("Unsafe virtual path components");
        return string.Join('/', parts);
    }
}

public static class Lz4Frame
{
    // xxHash32 verifies frame header and optional block/content checksums.
    public static uint XxHash(byte[] data)
    {
        const uint p1 = 2654435761, p2 = 2246822519, p3 = 3266489917, p4 = 668265263, p5 = 374761393;
        uint Round(uint a, uint b) => unchecked(BitOperations.RotateLeft(a + b * p2, 13) * p1);
        int i = 0; uint h;
        uint U(int n) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(n, 4));
        unchecked
        {
            if (data.Length >= 16)
            {
                uint v1 = p1 + p2, v2 = p2, v3 = 0, v4 = 0 - p1;
                while (i <= data.Length - 16) { v1 = Round(v1, U(i)); v2 = Round(v2, U(i + 4)); v3 = Round(v3, U(i + 8)); v4 = Round(v4, U(i + 12)); i += 16; }
                h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            }
            else h = p5;
            h += (uint)data.Length;
            while (i <= data.Length - 4) { h += U(i) * p3; h = BitOperations.RotateLeft(h, 17) * p4; i += 4; }
            while (i < data.Length) { h += data[i++] * p5; h = BitOperations.RotateLeft(h, 11) * p1; }
            h ^= h >> 15; h *= p2; h ^= h >> 13; h *= p3; return h ^ h >> 16;
        }
    }
    public static byte[] Decode(byte[] bytes, int expected)
    {
        if(expected<0||expected>BoundedBinary.Limit)throw new InvalidDataException("LZ4 frame output exceeds limit");
        var r = new BoundedBinary(bytes);
        if (r.U32() != 0x184D2204) throw new InvalidDataException("BSA105 requires supported LZ4 frame");
        int descriptorStart = r.Position; byte flg = r.U8(), bd = r.U8();
        if ((flg & 0xC3) != 0x40 || (bd & 0x8F) != 0) throw new InvalidDataException("Unsupported LZ4 frame version/dictionary/reserved flags FLG="+flg.ToString("X2")+" BD="+bd.ToString("X2"));
        int maxBlock = (bd >> 4) switch { 4 => 65536, 5 => 262144, 6 => 1048576, 7 => 4194304, _ => throw new InvalidDataException("LZ4 block limit invalid") };
        if ((flg & 8) != 0 && r.U64() != (ulong)expected) throw new InvalidDataException("LZ4 content size mismatch");
        byte checksum = (byte)(XxHash(bytes.AsSpan(descriptorStart, r.Position - descriptorStart).ToArray()) >> 8);
        if (r.U8() != checksum) throw new InvalidDataException("LZ4 header checksum mismatch");
        using var output = new MemoryStream();
        while (true)
        {
            uint size = r.U32(); if (size == 0) break; bool raw = (size & 0x80000000) != 0; int n = checked((int)(size & 0x7FFFFFFF));
            if (n > maxBlock) throw new InvalidDataException("LZ4 block exceeds descriptor limit");
            var data = r.Take(n);
            if ((flg & 0x10) != 0 && r.U32() != XxHash(data)) throw new InvalidDataException("LZ4 block checksum mismatch");
            // Official Skyrim texture archives can use linked frames (FLG40).
            // LZ4 author specification: retain only the preceding 64 KiB.
            int prior=(int)output.Length;byte[]? history=(flg&0x20)==0?output.GetBuffer().AsSpan(Math.Max(0,prior-65536),Math.Min(prior,65536)).ToArray():null;
            var decoded = raw ? data : BoundedBinary.Lz4(data, Math.Min(maxBlock, expected - prior), false,history);
            if (decoded.Length > expected - output.Length) throw new InvalidDataException("LZ4 frame output overflow"); output.Write(decoded);
        }
        byte[] result = output.ToArray();
        if ((flg & 4) != 0 && r.U32() != XxHash(result)) throw new InvalidDataException("LZ4 content checksum mismatch");
        r.End("LZ4 frame"); if (result.Length != expected) throw new InvalidDataException("LZ4 frame decoded length mismatch"); return result;
    }
}
