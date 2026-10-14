using System.Text;

namespace AutoFollower;

// MO2 PathSettings defaults to the directory containing its instance INI.
// Decode Qt scalars here; game INIs retain their separate existing parser.
public static class Mo2Configuration
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "General";
        foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            string line = WithoutComment(raw).Trim();
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                string key = section + "/" + line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                bool scalar = new[] { "General/selected_profile", "General/gamePath", "Settings/base_directory", "Settings/profiles_directory", "Settings/mod_directory", "Settings/overwrite_directory", "Settings/profile_local_inis", "General/LocalSettings", "General/LocalSaves" }.Contains(key, StringComparer.OrdinalIgnoreCase);
                values[key] = scalar ? Decode(value) : value;
            }
        }
        return values;
    }

    // Qt readIniLine recognizes semicolon comments before decoding scalars.
    // A backslash consumes the following character; only unescaped quotes
    // toggle quoting. Numeric escapes are left intact for Decode.
    private static string WithoutComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\') { if (i + 1 < line.Length) i++; continue; }
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) return line[..i];
        }
        return line;
    }

    private static string Decode(string value)
    {
        var decoded = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) throw new InvalidDataException("MO2 directory/profile setting is a Qt list rather than a scalar.");
            if (c != '\\') { decoded.Append(c); continue; }
            if (++i == value.Length) throw new InvalidDataException("MO2 setting has an incomplete Qt escape.");
            c = value[i];
            if (c == 'x' || c is >= '0' and <= '7')
            {
                int radix = c == 'x' ? 16 : 8, n = 0, digits = 0;
                if (radix == 8) i--;
                while (i + 1 < value.Length)
                {
                    int digit = "0123456789abcdef".IndexOf(char.ToLowerInvariant(value[i + 1]));
                    if (digit < 0 || digit >= radix) break;
                    n = (n * radix + digit) & 0xffff; i++; digits++;
                }
                if (digits == 0) throw new InvalidDataException("MO2 setting has an invalid Qt numeric escape.");
                decoded.Append((char)n);
            }
            else decoded.Append(c switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', '\\' => '\\', '"' => '"', '?' => '?', '\'' => '\'', _ => throw new InvalidDataException("MO2 setting has an unsupported Qt escape.") });
        }
        if (quoted) throw new InvalidDataException("MO2 setting has an unclosed quote.");
        string result = decoded.ToString();
        if (result.StartsWith("@ByteArray(", StringComparison.Ordinal) && result.EndsWith(')'))
        {
            result = result[11..^1];
            if (result.Any(c => c > 255)) throw new InvalidDataException("MO2 byte setting has an invalid encoded character.");
            result = new UTF8Encoding(false, true).GetString(result.Select(c => (byte)c).ToArray());
        }
        else if (result.StartsWith("@@", StringComparison.Ordinal)) result = result[1..];
        return result;
    }

    public static Dictionary<string, string> Read(string text, string instanceIni)
        => Normalize(Parse(text), instanceIni);

    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string> settings, string instanceIni)
    {
        var values = new Dictionary<string,string>(settings, StringComparer.OrdinalIgnoreCase);
        string iniDirectory = Path.GetDirectoryName(Path.GetFullPath(instanceIni))!;
        string Absolute(string value)
        {
            // MO2 resolves relative paths against its own working directory,
            // which need not be the launched tool's. Do not guess that directory.
            if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("MO2 has a relative directory setting. Use an absolute directory in MO2 Settings > Paths, then relaunch this app.");
            return Path.GetFullPath(value);
        }
        string baseDir = Absolute(values.GetValueOrDefault("Settings/base_directory") ?? iniDirectory);
        values["Settings/base_directory"] = baseDir;
        foreach (var (key, fallback) in new[] { ("profiles_directory", "profiles"), ("mod_directory", "mods"), ("overwrite_directory", "overwrite") })
        {
            string path = values.GetValueOrDefault("Settings/" + key) ?? "%BASE_DIR%/" + fallback;
            values["Settings/" + key] = Absolute(path.Replace("%BASE_DIR%", baseDir, StringComparison.Ordinal));
        }
        if (values.TryGetValue("General/gamePath", out string? game)) values["General/gamePath"] = Absolute(game);
        return values;
    }

    public static bool LocalInis(IReadOnlyDictionary<string, string> instance, string? settingsText)
    {
        var settings = settingsText == null ? new Dictionary<string, string>() : Parse(settingsText);
        string value = settings.GetValueOrDefault("General/LocalSettings") ?? instance.GetValueOrDefault("Settings/profile_local_inis") ?? "true";
        return value.ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => throw new InvalidDataException("MO2 profile-local INI setting is invalid.") };
    }
}

public sealed record Mo2ProfileEvidence(string IniBacking, string PluginsBacking, string OrderBacking)
{
    public static string IniQuery => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition", "Skyrim.ini");
    public static string PluginQuery(string file) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", file);
    public static Mo2ProfileEvidence Read()
    {
        string Backing(string path)
        {
            try { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); string backing = LeanMo2Sources.Backing(file); SafePaths.NoLinks(backing); return backing; }
            catch (IOException e) { throw new InvalidDataException("The live MO2 Skyrim INI or plugin-order mapping is unavailable. Run this app from the Skyrim Special Edition instance with the intended profile selected.", e); }
        }
        return new(Backing(IniQuery), Backing(PluginQuery("plugins.txt")), Backing(PluginQuery("loadorder.txt")));
    }
    public void Require(string profileDirectory, bool localInis)
    {
        static bool Same(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        if (!Same(PluginsBacking, Path.Combine(profileDirectory, "plugins.txt")) || !Same(OrderBacking, Path.Combine(profileDirectory, "loadorder.txt")))
            throw new InvalidDataException("MO2's live plugin order belongs to a different profile. Select the launched instance's configuration, or relaunch this app after selecting the intended profile in MO2.");
        string expected = localInis ? Path.Combine(profileDirectory, "Skyrim.ini") : IniQuery;
        if (!Same(IniBacking, expected)) throw new InvalidDataException("MO2's live Skyrim INI does not match the profile's INI mode. Relaunch this app from MO2 after selecting the intended profile.");
    }
}
