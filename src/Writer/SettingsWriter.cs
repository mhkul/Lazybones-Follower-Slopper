using System.Text.Json;
using AutoFollower;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

static class SettingsWriter
{
    public static FormKey Key(FollowerRecord record)=>Key(record.Reference);
    public static FormKey Key(string reference){var p=reference.Split('|');return new(ModKey.FromFileName(p[0]),Convert.ToUInt32(p[1],16));}
    public static ResolvedFollowerSettings Resolve(JsonElement plan)
    {
        var actor=JsonSerializer.Deserialize<LeanActor>(plan.GetProperty("savedActor").GetRawText(),Wire.Options)!;
        if(!plan.TryGetProperty("followerSettings",out var settings)||settings.ValueKind==JsonValueKind.Null)return FollowerSettingsPolicy.Resolve(null,actor);
        var resolved=JsonSerializer.Deserialize<ResolvedFollowerSettings>(settings.GetRawText(),Wire.Options)!;
        // A null plugin marks an explicit legacy resolution; never turn it into
        // named defaults simply because the plan contains resolved values.
        var checkedSettings=FollowerSettingsPolicy.Resolve(resolved.PluginName==null?null:resolved.Values,actor);
        checkedSettings=checkedSettings with{NameBasis=resolved.NameBasis,ScaleBasis=resolved.ScaleBasis,BehaviorBasis=resolved.BehaviorBasis};
        if(JsonSerializer.Serialize(checkedSettings,Wire.Options)!=JsonSerializer.Serialize(resolved,Wire.Options))throw new Exception("Follower settings resolution changed or was edited; refresh the settings plan");
        return checkedSettings;
    }
    public static void Apply(Npc npc,ResolvedFollowerSettings settings)
    {
        var v=settings.Values;npc.Name=v.CharacterName;npc.Height=1;
        npc.Configuration.Level=v.LevelMode=="fixed"?new NpcLevel{Level=(short)v.MinimumLevel}:new PcLevelMult{LevelMult=(float)v.LevelMultiplier};
        npc.Configuration.CalcMinLevel=(short)v.MinimumLevel;npc.Configuration.CalcMaxLevel=(short)(v.MaximumLevel??0);
        npc.Class.SetTo(Key(settings.Class));npc.CombatStyle.SetTo(Key(settings.CombatStyle));npc.Voice.SetTo(Key(settings.Voice));
        npc.Perks=new();foreach(var perk in settings.Perks)npc.Perks.Add(new PerkPlacement{Perk=new FormLink<IPerkGetter>(Key(perk)),Rank=1});
        npc.ActorEffect=new();foreach(var spell in settings.Spells)npc.ActorEffect.Add(new FormLink<ISpellRecordGetter>(Key(spell)));
        if(v.MarriageEligible)npc.Factions.Add(new RankPlacement{Faction=new FormLink<IFactionGetter>(Key(FollowerSettingsPolicy.MarriageFaction)),Rank=0});
    }
    public static object Validate(INpcGetter npc,IPlacedNpcGetter placed,ResolvedFollowerSettings settings)
    {
        var v=settings.Values;void Check(bool pass,string reason){if(!pass)throw new Exception("Follower settings readback: "+reason);}
        Check(npc.Name?.String==v.CharacterName&&npc.Height==1&&placed.Scale==(float)v.Scale!,"name or single placed scale");
        Check(npc.Class.FormKey==Key(settings.Class)&&npc.CombatStyle.FormKey==Key(settings.CombatStyle)&&npc.Voice.FormKey==Key(settings.Voice),"class/combat/voice");
        Check(npc.Configuration.CalcMinLevel==v.MinimumLevel&&npc.Configuration.CalcMaxLevel==(v.MaximumLevel??0),"level range");
        Check(v.LevelMode=="fixed"?npc.Configuration.Level is INpcLevelGetter fixedLevel&&fixedLevel.Level==v.MinimumLevel:npc.Configuration.Level is IPcLevelMultGetter relative&&relative.LevelMult==(float)v.LevelMultiplier,"level behavior");
        Check((npc.Perks??[]).Select(p=>p.Perk.FormKey).SequenceEqual(settings.Perks.Select(Key))&&(npc.Perks??[]).All(p=>p.Rank==1),"perk package/ranks");
        Check((npc.ActorEffect??[]).Select(p=>p.FormKey).SequenceEqual(settings.Spells.Select(Key)),"spell package");
        var marriage=npc.Factions.Where(f=>f.Faction.FormKey==Key(FollowerSettingsPolicy.MarriageFaction)).ToArray();
        Check(v.MarriageEligible?marriage.Length==1&&marriage[0].Rank==0:marriage.Length==0,"optional marriage faction");
        Check(npc.Factions.Any(f=>f.Faction.FormKey==Key("Skyrim.esm|05C84D")&&f.Rank==0)&&npc.Factions.Any(f=>f.Faction.FormKey==Key("Skyrim.esm|05C84E")&&f.Rank==-1),"ordinary follower factions");
        return new{passed=true,values=v,npcHeight=npc.Height,placedScale=placed.Scale,classReference=settings.Class.Reference,voiceReference=settings.Voice.Reference,marriageFactionPresent=marriage.Length==1,dialogueRuntimeVerified=false};
    }
}
