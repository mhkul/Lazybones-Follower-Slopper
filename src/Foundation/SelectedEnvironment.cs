using System.Diagnostics;

namespace AutoFollower;

// Executable/producer metadata is informational. Required binary structures
// are checked independently; no unknown runtime is mapped to an older game.
public sealed record SkyrimInputFormats(uint EssVersion,byte FormVersion,uint CosaveFormat,uint SkseVersion,uint CosaveRuntime,uint SkeeNodeVersion=2)
{
    public static SkyrimInputFormats Resolve(SkyrimInputFormats? formats)
    {
        formats??=Common;
        if(formats.EssVersion!=12||formats.FormVersion!=78||formats.CosaveFormat!=1)
            throw new InvalidDataException("Required reader layout is ESS12/form78/co-save format1; the requested framing is unsupported.");
        return Common;
    }
    public static readonly SkyrimInputFormats Common=new(12,78,1,0,0,0);
    public void RequireCosave(uint format,uint skse,uint runtime)
    {
        if(format!=CosaveFormat)
            throw new InvalidDataException($"Required co-save outer framing is format {CosaveFormat}; found {format}. SKSE {skse:X8}/runtime {runtime:X8} are informational producer metadata.");
    }
}
public sealed record SkyrimRuntimeProfile(string Store,string Version,SkyrimInputFormats Formats)
{
    public static readonly SkyrimRuntimeProfile Steam1597=new("Steam","1.5.97.0",new(12,78,1,0x02000140,0x01050610));
    // Controlled shared-reader profile only. SKSE2.2.6 and RaceMenu's pinned
    // 1.6.1170 revision establish their producers. ESS12/form78/player versions
    // remain an explicit candidate pending authentic target input validation.
    public static readonly SkyrimRuntimeProfile Steam1170=new("Steam","1.6.1170.0",new(12,78,1,0x02020060,0x01064920,3));
    public static SkyrimRuntimeProfile ForInputs(string store,string version)=>
        store=="Steam"&&version==Steam1597.Version?Steam1597:
        store=="Steam"&&version==Steam1170.Version?Steam1170:new(store,version,SkyrimInputFormats.Common);
    public static SkyrimRuntimeProfile Require(string store,string version)=>ForInputs(store,version);
    public string Notice=>"Observed executable: "+(string.IsNullOrWhiteSpace(Version)?"version metadata unavailable":Version)+"; store label: "+Store+" (declared, not detected). Compatibility testing is welcome; versions do not gate builds. Required formats and selected source checks still apply. Verified complete-build evidence is limited to 1.5.97/MO2 2.5.0; gameplay and other environments remain unverified.";
}
public sealed record EnvironmentFilePin(string Path,string Backing,string Sha256,long Bytes);
// A selected installation's visible Data view. Manager integration establishes
// activation/INI/provider facts; required loose/archive readers stay shared.
// Physical/deployed views use explicit effective configuration and final handles.
public sealed record SelectedDataView(string Directory,string AccessKind,string ProviderAuthority)
{
    public string Resolve(string reference)=>Path.Combine(Directory,VirtualPath.Normalize(reference).Replace('/',Path.DirectorySeparatorChar));
}
public sealed record SelectedEnvironment(string Manager,SkyrimRuntimeProfile Runtime,string Game,string Data,string InstanceIni,string Profile,string ConfigurationDirectory,string IniQuery,string[] ActivePlugins,string SourceAccess,EnvironmentFilePin[] Pins,string Executable,long ExecutableBytes,long ExecutableWriteTime)
{
    public SelectedDataView FileView=>new(Data,"selected-process-file-view",SourceAccess);
    public static string[] ProtectedDirectories(IReadOnlyDictionary<string,string> ini,string game,string instanceIni)
    {
        ini=Mo2Configuration.Normalize(ini,instanceIni);
        string baseDir=ini["Settings/base_directory"];
        string Expand(string key,string fallback)=>Path.GetFullPath((ini.GetValueOrDefault(key)??Path.Combine(baseDir,fallback)).Replace("%BASE_DIR%",baseDir,StringComparison.OrdinalIgnoreCase));
        return [game,baseDir,Path.GetDirectoryName(Path.GetFullPath(instanceIni))!,Expand("Settings/mod_directory","mods"),Expand("Settings/overwrite_directory","overwrite"),Expand("Settings/profiles_directory","profiles")];
    }
    public static EnvironmentFilePin Pin(LeanMo2Sources sources,string path)
    {if(!File.Exists(path))return new(Path.GetFullPath(path),"","",-1);using var file=sources.Open(path);string backing=LeanMo2Sources.Backing(file);return new(Path.GetFullPath(path),backing,BoundedBinary.Digest(sources.At(file,0,checked((int)file.Length))),file.Length);}
    public static SelectedEnvironment Create(string game,string instanceIni,string profile,string config,string query,string[] plugins,string store,EnvironmentFilePin[] pins)
    {
        game=Path.GetFullPath(game);instanceIni=Path.GetFullPath(instanceIni);config=Path.GetFullPath(config);query=Path.GetFullPath(query);
        string exe=Path.Combine(game,"SkyrimSE.exe");var info=new FileInfo(exe);
        return new("MO2",SkyrimRuntimeProfile.Require(store,FileVersionInfo.GetVersionInfo(exe).FileVersion??""),game,Path.Combine(game,"Data"),instanceIni,profile,config,query,plugins,"LeanMo2Sources: live selected USVFS; final read-only backing handles; validated loose/archive lookup policy",pins,exe,info.Length,info.LastWriteTimeUtc.Ticks);
    }
    public void RequireCurrent(LeanMo2Sources sources)
    {
        var exe=new FileInfo(Executable);
        if(exe.Length!=ExecutableBytes||exe.LastWriteTimeUtc.Ticks!=ExecutableWriteTime||(FileVersionInfo.GetVersionInfo(Executable).FileVersion??"")!=Runtime.Version)throw new InvalidDataException("Selected runtime changed during build. Refresh the selected environment.");
        foreach(var pin in Pins)
        {var current=Pin(sources,pin.Path);if(current!=pin)throw new InvalidDataException("Selected environment changed during build: "+pin.Path+". Refresh the selected environment.");}
    }
}
