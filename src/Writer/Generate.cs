using System.Text.Json;
using System.Drawing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using AutoFollower;

static class Generate {
 static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
 static FormKey K(string plugin,uint id)=>new(ModKey.FromFileName(plugin),id);
 public static void Run(string assetPath,string inputsPath,string output,bool bestEffort=false) {
  var buildWarnings=new List<BuildQualityWarning>();
  if(Directory.Exists(output))throw new InvalidOperationException("Unique output required");
  Directory.CreateDirectory(output);
  var m=JsonDocument.Parse(File.ReadAllText(assetPath)).RootElement;
  bool lean=m.TryGetProperty("schema",out var schema)&&schema.GetString()=="lean-private-assets/v1";
  var plan=lean?JsonDocument.Parse(File.ReadAllText(m.GetProperty("inputPlan").GetString()!)).RootElement:default;
  FormKey Canonical(string value){var pair=value.Split('|');return K(pair[0],Convert.ToUInt32(pair[1],16));}
  var actor=lean?plan.GetProperty("savedActor"):default;
  var followerSettings=lean?SettingsWriter.Resolve(plan):null;
  bool female=!lean||actor.GetProperty("sex").GetInt32()==1;
  var selectedRace=lean?Canonical(actor.GetProperty("race").GetString()!):K("Skyrim.esm",0x13742);
  bool existingRace=lean&&LeanRacePolicy.IsExisting(followerSettings!);
  if(lean&&actor.GetProperty("sex").GetInt32() is not (0 or 1))throw new Exception("Unsupported saved actor sex");
  var inp=JsonDocument.Parse(File.ReadAllText(inputsPath)).RootElement;
  var sources=new Dictionary<FormKey,IMajorRecordGetter>();
  var sourceProviders=new Dictionary<FormKey,ModKey>();
  var owners=new List<IDisposable>();
  var sourcePaths=new Dictionary<ModKey,string>();
  foreach(var ov in inp.GetProperty("overlays").EnumerateArray()) {
   var mod=SkyrimMod.CreateFromBinaryOverlay(ov.GetProperty("overlay").GetProperty("path").GetString()!,SkyrimRelease.SkyrimSE);owners.Add(mod);
   sourcePaths[mod.ModKey]=ov.GetProperty("source").GetString()!;
   foreach(var rec in mod.EnumerateMajorRecords()){sources.Add(rec.FormKey,rec);sourceProviders.Add(rec.FormKey,mod.ModKey);}
  }
  var plugin=ModKey.FromFileName(m.GetProperty("pluginIdentity").GetString()!);
  string Label(string role,string? name=null,uint? local=null)=>ReadableIds.For(plugin.FileName.String,role,name,local);
  if(followerSettings?.PluginName!=null&&plugin.FileName.String!=followerSettings.PluginName)throw new Exception("Named plugin identity differs from settings");
  var dst=new SkyrimMod(plugin,SkyrimRelease.SkyrimSE); dst.ModHeader.Flags=SkyrimModHeader.HeaderFlag.Small;dst.ModHeader.Stats.Version=1.7f;
  dst.ModHeader.Author="Skyrim Auto Follower";dst.ModHeader.Description="Generated follower from selected save assets; body variants share identity.";
  FormKey New(uint id)=>new(plugin,id);
  FormKey outputRace=existingRace?selectedRace:New(0x802);
  var map=new Dictionary<FormKey,FormKey>();var notes=new List<string>();var used=new Dictionary<FormKey,ISkyrimMajorRecord>();uint next=0x820;
  if(!existingRace&&lean)map[selectedRace]=outputRace;
  var privateRaceRecords=lean&&plan.TryGetProperty("privateRaceRecords",out var raceInputs)&&raceInputs.ValueKind==JsonValueKind.Array?raceInputs.EnumerateArray().Select(x=>Canonical(x.GetString()!)).ToHashSet():new HashSet<FormKey>();
  var privateSoundRecords=lean&&plan.TryGetProperty("privateSoundRecords",out var soundInputs)&&soundInputs.ValueKind==JsonValueKind.Array?soundInputs.EnumerateArray().Select(x=>Canonical(x.GetString()!)).ToHashSet():new HashSet<FormKey>();
  var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var a in m.GetProperty("assets").EnumerateArray()) {
   var original=a.GetProperty("assetReference").GetString()!;var dest=a.GetProperty("destination").GetString()!;
   if(!paths.TryAdd(Norm(original),dest)&&paths[Norm(original)]!=dest)throw new Exception("Path conflict");
  }
  var missingReferences=lean&&plan.TryGetProperty("missingReferences",out var missingInputs)&&missingInputs.ValueKind==JsonValueKind.Array?missingInputs.EnumerateArray().Select(x=>Norm(x.GetProperty("reference").GetString()!)).ToHashSet(StringComparer.OrdinalIgnoreCase):new HashSet<string>();
  string ResolveAsset(string value,string category) {
   var normalized=Norm(value);if(normalized.StartsWith(category+"/"))normalized=normalized[(category.Length+1)..];
   if(paths.TryGetValue(category+"/"+normalized,out var path))return path[(category.Length+1)..].Replace('/','\\');
   if(missingReferences.Contains(category+"/"+normalized))return normalized.Replace('/', '\\');
   if(bestEffort){VirtualPath.Normalize(category+"/"+normalized);buildWarnings.Add(BestEffortBuild.Finding("asset-reference","Uncaptured required asset: "+category+"/"+normalized,"asset-quality","A called asset is unavailable. The safe original reference is preserved; appearance or behavior may differ."));return normalized.Replace('/', '\\');}
   throw new Exception("Uncaptured required asset: "+category+"/"+normalized);
  }
  Model? ModelCopy(IModelGetter? model) {
   if(model==null||model.File.IsNull)return null;
   var copy=model.DeepCopy();
   if(m.TryGetProperty("groundShapeNames",out var shapeNames)) {
    string original="meshes/"+Norm(model.File.GivenPath).Replace("meshes/","");
    foreach(var proof in shapeNames.EnumerateArray().Where(p=>p.GetProperty("reference").GetString()==original)) {
     var index=proof.GetProperty("index").GetInt32();
     foreach(var alt in copy.AlternateTextures ?? new())
      if(alt.Index==index)alt.Name=proof.GetProperty("name").GetString()!;
    }
   }
   copy.File=ResolveAsset(model.File.GivenPath,"meshes");
   return copy;
  }
  IMajorRecordGetter Source(FormKey key) {
   if(sources.TryGetValue(key,out var rec))return rec;
   if(lean)throw new Exception("Required supporting record absent from selected winning input plan: "+key);
   if(!sourcePaths.TryGetValue(key.ModKey,out var path))throw new Exception("Missing supporting record "+key);
   var full=SkyrimMod.CreateFromBinaryOverlay(path,SkyrimRelease.SkyrimSE);owners.Add(full);
   // On-demand extension is limited to source-local effect/keyword records; no capture winner is invented.
   rec=full.EnumerateMajorRecords().FirstOrDefault(r=>r.FormKey==key)??throw new Exception("Supporting source record absent "+key);
   sources[key]=rec;notes.Add("Extended source-local dependency: "+key+" from "+path);return rec;
  }
  FormKey Clone(FormKey key) {
   if(key.IsNull||key.ID==0)return FormKey.Null;
   if(map.TryGetValue(key,out var done))return done;
   if(next>0xFFF)throw new Exception("Legacy ESL ID overflow");
   var src=Source(key);
   if(lean&&src is IMagicEffectGetter&&!privateRaceRecords.Contains(key)){notes.Add("Required original magic-effect master dependency: "+key+" / "+src.EditorID+"; host supplies gameplay scripts and effect art");return key;}
   var fk=New(next++);map[key]=fk;
   ISkyrimMajorRecord rec=src switch {
    IHeadPartGetter h=>Copy(new HeadPart(fk,SkyrimRelease.SkyrimSE),h),
    ITextureSetGetter t=>Copy(new TextureSet(fk,SkyrimRelease.SkyrimSE),t),
    IArmorGetter a=>Copy(new Armor(fk,SkyrimRelease.SkyrimSE),a),
    IWeaponGetter w=>Copy(new Weapon(fk,SkyrimRelease.SkyrimSE),w),
    IAmmunitionGetter a=>Copy(new Ammunition(fk,SkyrimRelease.SkyrimSE),a),
    IProjectileGetter p=>Copy(new Projectile(fk,SkyrimRelease.SkyrimSE),p),
    IStaticGetter s=>Copy(new Static(fk,SkyrimRelease.SkyrimSE),s),
    IArmorAddonGetter a=>Copy(new ArmorAddon(fk,SkyrimRelease.SkyrimSE),a),
    IFormListGetter f=>Copy(new FormList(fk,SkyrimRelease.SkyrimSE),f),
    IObjectEffectGetter e=>Copy(new ObjectEffect(fk,SkyrimRelease.SkyrimSE),e),
    ISpellGetter e=>Copy(new Spell(fk,SkyrimRelease.SkyrimSE),e),
    IMagicEffectGetter e=>Copy(new MagicEffect(fk,SkyrimRelease.SkyrimSE),e),
    IColorRecordGetter c=>Copy(new ColorRecord(fk,SkyrimRelease.SkyrimSE),c),
    ISoundDescriptorGetter s=>Copy(new SoundDescriptor(fk,SkyrimRelease.SkyrimSE),s),
    ISoundCategoryGetter s=>Copy(new SoundCategory(fk,SkyrimRelease.SkyrimSE),s),
    ISoundOutputModelGetter s=>Copy(new SoundOutputModel(fk,SkyrimRelease.SkyrimSE),s),
    _=>throw new Exception("Unsupported clone record "+src.GetType().Name+" "+key)
   };
   rec.EditorID=lean?Label(rec.GetType().Name,src.EditorID,fk.ID):"AF_"+fk.ID.ToString("X3")+"_"+src.EditorID;rec.IsCompressed=false;used[key]=rec;
   switch(rec) {
    case Ammunition a:
     a.Model=ModelCopy(a.Model);
     if(a.Icons!=null){if(!a.Icons.LargeIconFilename.IsNull)a.Icons.LargeIconFilename=ResolveAsset(a.Icons.LargeIconFilename.GivenPath,"textures");if(a.Icons.SmallIconFilename is {IsNull:false} icon)a.Icons.SmallIconFilename=ResolveAsset(icon.GivenPath,"textures");}
     if(a.Destructible!=null)foreach(var stage in a.Destructible.Stages)stage.Model=ModelCopy(stage.Model);
     if(!a.Projectile.IsNull)Clone(a.Projectile.FormKey);
     foreach(var sound in a.EnumerateFormLinks().Where(l=>privateSoundRecords.Contains(l.FormKey)))Clone(sound.FormKey);
     dst.Ammunitions.Add(a);break;
    case Projectile p:
     p.Model=ModelCopy(p.Model);
     if(p.Destructible!=null)foreach(var stage in p.Destructible.Stages)stage.Model=ModelCopy(stage.Model);
     if(!p.MuzzleFlashModel.IsNull)p.MuzzleFlashModel=ResolveAsset(p.MuzzleFlashModel.GivenPath,"meshes");
     foreach(var sound in p.EnumerateFormLinks().Where(l=>privateSoundRecords.Contains(l.FormKey)))Clone(sound.FormKey);
     dst.Projectiles.Add(p);break;
    case Weapon w:
     w.Model=ModelCopy(w.Model);w.ScopeModel=ModelCopy(w.ScopeModel);
     foreach(var sound in w.EnumerateFormLinks().Where(l=>privateSoundRecords.Contains(l.FormKey)))Clone(sound.FormKey);
     if(!w.FirstPersonModel.IsNull)Clone(w.FirstPersonModel.FormKey);
     if(!w.ObjectEffect.IsNull&&w.ObjectEffect.FormKey.ModKey!=ModKey.FromFileName("Skyrim.esm"))Clone(w.ObjectEffect.FormKey);
     dst.Weapons.Add(w);break;
    case Static s:s.Model=ModelCopy(s.Model);dst.Statics.Add(s);break;
    case HeadPart h:
     h.EditorID=src.EditorID; // FaceGen shape names identify these captured head parts.
     if(lean&&plan.TryGetProperty("headIdentityBindings",out var identities)&&identities.ValueKind==JsonValueKind.Array)foreach(var binding in identities.EnumerateArray().Where(x=>Canonical(x.GetProperty("canonical").GetString()!)==key)){
      if(h.EditorID!=binding.GetProperty("editorId").GetString())throw new Exception("Head identity binding differs from captured HDPT");h.EditorID=binding.TryGetProperty("outputShapeName",out var outputShape)?outputShape.GetString():binding.GetProperty("exportedShapeName").GetString();notes.Add("Private HDPT identity bound to preserved export shape: "+key+" / "+h.EditorID+"; exact captured packed-template/topology proof, no geometry/material edits");
     }
     if(lean&&plan.TryGetProperty("headMorphExclusions",out var excluded)&&excluded.ValueKind==JsonValueKind.Array)foreach(var input in excluded.EnumerateArray().Where(x=>Canonical(x.GetProperty("canonical").GetString()!)==key)){
      if(h.EditorID!=input.GetProperty("editorId").GetString()||h.Type is not (HeadPart.TypeEnum.Hair or HeadPart.TypeEnum.Misc)||!h.Flags.HasFlag(HeadPart.Flag.IsExtraPart))throw new Exception("Static hair applicability differs from validated input");
      if(h.Parts==null)throw new Exception("Validated static hair morph entries are absent");
      var matches=h.Parts.Where(p=>p.PartType.HasValue&&(int)p.PartType.Value==input.GetProperty("partType").GetInt32()&&p.FileName!=null&&Norm("meshes/"+p.FileName.GivenPath)==Norm(input.GetProperty("reference").GetString()!)).ToArray();if(matches.Length!=1)throw new Exception("Static hair morph entry absent/ambiguous");h.Parts.Remove(matches[0]);notes.Add("Unconsumed static extra-hair expression TRI entry removed: "+key+" / "+input.GetProperty("reference").GetString()+"; geometry and SMP retained");
     }
     h.Model=ModelCopy(h.Model);if(h.Parts!=null)foreach(var p in h.Parts)if(p.FileName!=null&&!p.FileName.IsNull)p.FileName=ResolveAsset(p.FileName.GivenPath,"meshes");
     h.ValidRaces.SetTo(New(0x80B));
     if(!h.TextureSet.IsNull)Clone(h.TextureSet.FormKey);
     foreach(var extra in h.ExtraParts)Clone(extra.FormKey);dst.HeadParts.Add(h);break;
    case TextureSet t:
     foreach(var p in typeof(TextureSet).GetProperties())if(p.PropertyType==typeof(AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimTextureAssetType>)) {
      var a=(AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimTextureAssetType>?)p.GetValue(t);if(a!=null&&!a.IsNull)p.SetValue(t,new AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimTextureAssetType>(ResolveAsset(a.GivenPath,"textures")));
     }
     dst.TextureSets.Add(t);break;
    case Armor a:
     // Both called ground models are required extraction inputs, never external notes.
     if(a.WorldModel!=null) {
      foreach(var model in new[]{a.WorldModel.Male,a.WorldModel.Female})if(model?.Model!=null)model.Model=ModelCopy(model.Model);
     }
     if(!a.ObjectEffect.IsNull&&a.ObjectEffect.FormKey.ModKey!=ModKey.FromFileName("Skyrim.esm"))Clone(a.ObjectEffect.FormKey);
     if(a.Armature!=null)foreach(var arm in a.Armature.ToArray()) {
      var aa=(IArmorAddonGetter)Source(arm.FormKey);
      var compatible=lean?plan.GetProperty("equipment").EnumerateArray().Where(e=>Canonical(e.GetProperty("canonical").GetString()!)==key).SelectMany(e=>e.GetProperty("addons").EnumerateArray()).Any(e=>Canonical(e.GetProperty("canonical").GetString()!)==arm.FormKey):aa.Race.FormKey==selectedRace||aa.AdditionalRaces.Any(r=>r.FormKey==selectedRace);
      if(!compatible){a.Armature.Remove(arm);notes.Add("Excluded race-inapplicable/meshless addon "+arm.FormKey+" from selected character equipment "+key);continue;}
      Clone(arm.FormKey);
     }
     // Acquisition scripts retain their exact quest dependency and are surfaced as a declared limit.
     if(a.VirtualMachineAdapter!=null)foreach(var sc in a.VirtualMachineAdapter.Scripts)notes.Add("Declared scripted-equipment dependency: "+key+" / "+sc.Name+" / "+string.Join(',',sc.Properties.OfType<IScriptObjectPropertyGetter>().Select(p=>p.Object.FormKey)));
     dst.Armors.Add(a);break;
    case ArmorAddon aa:
     if(lean&&plan.GetProperty("skin").GetProperty("addons").EnumerateArray().Any(x=>Canonical(x.GetString()!)==key))aa.FirstPersonModel=null;
     if(!lean&&key.ModKey==ModKey.FromFileName("Skyrim.esm")&&new uint[]{0x19386,0x1938C,0x1938D}.Contains(key.ID)) {
      aa.FirstPersonModel=null;aa.SkinTexture=null;aa.TextureSwapList=null;
      notes.Add("NPC-only private skin: uncaptured player first-person skin models are inapplicable and omitted; captured third-person female geometry/shaders retained: "+key);
     }
     if(female&&aa.WorldModel?.Female==null&&aa.WorldModel?.Male!=null) {
      aa.WorldModel.Female=aa.WorldModel.Male.DeepCopy();notes.Add("Captured female biped fallback retained explicitly: "+key);
     }
     if(aa.WorldModel!=null){if(female){aa.WorldModel.Female=ModelCopy(aa.WorldModel.Female);aa.WorldModel.Male=null;}else{aa.WorldModel.Male=ModelCopy(aa.WorldModel.Male);aa.WorldModel.Female=null;}}
     if(aa.FirstPersonModel!=null){if(female){if(lean&&aa.FirstPersonModel.Female==null&&aa.FirstPersonModel.Male!=null)aa.FirstPersonModel.Female=aa.FirstPersonModel.Male.DeepCopy();aa.FirstPersonModel.Female=ModelCopy(aa.FirstPersonModel.Female);aa.FirstPersonModel.Male=null;}else{aa.FirstPersonModel.Male=ModelCopy(aa.FirstPersonModel.Male);aa.FirstPersonModel.Female=null;}}
     // Skyrim replaces the actor race with its immediate ArmorRace before
     // testing ARMA RNAM/MODL. Direct private-race membership alone is insufficient.
     if(!lean){aa.Race.SetTo(selectedRace);aa.AdditionalRaces.Clear();}
     if(!aa.AdditionalRaces.Any(r=>r.FormKey==outputRace)&&aa.Race.FormKey!=outputRace)aa.AdditionalRaces.Add(new FormLink<IRaceGetter>(outputRace));
     var sourceArmorRace=((IRaceGetter)Source(selectedRace)).ArmorRace.FormKey;
     var effectiveArmorRace=sourceArmorRace.IsNull||sourceArmorRace==selectedRace?outputRace:sourceArmorRace;
     if(aa.Race.FormKey!=effectiveArmorRace&&!aa.AdditionalRaces.Any(r=>r.FormKey==effectiveArmorRace))aa.AdditionalRaces.Add(new FormLink<IRaceGetter>(effectiveArmorRace));
     if(aa.SkinTexture!=null) {
      if(female){if(!aa.SkinTexture.Female.IsNull)Clone(aa.SkinTexture.Female.FormKey);aa.SkinTexture.Male=new FormLinkNullable<ITextureSetGetter>();}
      else{if(!aa.SkinTexture.Male.IsNull)Clone(aa.SkinTexture.Male.FormKey);aa.SkinTexture.Female=new FormLinkNullable<ITextureSetGetter>();}
     }
     if(aa.TextureSwapList!=null){if(lean){var selected=female?aa.TextureSwapList.Female:aa.TextureSwapList.Male;if(!selected.IsNull)Clone(selected.FormKey);if(female)aa.TextureSwapList.Male=new FormLinkNullable<IFormListGetter>();else aa.TextureSwapList.Female=new FormLinkNullable<IFormListGetter>();}else aa.TextureSwapList=null;}
     dst.ArmorAddons.Add(aa);break;
    case FormList f:
     foreach(var item in f.Items){var member=Source(item.FormKey);if(member is not (ITextureSetGetter or IFormListGetter))throw new Exception("Native selected texture-swap list contains unsupported member: "+item.FormKey);Clone(item.FormKey);}
     dst.FormLists.Add(f);break;
    case ObjectEffect e:
     foreach(var link in e.EnumerateFormLinks().Where(l=>!l.FormKey.IsNull&&l.FormKey.ModKey!=ModKey.FromFileName("Skyrim.esm")))Clone(link.FormKey);
     dst.ObjectEffects.Add(e);break;
    case Spell e:
     foreach(var link in e.EnumerateFormLinks().Where(l=>privateRaceRecords.Contains(l.FormKey)))Clone(link.FormKey);
     dst.Spells.Add(e);break;
    case MagicEffect e:
     foreach(var link in e.EnumerateFormLinks().Where(l=>privateRaceRecords.Contains(l.FormKey)))Clone(link.FormKey);
     dst.MagicEffects.Add(e);break;
    case ColorRecord c:dst.Colors.Add(c);break;
    case SoundDescriptor s:
     for(int i=0;i<s.SoundFiles.Count;i++){string reference=LeanWeaponSounds.Reference(s.SoundFiles[i].GivenPath);s.SoundFiles[i]=new AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>("Data\\Sound\\"+ResolveAsset(reference,"sound"));}
     foreach(var link in s.EnumerateFormLinks().Where(l=>privateSoundRecords.Contains(l.FormKey)))Clone(link.FormKey);
     dst.SoundDescriptors.Add(s);break;
    case SoundCategory s:
     foreach(var link in s.EnumerateFormLinks().Where(l=>privateSoundRecords.Contains(l.FormKey)))Clone(link.FormKey);
     dst.SoundCategories.Add(s);break;
    case SoundOutputModel s:dst.SoundOutputModels.Add(s);break;
   }
   return fk;
  }
  var npc=new Npc(New(0x800),SkyrimRelease.SkyrimSE){EditorID="AF_SenAbersha",Name="Sen Abersha",Weight=0,Height=1,AIData=new AIData {Aggression=Aggression.Unaggressive,Confidence=Confidence.Brave,Assistance=Assistance.HelpsFriendsAndAllies,EnergyLevel=50,Responsibility=Responsibility.NoCrime}};
  // Config string filters must identify only this follower, including when
  // several generated followers are installed. Stable across alternative routes.
  if(lean&&plan.TryGetProperty("outputs",out var selectedOutputs)&&selectedOutputs.ValueKind!=JsonValueKind.Null)
   npc.EditorID=Label("NPC",followerSettings?.Values.CharacterName??actor.GetProperty("name").GetString());
  npc.Configuration.Flags=(female?NpcConfiguration.Flag.Female:0)|NpcConfiguration.Flag.Protected|NpcConfiguration.Flag.Unique|NpcConfiguration.Flag.AutoCalcStats;
  npc.Configuration.Level=new PcLevelMult{LevelMult=1};npc.Configuration.CalcMinLevel=10;npc.Configuration.CalcMaxLevel=80;npc.Configuration.SpeedMultiplier=100;npc.Configuration.DispositionBase=35;npc.Configuration.HealthOffset=100;npc.Configuration.MagickaOffset=50;npc.Configuration.StaminaOffset=50;
  string vanillaPath=SelectedMaster.Require(inp,"Skyrim.esm");
  using var vanilla=SkyrimMod.CreateFromBinaryOverlay(vanillaPath,SkyrimRelease.SkyrimSE);owners.Add(vanilla);
  FormKey ByEditor<T>(IEnumerable<T> group,string editor)where T:IMajorRecordGetter=>group.First(r=>r.EditorID==editor).FormKey;
  npc.Voice.SetTo(ByEditor(vanilla.VoiceTypes,"FemaleEvenToned"));npc.Class.SetTo(ByEditor(vanilla.Classes,"CombatWarrior1H"));
  npc.CombatStyle.SetTo(ByEditor(vanilla.CombatStyles,"csHumanMeleeLvl1"));
  npc.Factions.Add(new RankPlacement{Faction=new FormLink<IFactionGetter>(ByEditor(vanilla.Factions,"PotentialFollowerFaction")),Rank=0});
  npc.Factions.Add(new RankPlacement{Faction=new FormLink<IFactionGetter>(ByEditor(vanilla.Factions,"CurrentFollowerFaction")),Rank=-1});
  npc.Packages.Add(new FormLink<IPackageGetter>(ByEditor(vanilla.Packages,"DefaultSandboxEditorLocation512")));
  var race=new Race(New(0x802),SkyrimRelease.SkyrimSE);race.DeepCopyIn((IRaceGetter)Source(selectedRace));race.EditorID="AF_PrivateCapturedRace";race.IsCompressed=false;
  if(lean&&!race.Flags.HasFlag(Race.Flag.Playable))throw new Exception("Selected winning race is not playable: "+selectedRace);
  if(!existingRace)foreach(var ability in privateRaceRecords.OrderBy(x=>x.ID))Clone(ability);
  if(!existingRace){race.Skin.SetTo(New(0x803));if(!lean&&race.ArmorRace.IsNull)race.ArmorRace.SetTo(selectedRace);if(!lean&&race.MorphRace.IsNull)race.MorphRace.SetTo(selectedRace);}
  var skeletal=female?race.SkeletalModel?.Female:race.SkeletalModel?.Male;
  if(skeletal==null||skeletal.File.IsNull)throw new Exception("Selected-sex skeleton unavailable: "+selectedRace);
  if(!existingRace)skeletal.File=ResolveAsset(skeletal.File.GivenPath,"meshes");
  if(lean){if(female)race.SkeletalModel!.Male=null;else race.SkeletalModel!.Female=null;}
  // A fixed captured NPC never uses character-generation option lists; isolate their private defaults.
  foreach(var hd in new[]{race.HeadData?.Male,race.HeadData?.Female})if(hd!=null) {
   hd.HeadParts.Clear();hd.RacePresets.Clear();hd.AvailableHairColors.Clear();hd.FaceDetails.Clear();hd.AvailableMorphs=null;hd.TintMasks.Clear();hd.Model=null;hd.DefaultFaceTexture.SetTo(New(0x807));
  }
  notes.Add(existingRace?"Captured existing race is referenced without a RACE override; its installed skeleton provider is required.":"Private race preserves selected winning behavior/attacks/keywords and ArmorRace/MorphRace semantics, remapping source-race links to the private race. Null compatibility fields remain null. Unused generation choices are omitted for the fixed NPC.");
  var j=lean?plan.GetProperty("appearance"):JsonDocument.Parse(File.ReadAllText(m.GetProperty("faceGen").GetProperty("sourceProvenance").GetProperty("appearancePreset").GetProperty("path").GetString()!)).RootElement;
  if(lean){foreach(var h in actor.GetProperty("headParts").EnumerateArray())Clone(Canonical(h.GetString()!));foreach(var h in j.GetProperty("headParts").EnumerateArray()){string head=actor.GetProperty("headParts")[h.GetProperty("savedIndex").GetInt32()].GetString()!;if(!head.Equals(h.GetProperty("canonical").GetString(),StringComparison.OrdinalIgnoreCase))throw new Exception("Validated head identity differs from saved mapping");npc.HeadParts.Add(new FormLink<IHeadPartGetter>(map[Canonical(head)]));}npc.Weight=j.GetProperty("weight").GetSingle();npc.Name=actor.GetProperty("name").GetString();}
  else{foreach(var h in m.GetProperty("actor").GetProperty("headParts").EnumerateArray())Clone(K(h.GetProperty("plugin").GetString()!,h.GetProperty("localId").GetUInt32()));
  foreach(var h in j.GetProperty("headParts").EnumerateArray()) {
   var runtime=h.GetProperty("formId").GetUInt32();var canonical=m.GetProperty("actor").GetProperty("headParts").EnumerateArray().First(x=>x.GetProperty("runtimeId").GetUInt32()==runtime);
   npc.HeadParts.Add(new FormLink<IHeadPartGetter>(map[K(canonical.GetProperty("plugin").GetString()!,canonical.GetProperty("localId").GetUInt32())]));
  }}
  var hair=new ColorRecord(New(0x806),SkyrimRelease.SkyrimSE){EditorID="AF_SenHairColor",Color=Color.FromArgb(unchecked((int)(0xFF000000|(lean?j.GetProperty("hairColor"):j.GetProperty("actor").GetProperty("hairColor")).GetUInt32())))};dst.Colors.Add(hair);npc.HairColor.SetTo(hair.FormKey);
  var hptex=new TextureSet(New(0x807),SkyrimRelease.SkyrimSE){EditorID="AF_SenHeadTexture"};
  if(lean&&actor.GetProperty("faceTextureSet").GetString() is {Length:>0} capturedFaceTexture){hptex.DeepCopyIn((ITextureSetGetter)Source(Canonical(capturedFaceTexture)));hptex.EditorID="AF_SenHeadTexture";hptex.IsCompressed=false;}
  if(j.GetProperty("faceTextures").EnumerateArray().Any(t=>t.GetProperty("index").GetUInt32()>7))throw new Exception("RaceMenu face texture slot above 7 has no supported Skyrim TXST field mapping; preserve the export and resolve this concrete slot before generation");
  string? Tex(uint slot){var textures=j.GetProperty("faceTextures").EnumerateArray().Where(t=>t.GetProperty("index").GetUInt32()==slot).ToArray();return textures.Length==0?null:ResolveAsset(textures.Single().GetProperty("texture").GetString()!,"textures");}
  // RaceMenu stores shader slots, not the TXST subrecord numbering. Preserve
  // captured TXST flags/metadata and map the actual selected shader textures.
  hptex.Diffuse=Tex(0);hptex.NormalOrGloss=Tex(1);hptex.GlowOrDetailMap=Tex(2);hptex.Height=Tex(3);hptex.Environment=Tex(4);hptex.EnvironmentMaskOrSubsurfaceTint=Tex(5);hptex.Multilayer=Tex(6);hptex.BacklightMaskOrSpecular=Tex(7);dst.TextureSets.Add(hptex);npc.HeadTexture.SetTo(hptex.FormKey);
  if(!lean)map[K("Skyrim.esm",0x19390)]=hptex.FormKey;
  else if(actor.GetProperty("faceTextureSet").GetString() is {Length:>0} faceIdentity)map[Canonical(faceIdentity)]=hptex.FormKey;
  var defaults=lean?j.GetProperty("defaultMorphs"):j.GetProperty("morphs").GetProperty("default");var mf=defaults.GetProperty("morphs").EnumerateArray().Select(v=>v.GetSingle()).ToArray();npc.FaceMorph=new NpcFaceMorph();
  var morphNames=new[]{"NoseLongVsShort","NoseUpVsDown","JawUpVsDown","JawNarrowVsWide","JawForwardVsBack","CheeksUpVsDown","CheeksForwardVsBack","EyesUpVsDown","EyesInVsOut","BrowsUpVsDown","BrowsInVsOut","BrowsForwardVsBack","LipsUpVsDown","LipsInVsOut","ChinNarrowVsWide","ChinUpVsDown","ChinUnderbiteVsOverbite","EyesForwardVsBack","Unknown"};
  for(int i=0;i<morphNames.Length;i++)typeof(NpcFaceMorph).GetProperty(morphNames[i])!.SetValue(npc.FaceMorph,mf[i]);
  var presets=defaults.GetProperty("presets").EnumerateArray().Select(v=>v.GetUInt32()).ToArray();npc.FaceParts=new NpcFaceParts{Nose=presets[0],Unknown=presets[1],Eyes=presets[2],Mouth=presets[3]};npc.TextureLighting=Color.FromArgb(unchecked((int)(lean?j.GetProperty("tintLighting"):j.GetProperty("tintInfo")[0]).GetProperty("color").GetUInt32()));
  if(existingRace){npc.TintLayers.Add(CapturedSkinTint.Resolve((IRaceGetter)Source(selectedRace),female,j.GetProperty("tintLighting")));notes.Add("Follower-owned captured custom skin RGB/opacity binds to the original race skin-tone asset; no palette substitution or shared RACE override. NPC opacity uses native whole-percent precision.");}
  var skin=new Armor(New(0x803),SkyrimRelease.SkyrimSE){EditorID="AF_SenSkin",Name="Sen private skin",BodyTemplate=new BodyTemplate{FirstPersonFlags=(BipedObjectFlag)0x8C},Race=new FormLinkNullable<IRaceGetter>(race.FormKey)};
  if(lean){var input=plan.GetProperty("skin");skin.DeepCopyIn((IArmorGetter)Source(Canonical(input.GetProperty("originalSkin").GetString()!)));skin.EditorID="AF_SenSkin";skin.Name="Sen private skin";skin.Race.SetTo(race.FormKey);skin.Armature.Clear();foreach(var addon in input.GetProperty("addons").EnumerateArray())skin.Armature.Add(new FormLink<IArmorAddonGetter>(Clone(Canonical(addon.GetString()!))));notes.Add("Lean ordinary skin and applicable addons copied from selected record winners, retaining source flags. "+input.GetProperty("runtimeAssignmentOmission").GetString());}
  else{foreach(var id in new uint[]{0x19386,0x1938C,0x1938D}) {
   var fk=Clone(K("Skyrim.esm",id));var aa=(ArmorAddon)used[K("Skyrim.esm",id)];
   // Winning model shaders carry the captured female skin materials. Vanilla TXST would override them.
   aa.SkinTexture=null;aa.TextureSwapList=null;skin.Armature.Add(new FormLink<IArmorAddonGetter>(fk));
  }
  // Retain the captured TNG female blank addon; no male geometry or runtime-generated global skin is copied.
  skin.Armature.Add(new FormLink<IArmorAddonGetter>(Clone(K("TheNewGentleman.esp",0x812))));
  notes.Add("Captured runtime skin is TNG-generated with an empty disk armature; private skin explicitly binds captured female DarkElf body/hands/feet plus captured female blank addon. No TNG runtime dependency is required for this binding.");}
  skin.Race.SetTo(outputRace);dst.Armors.Add(skin);npc.WornArmor.SetTo(skin.FormKey);npc.Race.SetTo(outputRace);
  var outfit=new Outfit(New(0x804),SkyrimRelease.SkyrimSE){EditorID="AF_SenOutfit",Items=new()};
  if(lean){foreach(var eq in plan.GetProperty("equipment").EnumerateArray()){
   var key=Clone(Canonical(eq.GetProperty("canonical").GetString()!));
   if(eq.TryGetProperty("type",out var type)&&type.GetString() is "WEAP" or "AMMO"){
    npc.Items??=new();npc.Items.Add(new ContainerEntry{Item=new ContainerItem{Item=new FormLink<IItemGetter>(key),Count=eq.GetProperty("count").GetInt32()}});
   }else outfit.Items.Add(new FormLink<IOutfitTargetGetter>(key));
  }}
  else{var rawToCanonical=JsonDocument.Parse(File.ReadAllText(m.GetProperty("actor").GetProperty("canonicalRecordSource").GetProperty("path").GetString()!)).RootElement.GetProperty("slices").EnumerateArray().ToDictionary(s=>s.GetProperty("canonical").GetProperty("runtimeId").GetUInt32(),s=>K(s.GetProperty("canonical").GetProperty("plugin").GetString()!,s.GetProperty("canonical").GetProperty("localId").GetUInt32()));
  foreach(var eq in m.GetProperty("equipment").EnumerateArray().Where(e=>e.GetProperty("included").GetBoolean()))outfit.Items.Add(new FormLink<IOutfitTargetGetter>(Clone(rawToCanonical[Convert.ToUInt32(eq.GetProperty("item").GetString(),16)])));}
  dst.Outfits.Add(outfit);npc.DefaultOutfit.SetTo(outfit.FormKey);dst.Npcs.Add(npc);if(!existingRace)dst.Races.Add(race);
  var races=new FormList(New(0x80B),SkyrimRelease.SkyrimSE){EditorID="AF_SenValidRaces"};races.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(outputRace));dst.FormLists.Add(races);
  var rel=new Relationship(New(0x805),SkyrimRelease.SkyrimSE){EditorID="AF_SenPlayerRelationship",Rank=Relationship.RankType.Ally};rel.Parent.SetTo(npc.FormKey);rel.Child.SetTo(K("Skyrim.esm",7));dst.Relationships.Add(rel);
  var resolvedPlacement=lean?PlacementWriter.Read(plan):null;
  var cellKey=resolvedPlacement==null?K("Skyrim.esm",0x16DF2):Canonical(resolvedPlacement.ContainerCell);
  var origin=lean?null:vanilla.Cells.SelectMany(b=>b.SubBlocks.SelectMany(s=>s.Cells.Select(c=>new{block=b.BlockNumber,sub=s.BlockNumber,cell=c}))).Single(x=>x.cell.FormKey==cellKey);
  // Partial CELL supplies a container for children without overriding host cell data.
  // Skyrim/xEdit's Partial Form flag is bit 14; Mutagen 0.49 has no named CELL enum.
  var cell=new Cell(cellKey,SkyrimRelease.SkyrimSE){EditorID=resolvedPlacement?.ContainerOrigin.EditorId??origin?.cell.EditorID,Flags=resolvedPlacement==null?origin!.cell.Flags:resolvedPlacement.Interior?Cell.Flag.IsInteriorCell:0,MajorFlags=(Cell.MajorFlag)(0x4000|(resolvedPlacement is {Interior:false}?0x400:0))};
  var placement=lean?actor.GetProperty("placement"):m.GetProperty("savedPlacement");P3Float Vector(string key){var a=placement.GetProperty(key).EnumerateArray().Select(x=>x.GetSingle()).ToArray();return new P3Float(a[0],a[1],a[2]);}
  var placed=new PlacedNpc(New(0x801),SkyrimRelease.SkyrimSE){EditorID="AF_SenPlaced",MajorFlags=PlacedNpc.MajorFlag.Persistent,Scale=0.95f,Placement=new Placement{Position=Vector("position"),Rotation=Vector("rotation")}};placed.Base.SetTo(npc.FormKey);cell.Persistent.Add(placed);
  if(lean)placed.Scale=actor.GetProperty("scale").GetSingle();
  if(resolvedPlacement is {Interior:false}){var world=new Worldspace(Canonical(resolvedPlacement.Worldspace!),SkyrimRelease.SkyrimSE){EditorID=resolvedPlacement.WorldOrigin!.EditorId,MajorFlags=(Worldspace.MajorFlag)0x4000,TopCell=cell};dst.Worldspaces.Add(world);}
  else{var block=new CellBlock{BlockNumber=resolvedPlacement?.InteriorBlock??origin!.block,GroupType=GroupTypeEnum.InteriorCellBlock};var sub=new CellSubBlock{BlockNumber=resolvedPlacement?.InteriorSubBlock??origin!.sub,GroupType=GroupTypeEnum.InteriorCellSubBlock};sub.Cells.Add(cell);block.SubBlocks.Add(sub);dst.Cells.Add(block);}
  if(followerSettings!=null){SettingsWriter.Apply(npc,followerSettings);placed.Scale=(float)followerSettings.Values.Scale!;if(plan.TryGetProperty("followerSettings",out var settingsProof)&&settingsProof.ValueKind!=JsonValueKind.Null)foreach(var expected in new[]{followerSettings.Class,followerSettings.CombatStyle,followerSettings.Voice}.Concat(followerSettings.Perks).Concat(followerSettings.Spells)){
   var record=Source(SettingsWriter.Key(expected));bool compatible=expected.Type switch{"CLAS"=>record is IClassGetter,"CSTY"=>record is ICombatStyleGetter,"VTYP"=>record is IVoiceTypeGetter,"PERK"=>record is IPerkGetter,"SPEL"=>record is ISpellGetter,_=>false};if(!compatible)throw new Exception("Settings record type differs: "+expected.Reference);
  }}
  notes.Add("Origin-based minimal CELL/WRLD containers and exact saved persistent actor transform; physical exterior grid and persistent container remain distinct.");
  var nullKeys=dst.EnumerateMajorRecords().SelectMany(r=>r.EnumerateFormLinks()).Where(l=>l.FormKey.ID==0&&!l.FormKey.IsNull).Select(l=>l.FormKey).Distinct().ToArray();foreach(var key in nullKeys)map[key]=FormKey.Null;
  // Alternate texture tables in record models also require private texture records.
  foreach(var link in dst.EnumerateMajorRecords().SelectMany(r=>r.EnumerateFormLinks()).Select(l=>l.FormKey).Distinct().ToArray())
   if(!map.ContainsKey(link)&&sources.TryGetValue(link,out var source)&&source is ITextureSetGetter)Clone(link);
  dst.RemapLinks(map);
  if(lean)foreach(var key in privateSoundRecords)if(dst.EnumerateMajorRecords().SelectMany(r=>r.EnumerateFormLinks()).Any(l=>l.FormKey==key))throw new Exception("Private weapon sound closure left original sound link: "+key);
  if(!existingRace&&lean&&privateRaceRecords.Count>0){
   if(dst.EnumerateMajorRecords().SelectMany(r=>r.EnumerateFormLinks()).Any(l=>l.FormKey.ModKey==selectedRace.ModKey))throw new Exception("Private race still links its source plugin after ability closure: "+selectedRace.ModKey);
   notes.Add("Captured race abilities and source-local effects copied privately from selected winners; original behavior/conditions/art links preserved. Original race plugin dependency removed by remapped record closure.");
  }
  if(lean) {
   // All fixed local records keep their existing allocation and links.
   var roles=new Dictionary<uint,string>{{0x801,"Placed"},{0x802,"Race"},{0x803,"Skin"},{0x804,"Outfit"},{0x805,"Relationship"},{0x806,"HairColor"},{0x807,"HeadTexture"},{0x80B,"ValidRaces"}};
   foreach(var record in dst.EnumerateMajorRecords().Where(r=>r.FormKey.ModKey==plugin))
    if(roles.TryGetValue(record.FormKey.ID,out var role))record.EditorID=Label(role);
   var duplicates=dst.EnumerateMajorRecords().Where(r=>r.FormKey.ModKey==plugin&&r.EditorID!=null).GroupBy(r=>r.EditorID!,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).ToArray();
   if(duplicates.Length>0)throw new Exception("Generated EditorID collision: "+string.Join(", ",duplicates.Select(g=>g.Key)));
  }
  if(!lean){var registry=JsonDocument.Parse(File.ReadAllText(@"C:\Skyrim Auto Follower\prototypes\T08\identity-registry.json")).RootElement;
  foreach(var entry in registry.GetProperty("recordMap").EnumerateArray()) {
   var source=entry.GetProperty("source").GetString();var expected=entry.GetProperty("target").GetString();
   if(!map.Any(p=>p.Key.ToString()==source&&p.Value.ToString()==expected))throw new Exception("Stable ID registry mismatch: "+source);
  }
  var registered=registry.GetProperty("localIds").EnumerateArray().Select(e=>e.GetUInt32()).Order().ToArray();
  if(!dst.EnumerateMajorRecords().Where(r=>r.FormKey.ModKey==plugin).Select(r=>r.FormKey.ID).Order().SequenceEqual(registered))throw new Exception("Stable ID registry requires an explicit allocation amendment");}
  string DependencyReason(IMajorRecord r,FormKey target)=>r is Race rr&&rr.ActorEffect?.Any(e=>e.FormKey==target)==true?"Captured race ability (SPLO); original gameplay/effect dependencies remain installed":existingRace&&target==selectedRace?"Captured existing race and its installed behavior/skeleton are required":"Required "+r.GetType().Name+" link; preserve original behavior, material or compatibility";
  var dependencies=dst.EnumerateMajorRecords().SelectMany(r=>r.EnumerateFormLinks().Where(l=>!l.FormKey.IsNull&&l.FormKey.ModKey!=plugin).Select(l=>new {record=r.FormKey.ToString(),type=r.GetType().Name,target=l.FormKey.ToString(),master=l.FormKey.ModKey.FileName.String,reason=DependencyReason(r,l.FormKey)})).ToArray();
  var keywordProviders=new List<object>();var providerMasters=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  if(lean)foreach(var link in dst.EnumerateMajorRecords().SelectMany(r=>r.EnumerateFormLinks()).Where(l=>!l.FormKey.IsNull&&l.FormKey.ModKey!=plugin&&l.Type==typeof(IKeywordGetter)).DistinctBy(l=>l.FormKey)){
   // Captured winning keywords retain their identity and their actual host.
   // Older compact inputs may prove an ordinary keyword in its origin only;
   // they cannot discover an injected provider by searching unrelated plugins.
   ModKey provider=link.FormKey.ModKey;
   if(sources.TryGetValue(link.FormKey,out var source)){
    if(source is not IKeywordGetter){if(!bestEffort)throw new InvalidDataException("Captured keyword has wrong record type: "+link.FormKey);buildWarnings.Add(BestEffortBuild.Finding("keyword-provider","Captured keyword has wrong record type: "+link.FormKey,"plugin-quality","A keyword provider check failed. The original keyword link remains; behavior or loading may fail."));continue;}
    provider=sourceProviders[link.FormKey];
   }
   bool providerVerified=false;
   try{using var host=SkyrimMod.CreateFromBinaryOverlay(SelectedMaster.Require(inp,provider.FileName.String),SkyrimRelease.SkyrimSE);providerVerified=host.ModKey==provider&&host.Keywords.Any(k=>k.FormKey==link.FormKey&&!k.IsDeleted);}
   catch(Exception e)when(bestEffort && e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException){buildWarnings.Add(BestEffortBuild.Finding("keyword-provider",e.Message,"plugin-quality","A keyword provider could not be verified. Its original link remains; behavior or loading may fail."));}
   if(!providerVerified){if(!bestEffort)throw new InvalidDataException("Required selected keyword provider is absent or has changed: "+link.FormKey+" from "+provider);buildWarnings.Add(BestEffortBuild.Finding("keyword-provider","Unresolved keyword "+link.FormKey+" from "+provider,"plugin-quality","A keyword provider check failed. The original keyword link remains; behavior or loading may fail."));}
   if(provider!=link.FormKey.ModKey){providerMasters.Add(provider.FileName.String);keywordProviders.Add(new{target=link.FormKey.ToString(),provider=provider.FileName.String,reason="Captured winning injected keyword; preserve identity and required defining provider"});}
  }
  var required=dependencies.Select(d=>d.master).Concat(resolvedPlacement?.Masters??new[]{"Skyrim.esm"}).Concat(providerMasters).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x=="Skyrim.esm"?0:1).ThenBy(x=>x);
  var masters=lean?SelectedMaster.DependencyOrder(inp,required,bestEffort,buildWarnings):required.ToArray();
  foreach(string master in masters)dst.ModHeader.MasterReferences.Add(new MasterReference{Master=ModKey.FromFileName(master)});
  dst.BeginWrite.ToPath(Path.Combine(output,plugin.FileName)).WithLoadOrder(masters.Select(x=>ModKey.FromFileName(x)).Append(plugin)).WithDataFolder(SelectedMaster.HeaderFolder(inp,masters,bestEffort)).NoMastersListContentCheck().Write();
  PartialCell.Trim(Path.Combine(output,plugin.FileName),cell.FormKey,resolvedPlacement?.Worldspace==null?null:Canonical(resolvedPlacement.Worldspace));
  if(lean)SavedTransform.Preserve(Path.Combine(output,plugin.FileName),new[]{"position","rotation"}.SelectMany(k=>placement.GetProperty(k).EnumerateArray().Select(x=>x.GetSingle())).ToArray());
  File.WriteAllText(Path.Combine(output,"records.json"),JsonSerializer.Serialize(new{plugin=plugin.FileName.String,npcLocalId=0x800,referenceLocalId=0x801,raceLocalId=existingRace?(int?)null:0x802,raceMode=existingRace?"existing":"private",outputRace=outputRace.ToString(),skinLocalId=0x803,masters,notes,dependencies,keywordProviders,buildWarnings,recordMap=map.Select(p=>new{source=p.Key.ToString(),target=p.Value.ToString()}),records=dst.EnumerateMajorRecords().Select(r=>new{type=r.GetType().Name,id=r.FormKey.ID,editor=r.EditorID,links=r.EnumerateFormLinks().Select(l=>l.FormKey.ToString()).Distinct().ToArray()})},JsonOptions));
  foreach(var o in owners)o.Dispose();Console.WriteLine(JsonSerializer.Serialize(new{records=dst.EnumerateMajorRecords().Count(),masters,nextId=next}));
 }
 static string Norm(string s)=>s.Replace('\\','/').ToLowerInvariant().TrimStart('/');
 static HeadPart Copy(HeadPart x,IHeadPartGetter y){x.DeepCopyIn(y);return x;}
 static TextureSet Copy(TextureSet x,ITextureSetGetter y){x.DeepCopyIn(y);return x;}
 static Armor Copy(Armor x,IArmorGetter y){x.DeepCopyIn(y);return x;}
 static ArmorAddon Copy(ArmorAddon x,IArmorAddonGetter y){x.DeepCopyIn(y);return x;}
 static FormList Copy(FormList x,IFormListGetter y){x.DeepCopyIn(y);return x;}
 static ObjectEffect Copy(ObjectEffect x,IObjectEffectGetter y){x.DeepCopyIn(y);return x;}
 static Spell Copy(Spell x,ISpellGetter y){x.DeepCopyIn(y);return x;}
 static MagicEffect Copy(MagicEffect x,IMagicEffectGetter y){x.DeepCopyIn(y);return x;}
 static Weapon Copy(Weapon x,IWeaponGetter y){x.DeepCopyIn(y);return x;}
 static Ammunition Copy(Ammunition x,IAmmunitionGetter y){x.DeepCopyIn(y);return x;}
 static Projectile Copy(Projectile x,IProjectileGetter y){x.DeepCopyIn(y);return x;}
 static Static Copy(Static x,IStaticGetter y){x.DeepCopyIn(y);return x;}
 static ColorRecord Copy(ColorRecord x,IColorRecordGetter y){x.DeepCopyIn(y);return x;}
 static SoundDescriptor Copy(SoundDescriptor x,ISoundDescriptorGetter y){x.DeepCopyIn(y);return x;}
 static SoundCategory Copy(SoundCategory x,ISoundCategoryGetter y){x.DeepCopyIn(y);return x;}
 static SoundOutputModel Copy(SoundOutputModel x,ISoundOutputModelGetter y){x.DeepCopyIn(y);return x;}
}
