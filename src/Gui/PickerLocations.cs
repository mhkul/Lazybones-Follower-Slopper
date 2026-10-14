using AutoFollower;

namespace AutoFollower.Gui;

internal static class PickerLocations
{
    internal static string Default(string key, InstalledSetup? setup)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string saves = Path.Combine(documents, "My Games", "Skyrim Special Edition", "Saves");
        var mo2=setup?.Mo2;
        if (setup != null)
        {
            setup.Revalidate();
            if (key is "Save" or "Cosave")
            {
                if(mo2!=null){string path=Path.Combine(mo2.ProfileDirectory,"settings.ini");var settings=File.Exists(path)?Mo2Configuration.Parse(File.ReadAllText(path)):new Dictionary<string,string>();if(settings.GetValueOrDefault("General/LocalSaves")=="true")return Existing(Path.Combine(mo2.ProfileDirectory,"saves"),documents);}
                string query=mo2?.IniBacking??Path.Combine(setup.Physical!.IniDirectory,"Skyrim.ini");var ini=ActiveMo2.ParseIni(File.ReadAllText(query));string configured=ini.GetValueOrDefault("General/SLocalSavePath")??"Saves";
                string basePath=mo2==null?setup.Physical!.IniDirectory:Path.Combine(documents,"My Games","Skyrim Special Edition");saves=Path.GetFullPath(Path.IsPathRooted(configured)?configured:Path.Combine(basePath,configured));
            }
            else
            {
                string relative = Path.Combine("SKSE", "Plugins", "CharGen", key == "Jslot" ? "Presets" : "");
                string visible=Path.Combine(setup.Game,"Data",relative);return Nearest(visible,Path.Combine(setup.Game,"Data"));
            }
        }
        return Existing(key is "Save" or "Cosave" ? saves : documents, documents);
    }
    private static string Nearest(string desired,string fallback){for(string? p=desired;p!=null;p=Path.GetDirectoryName(p))if(Directory.Exists(p))return p;return fallback;}
    private static string Existing(string desired, string fallback) => Directory.Exists(desired) ? desired : fallback;
}
