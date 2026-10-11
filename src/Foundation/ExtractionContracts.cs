using System.Text.Json;
using System.Text.Json.Serialization;
namespace AutoFollower;

public enum OBodyState { Unknown, Absent, Inactive, Active }
public enum MeshApplication { Unknown, Unaffected, Applied }
public enum GeometryRoute { Unresolved, AsIs, XmlAndZeroed }
public enum MeshCallState { Unknown, NoMesh, CallsMesh }
public enum SourceKind { Unresolved, Loose, ArchiveMember, Generated }

public record MeshRouting(string Mesh, MeshApplication Application, GeometryRoute Route,
    List<EvidenceRef> Evidence, List<string> RequiredInputs, string Limits)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public record EquipmentInclusion(string Item, string ActorContext, MeshCallState Calls,
    List<string> Meshes, bool Included, Status Status, List<EvidenceRef> Evidence, string Reason);
public record ExtractionSource(string AssetReference, SourceKind Kind, Status Status,
    string? Provider, string? OriginalPath, string? ArchiveMember, string? PrivateCapturePath,
    string? ExtractionDestination, List<EvidenceRef> Evidence, string Basis, List<string> Unresolved)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
public record ExtractionMap : Contract
{
    public required string SelectedEnvironmentFingerprint { get; init; }
    public required OBodyState OBody { get; init; }
    public List<ExtractionSource> Sources { get; init; } = [];
    public List<EquipmentInclusion> Equipment { get; init; } = [];
    public List<MeshRouting> Meshes { get; init; } = [];
    public List<string> Unresolved { get; init; } = [];
}

public static class ExtractionPolicy
{
    public static void ValidateMap(ExtractionMap map)
    {
        if (string.IsNullOrWhiteSpace(map.SelectedEnvironmentFingerprint) || map.Evidence.Count == 0 || map.Sources.Count == 0)
            throw new InvalidDataException("Missing extraction identity/evidence/sources");
        VerifyEvidence(map.Evidence);
        if (map.Sources.Select(s => s.AssetReference).Distinct(StringComparer.OrdinalIgnoreCase).Count() != map.Sources.Count)
            throw new InvalidDataException("Duplicate extraction reference");
        foreach (var s in map.Sources)
        {
            VerifyEvidence(s.Evidence);
            if (s.Status == Status.Observed)
            {
                if (s.Evidence.Count == 0 || string.IsNullOrWhiteSpace(s.Provider) || string.IsNullOrWhiteSpace(s.Basis) || s.Unresolved.Count != 0)
                    throw new InvalidDataException("Observed source lacks evidence/provider or still has unresolved edges");
                if (s.Kind == SourceKind.Generated)
                {
                    if (s.OriginalPath != null || s.ArchiveMember != null || s.PrivateCapturePath == null || !s.Evidence.Any(e => e.Path == s.PrivateCapturePath))
                        throw new InvalidDataException("Generated export needs explicit artifact provenance");
                }
                else if (s.Kind is SourceKind.Loose or SourceKind.ArchiveMember)
                {
                    if (s.OriginalPath == null || !Path.IsPathFullyQualified(s.OriginalPath) || !File.Exists(s.OriginalPath) || (s.Kind == SourceKind.ArchiveMember) != (s.ArchiveMember != null))
                        throw new InvalidDataException("Observed extraction source needs absolute backing path and correct member kind");
                    if (s.ArchiveMember != null) VirtualPath.Normalize(s.ArchiveMember);
                }
                else throw new InvalidDataException("Unresolved source cannot be Observed");
            }
            else if (s.Unresolved.Count == 0 || s.OriginalPath != null || s.Provider != null)
                throw new InvalidDataException("Unresolved source must retain a gap without inventing a winner");
        }
        foreach (var e in map.Equipment)
        {
            var admitted = Equipment(e.Item, e.ActorContext, e.Calls, e.Meshes, e.Evidence);
            if (admitted.Included != e.Included || admitted.Status != e.Status) throw new InvalidDataException("Equipment inclusion disagrees with model-call evidence");
        }
        foreach (var mesh in map.Meshes)
            if (Route(mesh.Mesh, map.OBody, mesh.Application, mesh.Evidence).Route != mesh.Route) throw new InvalidDataException("Geometry route contradicts applicability");
    }
    public static void VerifyEvidence(IEnumerable<EvidenceRef> evidence)
    {
        foreach (var e in evidence)
        {
            SafePaths.NoLinks(e.Path);
            if (!Path.IsPathFullyQualified(e.Path) || e.Bytes <= 0 || new FileInfo(e.Path).Length != e.Bytes || Hashing.File(e.Path) != e.Sha256)
                throw new InvalidDataException("Extraction evidence changed or is empty: " + e.Path);
        }
    }
    public static MeshRouting Route(string mesh, OBodyState state, MeshApplication application, List<EvidenceRef> evidence)
    {
        mesh = VirtualPath.Normalize(mesh);
        VerifyEvidence(evidence);
        if (state == OBodyState.Unknown || evidence.Count == 0 || state == OBodyState.Active && application == MeshApplication.Unknown)
            return new(mesh, MeshApplication.Unknown, GeometryRoute.Unresolved, evidence, [], "Actual OBody application evidence is required; preset/project/TRI presence alone is insufficient");
        if (state is OBodyState.Absent or OBodyState.Inactive)
        {
            if (application == MeshApplication.Applied) throw new InvalidDataException("Applied mesh conflicts with absent/inactive OBody");
            application = MeshApplication.Unaffected;
        }
        return application == MeshApplication.Applied
            ? new(mesh, application, GeometryRoute.XmlAndZeroed, evidence, ["Actual assigned XML with all values", "Compatible project/shape/diff inputs", "Actual captured winning installed body/outfit meshes with compatible captured TRIs"], "XML-driven prebuilt only; OBody copies captured installed body/outfits with private-reference edits; installed OBody applies the actual preset")
            : new(mesh, application, GeometryRoute.AsIs, evidence, ["Winning extracted mesh and packaging dependencies"], "Preserve geometry; no preset or zeroed reconstruction; private reference rewrites permitted");
    }
    public static EquipmentInclusion Equipment(string item, string context, MeshCallState calls, List<string> meshes, List<EvidenceRef> evidence)
    {
        VerifyEvidence(evidence);
        var paths = meshes.Select(VirtualPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(context) || evidence.Count == 0 || calls == MeshCallState.Unknown)
            return new(item, context, MeshCallState.Unknown, paths, false, Status.Unknown, evidence, "Applicable record/addon/model evidence incomplete; exclusion not established");
        if ((calls == MeshCallState.NoMesh) != (paths.Count == 0)) throw new InvalidDataException("Mesh-call state disagrees with applicable model paths");
        return new(item, context, calls, paths, calls == MeshCallState.CallsMesh, Status.Observed, evidence,
            calls == MeshCallState.NoMesh ? "Excluded: applicable records call no mesh" : "Included: applicable records call a mesh; unresolved source remains a missing asset, never a meshless exclusion");
    }
    public static List<Variant> Variants(OBodyState state)
    {
        if (state is OBodyState.Absent or OBodyState.Inactive)
            return [new("as-is", Status.Unavailable, [], false, ["Extracted meshes and dependencies"], Status.Unavailable, "No manufactured OBody migration") { GeometrySourcePolicy = "extracted-as-is/v1" }];
        if (state == OBodyState.Unknown) return [];
        return [new("prebuilt", Status.Unavailable, ["No OBody runtime dependency"], false, ["XML/build inputs for applied meshes only; other meshes as-is"], Status.Unavailable, "T06 applicable migration validation") { GeometrySourcePolicy = "per-mesh-xml-or-as-is/v1" },
            new("obody", Status.Unavailable, ["Installed OBody for applicable targets; no PDA/custom runtime dependency"], false, ["Actual preset and captured winning installed body/outfit meshes with compatible captured TRIs; other meshes as-is"], Status.Unavailable, "Assignment/reset uses installed OBody; optional migration remains unverified") { GeometrySourcePolicy = "captured-installed-or-as-is/v2" }];
    }
}
