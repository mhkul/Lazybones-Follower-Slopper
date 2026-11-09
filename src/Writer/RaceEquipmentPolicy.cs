using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

// Immediate armor-parent matching follows the observed Skyrim 1.5.97 model
// application route (T12L), not recursive traversal of an armor-parent chain.
static class RaceEquipmentPolicy {
 static readonly ModKey Skyrim=ModKey.FromFileName("Skyrim.esm");
 static FormKey K(uint id)=>new(Skyrim,id);
 public static readonly FormKey Default=K(0x19);
 // Exact player race identities: do not extend to beast or arbitrary custom races.
 public static readonly FormKey[] Humans=new uint[]{0x13741,0x13742,0x13743,0x13744,0x13746,0x13747,0x13748,0x13749,0x8883C,0x8883D,0x88840,0x88844,0x88794,0xA82B9,0x88846,0x88884}.Select(K).ToArray();
 static readonly FormKey[] Beasts=new uint[]{0x13740,0x13745,0x8883A,0x88845}.Select(K).ToArray();
 static int Species(FormKey race)=>race==K(0x13740)||race==K(0x8883A)?1:race==K(0x13745)||race==K(0x88845)?2:0;
 public sealed record Decision(FormKey Parent,bool Human,string? Warning);
 public static Decision Choose(FormKey selected,FormKey parent,FormKey morph){
  if(Beasts.Contains(selected)){
   if(parent.IsNull||parent==Default)return new(selected,false,null);
   if(Species(parent)==Species(selected))return new(parent,false,null);
   return new(parent,false,"The captured beast race has a different authored armor parent. It was retained; species-specific fit is unverified.");
  }
  if(!parent.IsNull){
   if(Beasts.Contains(parent))return new(parent,false,null);
   if(parent==Default||Humans.Contains(parent))return new(Default,true,null);
   return new(parent,false,"The captured race uses an unrecognized armor parent. It was retained; ordinary armor and species-specific fit are unverified.");
  }
  if(Beasts.Contains(morph))return new(morph,false,"The captured race has no armor parent; its known beast MorphRace was used for armor compatibility. Species-specific fit remains unverified.");
  if(Humans.Contains(selected))return new(Default,true,null);
  if(selected.ModKey==Skyrim)return new(selected,false,"The captured race has no armor parent and is not a recognized playable race. Its own identity was retained; armor compatibility is unverified.");
  return new(Default,true,"The custom race has no armor parent. DefaultRace was used for ordinary humanoid armor; species-specific fit remains unverified.");
 }
 public static void Add(ArmorAddon addon,FormKey race){
  if(addon.Race.FormKey!=race&&!addon.AdditionalRaces.Any(x=>x.FormKey==race))addon.AdditionalRaces.Add(new FormLink<IRaceGetter>(race));
 }
 public static void Apply(Race race,Decision decision,IEnumerable<(ArmorAddon Addon,bool Wearable)> addons){
  race.ArmorRace.SetTo(decision.Parent);
  foreach(var entry in addons){
   Add(entry.Addon,decision.Parent);
   if(decision.Human&&entry.Wearable)foreach(var human in Humans)Add(entry.Addon,human);
  }
 }
 public static void ApplyArmors(Decision decision,FormKey privateRace,FormKey skin,IEnumerable<Armor> armors){
  foreach(var armor in armors)if(armor.FormKey!=skin&&armor.Race.FormKey==privateRace)armor.Race.SetTo(decision.Parent);
 }
 public static bool AllowsParentLink(IMajorRecordGetter record,FormKey link,FormKey parent,FormKey skin)=>link==parent&&(record is IRaceGetter rr&&rr.ArmorRace.FormKey==parent||record is IArmorAddonGetter aa&&(aa.Race.FormKey==parent||aa.AdditionalRaces.Any(a=>a.FormKey==parent))||record is IArmorGetter ar&&ar.FormKey!=skin&&ar.Race.FormKey==parent);
}
