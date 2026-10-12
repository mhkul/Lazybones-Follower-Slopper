namespace AutoFollower;

public sealed record LeanEquipmentChoice(string Canonical,string Name,string Type);
public static class LeanEquipmentChoices
{
    public static LeanEquipmentChoice[] Catalog(LeanActor actor,LeanRecordIndex index)
    {
        var races=LeanCharacterInputs.Resolve(actor,key=>index.Get(key)).ArmorRaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actor.Worn.GroupBy(x=>x.Canonical,StringComparer.OrdinalIgnoreCase).Select(x=>index.Get(x.Key)).Where(record=>{
            if(record.Type=="WEAP")return !LeanModelInputs.MeshlessWeapon(record);
            if(record.Type=="AMMO")return LeanAmmoInputs.HasModels(record,key=>index.Get(key));
            if(record.Type!="ARMO")return false;
            return record.Refs("MODL").Select(key=>index.Get(key)).Any(ar=>ar.Type=="ARMA"&&ar.Refs("RNAM").Concat(ar.Refs("MODL")).Any(races.Contains)&&(LeanModelInputs.Biped(ar,actor.Sex,false).Paths.Length>0||LeanModelInputs.Biped(ar,actor.Sex,true).Paths.Length>0));
        }).Select(r=>new LeanEquipmentChoice(r.Canonical,r.Text("FULL").FirstOrDefault(x=>x.Length>0)??r.Canonical,r.Type)).ToArray();
    }
    public static HashSet<string> Exclusions(LeanEquipmentChoice[] catalog,IEnumerable<string>? excluded)
    {
        var available=catalog.Select(x=>x.Canonical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected=(excluded??[]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(selected.Any(x=>!available.Contains(x)))throw new InvalidDataException("Equipment choices changed with the selected character/setup. Reopen Equipment and review the choices.");
        return selected;
    }
}
