using System.Xml;
using System.Xml.Linq;
using System.Diagnostics;
using AutoFollower;

namespace AutoFollower.Gui;

internal sealed record PresetInput(string Path, string Backing, string Provider, string Name, string Sha256, int SearchFiles = 0, long SearchBytes = 0, long SearchMilliseconds = 0);
internal static class BuildInputs
{
    internal static string Writer => ApplicationTools.Resolve().Writer;
    internal static string Stages => ApplicationTools.Resolve().Stages;
    internal static string Python => ApplicationTools.Resolve().Python;
    internal static void Preflight()=>ApplicationTools.Resolve().Require();

    internal static LeanEquipmentChoice[] EquipmentCatalog(InstalledSetup setup,FollowerInputPreview preview,CancellationToken token)
    {
        setup.Revalidate();
        using var sources=new LeanMo2Sources(Path.Combine(setup.Game,"Data"),Path.Combine(Path.GetTempPath(),"unused-"+Guid.NewGuid()),new(),token);
        string[] order;string query;
        if(setup.Physical!=null){var env=PhysicalEnvironment.Read(setup.Game,setup.Physical,sources,store:preview.Selection.Store);order=env.ActivePlugins;query=env.IniQuery;}
        else{
            var mo2=setup.Mo2!;query=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"My Games","Skyrim Special Edition","Skyrim.ini");
            string[] ReadLines(string path)=>File.ReadAllText(path).TrimStart('\uFEFF').Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0&&!x.StartsWith('#')&&!x.StartsWith(';')).ToArray();
            string[] Lines(string name)=>ReadLines(Path.Combine(mo2.ProfileDirectory,name));
            string ccc=Path.Combine(setup.Game,"Skyrim.ccc");
            order=SelectedPluginOrder.Resolve(Lines("plugins.txt"),Lines("loadorder.txt"),File.Exists(ccc)?ReadLines(ccc):[],x=>File.Exists(sources.Virtual(x)),"Selected MO2 profile '"+mo2.ProfileDirectory+"'");
            var ini=ActiveMo2.ParseIni(File.ReadAllText(query));string[] archives=new[]{"Archive/sResourceArchiveList","Archive/sResourceArchiveList2"}.SelectMany(k=>(ini.GetValueOrDefault(k)??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).ToArray();
            sources.ConfigureArchives(order,archives,File.Exists(Path.Combine(mo2.ProfileDirectory,"archives.txt"))?Lines("archives.txt"):[],LeanLookupPolicy.Evaluate("metadata preview","metadata preview",LeanMo2Sources.EffectiveInvalidation(query),query));
        }
        var index=new LeanRecordIndex(sources,token);index.SetLanguage(ActiveMo2.ParseIni(File.ReadAllText(query)).GetValueOrDefault("General/sLanguage")??"english");index.Scan(order);
        var choices=LeanEquipmentChoices.Catalog(preview.SavePreview.SavedState.Actor,index);token.ThrowIfCancellationRequested();setup.Revalidate();return choices;
    }

    internal static PresetInput[] ResolvePresets(InstalledSetup setup, FollowerInputPreview preview, CancellationToken token)
    {
        if (!ObodyAvailable(setup)) return [];
        if(preview.Cosave.Preset==null&&preview.Cosave.ObodyAssignment=="unassigned")return [];
        string? name = preview.Cosave.Preset;if(name==null)return [];
        string root = Path.Combine(setup.Game, "Data", "CalienteTools", "BodySlide", "SliderPresets");
        if (!Directory.Exists(root)) return [];
        var mo2=setup.Mo2;
        var values=mo2==null?new Dictionary<string,string>():Mo2Configuration.Read(File.ReadAllText(mo2.InstanceIni),mo2.InstanceIni);
        string mods = (values.GetValueOrDefault("Settings/mod_directory") ?? Path.Combine((mo2?.BaseDirectory??setup.Game), "mods")).Replace("%BASE_DIR%", (mo2?.BaseDirectory??setup.Game), StringComparison.OrdinalIgnoreCase);
        string overwrite = (values.GetValueOrDefault("Settings/overwrite_directory") ?? Path.Combine((mo2?.BaseDirectory??setup.Game), "overwrite")).Replace("%BASE_DIR%", (mo2?.BaseDirectory??setup.Game), StringComparison.OrdinalIgnoreCase);
        var enabled = (mo2==null?[]:File.ReadAllLines(Path.Combine(mo2!.ProfileDirectory, "modlist.txt"))).Where(x => x.StartsWith('+')).Select(x => x[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<PresetInput> matches = [];
        var clock = Stopwatch.StartNew(); int searchFiles = 0; long searchBytes = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.xml", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("A preset XML exceeds the supported metadata bound: " + Path.GetFileName(path));
            string backing = LeanMo2Sources.Backing(file); SafePaths.NoLinks(backing);
            byte[] raw = new byte[checked((int)file.Length)]; file.ReadExactly(raw);
            searchFiles++; searchBytes += raw.Length;
            try{if (!ContainsPreset(raw, name, Path.GetFileName(path))) continue;}catch(InvalidDataException){continue;}
            string ProviderBelow(string basePath)
            {
                string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath)) + Path.DirectorySeparatorChar;
                return backing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? backing[prefix.Length..].Split(Path.DirectorySeparatorChar)[0] : "";
            }
            string provider = mo2==null?"":ProviderBelow(mods);
            if (provider.Length > 0 && !enabled.Contains(provider)) throw new InvalidDataException("Preset backing provider is not enabled in the selected profile: " + provider);
            if (provider.Length == 0) provider = ProviderBelow(overwrite).Length > 0 ? "MO2 Overwrite" : ProviderBelow(Path.Combine(setup.Game, "Data")).Length > 0 ? "Game Data" : throw new InvalidDataException("Preset backing is outside the selected environment: " + backing);
            matches.Add(new(path, backing, provider, name, BoundedBinary.Digest(raw)));
        }
        // Missing authored XML is recorded by the builder; available installed meshes can still be delivered.

        return matches.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x with { SearchFiles = searchFiles, SearchBytes = searchBytes, SearchMilliseconds = clock.ElapsedMilliseconds }).ToArray();
    }

    internal static bool ContainsPreset(byte[] raw, string name, string label)
    {
        if (raw.Length > 4 * 1024 * 1024) throw new InvalidDataException("Preset XML exceeds the metadata bound: " + label);
        using var input = new MemoryStream(raw, false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        XDocument xml;
        try { xml = XDocument.Load(reader); }
        catch (XmlException e) { throw new InvalidDataException("Installed preset XML is malformed: " + label, e); }
        int matches = xml.Descendants("Preset").Count(x => (string?)x.Attribute("name") == name);
        if (matches > 1) throw new InvalidDataException("The assigned preset occurs more than once in " + label);
        return matches == 1;
    }

    internal static bool ObodyAvailable(InstalledSetup setup)
    {
        setup.Revalidate();
        string path = Path.Combine(setup.Game, "Data", "SKSE", "Plugins", "OBody.dll");
        if (!File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string backing = LeanMo2Sources.Backing(file); SafePaths.NoLinks(backing);
        if(setup.Physical!=null){if(!backing.StartsWith(Path.TrimEndingDirectorySeparator(Path.Combine(setup.Game,"Data"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Physical OBody backing is outside selected Data.");return true;}
        var mo2=setup.Mo2;
        var values=mo2==null?new Dictionary<string,string>():Mo2Configuration.Read(File.ReadAllText(mo2.InstanceIni),mo2.InstanceIni);
        string Expand(string key, string fallback) => Path.GetFullPath((values.GetValueOrDefault(key) ?? Path.Combine((mo2?.BaseDirectory??setup.Game), fallback)).Replace("%BASE_DIR%", (mo2?.BaseDirectory??setup.Game), StringComparison.OrdinalIgnoreCase));
        string mods = Path.TrimEndingDirectorySeparator(Expand("Settings/mod_directory", "mods")) + Path.DirectorySeparatorChar;
        if (backing.StartsWith(mods, StringComparison.OrdinalIgnoreCase))
        {
            string provider = backing[mods.Length..].Split(Path.DirectorySeparatorChar)[0];
            if (!File.ReadAllLines(Path.Combine(mo2!.ProfileDirectory, "modlist.txt")).Any(x => x.Equals("+" + provider, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("OBody backing provider is not enabled in the selected MO2 profile.");
        }
        else if (!new[] { Expand("Settings/overwrite_directory", "overwrite"), Path.Combine(setup.Game, "Data") }.Any(root => backing.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("OBody backing is outside the selected MO2 environment.");
        return true;
    }
}
