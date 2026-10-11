using System.Text.RegularExpressions;

namespace AutoFollower;

// One immutable model for Basic, Advanced, request, plan and result. UI acknowledgment
// deliberately lives outside this engine contract.
public sealed record FollowerSettings(string? ModName=null,string? CharacterName=null,double? Scale=null,
    string LevelMode="player-relative",int MinimumLevel=10,int? MaximumLevel=80,double LevelMultiplier=1,
    string ClassId="warrior",string CombatStyleId="melee",string PerkPackageId="none",
    string SpellPackageId="none",string? VoiceId=null,bool MarriageEligible=false,string RaceMode="private");
public sealed record FollowerRecord(string Reference,string Type,string EditorId);
public sealed record FollowerOption(string Id,string Label,FollowerRecord[] Records,string? Description=null);
public sealed record FollowerClass(string Id,string Label,FollowerRecord Record,string CombatStyleId,string PerkPackageId,string SpellPackageId);
public sealed record FollowerVoice(string Id,string Label,FollowerRecord Record,ushort Sex,bool MarriageSupported);
public sealed record FollowerCatalog(FollowerClass[] Classes,FollowerOption[] CombatStyles,FollowerOption[] PerkPackages,FollowerOption[] SpellPackages,FollowerVoice[] Voices);
public sealed record FollowerSettingsContext(FollowerSettings Defaults,FollowerCatalog Catalog,string NameBasis,string ScaleBasis,string BehaviorBasis);
public sealed record FollowerSavePreview(string SavePath,string Sha256,LeanSavedState SavedState,FollowerSettingsContext Context,SkyrimInputFormats? Formats=null);
public sealed record ResolvedFollowerSettings(FollowerSettings Values,string? PluginName,string? AssetNamespace,FollowerRecord Class,FollowerRecord CombatStyle,FollowerRecord[] Perks,FollowerRecord[] Spells,FollowerRecord Voice,string NameBasis,string ScaleBasis,string BehaviorBasis);

public static class FollowerSettingsPolicy
{
    public static FollowerSavePreview PreviewSave(string path,CancellationToken token,SkyrimInputFormats? formats=null)
    {
        formats=SkyrimInputFormats.Resolve(formats);
        path=Path.GetFullPath(path);SafePaths.NoLinks(path);token.ThrowIfCancellationRequested();
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(input.Length>BoundedBinary.Limit)throw new InvalidDataException("Selected save exceeds the supported input bound");
        byte[] bytes=new byte[checked((int)input.Length)];input.ReadExactly(bytes);token.ThrowIfCancellationRequested();
        var saved=LeanSaveReader.Read(bytes,path,token,formats);return new(path,BoundedBinary.Digest(bytes),saved,Context(saved.Actor),formats);
    }
    public static LeanSavedState ReusePreview(FollowerSavePreview preview,string path,string digest,SkyrimInputFormats? formats=null)
    {
        if(SkyrimInputFormats.Resolve(preview.Formats)!=SkyrimInputFormats.Resolve(formats))throw new InvalidDataException("Saved-input profile changed after settings preview; refresh the selected inputs");
        if(!Path.GetFullPath(path).Equals(preview.SavePath,StringComparison.OrdinalIgnoreCase)||digest!=preview.Sha256)throw new InvalidDataException("Selected save changed after settings preview; select it again to refresh character defaults");
        return preview.SavedState;
    }
    static FollowerRecord R(uint id,string type,string editor)=>new("Skyrim.esm|"+id.ToString("X6"),type,editor);
    public static readonly FollowerCatalog Catalog=new(
        [new("warrior","Warrior (one-handed)",R(0x13176,"CLAS","CombatWarrior1H"),"melee","golldir","none"),
         new("two-handed","Warrior (two-handed)",R(0x1CE15,"CLAS","CombatWarrior2H"),"melee","mjoll","none"),
         new("barbarian","Barbarian",R(0x1CE16,"CLAS","CombatBarbarian"),"berserker","mjoll","none"),
         new("ranger","Ranger",R(0x13181,"CLAS","CombatRanger"),"ranged","archery","none"),
         new("mage","Mage",R(0x1E7D0,"CLAS","CombatMageDestruction"),"magic","marcurio","mage"),
         new("spellsword","Spellsword",R(0x13177,"CLAS","CombatSpellsword"),"magic","teldryn","spellsword"),
         new("conjurer","Conjurer",R(0x1CE14,"CLAS","CombatMageConjurer"),"magic","brelyna","conjurer"),
         new("necromancer","Necromancer",R(0xC969F,"CLAS","CombatMageNecro"),"magic","serana","necromancer"),
         new("battlemage","Battlemage (Witchblade)",R(0x13178,"CLAS","CombatWitchblade"),"magic","teldryn","battlemage"),
         new("nightblade","Nightblade",R(0x1317C,"CLAS","CombatNightblade"),"magic","one-handed","nightblade")],
        [new("melee","Melee",[R(0x3BE1B,"CSTY","csHumanMeleeLvl1")]),
         new("berserker","Berserker",[R(0x3D2C8,"CSTY","csHumanBerserkerLvl1")]),
         new("ranged","Ranged",[R(0x3BE1D,"CSTY","csHumanMissile")]),
         new("magic","Magic",[R(0x3BE1C,"CSTY","csHumanMagic")])],
        [new("none","None",[]),new("one-handed","One-handed basics",[R(0xBABE4,"PERK","Armsman00")]),
         new("two-handed","Two-handed basics",[R(0xBABE8,"PERK","Barbarian00")]),new("archery","Archery basics",[R(0xBABED,"PERK","Overdraw00")]),
         new("mjoll","Mjoll — Two-handed",[R(0x058F6C,"PERK","TowerOfStrength"),R(0x052D51,"PERK","ChampionsStance"),R(0x03AF83,"PERK","DeepWounds30"),R(0x03AF84,"PERK","Skullcrusher30"),R(0x0C5C05,"PERK","Limbsplitter30")],"TowerOfStrength, ChampionsStance, DeepWounds30, Skullcrusher30, Limbsplitter30. Rank 1; gear, spell and game AI conditions apply."),
         new("golldir","Golldir — Heavy armor and shield",[R(0x058F6C,"PERK","TowerOfStrength"),R(0x058F6F,"PERK","WellFitted"),R(0x052D50,"PERK","FightingStance"),R(0x058F68,"PERK","DeflectArrows")],"TowerOfStrength, WellFitted, FightingStance, DeflectArrows. Rank 1; gear, spell and game AI conditions apply."),
         new("marcurio","Marcurio — Shock mage",[R(0x053128,"PERK","MagicResistance30"),R(0x0D7999,"PERK","MageArmor30"),R(0x058200,"PERK","AugmentedShock"),R(0x0581F8,"PERK","Regeneration"),R(0x0581F9,"PERK","Respite"),R(0x0581F5,"PERK","Recovery50")],"MagicResistance30, MageArmor30, AugmentedShock, Regeneration, Respite, Recovery50. Rank 1; gear, spell and game AI conditions apply."),
         new("brelyna","Brelyna — Elemental conjurer",[R(0x053128,"PERK","MagicResistance30"),R(0x0D7999,"PERK","MageArmor30"),R(0x0CB419,"PERK","Atromancy"),R(0x0581E7,"PERK","AugmentedFlames"),R(0x0581F8,"PERK","Regeneration"),R(0x0581F4,"PERK","Recovery30")],"MagicResistance30, MageArmor30, Atromancy, AugmentedFlames, Regeneration, Recovery30. Rank 1; gear, spell and game AI conditions apply."),
         new("serana","Serana — Frost defense",[R(0x053128,"PERK","MagicResistance30"),R(0x0581EA,"PERK","AugmentedFrost")],"MagicResistance30, AugmentedFrost. Rank 1; gear, spell and game AI conditions apply."),
         new("frea","Frea — Dual wield",[R(0x051B1B,"PERK","CustomFit"),R(0x052D50,"PERK","FightingStance"),R(0x0C3678,"PERK","HackAndSlash60"),R(0x106257,"PERK","DualFlurry50"),R(0x03AF81,"PERK","SavageStrike")],"CustomFit, FightingStance, HackAndSlash60, DualFlurry50, SavageStrike. Rank 1; gear, spell and game AI conditions apply."),
         new("teldryn","Teldryn Sero — Spellsword",[R(0x0C44C0,"PERK","DestructionAdept50"),R(0x0581E7,"PERK","AugmentedFlames"),R(0x079342,"PERK","Armsman40"),R(0x05F56F,"PERK","Bladesman30"),R(0x079389,"PERK","AgileDefender40"),R(0x051B1B,"PERK","CustomFit"),R(0x052D50,"PERK","FightingStance"),R(0x0CB419,"PERK","Atromancy")],"DestructionAdept50, AugmentedFlames, Armsman40, Bladesman30, AgileDefender40, CustomFit, FightingStance, Atromancy. Rank 1; gear, spell and game AI conditions apply.")],
        [new("none","None",[]),
         S("fire","Destruction — Fire",(0x12FCD,"Flames"),(0x12FD0,"Firebolt")),
         S("frost","Destruction — Frost",(0x2B96B,"Frostbite"),(0x2B96C,"IceSpike")),
         S("shock","Destruction — Shock",(0x2DD2A,"Sparks"),(0x2DD29,"LightningBolt")),
         S("alteration","Alteration — Armor",(0x5AD5C,"Oakflesh"),(0x5AD5D,"Stoneflesh")),
         S("conjuration","Conjuration — Summons",(0x640B6,"ConjureFamiliar"),(0x204C3,"ConjureFlameAtronach")),
         S("illusion","Illusion — Stealth and Calm",(0x8F3EB,"Muffle"),(0x4DEE9,"Calm")),
         S("restoration","Restoration — Healing",(0x12FCC,"Healing"),(0x2F3B8,"FastHealing")),
         S("mage","Mage — Elements and defense",(0x12FD0,"Firebolt"),(0x2B96C,"IceSpike"),(0x2DD29,"LightningBolt"),(0x5AD5C,"Oakflesh")),
         S("conjurer","Conjurer — Summon and support",(0x204C3,"ConjureFlameAtronach"),(0x5AD5D,"Stoneflesh"),(0x12FCC,"Healing")),
         S("necromancer","Necromancer — Reanimate and frost",(0xC969E,"RaiseZombieLeftHand"),(0x2B96C,"IceSpike"),(0x12FCC,"Healing")),
         S("battlemage","Battlemage — Fire and armor",(0x12FD0,"Firebolt"),(0x5AD5D,"Stoneflesh"),(0x2F3B8,"FastHealing")),
         S("spellsword","Spellsword — Shock and recovery",(0x2DD29,"LightningBolt"),(0x5AD5C,"Oakflesh"),(0x12FCC,"Healing")),
         S("nightblade","Nightblade — Stealth and control",(0x8F3EB,"Muffle"),(0x4DEE9,"Calm"),(0x2DD2A,"Sparks")),
         S("healer","Healer — Self and other healing",(0x2F3B8,"FastHealing"),(0x12FD2,"HealOther"),(0x5AD5C,"Oakflesh"))],
        [new("FemaleEvenToned","Female — even toned",R(0x13ADD,"VTYP","FemaleEvenToned"),1,true),
         new("FemaleYoungEager","Female — young and eager",R(0x13ADC,"VTYP","FemaleYoungEager"),1,true),
         new("FemaleSultry","Female — sultry",R(0x13AE0,"VTYP","FemaleSultry"),1,true),
         new("FemaleCondescending","Female — condescending",R(0x13AE4,"VTYP","FemaleCondescending"),1,false),
         new("FemaleCommander","Female — commander",R(0x13AE3,"VTYP","FemaleCommander"),1,true),
         new("FemaleDarkElf","Female — dark elf",R(0x13AF3,"VTYP","FemaleDarkElf"),1,true),
         new("FemaleOrc","Female — orc",R(0x13AEB,"VTYP","FemaleOrc"),1,true),
         new("MaleEvenToned","Male — even toned",R(0x13AD2,"VTYP","MaleEvenToned"),0,true),
         new("MaleEvenTonedAccented","Male — even toned, accented",R(0xEA267,"VTYP","MaleEvenTonedAccented"),0,true),
         new("MaleYoungEager","Male — young and eager",R(0x13AD1,"VTYP","MaleYoungEager"),0,true),
         new("MaleBrute","Male — brute",R(0x13ADA,"VTYP","MaleBrute"),0,true),
         new("MaleDrunk","Male — drunk",R(0x13AD4,"VTYP","MaleDrunk"),0,true),
         new("MaleNord","Male — nord",R(0x13AE6,"VTYP","MaleNord"),0,true),
         new("MaleDarkElf","Male — dark elf",R(0x13AF2,"VTYP","MaleDarkElf"),0,true),
         new("MaleOrc","Male — orc",R(0x13AEA,"VTYP","MaleOrc"),0,true),
         new("MaleArgonian","Male — argonian",R(0x13AEE,"VTYP","MaleArgonian"),0,true),
         new("MaleKhajiit","Male — khajiit",R(0x13AEC,"VTYP","MaleKhajiit"),0,false)]);
    public static readonly FollowerRecord FollowerVoices=R(0xC78E7,"FLST","VoicesFollowerAll"),MarriageVoices=R(0x47A00,"FLST","VoicesMarriageAll"),MarriageFaction=R(0x19809,"FACT","PotentialMarriageFaction");
    static FollowerOption S(string id,string label,params (uint Id,string Editor)[] spells)=>new(id,label,spells.Select(x=>R(x.Id,"SPEL",x.Editor)).ToArray(),string.Join(", ",spells.Select(x=>Regex.Replace(x.Editor,"([a-z])([A-Z])","$1 $2")))+". Casting and target choices depend on game AI; ally healing is unverified.");
    static string DefaultVoice(LeanActor actor)=>actor.Sex switch{0=>"MaleEvenToned",1=>"FemaleEvenToned",_=>throw new InvalidDataException("Unsupported saved actor sex: "+actor.Sex)};
    public static FollowerSettings Defaults(LeanActor actor)=>new(ModName:DefaultModName(actor.Name),CharacterName:actor.Name,Scale:actor.Scale,VoiceId:DefaultVoice(actor));
    public static string DefaultModName(string name)
    {
        // Only the suggested name is sanitized. User edits are validated verbatim.
        string clean=Regex.Replace(name,"[^A-Za-z0-9 _-]","").Trim();
        if(clean.Length==0||!char.IsAsciiLetterOrDigit(clean[0]))clean="Character";
        return clean[..Math.Min(clean.Length,55)].TrimEnd()+" Follower";
    }
    public static void ValidateModName(string name)
    {
        if(!Regex.IsMatch(name,"\\A[A-Za-z0-9][A-Za-z0-9 _-]{0,63}\\z")||name.EndsWith(' ')||name!=name.Trim())
            throw new InvalidDataException("Mod name: use 1–64 ASCII letters, numbers, spaces, underscores or hyphens; start with a letter/number and omit trailing spaces or extensions");
        string first=name.Split(' ')[0].ToUpperInvariant();
        if(new[]{"CON","PRN","AUX","NUL","CLOCK$"}.Contains(first)||Regex.IsMatch(first,"^(COM|LPT)[0-9]$")||new[]{"Skyrim","Update","Dawnguard","HearthFires","Dragonborn"}.Contains(name,StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Mod name is reserved: "+name);
    }
    public static void RequireNameAvailable(ResolvedFollowerSettings settings,Func<string,bool> installed)
    {
        if(settings.PluginName!=null&&installed(settings.PluginName))throw new InvalidDataException("Mod name collides with an installed plugin: "+settings.PluginName+"; choose another name");
    }
    public static FollowerSettings SelectClass(FollowerSettings settings,string id)
    {
        var choice=Catalog.Classes.SingleOrDefault(x=>x.Id==id)??throw new InvalidDataException("Unsupported class: "+id);
        return settings with{ClassId=id,CombatStyleId=choice.CombatStyleId,PerkPackageId=choice.PerkPackageId,SpellPackageId=choice.SpellPackageId};
    }
    public static ResolvedFollowerSettings Resolve(FollowerSettings? settings,LeanActor actor)
    {
        LeanRacePolicy.Require(settings?.RaceMode??LeanRacePolicy.Private);
        var value=settings??new();value=value with{VoiceId=value.VoiceId??DefaultVoice(actor)};if(settings!=null&&value.ModName==null)value=value with{ModName=DefaultModName(actor.Name)};string character=value.CharacterName??actor.Name;double scale=value.Scale??actor.Scale;
        if(string.IsNullOrWhiteSpace(character)||character!=character.Trim()||character.Length>128||character.Any(char.IsControl))throw new InvalidDataException("Character name must contain 1–128 characters without leading/trailing spaces or control characters");
        if(!double.IsFinite(scale)||scale<=0||scale>10||(float)scale<=0)throw new InvalidDataException("Character scale must be representable above zero and at most 10");
        if(value.MinimumLevel<1||value.MinimumLevel>1000||value.MaximumLevel is {} cap&&(cap>1000||cap<value.MinimumLevel))throw new InvalidDataException("Level range must be ordered within 1–1000; blank maximum means no user cap");
        if(value.LevelMode is not ("fixed" or "player-relative"))throw new InvalidDataException("Level behavior must be fixed or player-relative");
        if(value.LevelMode=="fixed"&&(value.MinimumLevel!=value.MaximumLevel||value.LevelMultiplier!=1))throw new InvalidDataException("Fixed level requires equal minimum/maximum and a multiplier of 1");
        if(!double.IsFinite(value.LevelMultiplier)||value.LevelMultiplier<0.1||value.LevelMultiplier>10||Math.Abs(value.LevelMultiplier*1000-Math.Round(value.LevelMultiplier*1000))>0.000001)throw new InvalidDataException("Level multiplier must be 0.1–10 with at most three decimal places");
        var cls=Catalog.Classes.SingleOrDefault(x=>x.Id==value.ClassId)??throw new InvalidDataException("Unsupported class: "+value.ClassId);
        FollowerOption Option(FollowerOption[] options,string id,string field)=>options.SingleOrDefault(x=>x.Id==id)??throw new InvalidDataException("Unsupported "+field+": "+id);
        var combat=Option(Catalog.CombatStyles,value.CombatStyleId,"combat style");var perks=Option(Catalog.PerkPackages,value.PerkPackageId,"perk package");var spells=Option(Catalog.SpellPackages,value.SpellPackageId,"spell package");
        var voice=Catalog.Voices.SingleOrDefault(x=>x.Id==value.VoiceId)??throw new InvalidDataException("Voice lacks established ordinary follower dialogue support: "+value.VoiceId);
        if(voice.Sex!=actor.Sex)throw new InvalidDataException("Selected voice is unsupported for this actor sex: "+value.VoiceId);
        if(value.MarriageEligible&&!voice.MarriageSupported)throw new InvalidDataException("Selected voice lacks ordinary marriage dialogue support: "+value.VoiceId);
        string? plugin=null,ns=null;if(value.ModName!=null){ValidateModName(value.ModName);plugin=value.ModName+".esp";ns="autofollower/"+value.ModName.ToLowerInvariant();}
        value=value with{CharacterName=character,Scale=scale};
        return new(value,plugin,ns,cls.Record,combat.Records.Single(),perks.Records,spells.Records,voice.Record,value.CharacterName==actor.Name?"selected save (or identical override)":"user override",settings?.Scale==null?"selected save":"user selection", "application follower defaults / explicit selections; not captured player gameplay");
    }
    public static FollowerSettingsContext Context(LeanActor actor)=>new(Defaults(actor),Catalog with{Voices=Catalog.Voices.Where(x=>x.Sex==actor.Sex).ToArray()},"selected ESS character name",actor.ScaleFromChangeForm?"selected saved ACHR scale":"unchanged unit scale; selected base player reference proof required during build","application defaults: player-relative 1×, level 10–80, warrior/melee, no perks/spells, selected-sex even toned, marriage off");
    public static void RequireRecords(ResolvedFollowerSettings settings,Func<string,bool,LeanRecord> get)
    {
        foreach(var expected in new[]{settings.Class,settings.CombatStyle,settings.Voice,FollowerVoices}.Concat(settings.Perks).Concat(settings.Spells).Concat(settings.Values.MarriageEligible?[MarriageVoices,MarriageFaction]:[])){
            var origin=get(expected.Reference,true);var winner=get(expected.Reference,false);
            if(origin.Plugin!="Skyrim.esm"||origin.Type!=expected.Type||origin.Text("EDID").SingleOrDefault()!=expected.EditorId||winner.Type!=expected.Type)throw new InvalidDataException("Settings record unavailable or incompatible: "+expected.Reference+" / "+expected.EditorId);
        }
        if(!get(FollowerVoices.Reference,false).Refs("LNAM").Contains(settings.Voice.Reference,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Selected voice removed from effective ordinary follower dialogue list");
        if(settings.Values.MarriageEligible&&!get(MarriageVoices.Reference,false).Refs("LNAM").Contains(settings.Voice.Reference,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Selected voice removed from effective marriage dialogue list");
    }
}
