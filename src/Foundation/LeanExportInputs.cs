using System.Text;
using System.Text.Json;

namespace AutoFollower;

public sealed record LeanCosave(PluginTable Plugins,string? Preset,bool PersistedNodeMaterials,LeanSkeeState Skee,string ObodyAssignment="unavailable",uint ProducerSkse=0,uint ProducerRuntime=0);
public sealed record LeanAppearance(double Weight,uint HairColor,JsonElement HeadParts,
    JsonElement FaceTextures,JsonElement DefaultMorphs,JsonElement TintLighting,
    string[] HeadDependencies,string TintReference,int DynamicHeadShapes,string Association,BuildQualityWarning[]? QualityWarnings=null);

public static class LeanExportInputs
{
    public static LeanCosave Cosave(byte[] bytes,PluginTable saved,ushort sex,SkyrimInputFormats? formats=null)
    {
        formats=SkyrimInputFormats.Resolve(formats);if(sex>1)throw new InvalidDataException("Co-save requires the saved actor's valid sex");
        var r=new BoundedBinary(bytes);if(Encoding.ASCII.GetString(r.Take(4))!="SKSE")throw new InvalidDataException("Unsupported co-save signature");
        uint framing=r.U32(),producerSkse=r.U32(),producerRuntime=r.U32();formats.RequireCosave(framing,producerSkse,producerRuntime);
        int sections=r.Count(4096);List<PluginEntry>? entries=null;byte[]? actors=null,names=null;bool persisted=false;HashSet<uint> uids=[];List<LeanSkeeChunk> skee=[];
        for(int i=0;i<sections;i++){uint uid=r.U32();int count=r.Count(100000),length=r.Count(),end=checked(r.Position+length);if(end>bytes.Length||!uids.Add(uid))throw new InvalidDataException("Co-save section framing invalid");
            for(int j=0;j<count;j++){uint tag=r.U32(),version=r.U32();int size=r.Count(),offset=r.Position;if(size>end-offset)throw new InvalidDataException("Co-save chunk escapes section");r.Seek(offset+size);
                if(uid==0&&tag==0x504C474E){if(version!=0||entries!=null)throw new InvalidDataException("Unsupported/duplicate PLGN");var p=new BoundedBinary(bytes.AsSpan(offset,size).ToArray());entries=[];int n=p.U16();for(int k=0;k<n;k++){byte slot=p.U8();int index=slot==0xfe?p.U16():slot;entries.Add(new(p.String16(),slot==0xfe?"light":"full",index));}p.End("co-save PLGN chunk at file offset "+offset);}
                if(uid==0xA0B0D9EE&&tag is 0xA0B0D9EA or 0xA0B0D9E0){if(version!=0)throw new InvalidDataException("OBody registry version unsupported");if(tag==0xA0B0D9EA){if(actors!=null)throw new InvalidDataException("Duplicate OBody actor registry");actors=bytes.AsSpan(offset,size).ToArray();}else{if(names!=null)throw new InvalidDataException("Duplicate OBody preset registry");names=bytes.AsSpan(offset,size).ToArray();}}
                if(uid==0x534b4545&&tag is 0x4143544d or 0x4e4f544d or 0x4e4f454e or 0x4e44454e or 0x4f565354 or 0x4f565256 or 0x414f564c)persisted|=size>0;
                if(uid==0x534b4545&&tag is 0x53545442 or 0x4143544d or 0x4e4f544d or 0x4e4f454e or 0x4e44454e or 0x4f565354 or 0x4f565256 or 0x414f564c or 0x4143454e or 0x5750454e or 0x534b4e52 or 0x4152454e or 0x4141454e or 0x534b4e44)skee.Add(new(tag,version,bytes.AsSpan(offset,size).ToArray()));
            }if(r.Position!=end)throw new InvalidDataException("Co-save section count mismatch");
        }r.End("co-save section framing");if(entries==null)throw new InvalidDataException("PLGN association missing");var table=new PluginTable("saved/cosave",entries,Status.Observed,"selected co-save");Semantics.Table(table);
        if(!saved.Entries.OrderBy(x=>x.Kind).ThenBy(x=>x.Index).SequenceEqual(entries.OrderBy(x=>x.Kind).ThenBy(x=>x.Index)))throw new InvalidDataException("ESS/co-save plugin identity mismatch");
        string? preset=null;string assignment="unavailable";if(actors!=null&&names!=null){var a=new BoundedBinary(actors);Dictionary<uint,uint> registry=[];while(a.Remaining>0)if(!registry.TryAdd(a.U32(),a.U32()))throw new InvalidDataException("Duplicate OBody actor");
            var n=new BoundedBinary(names);Dictionary<uint,string>[] presets=[[],[]];for(int s=0;s<2;s++){n.U32();while(true){int length=n.Count(65536);if(length==0)break;uint index=n.U32();string name=new UTF8Encoding(false,true).GetString(n.Take(length));n.Seek(n.Position+(4-length%4)%4);if(string.IsNullOrWhiteSpace(name)||!presets[s].TryAdd(index,name))throw new InvalidDataException("Invalid OBody preset registry");}}n.End("co-save OBody preset registry");
            assignment="unassigned";if(registry.TryGetValue(0x14,out uint state)&&state>>12>0){preset=presets[sex==1?0:1].GetValueOrDefault((state>>12)-1)??throw new InvalidDataException("Saved OBody player index has no matching sex-specific preset name");assignment="assigned";}
        }return new(table,preset,persisted,LeanSkeeReader.Read(skee,sex,formats),assignment,producerSkse,producerRuntime);
    }
    public sealed record TextureRepair(int Block,int Slot,string Original,string Reference);
    public sealed record Nif(string[] Types,string[] Strings,byte[][] Blocks,string[] Dependencies,int DynamicShapes,uint BsVersion=100,TextureRepair[]? TextureRepairs=null);
    public static Nif ReadNif(byte[] bytes)
    {
        int line=Array.IndexOf(bytes,(byte)'\n');if(line<0||line>128||!Encoding.ASCII.GetString(bytes,0,line).StartsWith("Gamebryo File Format"))throw new InvalidDataException("NIF signature invalid");
        var r=new BoundedBinary(bytes);r.Seek(line+1);if(r.U32()!=0x14020007||r.U8()!=1||r.U32()!=12)throw new InvalidDataException("Supported NIF framing is Skyrim 20.2.0.7/user 12");int blocks=r.Count(100000);uint bs=r.U32();if(bs is not (83 or 100))throw new InvalidDataException("NIF BS version unsupported: "+bs);for(int i=0;i<3;i++)r.Take(r.U8());
        string String(BoundedBinary q)=>new UTF8Encoding(false,true).GetString(q.Take(q.Count(1048576))).TrimEnd('\0');
        int typeCount=r.U16();string[] types=Enumerable.Range(0,typeCount).Select(_=>String(r)).ToArray();int[] indices=Enumerable.Range(0,blocks).Select(_=>(int)r.U16()).ToArray();int[] sizes=Enumerable.Range(0,blocks).Select(_=>r.Count()).ToArray();
        if(indices.Any(i=>i>=typeCount))throw new InvalidDataException("NIF type index invalid");int count=r.Count(1000000);r.Count(1048576);string[] strings=Enumerable.Range(0,count).Select(_=>String(r)).ToArray();int groups=r.Count(100000);r.Seek(checked(r.Position+groups*4));
        byte[][] payload=Enumerable.Range(0,blocks).Select(i=>r.Take(sizes[i])).ToArray();int roots=r.Count(100000);if(roots==0)throw new InvalidDataException("NIF roots absent");for(int i=0;i<roots;i++)if(r.U32()>=blocks)throw new InvalidDataException("NIF root invalid");r.End("NIF root/footer framing");
        List<string> deps=[];List<TextureRepair> repairs=[];for(int i=0;i<blocks;i++){
            if(types[indices[i]]=="BSShaderTextureSet"){var q=new BoundedBinary(payload[i]);int slots=q.Count(32);for(int j=0;j<slots;j++){string value=String(q);if(value.Length>0){string reference=LeanModelInputs.TextureReference(value);deps.Add(reference);if(value.Length>=3&&value[1]==':')repairs.Add(new(i,j,value,reference));}}q.End("NIF BSShaderTextureSet block "+i);}
            if(types[indices[i]]=="NiStringExtraData"){var q=new BoundedBinary(payload[i]);uint key=q.U32(),value=q.U32();q.End("NIF NiStringExtraData block "+i);if(key>=strings.Length||value>=strings.Length)throw new InvalidDataException("NIF extra string index invalid");string v=strings[value].Replace('\\','/');if(v.StartsWith("Data/",StringComparison.OrdinalIgnoreCase))v=v[5..];if(v.Length>0){if(strings[key]=="BODYTRI")deps.Add(VirtualPath.Normalize(v.StartsWith("meshes/",StringComparison.OrdinalIgnoreCase)?v:"meshes/"+v));else if(strings[key]=="HDT Skinned Mesh Physics Object")deps.Add(VirtualPath.Normalize(v));else if(new[]{".nif",".tri",".dds",".xml",".bgsm",".bgem"}.Any(x=>v.EndsWith(x,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Unsupported path-valued NIF extra data: "+strings[key]);}}
        }
        if(bs==83&&indices.Any(i=>types[i] is "BSTriShape" or "BSDynamicTriShape" or "BSSubIndexTriShape"))throw new InvalidDataException("SSE shape in BS83 mesh is unsupported");
        return new(indices.Select(i=>types[i]).ToArray(),strings,payload,deps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),indices.Count(i=>types[i]=="BSDynamicTriShape"),bs,repairs.ToArray());
    }
    public static string RaceMenuTintReference(Nif head)
    {
        string[] candidates=head.Dependencies.Where(x=>x.StartsWith("SKSE/Plugins/CharGen/",StringComparison.OrdinalIgnoreCase)&&x.EndsWith(".dds",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(candidates.Length!=1)throw new InvalidDataException(candidates.Length==0
            ?"The selected head does not call one supported RaceMenu composed tint texture. Choose its matching exported head NIF."
            :"The selected head calls more than one RaceMenu composed tint texture. The correct tint binding is ambiguous; choose a supported matching export bundle.");
        return candidates[0];
    }
    public static LeanAppearance Bundle(byte[] nif,byte[] dds,byte[] jslot,LeanSavedState saved,string tintReference,string declaration,bool bestEffort=false)
    {
        if(string.IsNullOrWhiteSpace(declaration))throw new InvalidDataException("Declare that exports match the selected character/save/environment");
        return Bundle(ReadNif(nif),dds,jslot,saved,tintReference,declaration,bestEffort);
    }
    public static LeanAppearance Bundle(Nif head,byte[] dds,byte[] jslot,LeanSavedState saved,string tintReference,string declaration,bool bestEffort=false)
    {
        if(string.IsNullOrWhiteSpace(declaration))throw new InvalidDataException("Declare that exports match the selected character/save/environment");
        List<BuildQualityWarning> quality=[];
        void Check(bool value,string detail){if(value)return;if(!bestEffort)throw new InvalidDataException(detail);quality.Add(BestEffortBuild.Finding("export-quality",detail));}
        Check(head.BsVersion==100&&head.DynamicShapes>0,"Head export has no supported SSE dynamic head shapes");
        var d=new BoundedBinary(dds);if(Encoding.ASCII.GetString(d.Take(4))!="DDS "||d.U32()!=124)throw new InvalidDataException("Tint DDS framing invalid");
        uint[] h=Enumerable.Range(0,30).Select(_=>d.U32()).ToArray();uint height=h[1],width=h[2];
        if(width==0||height==0||width>8192||height>8192||h[5]!=1||h[17]!=32||h[18]!=65||h[19]!=0||h[20]!=32||h[21]!=255||h[22]!=65280||h[23]!=16711680||h[24]!=4278190080||h[3]!=width*4||d.Remaining!=checked(width*height*4)){Check(false,"Composed tint differs from the supported one-mip RGBA8 DDS layout; original bytes preserved");}
        string tint=VirtualPath.Normalize(tintReference);if(head.Dependencies.Count(x=>x.Equals(tint,StringComparison.OrdinalIgnoreCase))!=1){Check(false,"Selected tint reference is not called by the head NIF; supplied bytes preserved");}
        using var doc=JsonDocument.Parse(jslot);var slot=doc.RootElement;var actor=slot.GetProperty("actor");double weight=actor.GetProperty("weight").GetDouble();if(!double.IsFinite(weight)||weight<0||weight>100)throw new InvalidDataException("JSlot weight invalid");uint color=actor.GetProperty("hairColor").GetUInt32();
        // RaceMenu's "type" is its partList index, not HDPT PNAM. Actual
        // Atae ESS and JSlot arrays have different orders. Match unique saved
        // canonical identities; the selected record flags later prove exact
        // primary/extra coverage. Keep the export index as provenance only.
        var parts=slot.GetProperty("headParts");HashSet<int> indices=[];HashSet<int> savedIndices=[];List<Dictionary<string,JsonElement>> resolvedParts=[];foreach(var part in parts.EnumerateArray()){
            int index=part.GetProperty("type").GetInt32();if(!indices.Add(index)||index<0||index>=saved.Actor.HeadParts.Length)throw new InvalidDataException("JSlot head-array indices invalid");string raw=part.GetProperty("formIdentifier").GetString()??"";int split=raw.LastIndexOf('|');if(split<1)throw new InvalidDataException("JSlot head identity malformed");
            string name=raw[..split];var plugin=saved.Plugins.Entries.SingleOrDefault(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))??throw new InvalidDataException("JSlot head plugin absent from saved table");uint local=Convert.ToUInt32(raw[(split+1)..],16)&(plugin.Kind=="light"?0xfffu:0xffffffu);string canonical=plugin.Name+"|"+local.ToString("X6");var matches=saved.Actor.HeadParts.Select((value,i)=>(value,i)).Where(x=>x.value.Equals(canonical,StringComparison.OrdinalIgnoreCase)).ToArray();if(matches.Length!=1||!savedIndices.Add(matches[0].i))throw new InvalidDataException("JSlot head part contradicts or ambiguously repeats saved character identity: "+canonical);
            var resolved=part.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());resolved["savedIndex"]=JsonSerializer.SerializeToElement(matches[0].i);resolved["canonical"]=JsonSerializer.SerializeToElement(canonical);resolvedParts.Add(resolved);
        }if(indices.Count==0)throw new InvalidDataException("JSlot head parts absent");
        var textures=slot.GetProperty("faceTextures");HashSet<int> slots=[];foreach(var texture in textures.EnumerateArray()){int index=texture.GetProperty("index").GetInt32();if(index<0||index>8||!slots.Add(index))throw new InvalidDataException("JSlot face-texture slots invalid");VirtualPath.Normalize(texture.GetProperty("texture").GetString()??"");}
        var morphs=slot.GetProperty("morphs").GetProperty("default");var tintInfo=slot.GetProperty("tintInfo");var skinTints=tintInfo.EnumerateArray().Where(x=>x.GetProperty("index").GetInt32()==0).ToArray();if(skinTints.Length!=1)throw new InvalidDataException("JSlot needs one unambiguous skin tint entry 0");skinTints[0].GetProperty("color").GetUInt32();VirtualPath.Normalize(skinTints[0].GetProperty("texture").GetString()??"");
        if(actor.TryGetProperty("headTexture",out var ht)&&!string.Equals(ht.GetString(),saved.Actor.FaceTextureSet,StringComparison.OrdinalIgnoreCase)){Check(false,"JSlot face TXST contradicts saved face; actual selected data preserved");}
        return new(weight,color,JsonSerializer.SerializeToElement(resolvedParts),textures.Clone(),morphs.Clone(),skinTints[0].Clone(),head.Dependencies,tint,head.DynamicShapes,declaration+"; ordinary exports contain no unique ESS/session binding",quality.ToArray());
    }
}
