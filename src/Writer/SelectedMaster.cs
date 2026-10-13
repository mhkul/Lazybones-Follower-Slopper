using System.Text.Json;
using AutoFollower;

static class SelectedMaster
{
    public static string[] DependencyOrder(JsonElement inputs,IEnumerable<string> required,bool bestEffort=false,List<BuildQualityWarning>? warnings=null)
    {
        var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var visiting=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var ordered=new List<string>();
        void Visit(string name){
            if(visited.Contains(name))return;
            if(!visiting.Add(name)){if(!bestEffort)throw new InvalidDataException("Required master dependency cycle: "+name);warnings?.Add(BestEffortBuild.Finding("master-dependencies","Required master dependency cycle: "+name,"plugin-quality","Plugin masters contain a dependency cycle. Available master headers are preserved; Skyrim loading may fail."));return;}
            string path=bestEffort?CapturedHeader(inputs,name):Require(inputs,name);var pin=Named(inputs.GetProperty("masterEvidence"),name,"Selected master evidence");
            byte[] header=new byte[pin.GetProperty("headerBytes").GetInt32()];using(var stream=File.OpenRead(path))stream.ReadExactly(header);
            foreach(byte[] value in LeanRecordIndex.Fields(header[24..]).GetValueOrDefault("MAST",[]))Visit(System.Text.Encoding.UTF8.GetString(value).TrimEnd('\0'));
            visiting.Remove(name);visited.Add(name);ordered.Add(Path.GetFileName(path));
        }
        var selectedOrder=inputs.GetProperty("masterPaths").EnumerateObject().Select((p,i)=>(p.Name,Index:i)).ToDictionary(p=>p.Name,p=>p.Index,StringComparer.OrdinalIgnoreCase);
        foreach(string name in required.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n=>selectedOrder.GetValueOrDefault(n,int.MaxValue)))Visit(name);
        // DFS proves the necessary closure/cycles; selected order decides override winners.
        return ordered.OrderBy(n=>selectedOrder.GetValueOrDefault(n,int.MaxValue)).ToArray();
    }
    static JsonElement Named(JsonElement values,string name,string context)
    {
        var matches=values.EnumerateObject().Where(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1)throw new InvalidDataException(context+(matches.Length==0?" missing: ":" ambiguous: ")+name);
        return matches[0].Value;
    }
    public static string HeaderFolder(JsonElement inputs,string[] masters,bool bestEffort=false)
    {
        string folder=Path.GetFullPath(inputs.GetProperty("masterHeaderFolder").GetString()!);SafePaths.NoLinks(folder);
        foreach(string name in masters)
        {
            string selected=bestEffort?CapturedHeader(inputs,name):Require(inputs,name);string header=SafePaths.Below(folder,name);
            var pin=Named(inputs.GetProperty("masterEvidence"),name,"Selected master evidence");
            if(new FileInfo(header).Length!=pin.GetProperty("headerBytes").GetInt32()||Hashing.File(header)!=pin.GetProperty("headerSha256").GetString())throw new InvalidDataException("Selected master header copy differs: "+selected);
        }
        return folder;
    }
    static string CapturedHeader(JsonElement inputs,string name)
    {
        string folder=Path.GetFullPath(inputs.GetProperty("masterHeaderFolder").GetString()!);SafePaths.NoLinks(folder);
        string header=SafePaths.Below(folder,name);var pin=Named(inputs.GetProperty("masterEvidence"),name,"Captured selected master evidence");
        if(new FileInfo(header).Length!=pin.GetProperty("headerBytes").GetInt32()||Hashing.File(header)!=pin.GetProperty("headerSha256").GetString())throw new IOException("Captured master header changed: "+name);
        return header;
    }
    public static string Require(JsonElement inputs,string name)
    {
        if(!inputs.TryGetProperty("masterPaths",out var paths))throw new InvalidDataException("Selected master provenance missing: "+name);
        var path=Named(paths,name,"Selected master provenance");
        string selected=Path.GetFullPath(path.GetString()!);SafePaths.NoLinks(selected);
        if(!Path.GetFileName(selected).Equals(name,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Selected master filename differs: "+name);
        if(!inputs.TryGetProperty("masterEvidence",out var evidence))throw new InvalidDataException("Selected master evidence missing: "+name);
        var pin=Named(evidence,name,"Selected master evidence");
        var info=new FileInfo(selected);
        if(selected!=pin.GetProperty("backing").GetString()||info.Length!=pin.GetProperty("bytes").GetInt64()||info.LastWriteTimeUtc.Ticks!=pin.GetProperty("writeTime").GetInt64())throw new InvalidDataException("Selected master changed; refresh inputs: "+name);
        using var stream=new FileStream(selected,FileMode.Open,FileAccess.Read,FileShare.Read);
        int count=pin.GetProperty("headerBytes").GetInt32();if(count<24||count>1048576||count>stream.Length)throw new InvalidDataException("Selected master header bound invalid");
        byte[] bytes=new byte[count];stream.ReadExactly(bytes);
        if(BoundedBinary.Digest(bytes)!=pin.GetProperty("headerSha256").GetString())throw new InvalidDataException("Selected master header changed: "+name);
        return selected;
    }
}
