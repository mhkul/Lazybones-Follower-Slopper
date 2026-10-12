using System.Text;

namespace AutoFollower;

public sealed record LeanWorn(string Canonical, int Count, int[] Markers);
public sealed record LeanActor(string Name, ushort Sex, string Race, string[] HeadParts,
    string FaceTextureSet, double Scale, Transform Placement, LeanWorn[] Worn,
    string? HeaderRaceEditorId=null,bool RaceFromChangeForm=true,bool ScaleFromChangeForm=true);
public sealed record LeanSavedState(LeanActor Actor, PluginTable Plugins, uint SaveNumber,
    int ChangeFormsScanned, int PlayerPrefixBytes, int PlayerTailSkipped);

// Bounded layout follows the installed Apache-2.0 FallrimTools readers
// (Mark Fairchild, 2016/2017), cross-checked by accepted T09E evidence.
// Only required player/NPC fields are retained; no Java dependency or raw dump.
public static class LeanSaveReader
{
    static int Vs(BoundedBinary r, int max=100000)
    {
        uint first=r.U8(), value=first; int size=(int)(first&3);
        if(size>0)value|=(uint)r.U8()<<8;
        if(size>1)value|=(uint)r.U8()<<16;
        value>>=2;if(value>max)throw new InvalidDataException("Saved variable count exceeds supported bound");return (int)value;
    }
    static bool Flag(uint flags,int bit)=>(flags&(1u<<bit))!=0;
    static void Skip(BoundedBinary r,int bytes)=>r.Seek(checked(r.Position+bytes));
    public static LeanSavedState Read(byte[] bytes,string source,CancellationToken token,SkyrimInputFormats? formats=null)
    {
        formats=SkyrimInputFormats.Resolve(formats);
        var r=new BoundedBinary(bytes);
        if(Encoding.ASCII.GetString(r.Take(13))!="TESV_SAVEGAME")throw new InvalidDataException("Unsupported ESS signature");
        int headerSize=r.Count(1024),start=r.Position;
        if(r.U32()!=formats.EssVersion)throw new InvalidDataException("Required ESS header layout is version12; observed header is unsupported");
        uint number=r.U32();string name=r.String16();r.U32();r.String16();r.String16();string headerRace=r.String16();ushort sex=r.U16();
        if(sex>1||string.IsNullOrWhiteSpace(name))throw new InvalidDataException("Invalid saved actor identity");
        Skip(r,16);int width=r.Count(8192),height=r.Count(8192);ushort compression=r.U16();
        if(r.Position-start!=headerSize)throw new InvalidDataException("ESS header size mismatch");
        Skip(r,checked(width*height*4));int bodyBase=r.Position;byte[] body;
        if(compression==0)body=r.Take(r.Remaining);
        else{int decoded=r.Count(),stored=r.Count();byte[] data=r.Take(stored);r.End("ESS compressed framing (compression="+compression+", stored bytes="+stored+", decoded bytes="+decoded+")");body=compression switch{1=>BoundedBinary.Inflate(data,decoded),2=>BoundedBinary.Lz4(data,decoded),_=>throw new InvalidDataException("Unsupported ESS compression")};}
        var b=new BoundedBinary(body);if(b.U8()!=formats.FormVersion)throw new InvalidDataException("Required saved form layout is version78; observed layout is unsupported");
        int pluginSize=b.Count(1024*1024),pluginStart=b.Position;List<PluginEntry> entries=[];
        int full=b.U8();for(int i=0;i<full;i++)entries.Add(new(b.String16(),"full",i));
        int light=b.U16();if(light>4096)throw new InvalidDataException("Light plugin overflow");
        for(int i=0;i<light;i++)entries.Add(new(b.String16(),"light",i));
        if(b.Position-pluginStart!=pluginSize)throw new InvalidDataException("ESS plugin block mismatch");
        var table=new PluginTable("saved/ess",entries,Status.Observed,source);Semantics.Table(table);
        int[] loc=Enumerable.Range(0,6).Select(_=>checked((int)b.U32()-bodyBase)).ToArray();
        b.Count(100000);b.Count(100000);b.Count(100000);int changes=b.Count(2000000);Skip(b,60);
        if(loc.Any(x=>x<b.Position||x>body.Length)||!(loc[2]<=loc[3]&&loc[3]<=loc[4]&&loc[4]<=loc[5]&&loc[5]<=loc[0]&&loc[0]<=loc[1]))throw new InvalidDataException("ESS location table invalid");
        b.Seek(loc[0]);int idCount=b.Count(4000000);uint[] ids=new uint[idCount];for(int i=0;i<idCount;i++)ids[i]=b.U32();
        uint Ref(BoundedBinary q){uint raw=q.Ref24(),v=raw&0x3fffff;if(v==0)return 0;return (raw>>22) switch{0 when v<=ids.Length=>ids[v-1],1=>v,2=>v|0xff000000,_=>throw new InvalidDataException("Saved reference index invalid")};}
        string Canon(uint id){var c=Semantics.Saved(id,table);return c.Plugin is not null&&c.LocalId is not null?c.Plugin+"|"+c.LocalId.Value.ToString("X6"):throw new InvalidDataException("Required saved form has no plugin identity: "+id.ToString("X8"));}
        byte[]? actor=null,npc=null;uint actorFlags=0,npcFlags=0;b.Seek(loc[4]);HashSet<uint> found=[];
        for(int i=0;i<changes;i++){
            token.ThrowIfCancellationRequested();uint id=Ref(b),flags=b.U32();byte type=b.U8(),version=b.U8();
            int Length()=>(type>>6) switch{0=>b.U8(),1=>b.U16(),2=>b.Count(),_=>throw new InvalidDataException("Changeform size encoding invalid")};
            int stored=Length(),decoded=Length(),offset=b.Position;Skip(b,stored);if(b.Position>loc[5])throw new InvalidDataException("Changeform escapes table");
            if(id is not (7 or 0x14))continue;if(!found.Add(id)||version!=formats.FormVersion||(type&63)!=(id==7?9:1))throw new InvalidDataException("Unsupported/duplicate player changeform for selected input profile");
            byte[] payload=body.AsSpan(offset,stored).ToArray();if(decoded>0)payload=BoundedBinary.Inflate(payload,decoded);
            if(id==7){npc=payload;npcFlags=flags;}else{actor=payload;actorFlags=flags;}
        }
        if(b.Position!=loc[5]||actor is null||npc is null)throw new InvalidDataException("Required player/NPC changeform missing or malformed");
        var n=new BoundedBinary(npc);uint supported=(1u<<0)|(1u<<1)|(1u<<3)|(1u<<4)|(1u<<5)|(1u<<6)|(1u<<9)|(1u<<10)|(1u<<11)|(1u<<12)|(1u<<13)|(1u<<24)|(1u<<25);
        if((npcFlags&~supported)!=0)throw new InvalidDataException("Unsupported saved NPC flags");
        if(Flag(npcFlags,0))Skip(n,6);if(Flag(npcFlags,1))Skip(n,24);
        if(Flag(npcFlags,6))Skip(n,checked(Vs(n)*4));
        if(Flag(npcFlags,4))for(int i=0;i<3;i++)Skip(n,checked(Vs(n)*3));
        if(Flag(npcFlags,3))Skip(n,20);if(Flag(npcFlags,5)){string savedName=n.String16();if(savedName!=name)throw new InvalidDataException("ESS/NPC actor name mismatch");}
        if(Flag(npcFlags,9))Skip(n,52);if(Flag(npcFlags,10))Ref(n);
        if(!Flag(npcFlags,11))throw new InvalidDataException("Required saved face unavailable; NPC change flags="+npcFlags.ToString("X8"));
        // An unchanged base race has no CHANGE_NPC_RACE bytes. Keep its header
        // identity pending until the selected environment proves NPC RNAM.
        string race="";if(Flag(npcFlags,25)){race=Canon(Ref(n));Ref(n);}if(n.U8()==0)throw new InvalidDataException("Saved face absent");
        Ref(n);n.U32();uint faceId=Ref(n);string face=faceId==0?"":Canon(faceId);int heads=Vs(n,256);string[] headParts=Enumerable.Range(0,heads).Select(_=>Canon(Ref(n))).ToArray();
        if(n.U8()!=0){Skip(n,checked(n.Count(1024)*4));Skip(n,checked(n.Count(1024)*4));}
        // The ESS header always records actual sex. Male saves commonly omit
        // CHANGE_NPC_GENDER; a byte exists only when that change flag is set.
        if(Flag(npcFlags,24)&&n.U8()!=sex)throw new InvalidDataException("ESS/NPC sex mismatch");if(Flag(npcFlags,12))Ref(n);if(Flag(npcFlags,13))Ref(n);n.End("ESS player NPC change form (flags="+npcFlags.ToString("X8")+")");
        var a=new BoundedBinary(actor);if(!Flag(actorFlags,1)&&!Flag(actorFlags,2)&&!Flag(actorFlags,3)&&!Flag(actorFlags,25))throw new InvalidDataException("Supported saved cell placement unavailable");
        uint location=Ref(a);string cell=(location>>24)==0xff?"dynamic/"+location.ToString("X8"):Canon(location);double[] position=Enumerable.Range(0,3).Select(_=>(double)a.Float()).ToArray(),rotation=Enumerable.Range(0,3).Select(_=>(double)a.Float()).ToArray();
        if(Flag(actorFlags,3)||Flag(actorFlags,25))Skip(a,7);if(Flag(actorFlags,2))Skip(a,Vs(a));Skip(a,8);if(Flag(actorFlags,0))Skip(a,6);if(Flag(actorFlags,7))Ref(a);
        // Missing CHANGE_REFR_SCALE means no override. A unit-scale preview is
        // provisional until the selected base player reference proves it.
        double scale=Flag(actorFlags,4)?a.Float():1;if(!double.IsFinite(scale)||scale<=0||scale>10)throw new InvalidDataException("Invalid saved scale");
        List<int> Extra(BoundedBinary q,int depth=0){if(depth>32)throw new InvalidDataException("Extra-data nesting overflow");List<int> worn=[];byte t=q.U8();
            // Save803's 16 at 3147 frames charge/reference/hotkey/worn through
            // 3160. Independent Java/Python readers agree on all 89 item ends
            // and the exact animation boundary at 4262; this is no byte skip.
            switch(t){case 4:case 8:case 12:case 16:for(int i=0;i<t/4;i++)worn.AddRange(Extra(q,depth+1));break;
            case 22:case 23:worn.Add(t);break;case 24:Skip(q,19);break;case 28:case 33:case 112:Ref(q);break;case 47:q.Float();break;case 73:q.U8();break;
            // Required Save803 count/time-left/charge fields. Lengths follow
            // FallrimTools' independently authored cases 36, 39 and 40.
            case 36:Skip(q,2);break;case 39:Skip(q,4);break;case 40:q.Float();break;
            case 136:Skip(q,checked(Vs(q)*7));break;case 149:Ref(q);Skip(q,4);break;
            case 152:for(int i=Vs(q);i>0;i--)if(Ref(q)!=0&&q.U16()!=0xffff)Skip(q,36);Skip(q,4);break;
            case 153:uint x=Ref(q),y=Ref(q);int v=unchecked((int)q.U32());if(x==0&&y==0&&v==-2)q.String16();break;
            case 155:Ref(q);Skip(q,2);break;case 159:Skip(q,6);break;
            default:throw new InvalidDataException("Unsupported required inventory framing extra-data type "+t+" at "+q.Position);}
            return worn;}
        uint extraMask=(1u<<6)|(1u<<25)|(1u<<9)|(1u<<11)|(1u<<17)|(1u<<26)|(1u<<29)|(1u<<30)|(1u<<31)|(1u<<18);
        if((actorFlags&extraMask)!=0)for(int i=Vs(a,1024);i>0;i--)Extra(a);
        if(!Flag(actorFlags,5)&&!Flag(actorFlags,27))throw new InvalidDataException("Saved worn inventory unavailable");
        List<LeanWorn> items=[];int inventory=Vs(a);for(int i=0;i<inventory;i++){token.ThrowIfCancellationRequested();uint id=Ref(a);int count=unchecked((int)a.U32());List<int> markers=[];for(int j=Vs(a,1024);j>0;j--)markers.AddRange(Extra(a));if(markers.Count>0){if(count<=0)throw new InvalidDataException("Worn count invalid");items.Add(new(Canon(id),count,markers.ToArray()));}}
        int initialType=Flag(actorFlags,3)||Flag(actorFlags,25)?6:4;
        var placement=new Transform(cell,null,position,rotation,"game-units","radians","saved/ACHR-initial-type"+initialType+" flags="+actorFlags.ToString("X8"),source,Status.Unknown);Semantics.Placement(placement);
        return new(new(name,sex,race,headParts,face,scale,placement,items.ToArray(),headerRace,Flag(npcFlags,25),Flag(actorFlags,4)),table,number,changes,a.Position,a.Remaining);
    }
}
