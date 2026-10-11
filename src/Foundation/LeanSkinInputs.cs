namespace AutoFollower;

public sealed record LeanSkin(string OriginalSkin,string[] Addons,string[] Models,string Basis,string RuntimeAssignmentOmission,string[]? ObodyBodyModels=null,string[]? AsIsFootModels=null);
public static class LeanSkinInputs
{
    public const string RuntimeAssignmentOmission = "D86: TNG and analogous runtime-assigned components are omitted. Their assignments and appearance are not reproduced; alternate-provider runtime behavior is unverified.";

    // T12C direct-user amendment: only preset-affected bodies and outfits build.
    // Base hands/feet and other non-body skin remain captured/as-is. The selected
    // skin ARMA's slots identify anatomy without vanilla filenames or races.
    public static (bool Body,bool Feet) BodyParts(LeanRecord addon)
    {
        var fields=addon.Fields.TryGetValue("BOD2",out var modern)?modern:addon.Fields.GetValueOrDefault("BODT");
        if(fields==null||fields.Count!=1||fields[0].Length<4)throw new InvalidDataException("Required skin biped slot framing unavailable: "+addon.Canonical);
        uint slots=BitConverter.ToUInt32(fields[0]);
        bool body=(slots&0x04)!=0,feet=(slots&0x80)!=0;
        if(body&&(slots&0x88)!=0)throw new InvalidDataException("Combined base body/hands/feet skin mesh cannot preserve the as-is non-body policy independently: "+addon.Canonical);
        return(body,feet);
    }

    // Only ordinary selected record winners enter this route. Optional runtime
    // providers, versions and assignment configs have no admission consumer.
    public static LeanSkin Resolve(Func<string,LeanRecord> winner,LeanActor actor,HashSet<string> races,Queue<(string Reference,string Consumer)> pending)
    {
        var npc=winner("Skyrim.esm|000007");var race=winner(actor.Race);
        string original=npc.Refs("WNAM").SingleOrDefault()??race.Refs("WNAM").SingleOrDefault()??throw new InvalidDataException("Effective original skin unavailable: NPC WNAM and race WNAM are absent");
        var skin=winner(original);if(skin.Type!="ARMO")throw new InvalidDataException("Original skin is not ARMO: "+original);
        List<string> addons=[],models=[],bodyModels=[],nonBodyModels=[],feet=[];
        foreach(string key in skin.Refs("MODL").Distinct(StringComparer.OrdinalIgnoreCase)){
            var ar=winner(key);if(ar.Type!="ARMA")throw new InvalidDataException("Original skin addon is not ARMA: "+key);
            if(!ar.Refs("RNAM").Concat(ar.Refs("MODL")).Any(races.Contains))continue;
            string tag=actor.Sex==1?"MOD3":"MOD2";string[] paths=LeanModelInputs.Biped(ar,actor.Sex,false).Paths;
            if(paths.Length==0)throw new InvalidDataException("Original skin addon has no applicable selected-sex model: "+key+" "+tag);
            addons.Add(key);
            var parts=BodyParts(ar);
            // The fixed follower does not consume naked first-person skin.
            // Required outfit first-person models are resolved independently.
            foreach(string path in paths){
                string reference=LeanModelInputs.Reference(path);models.Add(reference);(parts.Body?bodyModels:nonBodyModels).Add(reference);if(parts.Feet)feet.Add(reference);pending.Enqueue((reference,"effective original skin "+key));
                if(reference.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)&&ar.Fields.TryGetValue("DNAM",out var flags)&&flags.Count==1&&flags[0].Length>3&&(flags[0][actor.Sex==1?3:2]&2)!=0){string other=reference[..^6]+"_0.nif";models.Add(other);(parts.Body?bodyModels:nonBodyModels).Add(other);if(parts.Feet)feet.Add(other);pending.Enqueue((other,"skin weight endpoint "+key));}
            }
        }
        if(models.Count==0)throw new InvalidDataException("Effective skin has no applicable selected-sex model: "+original);
        if(bodyModels.Intersect(nonBodyModels,StringComparer.OrdinalIgnoreCase).Any())throw new InvalidDataException("Shared base body/non-body skin model cannot preserve the as-is non-body policy independently");
        return new(original,addons.ToArray(),models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),"NPC WNAM else race WNAM; selected ArmorRace/additional-race closure; ordinary selected record winners",RuntimeAssignmentOmission,bodyModels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),feet.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
