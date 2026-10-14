using AutoFollower;
using System.Diagnostics;

namespace AutoFollower.Gui;

internal sealed record Mo2SelectionSearch(ActiveMo2[] Matches, string[] Rejections, int Candidates)
{
    internal string Recovery => Matches.Length > 1
        ? "More than one MO2 configuration matches the live profile. Choose MO2 instance… to select the launched instance's ModOrganizer.ini."
        : Candidates == 0
            ? "No MO2 instance configuration was found. Choose MO2 instance… and select the launched instance's ModOrganizer.ini."
            : "MO2 configurations were found, but none match this launch. " + string.Join(" ", Rejections.Distinct().Take(3)) + " Choose MO2 instance… to select the launched instance's ModOrganizer.ini.";
}

// Module/process identity admits MO2; final plugin-order and effective INI
// handles independently establish the launched profile, even without local INIs.
internal sealed record ActiveMo2(string Executable, string InstanceIni, string Profile, string Game, string BaseDirectory, string ProfileDirectory, string IniBacking, bool LocalInis, string ConfigurationHash, string SettingsHash)
{
    public static string? LaunchIni { get; set; }
    public static string? LaunchExecutable { get; set; }
    public static ActiveMo2? TryRead(out string status)
    {
        try
        {
            string executable = LiveExecutable();
            if (LaunchExecutable != null && !Path.GetFullPath(LaunchExecutable).Equals(executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected MO2 executable differs from this launch. Relaunch this app from the intended MO2 instance.");
            var evidence = Mo2ProfileEvidence.Read();
            ActiveMo2 active;
            if (string.IsNullOrWhiteSpace(LaunchIni))
            {
                var search = SearchSelections(Candidates(executable), executable, evidence);
                if (search.Matches.Length != 1) throw new InvalidDataException(search.Recovery);
                active = search.Matches[0];
            }
            else { active = ReadSelection(LaunchIni, executable); evidence.Require(active.ProfileDirectory, active.LocalInis); }
            active = active with { IniBacking = evidence.IniBacking };
            status = $"MO2 · {Path.GetFileName(Path.GetDirectoryName(active.InstanceIni))} · {active.Profile}";
            return active;
        }
        catch (Exception e) when (SelectionError(e) || e is InvalidOperationException)
        { status = SafeReason(e); return null; }
    }

    private static string LiveExecutable()
    {
        using var process = Process.GetCurrentProcess();
        string? module = process.Modules.Cast<ProcessModule>().SingleOrDefault(x => x.ModuleName.Equals("usvfs_x64.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        if (module == null) throw new InvalidDataException("MO2 environment is not connected. Add this EXE in MO2’s Executables window, leave Arguments empty, then select it and Run. You can choose your files here first.");
        string executable = Path.Combine(Path.GetDirectoryName(module)!, "ModOrganizer.exe");
        LeanMo2Sources.RequireContext(executable);
        return Path.GetFullPath(executable);
    }

    internal static IEnumerable<string> Candidates(string executable)
    {
        string adjacent = Path.Combine(Path.GetDirectoryName(executable)!, "ModOrganizer.ini");
        if (File.Exists(adjacent)) yield return adjacent;
        string instances = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (Directory.Exists(instances))
            foreach (string directory in Directory.EnumerateDirectories(instances).Order(StringComparer.OrdinalIgnoreCase).Take(128))
            { string ini = Path.Combine(directory, "ModOrganizer.ini"); if (File.Exists(ini)) yield return ini; }
    }

    internal static Mo2SelectionSearch SearchSelections(IEnumerable<string> candidates, string executable, Mo2ProfileEvidence evidence)
    {
        List<ActiveMo2> matches = []; List<string> rejected = []; int count = 0;
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            count++;
            try { var selection = ReadSelection(candidate, executable); evidence.Require(selection.ProfileDirectory, selection.LocalInis); matches.Add(selection); }
            catch (Exception e) when (SelectionError(e)) { rejected.Add(SafeReason(e)); }
        }
        return new(matches.ToArray(), rejected.ToArray(), count);
    }

    internal static bool SelectionError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or HostProtectionException;
    internal static string SafeReason(Exception e) => e switch
    {
        InvalidDataException => e.Message,
        UnauthorizedAccessException => "An MO2 configuration or profile cannot be read with this app's permissions.",
        HostProtectionException => "An MO2 configuration or profile uses a linked path that this app cannot safely admit. Use the original non-linked instance paths.",
        InvalidOperationException => "MO2's live process identity could not be confirmed. Relaunch this app from the intended MO2 instance.",
        System.Text.DecoderFallbackException => "An MO2 byte setting is not valid UTF-8.",
        ArgumentException => "An MO2 setting contains an invalid path.",
        _ => "An MO2 configuration, Skyrim installation or profile file is unavailable."
    };

    internal static ActiveMo2 ReadSelection(string iniPath, string? selectedExecutable = null)
    {
        iniPath = Path.GetFullPath(iniPath); SafePaths.NoLinks(iniPath);
        string text = File.ReadAllText(iniPath); var values = Mo2Configuration.Read(text, iniPath);
        string Required(string key) => values.GetValueOrDefault(key) is { Length: > 0 } value ? value : throw new InvalidDataException("Selected MO2 setting missing: " + key);
        string executable = Path.GetFullPath(selectedExecutable ?? LaunchExecutable ?? throw new InvalidDataException("The launched MO2 executable is unavailable."));
        SafePaths.NoLinks(executable);
        if (!File.Exists(executable)) throw new InvalidDataException("The launched MO2 executable is unavailable.");
        string profile = Required("General/selected_profile");
        if (profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile is "." or "..") throw new InvalidDataException("Selected MO2 profile name is invalid.");
        string game = Required("General/gamePath"), baseDirectory = Required("Settings/base_directory");
        string profileDirectory = Path.GetFullPath(Path.Combine(Required("Settings/profiles_directory"), profile));
        SafePaths.NoLinks(profileDirectory); SafePaths.NoLinks(game);
        if (!File.Exists(Path.Combine(game, "SkyrimSE.exe")) || !Directory.Exists(profileDirectory)) throw new InvalidDataException("Selected Skyrim installation or MO2 profile is unavailable.");
        string settingsPath = Path.Combine(profileDirectory, "settings.ini"); SafePaths.NoLinks(settingsPath);
        string? settings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        bool localInis = Mo2Configuration.LocalInis(values, settings);
        return new(executable, iniPath, profile, game, baseDirectory, profileDirectory, "", localInis, BoundedBinary.Digest(System.Text.Encoding.UTF8.GetBytes(text)), settings == null ? "missing" : BoundedBinary.Digest(System.Text.Encoding.UTF8.GetBytes(settings)));
    }

    internal static ActiveMo2? Recover(string iniPath, out string status)
    {
        string? previous = LaunchIni;
        LaunchIni = iniPath;
        var active = TryRead(out status);
        if (active == null) LaunchIni = previous;
        return active;
    }

    public void Revalidate()
    {
        var current = TryRead(out string status);
        if (current != this) throw new InvalidDataException("MO2 instance/profile changed or cannot be confirmed. Relaunch the app. " + status);
    }

    // This parser remains for ordinary game INIs, which are not Qt settings.
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
