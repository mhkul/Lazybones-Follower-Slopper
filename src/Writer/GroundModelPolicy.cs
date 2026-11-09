using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

static class GroundModelPolicy
{
 public static bool Retained(string model,IEnumerable<string> choices) {
  static string PathKey(string value) {var p=value.Replace('\\','/').TrimStart('/');return p.StartsWith("meshes/",StringComparison.OrdinalIgnoreCase)?p[7..]:p;}
  return choices.Any(c=>PathKey(c).Equals(PathKey(model),StringComparison.OrdinalIgnoreCase));
 }
 static bool GameMaster(ModKey key)=>new[]{"Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm"}.Contains(key.FileName.String,StringComparer.OrdinalIgnoreCase);
 public static FormKey[] Unavailable(IModelGetter model,Func<FormKey,bool> hasSelectedTexture)=>
  (model.AlternateTextures??[]).Select(a=>a.NewTexture.FormKey).Where(k=>!k.IsNull&&!GameMaster(k.ModKey)&&!hasSelectedTexture(k)).Distinct().ToArray();
}
