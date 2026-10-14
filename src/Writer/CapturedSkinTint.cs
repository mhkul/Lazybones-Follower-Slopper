using System.Drawing;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

// A RaceMenu array index identifies a player tint entry. NPC TINI instead
// identifies the selected race's tint asset; arbitrary RGB needs no TIAS palette.
static class CapturedSkinTint
{
 public static Color Lighting(JsonElement tint)
 {
  uint color=tint.GetProperty("color").GetUInt32();uint alpha=color>>24;
  // QNAM has no opacity. Fold the captured skin opacity into RGB around
  // neutral grey, then truncate to the game's stored byte precision.
  // Skin TINC/TINV and the composed FaceTint remain independent inputs.
  int Channel(int shift)=>(int)((255*(255-alpha)+2*alpha*((color>>shift)&255))/510);
  return Color.FromArgb(0,Channel(16),Channel(8),Channel(0));
 }
 public static TintAssets Asset(IRaceGetter race,bool female,JsonElement tint)
 {
  if(tint.GetProperty("index").GetInt32()!=0)throw new InvalidDataException("Captured skin tint must be RaceMenu skin entry 0");
  string Norm(string path){path=AutoFollower.VirtualPath.Normalize(path);return path.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)?path[9..]:path;}
  string texture=Norm(tint.GetProperty("texture").GetString()??"");
  var masks=(female?race.HeadData?.Female:race.HeadData?.Male)?.TintMasks;
  var matches=masks?.Where(x=>x.MaskType==TintAssets.TintMaskType.SkinTone&&x.FileName!=null&&!x.FileName.IsNull&&Norm(x.FileName.GivenPath).Equals(texture,StringComparison.OrdinalIgnoreCase)).ToArray()??[];
  if(matches.Length!=1||!matches[0].Index.HasValue)throw new InvalidDataException("Captured existing race has no unique matching skin-tone tint asset for the exported custom color: "+texture);
  ushort index=matches[0].Index!.Value;
  if(masks!.Count(x=>x.Index==index)!=1)throw new InvalidDataException("Captured existing race skin-tone tint asset index is ambiguous");
  return matches[0].DeepCopy();
 }
 public static TintLayer Resolve(IRaceGetter race,bool female,JsonElement tint)
 {
  ushort index=Asset(race,female,tint).Index!.Value;
  uint color=tint.GetProperty("color").GetUInt32();
  // NPC TINV is a whole percentage. Use its nearest representable opacity;
  // keep RGB exact and omit TIAS so initialization cannot replace it by a preset.
  int percent=(int)(((color>>24)*100+127)/255);
  return new TintLayer{Index=index,Color=Color.FromArgb(0,(int)((color>>16)&255),(int)((color>>8)&255),(int)(color&255)),InterpolationValue=percent/100f,Preset=null};
 }
}
