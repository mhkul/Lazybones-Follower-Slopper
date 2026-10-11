namespace AutoFollower;

public sealed record LeanAssetSelection(string Id,string Mode,string? SourceIdentity=null);
public sealed record LeanAssetChoice(string Id,string Reference,string Name,string Type,string Component,
    string Layer,string[] Variants,string Mode,bool CanRetain,string? Constraint,LeanSource? Provider,
    string? SourceIdentity,string[] RequiredReferences,string? RequiredMode=null,string[]? WarnedMissingReferences=null)
{
    public string? ProviderName {get {if(Provider==null)return null;string path=Provider.Backing.Replace('\\','/');int mods=path.IndexOf("/mods/",StringComparison.OrdinalIgnoreCase);return mods>=0?path[(mods+6)..].Split('/')[0]:Provider.Kind=="Declared generated export"?"Selected generated export":Path.GetFileName(Provider.Backing);}}
}
public static class LeanPackagingChoices
{
    public const string Standalone="Package standalone",Retain="Retain dependency";
    public static string Ref(string value)=>VirtualPath.Normalize(value).ToLowerInvariant();
    public static string Identity(LeanSource source)=>BoundedBinary.Digest(System.Text.Encoding.UTF8.GetBytes(
        string.Join('\n',Path.GetFullPath(source.Backing).ToLowerInvariant(),source.ArchiveMember?.ToLowerInvariant()??"",source.Sha256,source.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    public static LeanAssetChoice[] Resolve(List<LeanSource> sources,object? body,string tint,LeanAssetSelection[]? selections,string[]? selectedVariants=null,string? existingSkeleton=null,LeanMissingReference[]? missingReferences=null)
    {
        var models=body is LeanBodyPlan b?b.Models:[];
        var affected=models.Where(x=>x.Affected).Select(x=>Ref(x.Reference)).ToHashSet();
        string[] variants=selectedVariants??(body==null?["as-is"]:["prebuilt","obody"]);
        List<LeanAssetChoice> rows=[];
        foreach(var s in sources.Where(x=>x.RuntimeAsset&&(Ref(x.Reference).StartsWith("meshes/")||Ref(x.Reference).StartsWith("textures/")||Ref(x.Reference).StartsWith("sound/")||Ref(x.Reference)==Ref(tint)))){
            string reference=Ref(s.Reference);bool exported=s.Kind=="Declared generated export";
            if(affected.Contains(reference)&&!variants.Contains("obody"))continue;
            bool unattributed=s.Kind=="Physical/deployed final loose backing";
            string? constraint=unattributed?"This physical loose winner has no evidenced named dependency. Package standalone; do not invent a provider.":exported?"Final FaceGen uses the new follower/plugin identity; this selected export has no installed external equivalent. Keep Package standalone.":null;
            if(!exported&&!unattributed&&(s.RequiredReferences?.Length??0)>0)constraint="Retaining this source NIF requires retaining its called dependencies; a packaged NIF can call either packaged or retained dependencies.";
            if(!exported&&(reference.EndsWith("_0.nif")||reference.EndsWith("_1.nif"))){string sibling=reference[..^6]+(reference.EndsWith("_0.nif")?"_1.nif":"_0.nif");if(sources.Any(x=>Ref(x.Reference)==sibling))constraint=(constraint==null?"":constraint+" ")+"Weight endpoints must share mode with shared/"+sibling+".";}
            bool repaired=(s.TextureRepairs?.Length??0)>0;
            if(repaired)constraint="Creator texture paths will be repaired in this packaged copy using resolved selected texture sources. Keep Package standalone.";
            bool raceProvided=existingSkeleton!=null&&reference==Ref(existingSkeleton);
            rows.Add(new("shared/"+reference,reference,Path.GetFileName(reference),Path.GetExtension(reference).TrimStart('.'),s.Consumer,"shared",affected.Contains(reference)?["obody"]:variants,raceProvided?Retain:Standalone,raceProvided||!exported&&!unattributed&&!repaired,raceProvided?"Provided by the existing captured race. Its installed skeleton is required and is never packaged in this race mode.":constraint,s,Identity(s),s.RequiredReferences??[],raceProvided?Retain:null));
        }
        foreach(var m in models.Where(x=>x.Affected&&variants.Contains("prebuilt"))){
            string reference=Ref(m.Reference);
            rows.Add(new("prebuilt/"+reference,reference,Path.GetFileName(reference),"nif",m.Basis,"prebuilt",["prebuilt"],Standalone,false,
                "This shape must be freshly built from the assigned XML. The installed zeroed mesh cannot represent the prebuilt shape. Keep Package standalone.",sources.SingleOrDefault(x=>Ref(x.Reference)==reference),null,(sources.SingleOrDefault(x=>Ref(x.Reference)==reference)?.RequiredReferences??[]).Where(x=>!x.EndsWith(".tri",StringComparison.OrdinalIgnoreCase)).ToArray()));
        }
        if(body!=null){
            rows.Add(new("generated/original-preset","docs/presets/original/Sen Abersha.xml","Authored preset","xml","preset documentation","generated",variants,Standalone,false,"The package must preserve the actual authored preset; a local selected XML is not an installed runtime reference. Keep Package standalone.",sources.SingleOrDefault(x=>Ref(x.Reference)=="metadata/authored-preset.xml"),null,[]));
            bool completed=variants.Contains("prebuilt")&&body is LeanBodyPlan plan&&plan.Projects.Length>0;
            rows.Add(new("generated/completed-preset","completed-preset",completed?"Completed private preset":"Private authored preset","xml","OBody preset/configuration","generated",variants,Standalone,false,completed?"The completed preset has a unique private name and project-default endpoints. No equivalent external preset is established. Keep Package standalone.":"The authored preset is privately named with omitted endpoints unchanged; no installed external equivalent exists. Keep Package standalone.",null,null,[]));
        }
        var missing=(missingReferences??[]).Select(x=>Ref(x.Reference)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows=rows.Select(r=>{var warned=r.RequiredReferences.Select(Ref).Where(missing.Contains).ToArray();return r with{WarnedMissingReferences=warned,Constraint=warned.Length==0?r.Constraint:(r.Constraint??"")+" Missing references stay unchanged: "+string.Join("; ",warned)+". No missing file is packaged."};}).ToList();
        var byId=rows.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);HashSet<string> seen=new(StringComparer.OrdinalIgnoreCase);
        foreach(var selection in selections??[]){
            if(!seen.Add(selection.Id))throw new InvalidDataException("Duplicate shared asset choice: "+selection.Id+"; supply one mode for all consumers");
            if(!byId.TryGetValue(selection.Id,out var row))throw new InvalidDataException("Unknown required asset choice: "+selection.Id+"; refresh the dependency preview");
            if(selection.Mode!=Standalone&&selection.Mode!=Retain)throw new InvalidDataException("Invalid packaging mode for "+selection.Id+"; use Package standalone or Retain dependency");
            if(row.RequiredMode!=null&&selection.Mode!=row.RequiredMode)throw new InvalidDataException(selection.Id+": "+row.Constraint);
            if(selection.SourceIdentity!=null&&selection.SourceIdentity!=row.SourceIdentity)throw new InvalidDataException("Required source identity changed: "+selection.Id+"; refresh the dependency preview and select the current provider");
            if(selection.Mode==Retain){
                if(!row.CanRetain)throw new InvalidDataException(selection.Id+": "+row.Constraint);
                if(selection.SourceIdentity==null)throw new InvalidDataException(selection.Id+": retention requires the preview's sourceIdentity; refresh the dependency preview");
            }
            byId[selection.Id]=row with {Mode=selection.Mode};
        }
        foreach(var row in byId.Values.Where(x=>x.Layer=="shared")){
            // Skyrim's weight-slider model call implicitly consumes its sibling.
            if(row.Reference.EndsWith("_0.nif")||row.Reference.EndsWith("_1.nif")){
                string sibling=row.Reference[..^6]+(row.Reference.EndsWith("_0.nif")?"_1.nif":"_0.nif");
                if(byId.TryGetValue("shared/"+sibling,out var other)&&other.Mode!=row.Mode)
                    throw new InvalidDataException(row.Id+": weight endpoints must share a reference location with "+other.Id+"; set either endpoint to the other's mode");
            }
            if(row.Mode!=Retain)continue;
            foreach(string dep in row.RequiredReferences.Select(Ref)){
                if(!byId.TryGetValue("shared/"+dep,out var child)){
                    if((row.WarnedMissingReferences??[]).Contains(dep,StringComparer.OrdinalIgnoreCase))continue;
                    throw new InvalidDataException(row.Id+": retained required reference unresolved: "+dep);
                }
                if(child.Mode!=Retain)throw new InvalidDataException(row.Id+": retained source cannot call privately relocated "+child.Id+"; set "+row.Id+" to Package standalone or "+child.Id+" to Retain dependency");
            }
        }
        return rows.Select(x=>byId[x.Id]).ToArray();
    }
}
