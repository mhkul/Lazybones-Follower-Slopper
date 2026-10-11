namespace AutoFollower;

// D86 permits analogous runtime-assigned components to be omitted explicitly.
// This narrow observed XPMSE weapon-style scale is not a head/body material or
// an ordinary skeleton/physics dependency. Other saved transforms still reject.
public static class LeanRuntimeAssignmentPolicy
{
 // ACTM stores the physical node in Registration and its named adjustment
 // in Node. RaceMenu rebuilds this internal NPC lift from footwear metadata;
 // Heels Fix reads it as heel height. Never copy that cached lift into output.
 public static bool CachedHeelOffset(LeanSkeeNode node)
 {
  if(node.Scope!="transform"||node.Perspective!=0||node.Sex is not (0 or 1)||node.Node!="internal"||node.Registration!="NPC"||node.Values.Length is not (3 or 12))return false;
  if(node.Values.Any(v=>v.Type!=4||v.Text!=null||v.Number==null||!double.IsFinite(v.Number.Value)))return false;
  if(node.Values.Select(v=>(v.Key,v.Index)).Distinct().Count()!=node.Values.Length)return false;
  for(int i=0;i<3;i++){var v=node.Values.SingleOrDefault(v=>v.Key==31&&v.Index==i);if(v==null||(i<2?v.Number!=0:v.Number<0))return false;}
  if(node.Values.Length==3)return true;
  for(int i=0;i<9;i++){var v=node.Values.SingleOrDefault(v=>v.Key==32&&v.Index==i);if(v==null||v.Number!=(i%4==0?1:0))return false;}
  return true;
 }
 public static bool Omitted(LeanSkeeNode node)=>CachedHeelOffset(node)||node.Scope=="transform"&&node.Perspective==0&&
  node.Node.Equals("XPMSE.esp",StringComparison.OrdinalIgnoreCase)&&node.Registration=="WEAPON"&&
  node.Values.Length==1&&node.Values[0] is {Key:30,Type:4,Index:0,Number:>0 and <=10};
 public static string[] Warnings(LeanSkeeState state)=>state.Nodes.Where(Omitted).Select(n=>CachedHeelOffset(n)?
  "Saved heel lift is discarded. Footwear keeps its adjustment metadata; installed RaceMenu/Heels Fix reapplies it when equipped. No heel lift is baked into the follower.":
  "Saved XPMSE weapon scale on WEAPON ("+n.Values[0].Number!.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
  ") is omitted under the runtime-assignment policy. Ordinary skeleton and SMP dependencies remain required.").ToArray();
}
