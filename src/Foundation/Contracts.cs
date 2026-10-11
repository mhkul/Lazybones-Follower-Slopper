using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFollower;

// Unknown is the zero/default state. An omitted observation cannot become a pass.
public enum Status { Unknown, Observed, Partial, Passed, Failed, Unavailable, Cancelled }
public abstract record Contract
{
    public required int SchemaVersion { get; init; }
    public required Guid FollowerId { get; init; }
    public required Guid AttemptId { get; init; }
    public List<EvidenceRef> Evidence { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public record EvidenceRef(string Path, string Sha256, long Bytes, string Role);
public record Observation<T>(Status Status, T? Value, string Source, string Limits);
public record PluginEntry(string Name, string Kind, int Index);
public record PluginTable(string Context, List<PluginEntry> Entries, Status Status, string EvidencePath);
public record CanonicalForm(string RawExporter, uint RuntimeId, string? Plugin, uint? LocalId, Status Status, string ResolutionEvidence);
public record InputPair(EvidenceRef Save, EvidenceRef Cosave, Status Association, string AssociationEvidence,
    string FilenameMetadata, JsonElement EmbeddedHeader, Status ReaderStatus, List<EvidenceRef> UnknownPayloads);
public record Transform(string Cell, string? Worldspace, double[] Position, double[] Rotation,
    string PositionUnits, string RotationUnits, string Context, string EvidencePath, Status WorldspaceStatus);
public record KeyedMorph(string Name, string Key, double Value);
public record PresetSource(string Name, string SliderSet, EvidenceRef OriginalBytes,
    List<KeyedMorph> CurrentXmlValues, EvidenceRef? DerivedBytes, string DerivationStatus);
public record CaptureRun(string Id, Observation<bool> NativeReady, Observation<bool> Loaded,
    Observation<bool> AssistanceRequired, Observation<bool> ExportComplete, Observation<int?> ApiVersion,
    List<KeyedMorph> LiveMorphs, List<Transform> LiveTransforms, JsonElement RawSamples);
public record Coverage(string Mechanism, Status Status, string Limits, List<string> EvidencePaths);
public record PreservationException(string Profile, List<string> Files, Status Recovery, string Description, EvidenceRef Evidence);
public record LaunchPreconditions
{
    public bool MarkerVerified { get; init; }
    public string? MarkerName { get; init; }
    public bool ConfigVerified { get; init; }
    public string? ExpectedInstance { get; init; }
    public string? ObservedInstance { get; init; }
    public bool ProfileVerified { get; init; }
    public bool PathsVerified { get; init; }
    public bool PrivateWritableDestinations { get; init; }
    public bool ExternalBackupsVerified { get; init; }
    public bool SharedConfigSnapshotsVerified { get; init; }
    public bool SharedFallbackProfilesSnapshotsVerified { get; init; }
    public Status Recovery { get; init; }
    public Status KnownWriteCoverage { get; init; }
    public List<EvidenceRef> Snapshots { get; init; } = [];
    public List<string> UnknownWriters { get; init; } = [];
}
public record EnvironmentSnapshot : Contract
{
    public required InputPair Inputs { get; init; }
    public required string Profile { get; init; }
    public required string RuntimeVersion { get; init; }
    public List<JsonElement> ToolVersions { get; init; } = [];
    public List<string> CaptureRequirements { get; init; } = [];
    public required PluginTable Saved { get; init; }
    public required PluginTable CurrentProfile { get; init; }
    public List<PluginTable> RuntimeTables { get; init; } = [];
    public required LaunchPreconditions FutureLaunch { get; init; }
    public List<PreservationException> PreservationExceptions { get; init; } = [];
}
public record CharacterCapture : Contract
{
    public required Transform SavedPlacement { get; init; }
    public required Observation<double> Weight { get; init; }
    public required Observation<double> Scale { get; init; }
    public List<KeyedMorph> SavedMorphs { get; init; } = [];
    public PresetSource? Preset { get; init; }
    public OBodyState OBody { get; init; }
    public List<MeshRouting> MeshRouting { get; init; } = [];
    public List<CaptureRun> Runs { get; init; } = [];
    public List<CanonicalForm> Forms { get; init; } = [];
    public required JsonElement NodeDifferences { get; init; }
    public required JsonElement Repeatability { get; init; }
    public List<Coverage> Coverage { get; init; } = [];
}
public record AssetEntry(string SourceIdentity, EvidenceRef? Source, string? ProviderIdentity,
    Status WinningProvider, string Transformation, string? Destination, List<string> Dependencies, List<string> UnresolvedEdges, Status Visibility);
public record AssetManifest : Contract
{
    public List<ExtractionSource> ExtractionSources { get; init; } = [];
    public List<AssetEntry> Assets { get; init; } = [];
    public Status Closure { get; init; }
}
public record RecordEntry(CanonicalForm Source, string? DestinationPlugin, uint? DestinationLocalId,
    string Transformation, List<string> UnresolvedEdges);
public record RecordMap : Contract
{
    public List<RecordEntry> Records { get; init; } = [];
    public Status Allocation { get; init; }
    public string AllocationRule { get; init; } = "Skyrim 1.5.97: new local IDs 0x800..0xFFF; overflow fails; allocation unavailable in T03";
}
public record Variant(string Name, Status BuildStatus, List<string> OutputRequirements, bool RequiresOBodyAdapter,
    List<string> RequiredAssets, Status MigrationValidation, string MigrationRule)
{
    public string GeometrySourcePolicy { get; init; } = "unspecified-historical";
    public bool AllowsRuntimeMorphFallback { get; init; }
}
public record VariantManifest : Contract
{
    public required string PluginIdentity { get; init; }
    public bool MutuallyExclusive { get; init; } = true;
    public List<Variant> Variants { get; init; } = [];
    public BodyBuildSource? PrebuiltSource { get; init; }
    public BodyEnvironment? BodyEnvironment { get; init; }
}
public record ValidationReport : Contract
{
    public string Assessment { get; init; } = "Incomplete capture assessment";
    public Status FullCapture { get; init; }
    public Status SourcePreservation { get; init; }
    public Status CompleteFollower { get; init; }
    public List<Coverage> Checks { get; init; } = [];
}
public record JobEnvelope : Contract
{
    public required string Owner { get; init; }
    public required string Directory { get; init; }
    public Guid? RetryOf { get; init; }
    public Status Status { get; init; }
    public required string InputFingerprint { get; init; }
}
public record Bundle(EnvironmentSnapshot Environment, CharacterCapture Capture, AssetManifest Assets,
    RecordMap Records, VariantManifest Variants, ValidationReport Validation);

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) where T : Contract
    {
        var item = JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Null contract");
        if (item.SchemaVersion != 1) throw new InvalidDataException("Unsupported contract schema version");
        if (item.FollowerId == Guid.Empty || item.AttemptId == Guid.Empty) throw new InvalidDataException("Missing identity");
        return item;
    }
}
