namespace AutoFollower;

// ACEN is scoped to an attached armor/addon, not to the finished head export.
// The retained version-2 producers use both VM handles and form-derived keys.
// A low-word match alone is not proof of the selected armor/addon consumer.
// Until that scope and all material layers are proved, preserve captured assets
// and disclose the omission rather than applying an edit to an unrelated mesh.
public static class LeanArmorAssociations
{
    public const string Warning="Saved armor/body material effects may differ. Captured base assets are kept.";
    public static BuildQualityWarning Unknown(uint version)=>BestEffortBuild.Finding("skee-armor","ACEN version="+version+"; unknown layout or value type; appearance extent unknown. The bounded actor group was omitted without interpreting its child payloads.","armor-material",Warning);
    public static BuildQualityWarning[] Warnings(LeanSkeeState state)
    {
        var unknown=state.ArmorWarnings??[];
        List<BuildQualityWarning> findings=[..unknown.Take(32)];
        if(unknown.Length>32)findings.Add(BestEffortBuild.Finding("skee-armor","Additional unknown ACEN groups="+(unknown.Length-32)+"; all remaining groups are omitted; appearance extent unknown.","armor-material",Warning));
        var rows=state.ArmorMaterials??[];
        // Local sequential references only: no saved handles, names, paths or
        // identifying/reversible hashes enter these recipient-facing findings.
        var armors=rows.Select(x=>x.Armor).Distinct().ToArray();
        for(int i=0;i<Math.Min(rows.Length,32);i++){
            var row=rows[i];int armor=Array.IndexOf(armors,row.Armor)+1;
            var addons=rows.Where(x=>x.Armor==row.Armor).Select(x=>x.Addon).Distinct().ToArray();
            int addon=Array.IndexOf(addons,row.Addon)+1;
            string keys=string.Join(",",row.Values.Select(v=>v.Key).Distinct().Order().Take(32));
            string types=string.Join(",",row.Values.Select(v=>v.Type).Distinct().Order());
            findings.Add(BestEffortBuild.Finding("skee-armor","ACEN association "+(i+1)+"; armor reference "+armor+"; addon reference "+addon+"; node reference "+(i+1)+"; sex="+row.Sex+"; values="+row.Values.Length+"; keys="+keys+"; types="+types+"; target="+(row.Node.Length==0?"single attached geometry (empty node name)":"named geometry")+". Saved handle/provider/attached-target and material-layer agreement are unproven; these saved effects are not reapplied. Visible extent unknown; this is not proof of an unused registration.","armor-material",Warning));
        }
        if(rows.Length>32)findings.Add(BestEffortBuild.Finding("skee-armor","Additional ACEN associations="+(rows.Length-32)+"; all remaining saved armor/body material effects are also omitted; visible extent unknown.","armor-material",Warning));
        return findings.ToArray();
    }
    public static string Texture(string? text)
    {
        if(string.IsNullOrEmpty(text))return ""; // An empty texture operation is still meaningful, and warned.
        string path=text.Replace('\\','/');
        try{return VirtualPath.Normalize(path.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)?path:"textures/"+path);}
        catch(InvalidDataException e){throw new HostProtectionException("Saved armor material has an unsafe texture reference",e);}
    }
}
