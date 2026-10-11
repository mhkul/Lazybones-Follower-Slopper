using System.Text;
using System.Text.Json;

namespace AutoFollower;

// Bounded readers for the retained Skyrim 1.5.97 CharGen export variant. These
// establish structure only, never full graph/skin/visual or follower validity.
internal static class CaptureRawEvidence
{
    public static JsonElement Json(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { Require(keys.Add(property.Name), "Duplicate JSON key"); Unique(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
        }
        Unique(doc.RootElement); return doc.RootElement.Clone();
    }
    public static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    public static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var q) && Equal(p.Value, q)),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Equal(p.First, p.Second)),
            JsonValueKind.Number => a.GetDouble() == b.GetDouble(),
            JsonValueKind.String => a.GetString() == b.GetString(),
            _ => a.GetRawText() == b.GetRawText()
        };
    }
    public static int Head(byte[] bytes, ISet<string>? textureReferences = null)
    {
        using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        byte[] Read(int size) { Require(size >= 0 && size <= r.BaseStream.Length - r.BaseStream.Position, "NIF extent"); return r.ReadBytes(size); }
        int Count(uint size, int maximum) { Require(size <= maximum, "NIF count bound"); return (int)size; }
        string Text(int size) => Encoding.UTF8.GetString(Read(size));
        string Str() => Text(Count(r.ReadUInt32(), 1024 * 1024));
        int newline = Array.IndexOf(bytes, (byte)10); Require(newline > 0 && newline < 128 && Encoding.ASCII.GetString(bytes, 0, newline).StartsWith("Gamebryo File Format", StringComparison.Ordinal), "NIF signature");
        r.BaseStream.Position = newline + 1;
        Require(r.ReadUInt32() == 0x14020007 && r.ReadByte() == 1 && r.ReadUInt32() == 12, "NIF version");
        int count = Count(r.ReadUInt32(), 100000); Require(count > 0 && r.ReadUInt32() == 100, "NIF SSE export variant");
        for (int i = 0; i < 3; i++) Text(r.ReadByte());
        int typeCount = r.ReadUInt16(); Require(typeCount > 0, "NIF types"); string[] types = Enumerable.Range(0, typeCount).Select(_ => Str()).ToArray();
        ushort[] indices = Enumerable.Range(0, count).Select(_ => r.ReadUInt16()).ToArray();
        int[] sizes = Enumerable.Range(0, count).Select(_ => Count(r.ReadUInt32(), bytes.Length)).ToArray();
        int stringCount = Count(r.ReadUInt32(), 100000); r.ReadUInt32();
        string[] strings = Enumerable.Range(0, stringCount).Select(_ => Str()).ToArray();
        int groups = Count(r.ReadUInt32(), 100000); for (int i = 0; i < groups; i++) r.ReadUInt32();
        byte[][] blocks = new byte[count][]; for (int i = 0; i < count; i++) { Require(indices[i] < typeCount, "NIF type index"); blocks[i] = Read(sizes[i]); }
        int roots = Count(r.ReadUInt32(), count); Require(roots > 0, "NIF roots"); for (int i = 0; i < roots; i++) { int root = r.ReadInt32(); Require(root >= 0 && root < count, "NIF root index"); }
        Require(r.BaseStream.Position == bytes.Length, "NIF trailing bytes"); int shapes = 0;
        for (int i = 0; i < count; i++)
        {
            if (textureReferences != null && types[indices[i]] == "BSShaderTextureSet")
            {
                var textures = new BoundedBinary(blocks[i]); int slots = textures.Count(32);
                for (int slot = 0; slot < slots; slot++)
                {
                    string path = new UTF8Encoding(false, true).GetString(textures.Take(textures.Count(4096)));
                    if (!string.IsNullOrEmpty(path)) textureReferences.Add(path);
                }
                textures.End();
            }
            if (types[indices[i]] != "BSDynamicTriShape") continue;
            using var s = new BinaryReader(new MemoryStream(blocks[i]));
            Require(s.ReadUInt32() < strings.Length, "NIF shape name");
            int extras = Count(s.ReadUInt32(), 100000); for (int j = 0; j < extras; j++) s.ReadInt32(); s.ReadInt32(); s.ReadUInt32();
            for (int j = 0; j < 13; j++) Require(float.IsFinite(s.ReadSingle()), "NIF nonfinite transform"); s.ReadInt32();
            for (int j = 0; j < 4; j++) Require(float.IsFinite(s.ReadSingle()), "NIF nonfinite bounds");
            int skin = s.ReadInt32(); s.ReadInt32(); s.ReadInt32(); ulong desc = s.ReadUInt64(); int triangles = s.ReadUInt16(), vertices = s.ReadUInt16(); int data = Count(s.ReadUInt32(), bytes.Length); int stride = (int)(desc & 15) * 4;
            Require(vertices > 0 && stride > 0, "NIF empty geometry");
            if (data > 0) { Require(data == stride * vertices + triangles * 6 && data <= s.BaseStream.Length - s.BaseStream.Position, "NIF packed geometry extent"); s.BaseStream.Position += data; }
            else
            {
                Require(skin >= 0 && skin < count && types[indices[skin]] is "NiSkinInstance" or "BSDismemberSkinInstance", "NIF skin reference");
                using var si = new BinaryReader(new MemoryStream(blocks[skin])); si.ReadInt32(); int partition = si.ReadInt32(); Require(partition >= 0 && partition < count && types[indices[partition]] == "NiSkinPartition", "NIF partition reference");
                using var p = new BinaryReader(new MemoryStream(blocks[partition])); p.ReadUInt32(); uint packedSize = p.ReadUInt32(), packedStride = p.ReadUInt32(); p.ReadUInt64();
                Require(packedStride == stride && packedSize == vertices * stride && packedSize <= p.BaseStream.Length - p.BaseStream.Position, "NIF skin vertex extent");
            }
            Require(s.ReadUInt32() == 0 && s.ReadUInt32() == vertices * 16, "NIF dynamic geometry variant");
            for (int j = 0; j < vertices; j++) for (int k = 0; k < 4; k++) { float value = s.ReadSingle(); Require(float.IsFinite(value) && (k == 3 || Math.Abs(value) < 1e6), "NIF dynamic vertex bounds"); }
            Require(s.BaseStream.Position == s.BaseStream.Length, "NIF dynamic shape trailing bytes"); shapes++;
        }
        Require(shapes > 0, "Head lacks supported dynamic shapes"); return shapes;
    }
    public static void Tint(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        Require(bytes.Length >= 128 && r.ReadUInt32() == 0x20534444 && r.ReadUInt32() == 124, "DDS header");
        r.ReadUInt32(); uint height = r.ReadUInt32(), width = r.ReadUInt32(); Require(height > 0 && height <= 8192 && width > 0 && width <= 8192, "DDS dimensions");
        r.BaseStream.Position = 76; Require(r.ReadUInt32() == 32, "DDS pixel format"); r.ReadUInt32(); uint fourcc = r.ReadUInt32(), bits = r.ReadUInt32();
        Require(fourcc == 0 && bits == 32 && bytes.Length == 128L + width * height * 4L, "Unsupported tint DDS payload variant");
    }
}
