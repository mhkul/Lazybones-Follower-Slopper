using System.Diagnostics;
using System.Text;

namespace AutoFollower;

public sealed record PhysicalConfiguration(string IniDirectory, string PluginDirectory);
public static class PhysicalEnvironment
{
    public static PhysicalConfiguration Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition"));
    public static void RequireDirect()
    {
        if (LeanMo2Sources.HasContext) throw new InvalidDataException("Physical Data cannot be selected inside a virtualized MO2 process. Relaunch directly for physical/deployed files.");
    }
    public static SelectedEnvironment Read(string game, PhysicalConfiguration config, LeanMo2Sources sources, string[]? savedPlugins = null, string store = "unspecified")
    {
        RequireDirect(); game = Path.GetFullPath(game);
        SafePaths.NoLinks(game); SafePaths.NoLinks(config.IniDirectory); SafePaths.NoLinks(config.PluginDirectory);
        if (!File.Exists(Path.Combine(game, "SkyrimSE.exe")) || !Directory.Exists(Path.Combine(game, "Data"))) throw new InvalidDataException("Choose the Skyrim folder containing SkyrimSE.exe and Data.");
        if (File.Exists(Path.Combine(game, "Skyrim.ini"))) throw new InvalidDataException("Game-directory Skyrim.ini override remains unsupported. Select an installation with unambiguous effective Documents INIs.");
        List<EnvironmentFilePin> pins = [];
        byte[] Read(string path) { pins.Add(SelectedEnvironment.Pin(sources, path)); return sources.Read(path); }
        string[] Lines(string path) => Encoding.UTF8.GetString(Read(path)).TrimStart('\uFEFF').Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#') && !x.StartsWith(';')).ToArray();
        string pluginsPath = Path.Combine(config.PluginDirectory, "plugins.txt");
        var lines = Lines(pluginsPath);
        if (lines.Any(x => x.TrimStart('*').IndexOfAny(['/', '\\', ':']) >= 0 || !new[]{".esm",".esp",".esl"}.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))) throw new InvalidDataException("Physical plugin list contains an invalid plugin name.");
        string[] masterNames=["Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm"];
        string[] listed=lines.Select(x=>x.StartsWith('*')?x[1..]:x).ToArray();
        if(listed.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=listed.Length)throw new InvalidDataException("Physical plugin list contains duplicate names.");
        var active=lines.Where(x=>x.StartsWith('*')).Select(x=>x[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] implicitMasters=masterNames.Where(x=>File.Exists(sources.Virtual(x))).ToArray();active.UnionWith(implicitMasters);
        foreach(string name in masterNames.Except(implicitMasters))pins.Add(SelectedEnvironment.Pin(sources,sources.Virtual(name)));
        string ccc=Path.Combine(game,"Skyrim.ccc");string[] ccNames=File.Exists(ccc)?Lines(ccc):[];if(!File.Exists(ccc))pins.Add(SelectedEnvironment.Pin(sources,ccc));
        if(ccNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ccNames.Length||ccNames.Any(x=>x.IndexOfAny(['/', '\\', ':','*'])>=0||!new[]{".esm",".esp",".esl"}.Contains(Path.GetExtension(x),StringComparer.OrdinalIgnoreCase)))throw new InvalidDataException("Skyrim.ccc contains invalid or duplicate plugin names.");
        // Source-supported implicit + present Creation Club + starred plugins
        // merge, matching Mutagen's selected-file listing providers. Missing CCC
        // entries are not installed plugins. Existing starred plugin order is
        // authoritative; loadorder.txt is a mod-utility list, not engine activation.
        string[] cc=ccNames.Where(x=>File.Exists(sources.Virtual(x))).OrderBy(x=>Path.GetExtension(x).ToLowerInvariant() switch{".esm"=>0,".esl"=>1,_=>2}).ThenBy(x=>Array.FindIndex(listed,n=>n.Equals(x,StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach(string name in ccNames.Except(cc,StringComparer.OrdinalIgnoreCase))pins.Add(SelectedEnvironment.Pin(sources,sources.Virtual(name)));
        active.UnionWith(cc);
        string loadorder=Path.Combine(config.PluginDirectory,"loadorder.txt");pins.Add(SelectedEnvironment.Pin(sources,loadorder));
        string[] order=implicitMasters.Concat(cc).Concat(listed.Where(active.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!active.Contains("Skyrim.esm")) throw new InvalidDataException("Physical Skyrim.esm is missing.");
        var missingSave=(savedPlugins??[]).Where(x=>!active.Contains(x)).ToArray();if(missingSave.Length>0)throw new InvalidDataException("Save plugins absent/inactive in physical Data: "+string.Join(", ",missingSave)+". Deploy these inputs or select a save for this installation.");
        foreach (string plugin in order) if (!File.Exists(sources.Virtual(plugin))) throw new FileNotFoundException("Active physical plugin is missing from selected Data: "+plugin, plugin);
        string query = Path.Combine(config.IniDirectory, "Skyrim.ini");
        Dictionary<string,string> primary = []; string? invalidation = null;
        void PolicyIni(string path, bool first)
        {
            var values = LeanExportBuilder.ParseIni(Encoding.UTF8.GetString(Read(path)));
            string? setting = LeanMo2Sources.EffectiveInvalidation(path);
            if (setting != null && (invalidation == null || setting != "1")) invalidation = setting;
            if (first) primary = values;
            else if (values.Keys.Any(x => x.StartsWith("Archive/sResourceArchiveList",StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Archive-list override outside primary Skyrim.ini remains unsupported: " + path);
        }
        foreach(string name in new[]{"Skyrim.ini","SkyrimPrefs.ini","SkyrimCustom.ini"})
        { string path=Path.Combine(config.IniDirectory,name); if(name=="Skyrim.ini"||File.Exists(path)) PolicyIni(path,name=="Skyrim.ini"); else pins.Add(SelectedEnvironment.Pin(sources,path)); }
        foreach(string plugin in order)
        { string path=sources.Virtual(Path.ChangeExtension(plugin,".ini")); if(File.Exists(path)) PolicyIni(path,false); else pins.Add(SelectedEnvironment.Pin(sources,path)); }
        var lookup = LeanLookupPolicy.Evaluate("physical/deployed",FileVersionInfo.GetVersionInfo(Path.Combine(game,"SkyrimSE.exe")).FileVersion??"",invalidation,query);
        if(!lookup.Supported) throw new InvalidDataException("Physical loose/archive lookup requires enabled/default invalidation; the effective INI override is unsupported.");
        string[] archives = new[]{"Archive/sResourceArchiveList","Archive/sResourceArchiveList2"}.SelectMany(x => (primary.GetValueOrDefault(x)??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).ToArray();
        sources.ConfigureArchives(order,archives,[],lookup);
        var info = new FileInfo(Path.Combine(game,"SkyrimSE.exe"));
        var environment = new SelectedEnvironment("Physical",SkyrimRuntimeProfile.ForInputs(store,FileVersionInfo.GetVersionInfo(info.FullName).FileVersion??""),game,Path.Combine(game,"Data"),"","",config.IniDirectory,query,order,"Physical/deployed read-only final backing handles; explicit effective INIs, implicit/present CCC and starred plugin order; shared proven archive policy",pins.ToArray(),info.FullName,info.Length,info.LastWriteTimeUtc.Ticks);
        sources.Bind(environment); environment.RequireCurrent(sources); return environment;
    }
}
