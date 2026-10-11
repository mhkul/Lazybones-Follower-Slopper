namespace AutoFollower;

public sealed record LeanGroup(int Type,uint Label);
public sealed record LeanLocationProof(string Canonical,string Plugin,string Backing,long Offset,int Bytes,string Sha256,string? EditorId,LeanGroup[] Groups);
public sealed record LeanPlacement(string Schema,string SavedReference,Transform Transform,bool Interior,
    string ContainerCell,string? Worldspace,int? GridX,int? GridY,int? BlockX,int? BlockY,int? SubBlockX,int? SubBlockY,
    int? InteriorBlock,int? InteriorSubBlock,LeanLocationProof CellOrigin,LeanLocationProof ContainerOrigin,LeanLocationProof? WorldOrigin,string[] Masters,bool PhysicalCellRecorded=true)
{
    public string DisplayLabel=>(CellOrigin.EditorId??WorldOrigin?.EditorId??(Interior?"saved interior location":"saved exterior location"))+" ("+(WorldOrigin?.Plugin??CellOrigin.Plugin)+")";
}

// Location records are evidence/container identities, never copied host overrides.
public static class LeanPlacementPolicy
{
    public static bool Same(string? a,string? b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    public static int Grid(double value)=>checked((int)Math.Floor(value/4096));
    public static int Divide(int value,int size)=>(int)Math.Floor((double)value/size);
    public static uint XY(int x,int y){if(x<short.MinValue||x>short.MaxValue||y<short.MinValue||y>short.MaxValue)throw new InvalidDataException("Exterior group coordinate exceeds signed 16-bit format");return (uint)((ushort)(short)y|((uint)(ushort)(short)x<<16));}
    public static bool Interior(LeanRecord record){var data=record.Fields.GetValueOrDefault("DATA",[]);if(record.Type!="CELL"||data.Count!=1||data[0].Length is not (1 or 2))throw new InvalidDataException("CELL DATA framing unavailable: "+record.Canonical);return (data[0][0]&1)!=0;}
    public static (int X,int Y) Coordinates(LeanRecord record){var fields=record.Fields.GetValueOrDefault("XCLC",[]);if(fields.Count!=1||fields[0].Length is not (8 or 12))throw new InvalidDataException("Exterior CELL XCLC framing unavailable: "+record.Canonical);return (BitConverter.ToInt32(fields[0],0),BitConverter.ToInt32(fields[0],4));}
    public static string? World(LeanRecord record){var groups=(record.Groups??[]).Where(x=>x.Type==1).ToArray();if(groups.Length>1)throw new InvalidDataException("Ambiguous CELL worldspace groups: "+record.Canonical);return groups.Length==0?null:record.Ref(groups[0].Label);}
    static LeanLocationProof Proof(LeanRecord r)=>new(r.Canonical,r.Plugin,r.Backing,r.Offset,r.Bytes,r.Sha256,r.Text("EDID").SingleOrDefault(),r.Groups??[]);
    public static LeanPlacement Resolve(Transform saved,Func<string,bool,LeanRecord> get,Func<string,int,int,string?> physical,Func<string,string> persistent)
    {
        Semantics.Placement(saved);if(saved.RotationUnits!="radians")throw new InvalidDataException("Saved placement must use radians");
        if(saved.Cell.StartsWith("dynamic/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Saved created CELL "+saved.Cell+" has no originating plugin/group identity; saved dynamic CELL reconstruction is required before standalone placement");
        LeanRecord Origin(string key){var r=get(key,true);if(!r.Plugin.Equals(key.Split('|')[0],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Originating location master/record missing: "+key+" (first available override is "+r.Plugin+")");return r with{Canonical=r.Plugin+"|"+key.Split('|')[1].ToUpperInvariant()};}
        var host=get(saved.Cell,false);var origin=Origin(saved.Cell);bool savedWorld=host.Type=="WRLD";if(savedWorld&&origin.Type!="WRLD")throw new InvalidDataException("Saved WRLD origin type differs: "+saved.Cell);
        bool inside=!savedWorld&&Interior(host);if(!savedWorld&&Interior(origin)!=inside)throw new InvalidDataException("Origin/winner interior status disagrees: "+saved.Cell);
        string cell=origin.Canonical,container=cell;string? world=savedWorld?origin.Canonical:World(host);LeanRecord? worldOrigin=null;int? x=null,y=null,bx=null,by=null,sx=null,sy=null,block=null,sub=null;bool recorded=true;
        if(inside){if(world!=null||World(origin)!=null||saved.Worldspace!=null)throw new InvalidDataException("Interior CELL has inconsistent WRLD relationship: "+cell);var groups=origin.Groups??[];block=checked((int)groups.Single(g=>g.Type==2).Label);sub=checked((int)groups.Single(g=>g.Type==3).Label);if(block!=Convert.ToUInt32(cell.Split('|')[1],16)%10||sub!=Convert.ToUInt32(cell.Split('|')[1],16)/10%10)throw new InvalidDataException("Interior origin block/subblock differs from CELL identity: "+cell);}
        else{
            if(world==null||!savedWorld&&!Same(World(origin),world)||saved.Worldspace!=null&&!saved.Worldspace.Equals(world,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Exterior CELL/WRLD relationship mismatch: "+cell);
            worldOrigin=Origin(world);world=worldOrigin.Canonical;if(worldOrigin.Type!="WRLD"||get(world,false).Type!="WRLD")throw new InvalidDataException("Location worldspace is not WRLD: "+world);
            x=Grid(saved.Position[0]);y=Grid(saved.Position[1]);bx=Divide(x.Value,32);by=Divide(y.Value,32);sx=Divide(x.Value,8);sy=Divide(y.Value,8);XY(bx.Value,by.Value);XY(sx.Value,sy.Value);
            container=persistent(world);var cp=get(container,false);var co=Origin(container);container=co.Canonical;if(Interior(cp)||Interior(co)||(cp.Flags&0x400)==0||(co.Flags&0x400)==0||!Same(World(cp),world)||!Same(World(co),world)||(co.Groups??[]).Any(g=>g.Type is 4 or 5))throw new InvalidDataException("Worldspace persistent CELL relationship invalid: "+container);
            // A saved WRLD/top CELL names the world. A grid with no stored CELL
            // is still representable by a persistent world reference at the exact
            // coordinates; do not fabricate a physical CELL or source proof.
            if(savedWorld||(host.Flags&0x400)!=0){var found=physical(world,x.Value,y.Value);recorded=found!=null;cell=found??container;host=get(cell,false);origin=Origin(cell);cell=origin.Canonical;}
            if(recorded){
                var grid=Coordinates(host);var originGrid=Coordinates(origin);
                if(Interior(host)||Interior(origin)||!Same(World(host),world)||!Same(World(origin),world)||grid.X!=x||grid.Y!=y||originGrid!=grid)throw new InvalidDataException("Saved exterior coordinates disagree with physical CELL/WRLD: "+cell);
                var groups=origin.Groups??[];if(groups.Single(g=>g.Type==4).Label!=XY(bx.Value,by.Value)||groups.Single(g=>g.Type==5).Label!=XY(sx.Value,sy.Value))throw new InvalidDataException("Exterior origin block/subblock differs from XCLC: "+cell);
            }
        }
        var target=saved with{Cell=cell,Worldspace=world,WorldspaceStatus=Status.Observed,Context=saved.Context+"; selected CELL/WRLD origin and container"+(recorded?"":"; no stored physical CELL: world grid represented through persistent container")};
        var result=new LeanPlacement("lean-saved-placement/v1",saved.Cell,target,inside,container,world,x,y,bx,by,sx,sy,block,sub,Proof(origin),Proof(Origin(container)),worldOrigin==null?null:Proof(worldOrigin),new[]{cell,container,world}.Where(k=>k!=null).Select(k=>k!.Split('|')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),recorded);
        Require(result,target);return result;
    }
    public static void Require(LeanPlacement p,Transform actor)
    {
        Semantics.Placement(actor);if(p.Schema!="lean-saved-placement/v1"||p.Transform.Cell!=actor.Cell||p.Transform.Worldspace!=actor.Worldspace||p.Transform.PositionUnits!=actor.PositionUnits||p.Transform.RotationUnits!=actor.RotationUnits||p.CellOrigin.Canonical!=actor.Cell||p.ContainerOrigin.Canonical!=p.ContainerCell||p.Worldspace!=actor.Worldspace||p.Interior!=(p.Worldspace==null)||p.Masters.Length==0)throw new InvalidDataException("Resolved placement contract disagrees with saved actor");
        if(!p.PhysicalCellRecorded&&(p.Interior||p.Transform.Cell!=p.ContainerCell||!Same(p.SavedReference,p.Worldspace)&&!Same(p.SavedReference,p.ContainerCell)))throw new InvalidDataException("Unrecorded exterior grid is not evidenced by saved WRLD/persistent CELL identity");
        if(actor.Position.Concat(actor.Rotation).Any(v=>v!=(double)(float)v))throw new InvalidDataException("Saved transform cannot be represented exactly in ESP float fields");
        if(!p.Transform.Position.SequenceEqual(actor.Position)||!p.Transform.Rotation.SequenceEqual(actor.Rotation))throw new InvalidDataException("Resolved placement transform differs");
    }
}
