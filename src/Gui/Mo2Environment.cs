using AutoFollower;
using System.Diagnostics;

namespace AutoFollower.Gui;

// Explicit supported launch selection plus the live VFS's profile-local INI
// backing establishes the instance. Module location alone does not.
internal sealed record ActiveMo2(string Executable, string InstanceIni, string Profile, string Game, string BaseDirectory, string ProfileDirectory, string IniBacking)
{
    public static string? LaunchIni { get; set; }
    public static string? LaunchExecutable { get; set; }
    public static ActiveMo2? TryRead(out string status)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LaunchIni)) Discover();
            var active = ReadSelection(LaunchIni!);
            LeanMo2Sources.RequireContext(active.Executable);
            string query = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition", "Skyrim.ini");
            using var file = new FileStream(query, FileMode.Open, FileAccess.Read, FileShare.Read);
            string backing = LeanMo2Sources.Backing(file);
            RequireBacking(active.ProfileDirectory, backing);
            active = active with { IniBacking = backing };
            status = $"MO2 · {Path.GetFileName(Path.GetDirectoryName(active.InstanceIni))} · {active.Profile}";
            return active;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { status = e.Message; return null; }
    }

    private static void Discover()
    {
        using var process = Process.GetCurrentProcess();
        string? module = process.Modules.Cast<ProcessModule>().SingleOrDefault(x => x.ModuleName.Equals("usvfs_x64.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        if (module == null) throw new InvalidDataException("MO2 environment is not connected. Add this EXE in MO2’s Executables window, leave Arguments empty, then select it and Run. You can choose your files here first.");
        string executable = Path.Combine(Path.GetDirectoryName(module)!, "ModOrganizer.exe");
        LeanMo2Sources.RequireContext(executable);
        string query = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition", "Skyrim.ini");
        using var file = new FileStream(query, FileMode.Open, FileAccess.Read, FileShare.Read);
        string backing = LeanMo2Sources.Backing(file);
        string instances = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        var candidates = new List<string> { Path.Combine(Path.GetDirectoryName(executable)!, "ModOrganizer.ini") };
        if (Directory.Exists(instances)) candidates.AddRange(Directory.EnumerateDirectories(instances).Take(128).Select(x => Path.Combine(x, "ModOrganizer.ini")));
        var matches = MatchingSelections(candidates.Where(File.Exists), executable, backing);
        if (matches.Length != 1) throw new InvalidDataException("The live MO2 profile matches " + matches.Length + " instance configurations. Choose the intended instance INI with Confirm MO2; it must match this live profile.");
        LaunchExecutable = executable; LaunchIni = matches[0].InstanceIni;
    }

    internal static ActiveMo2[] MatchingSelections(IEnumerable<string> candidates, string executable, string backing)
    {
        List<ActiveMo2> matches = [];
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { var selection = ReadSelection(candidate, executable); RequireBacking(selection.ProfileDirectory, backing); matches.Add(selection); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        }
        return matches.ToArray();
    }

    internal static ActiveMo2 ReadSelection(string iniPath, string? selectedExecutable = null)
    {
        iniPath = Path.GetFullPath(iniPath); SafePaths.NoLinks(iniPath);
        var values = ParseIni(File.ReadAllText(iniPath));
        string Required(string key) => values.GetValueOrDefault(key) is { Length: > 0 } value ? value : throw new InvalidDataException("Selected MO2 setting missing: " + key);
        string executable = Path.GetFullPath(selectedExecutable ?? LaunchExecutable ?? throw new InvalidDataException("Choose the existing MO2 executable."));
        SafePaths.NoLinks(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Selected MO2 executable is unavailable.", executable);
        string profile = Required("General/selected_profile");
        if (Path.GetFileName(profile) != profile || profile is "." or "..") throw new InvalidDataException("Selected MO2 profile name is invalid.");
        string game = Path.GetFullPath(Required("General/gamePath"));
        string baseDirectory = Path.GetFullPath(Required("Settings/base_directory"));
        string profiles = (values.GetValueOrDefault("Settings/profiles_directory") ?? Path.Combine(baseDirectory, "profiles")).Replace("%BASE_DIR%", baseDirectory, StringComparison.OrdinalIgnoreCase);
        string profileDirectory = Path.GetFullPath(Path.Combine(profiles, profile));
        if (!File.Exists(Path.Combine(game, "SkyrimSE.exe")) || !Directory.Exists(profileDirectory)) throw new InvalidDataException("Selected Skyrim instance or profile is unavailable.");
        var settings = ParseIni(File.ReadAllText(Path.Combine(profileDirectory, "settings.ini")));
        if (settings.GetValueOrDefault("General/LocalSettings") != "true") throw new InvalidDataException("This build route requires selected MO2 profile-local INIs.");
        return new(executable, iniPath, profile, game, baseDirectory, profileDirectory, "");
    }

    internal static void RequireBacking(string profileDirectory, string actual)
    {
        if (!Path.GetFullPath(actual).Equals(Path.Combine(profileDirectory, "Skyrim.ini"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The launched MO2 profile does not match the selected instance. Close this app and relaunch from the intended instance.");
    }

    public void Revalidate()
    {
        var current = TryRead(out string status);
        if (current != this) throw new InvalidDataException("MO2 instance/profile changed or cannot be confirmed. Relaunch the app. " + status);
    }

    internal static Dictionary<string, string> ParseIni(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); string section = "";
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int eq = line.IndexOf('='); if (eq < 1) continue;
            string value = line[(eq + 1)..].Trim();
            if (value.StartsWith("@ByteArray(") && value.EndsWith(')')) value = value[11..^1];
            values[section + "/" + line[..eq].Trim()] = value.Replace("\\\\", "\\");
        }
        return values;
    }
}
