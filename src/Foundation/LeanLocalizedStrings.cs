using System.Text;
namespace AutoFollower;

// Inline only consumed fields, resolving tables through the selected winner.
public sealed class LeanLocalizedStrings(LeanMo2Sources sources,string language)
{
    readonly Dictionary<string,Dictionary<uint,byte[]>> tables=new(StringComparer.OrdinalIgnoreCase);
    public byte[] Resolve(string plugin,string table,uint id)
    {
        if(id==0)return [0];
        if(string.IsNullOrWhiteSpace(language)||language.Any(c=>!char.IsAsciiLetter(c)))throw new InvalidDataException("Unsupported localization language: "+language);
        string reference="strings/"+Path.GetFileNameWithoutExtension(plugin)+"_"+language.ToLowerInvariant()+"."+table;
        if(!tables.TryGetValue(reference,out var entries)){
            byte[]? raw=null;sources.Stage(reference,"localized "+plugin+" "+table+" fields; selected language "+language,new(),consume:b=>raw=b,followDependencies:false,stagePayload:false);
            var q=new BoundedBinary(raw!);int count=q.Count(1000000),size=q.Count(),start=checked(8+count*8);
            if(start>raw!.Length||size!=raw.Length-start)throw new InvalidDataException("Localized directory bounds: "+reference);
            entries=[];
            for(int i=0;i<count;i++){
                uint key=q.U32(),offset=q.U32();if(offset>=size)throw new InvalidDataException("Localized offset: "+reference);
                int at=checked(start+(int)offset),length;
                if(table=="strings"){int end=Array.IndexOf(raw,(byte)0,at);if(end<0)throw new InvalidDataException("Unterminated localized string: "+reference);length=end-at+1;}
                else{if(at>raw.Length-4)throw new InvalidDataException("Localized length missing: "+reference);length=checked((int)BitConverter.ToUInt32(raw,at));at+=4;if(length<1||length>raw.Length-at||raw[at+length-1]!=0)throw new InvalidDataException("Localized length invalid: "+reference);}
                if(!entries.TryAdd(key,raw.AsSpan(at,length).ToArray()))throw new InvalidDataException("Duplicate localized ID: "+reference);
            }
            tables.Add(reference,entries);
        }
        return entries.GetValueOrDefault(id)??throw new InvalidDataException("Required localized string missing: "+plugin+" "+table+" ID "+id+" language "+language);
    }
    public byte[] Inline(string plugin,string type,byte[] payload)
    {
        var q=new BoundedBinary(payload);using var stream=new MemoryStream();using var w=new BinaryWriter(stream);int? extended=null;
        while(q.Remaining>0){string tag=Encoding.ASCII.GetString(q.Take(4));int n=q.U16();if(tag=="XXXX"){if(n!=4||extended!=null)throw new InvalidDataException("Localized extended framing");extended=q.Count();continue;}n=extended??n;extended=null;byte[] value=q.Take(n);
            // Magic-effect DNAM is a Normal/STRINGS field, unlike item DESC (DL).
            string? table=tag is "FULL" or "SHRT"||type=="MGEF"&&tag=="DNAM"?"strings":tag=="DESC"?"dlstrings":null;
            if(table!=null){if(value.Length!=4)throw new InvalidDataException("Localized field is not an ID: "+plugin+" "+type+" "+tag);value=Resolve(plugin,table,BitConverter.ToUInt32(value));}
            if(value.Length>ushort.MaxValue){w.Write(Encoding.ASCII.GetBytes("XXXX"));w.Write((ushort)4);w.Write(value.Length);}
            w.Write(Encoding.ASCII.GetBytes(tag));w.Write(value.Length>ushort.MaxValue?(ushort)0:(ushort)value.Length);w.Write(value);
        }
        if(extended!=null)throw new InvalidDataException("Dangling localized extended framing");return stream.ToArray();
    }
}
