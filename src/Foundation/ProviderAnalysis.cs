using System.Text.Json;
namespace AutoFollower;

// Offline successor analysis. Never edits or upgrades a frozen observation.
public static class ProviderAnalysis
{
    public static void Run(string selectionPath, string referencesPath, string output)
    {
        selectionPath = Path.GetFullPath(selectionPath); referencesPath = Path.GetFullPath(referencesPath); output = Path.GetFullPath(output);
        SafePaths.NoLinks(output);
        if (Directory.Exists(output)) throw new InvalidDataException("Provider analysis requires a new output directory");
        var selection = JsonSerializer.Deserialize<ResolveRequest>(File.ReadAllText(selectionPath), Wire.Options) ?? throw new InvalidDataException("Missing selection");
        var requested = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(referencesPath), Wire.Options) ?? throw new InvalidDataException("Missing asset references");
        if (requested.Count is 0 or > 1024) throw new InvalidDataException("Reference bound");
        var pins = ResolutionRunner.Fingerprints(selectionPath, selection);
        pins.Add(new(referencesPath, Hashing.File(referencesPath), new FileInfo(referencesPath).Length, "Required references"));
        Directory.CreateDirectory(output);
        var stager = new InputStager(Path.Combine(output, "inputs"), CancellationToken.None);
        var resolver = new EnvironmentResolver(stager, CancellationToken.None);
        try
        {
            var table = resolver.Discover(selection); List<object> resources = [];
            foreach (var path in requested.Select(VirtualPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("Resolving " + path);
                var candidates = resolver.StreamCandidates(path, output);
                resources.Add(new { path, candidates = candidates.Candidates, errors = candidates.Errors });
            }
            resolver.VerifyHeaders(); stager.Verify();
            // Empty configuration files are valid environment inputs, not
            // positive application observations.
            foreach (var pin in pins) if (new FileInfo(pin.Path).Length != pin.Bytes || Hashing.File(pin.Path) != pin.Sha256) throw new InvalidDataException("Selected environment changed: " + pin.Path);
            File.WriteAllText(Path.Combine(output, "providers.json"), Wire.Write(new { selection = pins, selectedEnvironmentFingerprint = Hashing.Inputs(pins), current = table, tools = resolver.Tools, archives = resolver.Archives, resources,
                limits = "Candidate inventory only. Source winners require provider/selected-environment evidence. Byte matches alone do not establish winners." }));
        }
        finally { resolver.Close(); }
    }
}
