using System.Text;
namespace AutoFollower;

public sealed record LeanHeadMorphExclusion(string Canonical,int PartType,string Reference,string EditorId,string Basis);
public sealed record LeanHeadIdentityBinding(string Canonical,string EditorId,string ExportedShapeName,string SourceShapeName,string PackedSha256,string TopologySha256,string Basis,bool RetainSourceIdentity=false)
{
 public string OutputShapeName=>RetainSourceIdentity?EditorId:ExportedShapeName;
}
public static class LeanHeadMorphInputs
{
 public static LeanHeadIdentityBinding? BindIdentity(LeanRecord part,LeanExportInputs.Nif source,LeanExportInputs.Nif head,bool retainSourceIdentity=false)
 {
  string editor=part.Text("EDID").Single();var flags=part.Fields.GetValueOrDefault("DATA",[]);
  if(flags.Count!=1||flags[0].Length!=1)throw new InvalidDataException("HDPT identity flags unavailable: "+part.Canonical);
  if((flags[0][0]&8)!=0||Shapes(head).Any(x=>x.Name==editor))return null;
  // A renamed primary HDPT may retain the same exact packed template and
  // topology as its morphed finished export. Vertex counts alone never bind it.
  var installed=Signatures(source).ToArray();
  if(installed.Length!=1)throw new InvalidDataException("Primary HDPT/export identity mismatch lacks one captured dynamic template: "+part.Canonical+" / "+editor);
  var input=installed[0];var matches=Signatures(head).Where(x=>x.Descriptor==input.Descriptor&&x.Packed==input.Packed&&x.Topology==input.Topology).ToArray();
  if(matches.Length!=1)throw new InvalidDataException("Primary HDPT/export identity mismatch lacks unique exact packed-template/topology proof: "+part.Canonical+" / "+editor);
  return new(part.Canonical,editor,matches[0].Name,input.Name,input.Packed,input.Topology,retainSourceIdentity?"Exact captured packed-template/topology correspondence binds this exported shape to its original HDPT identity. Retained-race defaults and the owned copy consume one shape under the original EditorID; geometry and materials stay untouched.":"Captured selected HDPT model and finished export share exact packed vertices, descriptor and skin-partition topology. Private HDPT EditorID binds to the preserved export shape; morphed positions and materials stay untouched.",retainSourceIdentity);
 }
 static IEnumerable<(string Name,ulong Descriptor,string Packed,string Topology)> Signatures(LeanExportInputs.Nif nif)
 {
  if(nif.BsVersion!=100)yield break;
  for(int i=0;i<nif.Types.Length;i++)if(nif.Types[i]=="BSDynamicTriShape"){
   var q=new BoundedBinary(nif.Blocks[i]);uint name=q.U32();if(name>=nif.Strings.Length)throw new InvalidDataException("Head identity shape name absent");q.Take(checked(q.Count(100000)*4));q.Take(4+4+52+4+16);int skin=unchecked((int)q.U32());q.Take(8);ulong descriptor=q.U64();q.U16();q.U16();int bytes=q.Count();
   if(bytes!=0||skin<0||skin>=nif.Types.Length||nif.Types[skin] is not ("NiSkinInstance" or "BSDismemberSkinInstance"))continue;
   int partition=BitConverter.ToInt32(nif.Blocks[skin],4);if(partition<0||partition>=nif.Types.Length||nif.Types[partition]!="NiSkinPartition")throw new InvalidDataException("Head identity partition unavailable");var p=new BoundedBinary(nif.Blocks[partition]);p.Count(100000);int size=p.Count(),stride=p.Count(1024);ulong desc=p.U64();if(stride==0||desc!=descriptor||stride!=(int)(desc&15)*4||size%stride!=0)throw new InvalidDataException("Head identity packed framing invalid");
   yield return(nif.Strings[name],desc,BoundedBinary.Digest(p.Take(size)),BoundedBinary.Digest(p.Take(p.Remaining)));
  }
 }
 // Expression TRIs serve facial vertex deformation, not SMP bone motion.
 // Scope is extra Hair only, uniquely bound by preserved HDPT EditorID to an
 // already finished static export shape, with static installed source geometry.
 public static bool StaticExtraHair(LeanRecord part,LeanExportInputs.Nif source,LeanExportInputs.Nif head,bool belongsToHair=false)
 {
  var flags=part.Fields.GetValueOrDefault("DATA",[]);var types=part.Fields.GetValueOrDefault("PNAM",[]);
  if(!belongsToHair||flags.Count!=1||flags[0].Length!=1||(flags[0][0]&8)==0||types.Count!=1||types[0].Length!=4||BitConverter.ToUInt32(types[0]) is not (0 or 3))return false;
  string[] ids=part.Text("EDID");if(ids.Length!=1||ids[0].Length==0)return false;
  var exported=Shapes(head).Where(x=>x.Name==ids[0]).ToArray();var installed=Shapes(source).ToArray();
  return head.BsVersion==100&&source.BsVersion==100&&exported.Length==1&&exported[0].Type=="BSTriShape"&&exported[0].Skin>=0&&installed.Length>0&&installed.All(x=>x.Type=="BSTriShape"&&x.Skin>=0);
 }
 static IEnumerable<(string Name,string Type,int Skin)> Shapes(LeanExportInputs.Nif nif)
 {
  for(int i=0;i<nif.Types.Length;i++)if(nif.Types[i] is "BSTriShape" or "BSDynamicTriShape" or "BSSubIndexTriShape" or "NiTriShape" or "NiTriStrips"){
   string type=nif.Types[i];var q=new BoundedBinary(nif.Blocks[i]);uint name=q.U32();if(name>=nif.Strings.Length)throw new InvalidDataException("Head shape name absent");
   if(type is "NiTriShape" or "NiTriStrips"){yield return(nif.Strings[name],type,-1);continue;}
   q.Take(checked(q.Count(100000)*4));q.Take(4+4+52+4+16);int skin=unchecked((int)q.U32());yield return(nif.Strings[name],type,skin);
  }
 }
 public static string[] ShapeNames(LeanExportInputs.Nif nif)=>Shapes(nif).Select(x=>x.Name).ToArray();
 public static (int Type,string Path)[] Parts(LeanRecord part)
 {
  var kinds=part.Fields.GetValueOrDefault("NAM0",[]);var paths=part.Text("NAM1");if(kinds.Count!=paths.Length||kinds.Any(x=>x.Length!=4))throw new InvalidDataException("HDPT morph part framing invalid: "+part.Canonical);
  return kinds.Select((b,i)=>((int)BitConverter.ToUInt32(b),paths[i])).ToArray();
 }
}
