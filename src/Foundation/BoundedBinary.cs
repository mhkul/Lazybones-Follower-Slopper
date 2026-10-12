using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AutoFollower;

// All offsets/counts are checked before allocation or slicing. Maximum decoded blob: 256 MiB.
public sealed class BoundedBinary(byte[] bytes)
{
    public const int Limit = 256 * 1024 * 1024;
    public byte[] Bytes { get; } = bytes;
    public int Position { get; private set; }
    public int Remaining => Bytes.Length - Position;
    public void Seek(int p) { if (p < 0 || p > Bytes.Length) throw new InvalidDataException("Binary offset out of bounds"); Position = p; }
    public byte[] Take(int n) { if (n < 0 || n > Remaining || n > Limit) throw new InvalidDataException("Truncated/oversized binary field"); var b = Bytes.AsSpan(Position, n).ToArray(); Position += n; return b; }
    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float Float() { var x = BitConverter.Int32BitsToSingle((int)U32()); if (!float.IsFinite(x)) throw new InvalidDataException("Nonfinite float"); return x; }
    public int Count(int max = Limit) { var n = U32(); if (n > max) throw new InvalidDataException("Count exceeds supported bound"); return (int)n; }
    public string String16() => new UTF8Encoding(false, true).GetString(Take(U16()));
    public uint Ref24() => (uint)(U8() << 16 | U8() << 8 | U8());
    public void End(string? context = null)
    {
        if (Remaining != 0) throw new InvalidDataException("Unexpected binary tail" + (context == null ? "" : " in " + context)
            + "; consumed=" + Position + ", remaining=" + Remaining + ", expected buffer size=" + Bytes.Length + " bytes");
    }
    public static string Digest(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    public static byte[] Read(string path, int max = Limit)
    {
        using var f = File.OpenRead(path);
        if (f.Length > max) throw new InvalidDataException("Input exceeds byte limit: " + path);
        byte[] b = new byte[(int)f.Length]; f.ReadExactly(b); return b;
    }
    public static byte[] Inflate(byte[] b, int expected)
    {
        if (expected < 0 || expected > Limit) throw new InvalidDataException("Decoded length exceeds limit");
        using var input = new MemoryStream(b);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        byte[] output = new byte[expected]; z.ReadExactly(output);
        if (z.ReadByte() != -1) throw new InvalidDataException("Zlib output length mismatch");
        return output;
    }
    public static byte[] Lz4(byte[] bytes, int expected, bool exact = true,byte[]? history=null)
    {
        if (expected < 0 || expected > Limit) throw new InvalidDataException("LZ4 output exceeds limit");
        if(history is {Length:>65536})throw new InvalidDataException("LZ4 history exceeds window");
        int prefix=history?.Length??0,capacity=checked(expected+prefix);
        var r = new BoundedBinary(bytes); var output = new byte[capacity]; int p = prefix;history?.CopyTo(output,0);
        int Length(int n) { if (n == 15) { byte x; do { x = r.U8(); n = checked(n + x); } while (x == 255); } return n; }
        while (r.Remaining > 0)
        {
            byte token = r.U8(); int literals = Length(token >> 4);
            if (literals > capacity - p) throw new InvalidDataException("LZ4 literal overflow");
            r.Take(literals).CopyTo(output, p); p += literals;
            if (r.Remaining == 0) break;
            int offset = r.U16(); int match = checked(Length(token & 15) + 4);
            if (offset == 0 || offset > p || match > capacity - p) throw new InvalidDataException("Invalid LZ4 match");
            for (int j = 0; j < match; j++) { output[p] = output[p - offset]; p++; }
        }
        if (exact && p-prefix != expected) throw new InvalidDataException("LZ4 length mismatch");
        return output.AsSpan(prefix, p-prefix).ToArray();
    }
}

public sealed class InputStager(string ownedRoot, CancellationToken token)
{
    public List<StagedInput> Inputs { get; } = [];
    public EvidenceRef Stage(string source, string relative, int max = BoundedBinary.Limit)
    {
        token.ThrowIfCancellationRequested(); SafePaths.NoLinks(source);
        var data = BoundedBinary.Read(source, max); var before = BoundedBinary.Digest(data);
        var dest = SafePaths.Below(ownedRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using (var f = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None)) f.Write(data);
        var after = Hashing.File(source);
        if (before != after || new FileInfo(source).Length != data.Length) throw new InvalidDataException("Source changed during staging: " + source);
        File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.ReadOnly);
        var original = new EvidenceRef(Path.GetFullPath(source), before, data.Length, "original read-only input");
        var staged = new EvidenceRef(dest, before, data.Length, "immutable staged input");
        Inputs.Add(new(original, staged, before, after)); return staged;
    }
    public void Verify()
    {
        foreach (var i in Inputs)
        {
            token.ThrowIfCancellationRequested();
            if (Hashing.File(i.Original.Path) != i.Before || Hashing.File(i.Staged.Path) != i.Before ||
                new FileInfo(i.Original.Path).Length != i.Original.Bytes) throw new InvalidDataException("Changed input invalidates attempt: " + i.Original.Path);
        }
    }
}
