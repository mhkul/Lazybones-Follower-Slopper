namespace AutoFollower;

// Skyrim AMMO DATA begins with its PROJ link. These models are ordinary
// equipment/inventory and projectile calls, independent of body morphs.
public static class LeanAmmoInputs
{
    public static string? Projectile(LeanRecord ammo)
    {
        if(ammo.Type!="AMMO")throw new InvalidDataException("Ammunition input is not AMMO");
        var data=ammo.Fields.GetValueOrDefault("DATA",[]);
        if(data.Count!=1||data[0].Length is not (16 or 20))throw new InvalidDataException("Ammunition DATA layout unavailable: "+ammo.Canonical);
        return ammo.Ref(BitConverter.ToUInt32(data[0]));
    }
    public static LeanRecord? CalledProjectile(LeanRecord ammo,Func<string,LeanRecord> get)
    {
        string? key=Projectile(ammo);if(key==null)return null;
        var record=get(key);if(record.Type!="PROJ")throw new InvalidDataException("Ammunition projectile is not PROJ: "+key);
        return record;
    }
    public static string[] Models(LeanRecord record)=>record.Text("MODL").Concat(record.Text("DMDL")).Concat(record.Type=="PROJ"?record.Text("NAM1"):[]).Where(x=>x.Length>0).ToArray();
    public static bool HasModels(LeanRecord ammo,Func<string,LeanRecord> get)=>Models(ammo).Length>0||CalledProjectile(ammo,get) is { } p&&Models(p).Length>0;
    public static string[] Resolve(LeanRecord ammo,Func<string,LeanRecord> get,Queue<(string Reference,string Consumer)> pending,Action<LeanRecord,string> alternate,Action<string> material)
    {
        List<string> models=[];
        void Model(LeanRecord record){foreach(string path in Models(record)){models.Add(path);pending.Enqueue((LeanModelInputs.Reference(path),ammo.Canonical+" ammunition/projectile model "+record.Canonical));}alternate(record,"MODS");alternate(record,"DMDS");}
        Model(ammo);
        foreach(string path in ammo.Text("ICON").Concat(ammo.Text("MICO")).Where(p=>p.Length>0))pending.Enqueue((LeanModelInputs.Reference(path,"textures"),ammo.Canonical+" ammunition icon"));
        if(CalledProjectile(ammo,get) is { } projectile){
            Model(projectile);
            var data=projectile.Fields.GetValueOrDefault("DATA",[]);
            if(data.Count!=1||data[0].Length is not (84 or 88 or 92))throw new InvalidDataException("Projectile DATA layout unavailable: "+projectile.Canonical);
            if(data[0].Length>=88&&projectile.Ref(BitConverter.ToUInt32(data[0],84)) is { } decal)material(decal);
        }
        return models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
