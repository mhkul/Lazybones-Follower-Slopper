namespace AutoFollower;

public record PayloadSlice(string Container, string PluginUid, string Tag, uint Version, long Offset,
    int Bytes, string Sha256, string Interpretation);
public record StagedInput(EvidenceRef Original, EvidenceRef Staged, string Before, string After);
public record SaveMetadata(uint Format, byte FormVersion, ushort Compression, uint SaveNumber,
    string Player, uint Level, string Location, string GameDate, string Race, ushort Sex);
public record SaveRead(SaveMetadata Metadata, PluginTable Plugins, Transform? Placement,
    CanonicalForm? Cell, Status AchrStatus, string AchrLimits, List<PayloadSlice> Payloads, int ChangeForms);
public record CosaveRead(uint Format, uint Skse, uint Runtime, PluginTable Plugins,
    List<PayloadSlice> Payloads, string? AssignedPreset, List<KeyedMorph> SavedMorphs, List<string> Diagnostics);
public record PluginHeader(PluginEntry Identity, string Provider, uint Flags, List<string> Masters,
    EvidenceRef HeaderBytes, long FileBytes);
public record PluginComparison(string Name, string Result, PluginEntry? Saved, PluginEntry? Current,
    string ContentIdentity);
public record ProviderRoot(string Identity, string Path, int Priority);
public record LoadedArchive(string Name, string Path, string Provider, int Order, string Activation);
public record ProviderCandidate(string Identity, string Kind, string Path, string? Member, int Priority,
    string Precedence, EvidenceRef? Container, EvidenceRef? Content);
public record AssetResolution(string VirtualPath, Status Status, string? Winner, List<ProviderCandidate> Candidates,
    string Evidence, string? StagedPath);
public record XmlSlider(string Name, string Endpoint, double Value);
public record BodyBuildSource(string Policy, Status Status, string? PresetName, string? SliderSet,
    EvidenceRef? Xml, string? Provider, List<XmlSlider> Sliders, bool AllowsRuntimeMorphFallback,
    bool RequiresOBodyAdapter, string Limits);
public record ResolutionDetails : Contract
{
    public required SaveRead Save { get; init; }
    public required CosaveRead Cosave { get; init; }
    public List<StagedInput> Staging { get; init; } = [];
    public List<PluginHeader> Headers { get; init; } = [];
    public List<PluginComparison> Compatibility { get; init; } = [];
    public List<LoadedArchive> Archives { get; init; } = [];
    public List<AssetResolution> Providers { get; init; } = [];
    public required BodyBuildSource PrebuiltSource { get; init; }
    public BodyEnvironment? BodyEnvironment { get; init; }
    public List<string> Diagnostics { get; init; } = [];
}

public static class Semantics
{
    public static void Table(PluginTable t)
    {
        if (t == null || string.IsNullOrWhiteSpace(t.Context) || t.Entries == null || t.Entries.Count == 0)
            throw new InvalidDataException("Missing plugin table/context");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<(string, int)> slots = [];
        foreach (var p in t.Entries)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Name) || Path.GetFileName(p.Name) != p.Name ||
                p.Name.IndexOfAny(['/', '\\', ':']) >= 0 || p.Kind is not ("full" or "light") || p.Index < 0 ||
                p.Index >= (p.Kind == "full" ? 254 : 4096) || !names.Add(p.Name) || !slots.Add((p.Kind, p.Index)))
                throw new InvalidDataException("Duplicate, invalid or conflicting plugin identity");
        }
        foreach (var kind in new[] { "full", "light" })
        {
            var indices = t.Entries.Where(p => p.Kind == kind).Select(p => p.Index).Order().ToArray();
            if (!indices.SequenceEqual(Enumerable.Range(0, indices.Length))) throw new InvalidDataException("Noncontiguous plugin table");
        }
    }
    public static void Placement(Transform t)
    {
        if (string.IsNullOrWhiteSpace(t.Cell) || string.IsNullOrWhiteSpace(t.Context) || t.Position == null || t.Rotation == null ||
            t.Position.Length != 3 || t.Rotation.Length != 3 || t.Position.Any(x => !double.IsFinite(x) || Math.Abs(x) > float.MaxValue) ||
            t.Rotation.Any(x => !double.IsFinite(x) || Math.Abs(x) > float.MaxValue) ||
            t.PositionUnits != "game-units" || t.RotationUnits is not ("radians" or "degrees"))
            throw new InvalidDataException("Invalid placement vectors, bounds or units");
    }
    public static CanonicalForm Saved(uint id, PluginTable saved)
    {
        Table(saved);
        if (!saved.Context.StartsWith("saved/", StringComparison.Ordinal)) throw new InvalidDataException("Saved reference requires saved table");
        if (id == 0 || id >> 24 == 0xFF) return new(id.ToString("X8"), id, null, null, Status.Unknown, "Null/dynamic reference; no plugin ownership");
        var light = id >> 24 == 0xFE;
        var index = light ? (int)((id >> 12) & 0xFFF) : (int)(id >> 24);
        var p = saved.Entries.SingleOrDefault(p => p.Kind == (light ? "light" : "full") && p.Index == index);
        return new(id.ToString("X8"), id, p?.Name, p == null ? null : id & (light ? 0xFFFu : 0xFFFFFFu),
            p == null ? Status.Unknown : Status.Observed, saved.EvidencePath);
    }
    public static uint? Remap(CanonicalForm saved, PluginTable current)
    {
        Table(current);
        if (saved.Status != Status.Observed || saved.Plugin == null || saved.LocalId == null) return null;
        var p = current.Entries.SingleOrDefault(p => string.Equals(p.Name, saved.Plugin, StringComparison.OrdinalIgnoreCase));
        if (p == null || saved.LocalId > (p.Kind == "light" ? 0xFFFu : 0xFFFFFFu)) return null;
        return p.Kind == "light" ? 0xFE000000u | (uint)p.Index << 12 | saved.LocalId.Value : (uint)p.Index << 24 | saved.LocalId.Value;
    }
}
