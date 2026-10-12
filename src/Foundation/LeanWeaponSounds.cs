namespace AutoFollower;

// Only sound records called by the selected weapons are consumed. Bethesda
// sounds retain their ordinary masters; mod-owned descriptors and their exact
// category/output closure are private, with selected source audio preserved.
public static class LeanWeaponSounds
{
 public static bool IsHost(string key)=>new[]{"Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm"}.Contains(key.Split('|')[0],StringComparer.OrdinalIgnoreCase);
 public static string Reference(string path){
  string value=path.Replace('\\','/');
  if(value.StartsWith("data/",StringComparison.OrdinalIgnoreCase))value=value[5..];
  if(!value.StartsWith("sound/",StringComparison.OrdinalIgnoreCase))value="sound/"+value;
  value=VirtualPath.Normalize(value).ToLowerInvariant();
  return value;
 }
 public static string[] Resolve(IEnumerable<LeanRecord> weapons,Func<string,LeanRecord> get,Queue<(string Reference,string Consumer)> pending){
  HashSet<string> needed=new(StringComparer.OrdinalIgnoreCase),visited=new(StringComparer.OrdinalIgnoreCase);string currentWeapon="";
  void Visit(string key,string expected){
   if(IsHost(key))return;
   var record=get(key);if(record.Type!=expected)throw new InvalidDataException("Required private sound link type differs: "+key+" / "+record.Type+" expected "+expected);
   if(!visited.Add(key))return;needed.Add(key);
   if(record.Fields.ContainsKey("VMAD")||record.Fields.ContainsKey("CTDA"))throw new InvalidDataException("Required private sound has unsupported script/condition closure: "+key);
   void Child(string tag,string type){foreach(string child in record.Refs(tag))Visit(child,type);}
   if(record.Type=="SNDR"){
    if(record.Fields.Keys.Any(x=>!new[]{"EDID","CNAM","GNAM","SNAM","ANAM","ONAM","FNAM","LNAM","BNAM"}.Contains(x)))throw new InvalidDataException("Required standard sound descriptor has unsupported fields: "+key);
    var descriptor=record.Fields.GetValueOrDefault("CNAM",[]);
    if(descriptor.Count!=1||descriptor[0].Length!=4||BitConverter.ToUInt32(descriptor[0])!=0x1EEF540A)throw new InvalidDataException("Required sound descriptor type unsupported: "+key);
    Child("GNAM","SNCT");Child("SNAM","SNDR");Child("ONAM","SOPM");
    foreach(string path in record.Text("ANAM").Where(x=>x.Length>0))pending.Enqueue((Reference(path),currentWeapon+" private weapon sound "+key));
   }else if(record.Type=="SNCT"){
    if(record.Fields.Keys.Any(x=>!new[]{"EDID","FULL","FNAM","PNAM","VNAM","UNAM"}.Contains(x)))throw new InvalidDataException("Required sound category has unsupported fields: "+key);
    Child("PNAM","SNCT");
   }else if(record.Type=="SOPM"&&record.Fields.Keys.Any(x=>!new[]{"EDID","NAM1","FNAM","MNAM","CNAM","SNAM","ONAM","ANAM"}.Contains(x)))throw new InvalidDataException("Required sound output model has unsupported fields: "+key);
  }
  foreach(var weapon in weapons){currentWeapon=weapon.Canonical;visited.Clear();if(weapon.Type is not ("WEAP" or "AMMO"))throw new InvalidDataException("Equipment sound input is not WEAP/AMMO");foreach(string tag in weapon.Type=="WEAP"?new[]{"SNAM","XNAM","NAM7","TNAM","UNAM","NAM9","NAM8","YNAM","ZNAM"}:new[]{"YNAM","ZNAM"})foreach(string key in weapon.Refs(tag))Visit(key,"SNDR");
   if(weapon.Type=="AMMO"&&LeanAmmoInputs.CalledProjectile(weapon,get) is { } projectile){
    var data=projectile.Fields.GetValueOrDefault("DATA",[]);if(data.Count!=1||data[0].Length is not (84 or 88 or 92))throw new InvalidDataException("Projectile sound DATA layout unavailable: "+projectile.Canonical);
    foreach(int offset in new[]{40,56,60})if(projectile.Ref(BitConverter.ToUInt32(data[0],offset)) is { } sound)Visit(sound,"SNDR");
   }
  }
  return needed.Order(StringComparer.OrdinalIgnoreCase).ToArray();
 }
}
