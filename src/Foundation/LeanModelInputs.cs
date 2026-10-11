namespace AutoFollower;

// SSE uses a missing female biped model's male model. The reverse fallback
// does not exist. First/third person are resolved independently; weight flags
// belong to the selected actor sex, rather than to the fallback model slot.
public static class LeanModelInputs
{
    // Only embedded texture slots may recover a creator drive prefix. The
    // generic VirtualPath boundary remains strict, and never opens that drive.
    public static string TextureReference(string raw)
    {
        string value=raw.Replace('\\','/');
        if(value.Length>=3&&char.IsAsciiLetter(value[0])&&value[1]==':'&&value[2]=='/'){
            if(value.Length>4096||value.Any(char.IsControl)||value[3..].IndexOfAny([':', '*', '?', '"', '<', '>', '|'])>=0)
                throw new InvalidDataException("Malformed creator texture path");
            var parts=value[3..].Split('/');
            if(parts.Any(p=>p.Length==0||p is "." or ".."||p.EndsWith(' ')||p.EndsWith('.')))
                throw new InvalidDataException("Malformed creator texture path components");
            var roots=parts.Select((p,i)=>(p,i)).Where(x=>x.p.Equals("textures",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(roots.Length!=1||roots[0].i>=parts.Length-1||!value.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Creator texture path has no unique complete textures/*.dds suffix");
            return VirtualPath.Normalize(string.Join('/',parts[roots[0].i..]));
        }
        value=SerializedPath(value);
        if(value.StartsWith("Data/",StringComparison.OrdinalIgnoreCase))value=value[5..];
        return VirtualPath.Normalize(value.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)||value.StartsWith("SKSE/",StringComparison.OrdinalIgnoreCase)?value:"textures/"+value);
    }
    // WEAP's third-person model is MODL; WNAM links its first-person STAT.
    // Native Unarmed can be marked worn without either call. Do not treat
    // another equipment type or a weapon with a referenced model as meshless.
    public static bool MeshlessWeapon(LeanRecord record)=>record.Type=="WEAP"&&!record.Text("MODL").Any(x=>x.Length>0)&&record.Refs("WNAM").Length==0;
    public static string SerializedPath(string path){path=path.Replace('\\','/');if(path.StartsWith('/'))throw new InvalidDataException("Rooted asset reference is unsupported");while(path.Contains("//",StringComparison.Ordinal))path=path.Replace("//","/",StringComparison.Ordinal);return path;}
    public static string Reference(string path,string category="meshes"){path=path.Replace('\\','/');if(path.StartsWith("Data/",StringComparison.OrdinalIgnoreCase))path=path[5..];return VirtualPath.Normalize(path.StartsWith(category+"/",StringComparison.OrdinalIgnoreCase)?path:category+"/"+path);}
    public static (string[] Paths,string Basis) Biped(LeanRecord addon,ushort sex,bool first)
    {
        string selected=first?(sex==1?"MOD5":"MOD4"):(sex==1?"MOD3":"MOD2");
        string[] paths=addon.Text(selected).Where(x=>x.Length>0).ToArray();string basis="ARMA "+selected;
        if(sex==1&&paths.Length==0){string fallback=first?"MOD4":"MOD2";paths=addon.Text(fallback).Where(x=>x.Length>0).ToArray();if(paths.Length>0)basis+=" empty; SSE female-to-male fallback "+fallback;}
        if(paths.Length>1)throw new InvalidDataException("Ambiguous ARMA model field: "+addon.Canonical+" "+selected);return(paths,basis);
    }
}
