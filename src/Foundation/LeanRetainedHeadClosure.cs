namespace AutoFollower;

public sealed record LeanHeadClosurePart(string Canonical,bool Owned,string EditorId,string OutputShapeName,int Type,string Model);
public static class LeanRetainedHeadClosure
{
    public static LeanHeadClosurePart[] Resolve(IEnumerable<string> primary,IEnumerable<string> defaults,Func<string,LeanRecord> get,IEnumerable<LeanHeadIdentityBinding> bindings,IEnumerable<string> exportedNames)
    {
        var identities=bindings.ToDictionary(x=>x.Canonical,StringComparer.OrdinalIgnoreCase);
        var names=exportedNames.ToHashSet(StringComparer.Ordinal);uint mask=0;HashSet<string> pointers=new(StringComparer.OrdinalIgnoreCase);List<LeanHeadClosurePart> result=[];
        void Visit(string key,bool owned,HashSet<string> chain){
            string pointer=(owned?"owned/":"source/")+key;
            if(!chain.Add(pointer))throw new InvalidDataException("Retained headpart closure cycle: "+key);
            var part=get(key);var types=part.Fields.GetValueOrDefault("PNAM",[]);
            if(part.Type!="HDPT"||types.Count!=1||types[0].Length!=4)throw new InvalidDataException("Retained headpart type unavailable: "+key);
            int type=checked((int)BitConverter.ToUInt32(types[0]));if(type>6)throw new InvalidDataException("Retained headpart type unsupported: "+key);
            bool exclusive=type is 1 or 2 or 3 or 4 or 6;
            // Installed 1.5.97 visitor 36B530 / predicate 332450: suppress
            // repeated exclusive types before extras, deduplicate pointers for
            // Misc/Scar, then visit extras. Distinct default pointers survive.
            if((mask&(1u<<type))!=0&&exclusive){chain.Remove(pointer);return;}
            if(pointers.Add(pointer)){
                string editor=part.Text("EDID").Single();string output=owned&&identities.TryGetValue(key,out var binding)?binding.OutputShapeName:editor;
                string model=part.Text("MODL").SingleOrDefault()??"";
                if(model.Length>0&&!names.Contains(output))throw new InvalidDataException("Retained race requires exported headpart "+key+" / "+output+" that is absent from the selected FaceGen; no source-part regeneration or shared race override is allowed.");
                result.Add(new(key,owned,editor,output,type,model));
            }
            mask|=1u<<type;foreach(string child in part.Refs("HNAM"))Visit(child,owned,chain);chain.Remove(pointer);
        }
        foreach(string key in primary)Visit(key,true,new(StringComparer.OrdinalIgnoreCase));
        foreach(string key in defaults)Visit(key,false,new(StringComparer.OrdinalIgnoreCase));
        return result.ToArray();
    }
}
