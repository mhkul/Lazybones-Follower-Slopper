using System.Text.Json;

namespace AutoFollower;

public sealed record ApplicationBuildConfiguration(string? Python=null,string? OutputRoot=null);
public sealed record ApplicationTools(string Python,string Writer,string Stages)
{
    public static ApplicationTools Resolve(string? python=null,string? writer=null,string? stages=null,string? application=null)
    {
        string? bundled=BundledRuntime.Resolve();
        if(bundled!=null)return new(Path.Combine(bundled,"tools/python/python.exe"),Path.Combine(bundled,"writer/PluginWriter.exe"),Path.Combine(bundled,"tools/stages"));
        application=Path.GetFullPath(application??AppContext.BaseDirectory);
        SafePaths.NoLinks(application);
        string configPath=Path.Combine(application,"application.json");
        var config=File.Exists(configPath)?ReadConfiguration(configPath):new ApplicationBuildConfiguration();
        string ResolvePath(string path)=>Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(application,path));
        return new(ResolvePath(string.IsNullOrWhiteSpace(python)?config.Python??"tools/python/python.exe":python),
            ResolvePath(string.IsNullOrWhiteSpace(writer)?"writer/PluginWriter.exe":writer),ResolvePath(stages??"tools/stages"));
    }
    public static ApplicationBuildConfiguration ReadConfiguration(string path)
    {
        SafePaths.NoLinks(path);
        if(new FileInfo(path).Length>16384)throw new InvalidDataException("Application configuration exceeds its bound.");
        return JsonSerializer.Deserialize<ApplicationBuildConfiguration>(File.ReadAllText(path),Wire.Options)??throw new InvalidDataException("Application configuration missing.");
    }
    public void Require()
    {
        foreach(string path in new[]{Python,Writer,Path.Combine(Stages,"lean_head_stage.py"),Path.Combine(Stages,"lean_build_stage.py"),Path.Combine(Stages,"stage-files.json")})
        {SafePaths.NoLinks(path);if(!File.Exists(path))throw new FileNotFoundException("Required application build tool missing. Re-extract the complete application packet.",path);}
        string manifestPath=Path.Combine(Stages,"stage-files.json");
        if(new FileInfo(manifestPath).Length>65536)throw new InvalidDataException("Application stage manifest exceeds its bound.");
        using var manifest=JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var names=manifest.RootElement.EnumerateArray().Select(x=>x.GetProperty("name").GetString()!).ToArray();
        if(names.Length>64||names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length||!names.Contains("lean_head_stage.py")||!names.Contains("lean_build_stage.py"))throw new InvalidDataException("Application stage manifest is incomplete or ambiguous.");
        foreach(var item in manifest.RootElement.EnumerateArray())
        {
            string path=SafePaths.Below(Stages,item.GetProperty("name").GetString()!);
            if(!File.Exists(path)||Hashing.File(path)!=item.GetProperty("sha256").GetString())throw new InvalidDataException("Application stage module missing or changed: "+path);
        }
        var start=new System.Diagnostics.ProcessStartInfo(Python){UseShellExecute=false,CreateNoWindow=true};
        foreach(string arg in new[]{"-I","-B","-c","import sys; sys.path.insert(0, sys.argv[1]); import lean_build_stage, lean_head_stage; sys.exit(0 if sys.version_info >= (3,12) else 1)"})start.ArgumentList.Add(arg);
        start.ArgumentList.Add(Stages);
        using var launch=RuntimeStorage.Launch(start); var process=launch.Process;
        if(!process.WaitForExit(5000)){process.Kill(entireProcessTree:true);process.WaitForExit();throw new InvalidDataException("Bundled Python did not complete its version check.");}
        if(process.ExitCode!=0)throw new InvalidDataException("Application stages require Python 3.12 or newer.");
    }
}

public static class BuildDestinations
{
    public static BuildDestinationProtection Protection(LeanBuildRequest request,ApplicationTools tools,string? application=null)
    {
        string[] inputs=[request.Save,request.Cosave,request.HeadNif,request.TintDds,request.Jslot];
        string[] presets=new[]{request.PresetXml,request.PresetBacking}.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!).ToArray();
        string[] files=inputs.Concat(new[]{request.InstanceIni,request.Mo2,tools.Python,tools.Writer}.Where(x=>!string.IsNullOrWhiteSpace(x))).Concat(presets).ToArray();
        string[] sourceDirectories=request.Physical==null?SelectedEnvironment.ProtectedDirectories(Mo2Configuration.Read(File.ReadAllText(request.InstanceIni),request.InstanceIni),request.Game,request.InstanceIni):[request.Game,request.Physical.IniDirectory,request.Physical.PluginDirectory];
        string[] directories=sourceDirectories
            .Concat(new[]{application??AppContext.BaseDirectory,tools.Stages})
            .Concat(inputs.Concat(presets).Concat(new[]{request.Mo2,tools.Python,tools.Writer}).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>Path.GetDirectoryName(Path.GetFullPath(x))!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(files,directories);
    }
    public static string DefaultRoot(string? application=null)
    {
        application=Path.GetFullPath(application??AppContext.BaseDirectory);
        string config=Path.Combine(application,"application.json");
        string? chosen=File.Exists(config)?ApplicationTools.ReadConfiguration(config).OutputRoot:null;
        return Path.GetFullPath(chosen==null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Lazybones Follower Slopper","Builds"):Path.IsPathRooted(chosen)?chosen:Path.Combine(application,chosen));
    }
    public static void RequireNew(string output,string root,IEnumerable<string> protectedFiles,IEnumerable<string> protectedDirectories)
    {
        output=Path.GetFullPath(output);root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(!string.Equals(Path.GetDirectoryName(output),root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Build destination must be a direct child of the configured output root.");
        SafePaths.NoLinks(output);SafePaths.NoLinks(root);
        if(File.Exists(output)||Directory.Exists(output))throw new InvalidDataException("Output collision: destination exists. Existing output is preserved; choose a new path.");
        RequireOutsideProtected(output,protectedFiles,protectedDirectories);
    }
    public static void RequireRequestPaths(string requestPath,string output,string root,BuildDestinationProtection protection)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));requestPath=Path.GetFullPath(requestPath);
        string directory=Path.Combine(root,"requests");
        if(!string.Equals(Path.GetDirectoryName(requestPath),directory,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Request files must be direct children of the configured output root's requests directory.");
        RequireNew(output,root,protection.Files,protection.Directories);
        if(Path.GetFullPath(output).Equals(directory,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Build destination collides with the requests directory. Choose a new build path.");
        SafePaths.NoLinks(directory);
        if(File.Exists(directory))throw new InvalidDataException("Request directory collides with an existing file. Choose another output root.");
        RequireOutsideProtected(directory,protection.Files,protection.Directories);
        foreach(string path in new[]{requestPath,requestPath+".inputs.json"})
        {
            SafePaths.NoLinks(path);
            if(File.Exists(path)||Directory.Exists(path))throw new InvalidDataException("Request collision: destination exists. Existing files are preserved; start a fresh build.");
            RequireOutsideProtected(path,protection.Files,protection.Directories);
        }
    }
    public static void WriteRequestFiles(string requestPath,string output,string root,BuildDestinationProtection protection,string requestJson,string inputsJson,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireRequestPaths(requestPath,output,root,protection);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(requestPath))!);
        // CreateNew preserves files that appear after validation. A partial owned
        // pair is retained on an I/O failure; retries always use fresh names.
        using var request=new FileStream(requestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var inputs=new FileStream(requestPath+".inputs.json",FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var requestWriter=new StreamWriter(request);
        using var inputsWriter=new StreamWriter(inputs);
        requestWriter.Write(requestJson);inputsWriter.Write(inputsJson);
    }
    private static void RequireOutsideProtected(string destination,IEnumerable<string> protectedFiles,IEnumerable<string> protectedDirectories)
    {
        static bool Below(string path,string dir)=>path.Equals(dir,StringComparison.OrdinalIgnoreCase)||path.StartsWith(Path.TrimEndingDirectorySeparator(dir)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        foreach(string path in protectedFiles.Select(Path.GetFullPath))if(Below(path,destination))throw new InvalidDataException("Destination/source collision: "+path+". Choose an output root outside the input/source directories.");
        foreach(string path in protectedDirectories.Select(Path.GetFullPath))if(Below(destination,path)||Below(path,destination))throw new InvalidDataException("Destination aliases a protected source/application directory: "+path+". Choose an output root outside the input/source directories.");
    }
}
public sealed record BuildDestinationProtection(string[] Files,string[] Directories);

