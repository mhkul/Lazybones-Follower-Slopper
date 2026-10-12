using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using AutoFollower;
static class Validate {
 public static void Run(string pluginPath,string masterPaths,string output,string? planPath=null,string? manifestPath=null) {
  using var mod=SkyrimMod.CreateFromBinaryOverlay(pluginPath,SkyrimRelease.SkyrimSE);
  var records=mod.EnumerateMajorRecords().ToDictionary(r=>r.FormKey);
  void Check(bool value,string reason){if(!value)throw new Exception(reason);}
  var planData=planPath==null?default:JsonDocument.Parse(File.ReadAllText(planPath)).RootElement;
  var warnedMissing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  if(planPath!=null&&planData.TryGetProperty("missingReferences",out var absent)&&absent.ValueKind==JsonValueKind.Array)foreach(var row in absent.EnumerateArray()){
   Check(row.GetProperty("status").GetString()=="Unavailable in selected loose/active-archive view; original safe reference preserved"&&row.GetProperty("consumers").GetArrayLength()>0&&!string.IsNullOrWhiteSpace(row.GetProperty("warning").GetString()),"Invalid missing-reference disposition");
   Check(warnedMissing.Add(VirtualPath.Normalize(row.GetProperty("reference").GetString()!).ToLowerInvariant()),"Duplicate missing-reference disposition");
  }
  string AssetKey(string path,string category){var key=VirtualPath.Normalize(path).ToLowerInvariant();if(key.StartsWith("data/"))key=key[5..];return key.StartsWith(category+"/")?key:category+"/"+key;}
  var retained=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  if(manifestPath!=null){var manifest=JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;foreach(var asset in manifest.GetProperty("assets").EnumerateArray())if(asset.TryGetProperty("mode",out var mode)&&mode.GetString()=="Retain dependency"){
   var source=asset.GetProperty("source");Check(source.GetProperty("kind").GetString()!="Declared generated export"&&source.GetProperty("sha256").GetString()!.Length==64,"Retained provider absent");
   var destination=asset.GetProperty("destination").GetString()!.Replace('/','\\');
   retained.Add(destination.StartsWith("meshes\\",StringComparison.OrdinalIgnoreCase)?destination[7..]:destination);}}
  bool ValidModel(string path)=>path.StartsWith("autofollower\\",StringComparison.OrdinalIgnoreCase)||retained.Contains(path)||warnedMissing.Contains(AssetKey(path,"meshes"));
  Check(mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small),"Missing ESL flag");
  Check(mod.ModHeader.Stats.Version==1.7f,"Wrong legacy header");
  var locals=records.Values.Where(r=>r.FormKey.ModKey==mod.ModKey).ToArray();
  Check(locals.All(r=>r.FormKey.ID>=0x800&&r.FormKey.ID<=0xFFF),"Legacy ID bounds");
  if(planPath==null){var registry=JsonDocument.Parse(File.ReadAllText(@"C:\Skyrim Auto Follower\prototypes\T08\identity-registry.json")).RootElement;
  Check(locals.Select(r=>r.FormKey.ID).Order().SequenceEqual(registry.GetProperty("localIds").EnumerateArray().Select(e=>e.GetUInt32()).Order()),"Identity registry mismatch");}
  else{var plan=JsonDocument.Parse(File.ReadAllText(planPath)).RootElement;Check(locals.Select(r=>r.FormKey.ID).Distinct().Count()==locals.Length,"Duplicate lean local IDs");Check(plan.GetProperty("schema").GetString()=="lean-export-input-plan/v1"&&plan.GetProperty("gaps").GetArrayLength()==0,"Lean generation has unresolved inputs");}
  var paths=JsonDocument.Parse(File.ReadAllText(masterPaths)).RootElement.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!,StringComparer.OrdinalIgnoreCase);
  var unresolved=new List<string>();var masterRecords=new Dictionary<ModKey,HashSet<FormKey>>();
  foreach(var master in mod.ModHeader.MasterReferences) {
   Check(paths.TryGetValue(master.Master.FileName.String,out var path)&&File.Exists(path),"Missing required master "+master.Master);
   using var src=SkyrimMod.CreateFromBinaryOverlay(path!,SkyrimRelease.SkyrimSE);
   masterRecords[master.Master]=src.EnumerateMajorRecords().Select(r=>r.FormKey).ToHashSet();
  }
  foreach(var rec in records.Values)foreach(var l in rec.EnumerateFormLinks().Where(l=>!l.FormKey.IsNull)) {
   if(l.FormKey.ModKey==mod.ModKey){if(!records.ContainsKey(l.FormKey))unresolved.Add(rec.FormKey+" -> "+l.FormKey);}
   else if(!masterRecords.TryGetValue(l.FormKey.ModKey,out var keys)||!keys.Contains(l.FormKey))unresolved.Add(rec.FormKey+" -> "+l.FormKey);
  }
  Check(unresolved.Count==0,"Unresolved emitted links: "+string.Join(',',unresolved));
  bool existingRace=planPath!=null&&LeanRacePolicy.IsExisting(SettingsWriter.Resolve(planData));
  var npc=mod.Npcs.Single();IRaceGetter race;
  if(planPath!=null&&manifestPath!=null){
   var captured=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!,"record-inputs.json"))).RootElement;
   var material=mod.TextureSets.Single(t=>t.FormKey==npc.HeadTexture.FormKey);
   var sourceKey=planData.GetProperty("savedActor").GetProperty("faceTextureSet").GetString();
   if(!string.IsNullOrEmpty(sourceKey)){
    bool found=false;
    foreach(var overlay in captured.GetProperty("overlays").EnumerateArray()){
     using var source=SkyrimMod.CreateFromBinaryOverlay(overlay.GetProperty("overlay").GetProperty("path").GetString()!,SkyrimRelease.SkyrimSE);
     foreach(var texture in source.TextureSets.Where(t=>t.FormKey==SettingsWriter.Key(sourceKey))){Check(!found,"Duplicate captured head TXST");found=true;Check(material.Flags==texture.Flags,"Captured face texture flags lost");}
    }
    Check(found,"Captured head TXST missing");
   }
   var assets=JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement.GetProperty("assets").EnumerateArray().ToDictionary(a=>a.GetProperty("assetReference").GetString()!.Replace('\\','/').ToLowerInvariant(),a=>a.GetProperty("destination").GetString()!,StringComparer.OrdinalIgnoreCase);
   string? Expected(int slot){var texture=planData.GetProperty("appearance").GetProperty("faceTextures").EnumerateArray().Where(t=>t.GetProperty("index").GetInt32()==slot).ToArray();if(texture.Length==0)return null;string key=AssetKey(texture.Single().GetProperty("texture").GetString()!,"textures");return assets.TryGetValue(key,out var target)?target[9..].Replace('/','\\'):warnedMissing.Contains(key)?key[9..].Replace('/','\\'):throw new Exception("Undeclared missing head texture: "+key);}
   string? PathOf(Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter<Mutagen.Bethesda.Skyrim.Assets.SkyrimTextureAssetType>? path)=>path==null||path.IsNull?null:path.GivenPath;
   var actual=new[]{PathOf(material.Diffuse),PathOf(material.NormalOrGloss),PathOf(material.GlowOrDetailMap),PathOf(material.Height),PathOf(material.Environment),PathOf(material.EnvironmentMaskOrSubsurfaceTint),PathOf(material.Multilayer),PathOf(material.BacklightMaskOrSpecular)};
   for(int slot=0;slot<actual.Length;slot++)Check(string.Equals(actual[slot],Expected(slot),StringComparison.OrdinalIgnoreCase),"Shader/TXST slot mapping differs: "+slot);
   foreach(var sound in mod.SoundDescriptors)foreach(var file in sound.SoundFiles){var key=LeanWeaponSounds.Reference(file.GivenPath);Check(assets.Values.Any(v=>v.Equals(key,StringComparison.OrdinalIgnoreCase))||warnedMissing.Contains(key),"Private sound path not packaged/retained or explicitly warned: "+key);}
   if(planData.TryGetProperty("privateSoundRecords",out var soundRecords)&&soundRecords.ValueKind==JsonValueKind.Array)foreach(var sound in soundRecords.EnumerateArray()){
    var key=SettingsWriter.Key(sound.GetString()!);Check(!records.Values.SelectMany(r=>r.EnumerateFormLinks()).Any(l=>l.FormKey==key),"Original private sound link survives: "+key);
   }
  }
  if(planPath!=null&&planData.TryGetProperty("headIdentityBindings",out var bindings)&&bindings.ValueKind==JsonValueKind.Array)foreach(var binding in bindings.EnumerateArray())Check(mod.HeadParts.Count(h=>h.EditorID==(binding.TryGetProperty("outputShapeName",out var outputShape)?outputShape.GetString():binding.GetProperty("exportedShapeName").GetString()))==1,"Private HDPT identity/export binding missing or duplicated");
  if(existingRace){Check(mod.Races.Count==0,"Existing race mode must not emit any RACE");var captured=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath!))!,"record-inputs.json"))).RootElement;Race? found=null;foreach(var overlay in captured.GetProperty("overlays").EnumerateArray()){using var source=SkyrimMod.CreateFromBinaryOverlay(overlay.GetProperty("overlay").GetProperty("path").GetString()!,SkyrimRelease.SkyrimSE);foreach(var r in source.Races.Where(r=>r.FormKey==npc.Race.FormKey)){Check(found==null,"Duplicate existing race input");found=r.DeepCopy();}}race=found??throw new Exception("Captured existing race missing");}
  else race=mod.Races.Single();
  if(!existingRace&&planPath!=null&&planData.TryGetProperty("privateRaceRecords",out var privateInputs)&&privateInputs.GetArrayLength()>0){
   string original=planData.GetProperty("savedActor").GetProperty("race").GetString()!.Split('|')[0];
   Check(!mod.ModHeader.MasterReferences.Any(m=>m.Master.FileName.String.Equals(original,StringComparison.OrdinalIgnoreCase)),"Copied private race abilities still require original race plugin");
   Check(!records.Values.SelectMany(r=>r.EnumerateFormLinks()).Any(l=>l.FormKey.ModKey.FileName.String.Equals(original,StringComparison.OrdinalIgnoreCase)),"Private ability closure left original race links");
   foreach(var ability in race.ActorEffect??[])Check(ability.FormKey.ModKey!=ModKey.FromFileName(original),"Private race ability not remapped");
  }
  FormKey outputRace=npc.Race.FormKey;var outfit=mod.Outfits.Single();var cell=mod.Cells.SelectMany(b=>b.SubBlocks).SelectMany(b=>b.Cells).Concat(mod.Worldspaces.Where(w=>w.TopCell!=null).Select(w=>w.TopCell!)).Single();var actor=cell.Persistent.OfType<IPlacedNpcGetter>().Single();
  Check(npc.FormKey.ID==0x800&&actor.FormKey.ID==0x801&&actor.Base.FormKey==npc.FormKey,"Exact actor identity");
  Check(npc.Race.FormKey==race.FormKey&&npc.WornArmor.FormKey.ID==0x803&&npc.DefaultOutfit.FormKey==outfit.FormKey,"Private record binding");
  bool female=planPath==null||planData.GetProperty("savedActor").GetProperty("sex").GetInt32()==1;
  Check(npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)==female,"NPC sex differs from saved actor");
  var skeleton=female?race.SkeletalModel?.Female:race.SkeletalModel?.Male;
  Check(skeleton!=null&&!skeleton.File.IsNull&&ValidModel(skeleton.File.GivenPath),"Selected-sex skeleton is neither packaged privately nor explicitly retained");
  var armorRace=race.ArmorRace.FormKey;
  var compatibleRaces=new HashSet<FormKey>{armorRace};
  if(planPath==null)Check(!armorRace.IsNull&&!race.MorphRace.IsNull,"Race compatibility absent");
  if(planPath==null)Check(armorRace==new FormKey(ModKey.FromFileName("Skyrim.esm"),0x13742),"Legacy ArmorRace differs");
  else{
   var selectedRace=SettingsWriter.Key(planData.GetProperty("savedActor").GetProperty("race").GetString()!);
   // Captured overlays contain each winner exactly once. Plan receipts can also
   // contain the originating RACE, which must not replace the effective winner.
   var inputFile=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath!))!,"record-inputs.json");
   var captured=JsonDocument.Parse(File.ReadAllText(inputFile)).RootElement;
   var raceInputs=new Dictionary<FormKey,Race>();
   foreach(var overlay in captured.GetProperty("overlays").EnumerateArray()){
    using var source=SkyrimMod.CreateFromBinaryOverlay(overlay.GetProperty("overlay").GetProperty("path").GetString()!,SkyrimRelease.SkyrimSE);
    foreach(var input in source.Races)Check(raceInputs.TryAdd(input.FormKey,input.DeepCopy()),"Duplicate captured winning race "+input.FormKey);
   }
   var original=raceInputs[selectedRace];
   if(existingRace){var expectedTint=CapturedSkinTint.Resolve(original,female,planData.GetProperty("appearance").GetProperty("tintLighting"));Check(npc.TintLayers.Count==1,"Existing-race NPC custom skin layer missing");var tint=npc.TintLayers.Single();Check(tint.Index==expectedTint.Index&&tint.Color==expectedTint.Color&&tint.InterpolationValue==expectedTint.InterpolationValue&&!tint.Preset.HasValue,"Captured custom skin color/opacity was replaced or uses a race palette");}
   else Check(npc.TintLayers.Count==0,"Private-race captured material behavior changed");
   Check(original.Flags==race.Flags&&original.BodyTemplate?.FirstPersonFlags==race.BodyTemplate?.FirstPersonFlags&&original.BodyPartData.FormKey==race.BodyPartData.FormKey,"Selected winning race anatomy/flags were replaced");
   FormKey Remap(FormKey key)=>!existingRace&&key==selectedRace?outputRace:key;
   Check(armorRace==Remap(original.ArmorRace.FormKey)&&race.MorphRace.FormKey==Remap(original.MorphRace.FormKey),"Winning ArmorRace/MorphRace were replaced");
   Check(race.Flags.HasFlag(Race.Flag.Playable),"Selected race is not playable");
   compatibleRaces.Clear();var key=selectedRace;
   while(!key.IsNull){Check(compatibleRaces.Add(Remap(key)),"Captured ArmorRace cycle");Check(raceInputs.TryGetValue(key,out var input),"Captured ArmorRace input missing "+key);key=input!.ArmorRace.FormKey;}
  }
  var skin=mod.Armors.Single(a=>a.FormKey==npc.WornArmor.FormKey);
  Check((existingRace||race.Skin.FormKey==skin.FormKey)&&skin.Race.FormKey==outputRace,"Skin race/NPC/race binding mismatch");
  var armorChecks=new List<object>();
  foreach(var armorKey in outfit.Items!.Select(i=>i.FormKey).Prepend(skin.FormKey)) {
   var armor=mod.Armors.Single(a=>a.FormKey==armorKey);
   Check(armor.Armature.Count>0,"Missing armature "+armorKey);
   foreach(var addonKey in armor.Armature.Select(i=>i.FormKey)) {
    var addon=mod.ArmorAddons.Single(a=>a.FormKey==addonKey);
    var effectiveArmorRace=armorRace.IsNull?race.FormKey:armorRace;
    Check(addon.Race.FormKey==effectiveArmorRace||addon.AdditionalRaces.Any(r=>r.FormKey==effectiveArmorRace),"Addon rejects engine-selected immediate ArmorRace: "+addonKey);
    Check(addon.Race.FormKey==race.FormKey||addon.AdditionalRaces.Any(r=>r.FormKey==race.FormKey),"Addon rejects direct private race: "+addonKey);
    var model=female?addon.WorldModel?.Female:addon.WorldModel?.Male;
    var first=female?addon.FirstPersonModel?.Female:addon.FirstPersonModel?.Male;
    Check(model!=null&&!model.File.IsNull||first!=null&&!first.File.IsNull,"Missing selected-sex biped model "+addonKey);
    if(model!=null&&!model.File.IsNull)Check(ValidModel(model.File.GivenPath),"Biped model is neither packaged privately nor explicitly retained "+addonKey);
    if(first!=null&&!first.File.IsNull)Check(ValidModel(first.File.GivenPath),"First-person model is neither packaged privately nor explicitly retained "+addonKey);
    var sharedSlot=armor.BodyTemplate!=null&&addon.BodyTemplate!=null&&(armor.BodyTemplate.FirstPersonFlags&addon.BodyTemplate.FirstPersonFlags)!=0;
    // Captured TNG female blank has no geometry; its slot 52 is not selected by
    // this skin's body/hands/feet mask. Independent asset validation verifies it
    // is actually empty. Do not widen skin slots to activate an unused blank.
    var blank=planPath==null&&armorKey==skin.FormKey&&addonKey.ID==0x82E&&model!.File.GivenPath.EndsWith("tng\\f_blank.nif",StringComparison.OrdinalIgnoreCase);
    Check(sharedSlot||blank,"No shared biped slot "+armorKey+" / "+addonKey);
    armorChecks.Add(new{armor=armorKey.ToString(),addon=addonKey.ToString(),primary=addon.Race.FormKey.ToString(),additional=addon.AdditionalRaces.Select(r=>r.FormKey.ToString()),model=model?.File.GivenPath,firstPerson=first?.File.GivenPath,sex=female?1:0,slots=(uint)addon.BodyTemplate!.FirstPersonFlags,sharedSlot,retainedEmptyBlank=blank});
   }
  }
  var plannedEquipment=planPath==null?Array.Empty<JsonElement>():planData.GetProperty("equipment").EnumerateArray().ToArray();
  bool IsWeapon(JsonElement e)=>e.TryGetProperty("type",out var type)&&type.GetString()=="WEAP";
  bool IsAmmo(JsonElement e)=>e.TryGetProperty("type",out var type)&&type.GetString()=="AMMO";
  Check(outfit.Items!.Count==(planPath==null?6:plannedEquipment.Count(e=>!IsWeapon(e)&&!IsAmmo(e))),"Included mesh-bearing armor differs from actual plan");
  var weaponEntries=npc.Items?.Where(e=>mod.Weapons.Any(w=>w.FormKey==e.Item.Item.FormKey)).ToArray()??[];
  Check(weaponEntries.Length==plannedEquipment.Count(IsWeapon),"Included weapon inventory differs from actual plan");
  foreach(var entry in weaponEntries){var weapon=mod.Weapons.Single(w=>w.FormKey==entry.Item.Item.FormKey);Check(weapon.Model!=null&&!weapon.Model.File.IsNull||!weapon.FirstPersonModel.IsNull,"Weapon has no third-person or first-person model call");if(weapon.Model!=null)Check(!weapon.Model.File.IsNull&&ValidModel(weapon.Model.File.GivenPath),"Weapon model is neither private nor explicitly retained");if(weapon.ScopeModel!=null&&!weapon.ScopeModel.File.IsNull)Check(ValidModel(weapon.ScopeModel.File.GivenPath),"Weapon scope model missing");if(!weapon.FirstPersonModel.IsNull){var stat=mod.Statics.Single(s=>s.FormKey==weapon.FirstPersonModel.FormKey);Check(stat.Model!=null&&ValidModel(stat.Model.File.GivenPath),"Weapon first-person model missing");}Check(entry.Item.Count>0,"Invalid weapon inventory count");}
  var ammoEntries=npc.Items?.Where(e=>mod.Ammunitions.Any(a=>a.FormKey==e.Item.Item.FormKey)).ToArray()??[];
  Check(ammoEntries.Length==plannedEquipment.Count(IsAmmo),"Included ammunition inventory differs from actual plan");
  if(ammoEntries.Length>0){
   var identities=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath))!,"records.json"))).RootElement.GetProperty("recordMap").EnumerateArray().ToDictionary(x=>x.GetProperty("source").GetString()!,x=>x.GetProperty("target").GetString()!);
   foreach(var planned in plannedEquipment.Where(IsAmmo)){
    var source=SettingsWriter.Key(planned.GetProperty("canonical").GetString()!);var target=identities[source.ToString()];var entry=ammoEntries.Single(e=>e.Item.Item.FormKey.ToString()==target);var ammo=mod.Ammunitions.Single(a=>a.FormKey==entry.Item.Item.FormKey);
    Check(entry.Item.Count==planned.GetProperty("count").GetInt32()&&entry.Item.Count>0,"Ammunition identity/count differs from actual plan");
    if(ammo.Model!=null&&!ammo.Model.File.IsNull)Check(ValidModel(ammo.Model.File.GivenPath),"Ammunition model is neither private nor explicitly retained");
    if(!ammo.Projectile.IsNull){var projectile=mod.Projectiles.Single(p=>p.FormKey==ammo.Projectile.FormKey);if(projectile.Model!=null&&!projectile.Model.File.IsNull)Check(ValidModel(projectile.Model.File.GivenPath),"Ammunition projectile model missing");if(!projectile.MuzzleFlashModel.IsNull)Check(ValidModel(projectile.MuzzleFlashModel.GivenPath),"Projectile muzzle model missing");}
   }
  }
  var resolvedPlacement=planPath==null?null:PlacementWriter.Read(planData);
  Check(cell.FormKey==(resolvedPlacement==null?new FormKey(ModKey.FromFileName("Skyrim.esm"),0x16DF2):PlacementWriter.Key(resolvedPlacement.ContainerCell))&&actor.MajorFlags.HasFlag(PlacedNpc.MajorFlag.Persistent),"Canonical persistent placement");
  if(resolvedPlacement is {Interior:false}){Check(mod.Cells.Count==0&&mod.Worldspaces.Single().FormKey==PlacementWriter.Key(resolvedPlacement.Worldspace!)&&mod.Worldspaces.Single().MajorRecordFlagsRaw==0x4000&&(cell.MajorRecordFlagsRaw&0x400)!=0,"Exterior persistent CELL/WRLD placement differs");}
  else{var b=mod.Cells.Single();Check(mod.Worldspaces.Count==0&&b.BlockNumber==(resolvedPlacement?.InteriorBlock??2)&&b.SubBlocks.Single().BlockNumber==(resolvedPlacement?.InteriorSubBlock??3),"Interior origin grouping differs");}
  Check((cell.MajorRecordFlagsRaw&0x4000)!=0,"Partial CELL flag absent");
  Check(!mod.ModHeader.MasterReferences.Any(m=>m.Master.FileName.String.Equals("Lux.esp",StringComparison.OrdinalIgnoreCase)),"Unexpected Lux master");
  Check(!records.Values.SelectMany(r=>r.EnumerateFormLinks()).Any(l=>l.FormKey.ModKey.FileName.String.Equals("Lux.esp",StringComparison.OrdinalIgnoreCase)),"Unexpected Lux reference");
  Check(npc.VirtualMachineAdapter==null,"Unexpected NPC adapter reference");
  object? settingsReadback=null;
  if(planPath!=null){var plan=JsonDocument.Parse(File.ReadAllText(planPath)).RootElement;var settings=SettingsWriter.Resolve(plan);settingsReadback=SettingsWriter.Validate(npc,actor,settings);if(settings.PluginName!=null)Check(mod.ModKey.FileName.String==settings.PluginName,"Named plugin mismatch");
   var position=plan.GetProperty("savedActor").GetProperty("placement").GetProperty("position").EnumerateArray().Select(x=>x.GetSingle()).ToArray();var rotation=plan.GetProperty("savedActor").GetProperty("placement").GetProperty("rotation").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
   Check(actor.Placement!.Position==new Noggog.P3Float(position[0],position[1],position[2])&&actor.Placement.Rotation==new Noggog.P3Float(rotation[0],rotation[1],rotation[2]),"Saved position/rotation changed by settings");
  }else Check(actor.Placement!.Position.X==-3897.552734375f&&actor.Placement.Position.Y==-1116.3406982421875f&&actor.Placement.Position.Z==6656f&&actor.Placement.Rotation.X==0.2665759325027466f&&actor.Placement.Rotation.Y==0&&actor.Placement.Rotation.Z==4.975196361541748f&&actor.Scale==0.95f,"Full captured transform mismatch");
  File.WriteAllText(output,JsonSerializer.Serialize(new {passed=true,settings=settingsReadback,character=new{sex=female?1:0,sourceRace=planPath==null?"Skyrim.esm|013742":planData.GetProperty("savedActor").GetProperty("race").GetString(),armorRace=armorRace.ToString(),morphRace=race.MorphRace.FormKey.ToString()},records=records.Count,newRecords=locals.Length,masters=masterRecords.Keys.Select(k=>k.FileName.String),unresolvedLinks=unresolved,warnedMissingReferences=warnedMissing.Order(StringComparer.OrdinalIgnoreCase).ToArray(),placement=new {cell=resolvedPlacement?.Transform.Cell??"Skyrim.esm|016DF2",worldspace=resolvedPlacement?.Worldspace,containerCell=cell.FormKey.ToString(),partialForm=true,position=actor.Placement.Position.ToString(),rotation=actor.Placement.Rotation.ToString(),scale=actor.Scale},outfitItems=outfit.Items.Select(i=>i.FormKey.ToString()),armorApplicability=armorChecks,raceMode=existingRace?"existing":"private",privateSkeleton=existingRace?null:skeleton!.File.GivenPath,existingSkeleton=existingRace?skeleton!.File.GivenPath:null,independentReadback=true,runtimeAcceptance=false},new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
  Console.WriteLine("Independent plugin readback passed: "+records.Count+" records");
 }
}
