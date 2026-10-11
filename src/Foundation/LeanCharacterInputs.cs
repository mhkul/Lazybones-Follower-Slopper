namespace AutoFollower;

public sealed record LeanCharacter(string Race,ushort Sex,string[] ArmorRaces,string Skeleton,string Basis);
public static class LeanCharacterInputs
{
    public static LeanActor ResolveSavedDefaults(LeanActor actor,Func<string,LeanRecord> get,Func<LeanRecord?>? playerReference=null)
    {
        actor=ResolveSavedRace(actor,get);
        if(!actor.ScaleFromChangeForm){
            if(playerReference==null)throw new InvalidDataException("Unchanged saved scale needs the selected environment's player reference check");
            var placed=playerReference();
            // Player 0x14 is normally an intrinsic engine reference, with no
            // plugin ACHR. An active authored reference must be checked instead.
            if(placed!=null&&(placed.Type!="ACHR"||placed.Refs("NAME").SingleOrDefault()!="Skyrim.esm|000007"))throw new InvalidDataException("Unchanged saved scale has an incompatible selected base player ACHR/NPC link");
            var values=placed?.Fields.GetValueOrDefault("XSCL",[])??[];
            if(values.Count>1||values.Any(x=>x.Length!=4))throw new InvalidDataException("Selected base player scale field is malformed");
            double scale=values.Count==0?1:BitConverter.ToSingle(values[0]);
            if(scale!=1||actor.Scale!=1)throw new InvalidDataException("Unchanged saved scale has a non-unit selected player base scale; source-default settings handoff is required, no unit-scale substitution");
        }
        return actor;
    }
    public static LeanActor ResolveSavedRace(LeanActor actor,Func<string,LeanRecord> get)
    {
        if(actor.RaceFromChangeForm)return actor;
        if(actor.Race.Length>0)throw new InvalidDataException("Unchanged saved race already has an unverified canonical identity");
        if(string.IsNullOrWhiteSpace(actor.HeaderRaceEditorId))throw new InvalidDataException("Unchanged saved race header identity is absent");
        var player=get("Skyrim.esm|000007");if(player.Type!="NPC_")throw new InvalidDataException("Selected player base record is not NPC_");
        string key=player.Refs("RNAM").SingleOrDefault()??throw new InvalidDataException("Unchanged saved race needs the selected player base RNAM");
        var race=get(key);if(race.Type!="RACE"||race.Text("EDID").SingleOrDefault()!=actor.HeaderRaceEditorId)throw new InvalidDataException("Unchanged saved race header differs from selected player base RNAM: "+actor.HeaderRaceEditorId+" / "+key+"; no race substitution");
        return actor with{Race=key};
    }
    public static LeanCharacter Resolve(LeanActor actor,Func<string,LeanRecord> get)
    {
        if(actor.Sex>1)throw new InvalidDataException("Unsupported saved actor sex: "+actor.Sex);
        var race=get(actor.Race);if(race.Type!="RACE")throw new InvalidDataException("Saved character race is not RACE: "+actor.Race);
        // SSE RACE DATA: seven skill pairs, unknown ushort, height/weight pairs,
        // then the lower race flags at byte 32. Record winner supplies authority.
        var data=race.Fields.GetValueOrDefault("DATA",[]);
        if(data.Count!=1||data[0].Length<36)throw new InvalidDataException("Selected race DATA flags unavailable: "+actor.Race);
        if((BitConverter.ToUInt32(data[0],32)&1)==0)throw new InvalidDataException("Selected race is not marked playable: "+actor.Race+"; transformed/nonplayable forms require their concrete saved mechanism");
        HashSet<string> seen=new(StringComparer.OrdinalIgnoreCase);List<string> chain=[];string? key=actor.Race;
        while(key!=null){if(!seen.Add(key))throw new InvalidDataException("ArmorRace cycle: "+key);chain.Add(key);var r=get(key);if(r.Type!="RACE")throw new InvalidDataException("ArmorRace reference is not RACE: "+key);key=r.Refs("RNAM").SingleOrDefault();}
        string[] skeletons=race.Text("ANAM");if(skeletons.Length!=2||skeletons[actor.Sex].Length==0)throw new InvalidDataException("Selected-sex race skeleton unavailable: "+actor.Race+" sex "+actor.Sex);
        foreach(string morph in race.Refs("NAM8")){if(get(morph).Type!="RACE")throw new InvalidDataException("MorphRace reference is not RACE: "+morph);}
        return new(actor.Race,actor.Sex,chain.ToArray(),LeanModelInputs.Reference(skeletons[actor.Sex]),"Selected saved sex/race; winning playable RACE DATA/ANAM and ArmorRace chain, without vanilla substitution");
    }
}
