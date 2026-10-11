using AutoFollower;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.Text;

static class PlacementWriter
{
 public static LeanPlacement Read(JsonElement plan){var p=plan.GetProperty("placement").Deserialize<LeanPlacement>(Wire.Options)??throw new InvalidDataException("Resolved saved placement is required");var t=plan.GetProperty("savedActor").GetProperty("placement").Deserialize<Transform>(Wire.Options)!;LeanPlacementPolicy.Require(p,t);
  var cell=Verify(p.CellOrigin,"CELL");var container=Verify(p.ContainerOrigin,"CELL");if(p.WorldOrigin!=null)Verify(p.WorldOrigin,"WRLD");
  if(LeanPlacementPolicy.Interior(cell)!=p.Interior||LeanPlacementPolicy.Interior(container)!=p.Interior||!LeanPlacementPolicy.Same(LeanPlacementPolicy.World(cell),p.Worldspace)||!LeanPlacementPolicy.Same(LeanPlacementPolicy.World(container),p.Worldspace))throw new InvalidDataException("Location contract contradicts originating CELL fields");
  if(!p.Interior&&(p.PhysicalCellRecorded&&LeanPlacementPolicy.Coordinates(cell)!=(p.GridX!.Value,p.GridY!.Value)||(container.Flags&0x400)==0))throw new InvalidDataException("Exterior physical grid/persistent source differs");
  string[] masters=new[]{p.CellOrigin,p.ContainerOrigin,p.WorldOrigin}.Where(x=>x!=null).Select(x=>x!.Canonical.Split('|')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  if(!masters.Order().SequenceEqual(p.Masters.Order()))throw new InvalidDataException("Location master contract differs from origin identities");
  if(p.Interior){uint id=Key(p.ContainerCell).ID;if(p.InteriorBlock!=id%10||p.InteriorSubBlock!=id/10%10||p.CellOrigin.Groups.Single(g=>g.Type==2).Label!=p.InteriorBlock||p.CellOrigin.Groups.Single(g=>g.Type==3).Label!=p.InteriorSubBlock||p.ContainerCell!=p.Transform.Cell||p.WorldOrigin!=null)throw new InvalidDataException("Interior placement group contract invalid");}
  else{int x=LeanPlacementPolicy.Grid(t.Position[0]),y=LeanPlacementPolicy.Grid(t.Position[1]);if(p.GridX!=x||p.GridY!=y||p.BlockX!=LeanPlacementPolicy.Divide(x,32)||p.BlockY!=LeanPlacementPolicy.Divide(y,32)||p.SubBlockX!=LeanPlacementPolicy.Divide(x,8)||p.SubBlockY!=LeanPlacementPolicy.Divide(y,8)||p.WorldOrigin?.Canonical!=p.Worldspace||p.PhysicalCellRecorded&&(p.CellOrigin.Groups.Single(g=>g.Type==4).Label!=LeanPlacementPolicy.XY(p.BlockX.Value,p.BlockY.Value)||p.CellOrigin.Groups.Single(g=>g.Type==5).Label!=LeanPlacementPolicy.XY(p.SubBlockX.Value,p.SubBlockY.Value)))throw new InvalidDataException("Exterior placement group/grid contract invalid");}
  return p;
 }
 public static FormKey Key(string value){var parts=value.Split('|');if(parts.Length!=2)throw new InvalidDataException("Location canonical identity malformed: "+value);return new(ModKey.FromFileName(parts[0]),Convert.ToUInt32(parts[1],16));}
 static LeanRecord Verify(LeanLocationProof p,string tag){SafePaths.NoLinks(p.Backing);using var f=File.OpenRead(p.Backing);if(p.Offset<0||p.Bytes<24||p.Bytes>16777216||p.Offset+p.Bytes>f.Length)throw new InvalidDataException("Origin location range invalid: "+p.Canonical);f.Position=p.Offset;byte[] bytes=new byte[p.Bytes];f.ReadExactly(bytes);if(BoundedBinary.Digest(bytes)!=p.Sha256||!p.Plugin.Equals(p.Canonical.Split('|')[0],StringComparison.OrdinalIgnoreCase)||(BitConverter.ToUInt32(bytes,12)&0xffffff)!=Key(p.Canonical).ID||Encoding.ASCII.GetString(bytes,0,4)!=tag)throw new InvalidDataException("Origin location input changed: "+p.Canonical);
  byte[] payload=bytes[24..];if((BitConverter.ToUInt32(bytes,8)&0x40000)!=0)payload=BoundedBinary.Inflate(payload[4..],checked((int)BitConverter.ToUInt32(payload)));var fields=LeanRecordIndex.Fields(payload);var edid=fields.GetValueOrDefault("EDID",[]).Select(b=>Encoding.UTF8.GetString(b).TrimEnd('\0')).SingleOrDefault();if(edid!=p.EditorId)throw new InvalidDataException("Origin location EditorID changed: "+p.Canonical);
  f.Position=0;byte[] header=new byte[24];f.ReadExactly(header);byte[] head=new byte[BitConverter.ToUInt32(header,4)];f.ReadExactly(head);string[] masters=LeanRecordIndex.Fields(head).GetValueOrDefault("MAST",[]).Select(b=>Encoding.UTF8.GetString(b).TrimEnd('\0')).ToArray();if((bytes[15])!=masters.Length)throw new InvalidDataException("Location range is an override rather than origin: "+p.Canonical);
  LeanGroup[] Locate(long start,long end,LeanGroup[] parents){for(long at=start;at<end;){f.Position=at;byte[] h=new byte[24];f.ReadExactly(h);uint size=BitConverter.ToUInt32(h,4);bool group=Encoding.ASCII.GetString(h,0,4)=="GRUP";long next=at+size+(group?0:24);if(next>end||next<=at)throw new InvalidDataException("Origin group bounds invalid");if(at==p.Offset&&!group)return parents;if(group&&p.Offset>=at+24&&p.Offset<next)return Locate(at+24,next,[..parents,new(BitConverter.ToInt32(h,12),BitConverter.ToUInt32(h,8))]);at=next;}throw new InvalidDataException("Origin record is not in the evidenced groups");}
  if(!Locate(24+head.Length,f.Length,[]).SequenceEqual(p.Groups))throw new InvalidDataException("Origin location group provenance changed: "+p.Canonical);
  return new(p.Canonical,tag,p.Plugin,p.Backing,p.Offset,p.Bytes,BitConverter.ToUInt32(bytes,8),masters,fields,p.Sha256,p.Groups);
 }
}
