using System.Text;

namespace AutoFollower;

public sealed record LeanSkeeChunk(uint Tag,uint Version,byte[] Data);
public sealed record LeanSkeeValue(int Key,int Type,int Index,string? Text,double? Number);
public sealed record LeanSkeeNode(string Node,string Scope,int Sex,int Perspective,string? Registration,LeanSkeeValue[] Values);
public sealed record LeanSkeeState(LeanSkeeNode[] Nodes,bool PlayerOverlay,int EmptySets,int PlayerGroups);

// Consumed associations only. RaceMenu 0.4.16's OverrideInterface calls its
// string-table format "Version3" but assigns it value 2 (6379d939, header).
// Transform format 3 and STTB3 are independent version namespaces.
public static class LeanSkeeReader
{
    static uint Tag(string s)=>Encoding.ASCII.GetBytes(s).Aggregate(0u,(n,b)=>(n<<8)|b);
    public static LeanSkeeState Read(List<LeanSkeeChunk> chunks,ushort selectedSex,SkyrimInputFormats? formats=null)
    {
        formats=SkyrimInputFormats.Resolve(formats);
        string[] strings=[];bool table=false;int empty=0,groups=0;List<LeanSkeeNode> nodes=[];bool overlay=false;
        var st=chunks.Where(x=>x.Tag==Tag("STTB")).ToArray();
        if(st.Length>1)throw new InvalidDataException("Duplicate SKEE string table");
        if(st.Length==1){if(st[0].Version!=3)throw new InvalidDataException("Required SKEE associations support STTB3");var r=new BoundedBinary(st[0].Data);strings=Enumerable.Range(0,r.Count(100000)).Select(_=>r.String16()).ToArray();r.End();table=true;}
        int at=0;BoundedBinary? current=null;
        (LeanSkeeChunk Chunk,BoundedBinary Reader) Next(string expected){if(at>=chunks.Count||chunks[at].Tag!=Tag(expected))throw new InvalidDataException("SKEE association expected "+expected);current?.End();var c=chunks[at++];current=new(c.Data);return(c,current);}
        string String(BoundedBinary r){if(!table)throw new InvalidDataException("SKEE association has no STTB");uint id=r.U32();if(id>=strings.Length)throw new InvalidDataException("SKEE association string index invalid");return strings[id];}
        LeanSkeeValue[] Set(bool retain,uint version){var (c,r)=Next("OVST");if(c.Version!=version)throw new InvalidDataException("Unsupported/mismatched SKEE override set version");int count=r.Count(10000);if(count==0&&retain)empty++;List<LeanSkeeValue> values=[];HashSet<(int,int)> keys=[];
            for(int n=0;n<count;n++){var (v,q)=Next("OVRV");if(v.Version!=version)throw new InvalidDataException("Unsupported/mismatched SKEE override value version");int key=q.U16(),type=q.U8(),index=key==9||key is >=20 and <=24||key is >=30 and <=32?q.U8():-1;string? text=null;double? number=null;
                switch(type){case 0:break;case 1:ulong handle=q.U32()|((ulong)q.U32()<<32);text="TXST-handle:"+handle.ToString("X16");break;case 2:text=String(q);break;case 3:number=unchecked((int)q.U32());break;case 4:number=q.Float();if(!double.IsFinite(number.Value))throw new InvalidDataException("SKEE nonfinite override");break;case 5:byte b=q.U8();if(b>1)throw new InvalidDataException("SKEE Boolean invalid");number=b;break;default:throw new InvalidDataException("SKEE override type unsupported");}
                if(!keys.Add((key,index)))throw new InvalidDataException("Duplicate SKEE override key/index");if(type!=0)values.Add(new(key,type,index,text,number));}
            return values.ToArray();}
        void Registration(int sex,int perspective,string scope,string? parent,bool retain,uint version){int count=current!.Count(10000);for(int i=0;i<count;i++){var (c,q)=Next("NOEN");if(c.Version!=version)throw new InvalidDataException("Unsupported/mismatched SKEE node entry version");string node=String(q);var values=Set(retain&&sex==selectedSex,version);if(retain&&sex==selectedSex&&values.Length>0)nodes.Add(new(node,scope,sex,perspective,parent,values));}}
        void Multi(Action<int> visit){byte n=current!.U8();if(n>2)throw new InvalidDataException("SKEE multi-registration count invalid");HashSet<int> indices=[];for(int j=0;j<n;j++){int i=current!.U8();if(i>1||!indices.Add(i))throw new InvalidDataException("SKEE multi-registration index invalid");visit(i);}}
        while(at<chunks.Count){var c=chunks[at];
            if(c.Tag==Tag("ACTM")||c.Tag==Tag("NDEN")){current?.End();at++;var r=current=new BoundedBinary(c.Data);ulong handle=r.U32()|((ulong)r.U32()<<32);bool player=(uint)handle==0x14;if(!player){current=null;continue;}groups++;
                if(c.Tag==Tag("ACTM")){if(c.Version!=3)throw new InvalidDataException("Unsupported SKEE transform version");Multi(sex=>Multi(perspective=>{int count=current!.Count(10000);for(int i=0;i<count;i++){var (entry,q)=Next("NOTM");if(entry.Version!=3)throw new InvalidDataException("Unsupported SKEE transform entry version");string node=String(q);Registration(sex,perspective,"transform",node,player,3);}}));}
                else{if(c.Version is not (2 or 3))throw new InvalidDataException("Required SKEE node material layout supports versions 2 and 3; found "+c.Version);Multi(sex=>Registration(sex,-1,"material",null,player,c.Version));}current!.End();current=null;
            }else if(c.Tag==Tag("AOVL")){at++;if(c.Version!=1||c.Data.Length!=8)throw new InvalidDataException("Unsupported SKEE overlay actor framing");overlay|=BitConverter.ToUInt32(c.Data)==0x14;}
            else if(c.Tag==Tag("ACEN")||c.Tag==Tag("WPEN")||c.Tag==Tag("SKNR")){if(c.Data.Length<8)throw new InvalidDataException("SKEE actor material framing invalid");if(BitConverter.ToUInt32(c.Data)==0x14)throw new InvalidDataException("Required player SKEE "+Encoding.ASCII.GetString(BitConverter.GetBytes(c.Tag).Reverse().ToArray())+" associations remain unsupported");at++;}
            else at++;
        }
        return new(nodes.ToArray(),overlay,empty,groups);
    }
}
