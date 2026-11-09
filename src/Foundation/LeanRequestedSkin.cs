using System.Text;

namespace AutoFollower;

public sealed record RequestedSkin(string Canonical,string Proof);
public sealed record SkinChoice(string Canonical,string Label,string Proof);
public sealed record SkinMaterial(string Addon,string TextureSet);
public static class LeanRequestedSkin
{
    const string Plugin="Racial Skin Variance - SPID.esp";
    static int Part(LeanRecord addon)
    {
        var fields=addon.Fields.GetValueOrDefault("BOD2")??addon.Fields.GetValueOrDefault("BODT");
        if(fields?.Count!=1||fields[0].Length<4)throw new InvalidDataException("Skin part has no valid biped slots");
        uint mask=BitConverter.ToUInt32(fields[0])&0x8c;
        return mask switch{0=>0,4=>4,8=>8,128=>128,_=>throw new InvalidDataException("Skin has an unsupported combined or unrelated part")};
    }
    static string[] Bindings(LeanRecord quest)
    {
        try{return ReadBindings(quest);}catch(DecoderFallbackException e){throw new InvalidDataException("Malformed RSV VMAD text",e);}
    }
    static string[] ReadBindings(LeanRecord quest)
    {
        if(quest.QuestFields==null)throw new InvalidDataException("RSV quest alias structure unavailable");
        var aliases=new Dictionary<short,string?>();short? current=null;string? forced=null;bool forcedSeen=false,conflict=false;
        var q=new BoundedBinary(quest.QuestFields);int? extended=null;
        while(q.Remaining>0){string tag=Encoding.ASCII.GetString(q.Take(4));int length=q.U16();if(tag=="XXXX"){if(length!=4||extended!=null)throw new InvalidDataException("Invalid quest field length");extended=q.Count();continue;}length=extended??length;extended=null;byte[] value=q.Take(length);
            if(tag=="ALST"||tag=="ALLS"){if(current!=null||value.Length!=4||BitConverter.ToUInt32(value)>short.MaxValue)throw new InvalidDataException("Invalid quest alias");current=(short)BitConverter.ToUInt32(value);forced=null;forcedSeen=false;conflict=tag=="ALLS";}
            else if(tag=="ALFR"){if(current==null||value.Length!=4||forcedSeen)throw new InvalidDataException("Invalid forced quest alias");forcedSeen=true;forced=quest.Ref(BitConverter.ToUInt32(value));}
            else if(tag is "ALUA" or "ALEQ" or "ALFI" or "ALFL")conflict=true;
            else if(tag=="ALED"){if(current==null||value.Length!=0||!aliases.TryAdd(current.Value,conflict?null:forced))throw new InvalidDataException("Ambiguous quest alias");current=null;}
        }
        if(current!=null||extended!=null)throw new InvalidDataException("Unfinished quest alias");
        var fields=quest.Fields.GetValueOrDefault("VMAD");if(fields?.Count!=1)throw new InvalidDataException("Missing or ambiguous RSV VMAD");
        var r=new BoundedBinary(fields[0]);
        void Header(){if(r.U16()!=5||r.U16()!=2)throw new InvalidDataException("Unsupported RSV VMAD version/object format");}
        (short Alias,string? Form) Object(){if(r.U16()!=0)throw new InvalidDataException("Invalid RSV object padding");short alias=unchecked((short)r.U16());return(alias,quest.Ref(r.U32()));}
        bool Known(string name)=>name.Equals("RSVplayer",StringComparison.OrdinalIgnoreCase)||name.Equals("RSV_PlayerAliasScript",StringComparison.OrdinalIgnoreCase);
        var candidates=new List<string[]>();
        void Scripts(bool applicable){int count=r.U16();if(count>256)throw new InvalidDataException("RSV script count exceeds bound");for(int i=0;i<count;i++){
            string name=r.String16();int scriptFlags=r.U8();int properties=r.U16();if(properties>1024)throw new InvalidDataException("RSV property count exceeds bound");bool known=Known(name);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);List<string> skins=[];bool player=false;
            for(int n=0;n<properties;n++){string property=r.String16();if(!names.Add(property))throw new InvalidDataException("Duplicate RSV property");int type=r.U8();int propertyFlags=r.U8();bool wanted=property is "RSV_SkinNaked" or "RSV_SkinNakedPlayer" or "RSV_SkinNakedFantasyPlayer" or "RSVNpcsSkins";if(known&&(wanted||property.Equals("PlayerRef",StringComparison.OrdinalIgnoreCase))&&propertyFlags>1)throw new InvalidDataException("Removed or unsupported RSV binding property");if(known&&((property.Equals("PlayerRef",StringComparison.OrdinalIgnoreCase)&&type!=1)||(wanted&&type!=(property=="RSVNpcsSkins"?11:1))))throw new InvalidDataException("Invalid required RSV property type");
                void Value(int kind){switch(kind){case 1:var value=Object();if(property.Equals("PlayerRef",StringComparison.OrdinalIgnoreCase))player=value.Alias==-1&&value.Form=="Skyrim.esm|000014";if(wanted){if(value.Alias!=-1||value.Form==null)throw new InvalidDataException("Invalid skin object binding");skins.Add(value.Form);}break;case 2:r.String16();break;case 3:case 4:r.U32();break;case 5:if(r.U8()>1)throw new InvalidDataException("Invalid RSV boolean");break;default:throw new InvalidDataException("Unsupported RSV property type");}}
                if(type>=11&&type<=15){for(int k=r.Count(256);k>0;k--)Value(type-10);}else Value(type);
            }
            if(known){if(scriptFlags>1||!applicable||!player||skins.Count==0)throw new InvalidDataException("RSV script does not prove the applicable player binding");candidates.Add(skins.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());}
        }}
        Header();Scripts(false);if(r.U8()!=2)throw new InvalidDataException("Unsupported quest bind data");int fragments=r.U16();if(fragments>256)throw new InvalidDataException("Quest fragment count exceeds bound");r.String16();for(int i=0;i<fragments;i++){r.Take(9);r.String16();r.String16();}
        int aliasCount=r.U16();if(aliasCount>256)throw new InvalidDataException("Quest alias count exceeds bound");var seen=new HashSet<short>();for(int i=0;i<aliasCount;i++){var target=Object();if(!seen.Add(target.Alias))throw new InvalidDataException("Duplicate VMAD alias binding");Header();Scripts(target.Form==quest.Canonical&&target.Alias>=0&&aliases.GetValueOrDefault(target.Alias)=="Skyrim.esm|000014");}
        r.End("RSV quest VMAD");if(candidates.Count!=1)throw new InvalidDataException("Missing or ambiguous RSV player script");return candidates[0];
    }
    public static LeanSkin Prepare(RequestedSkin selected,LeanActor actor,LeanSkin captured,HashSet<string> races,LeanRecordIndex index,LeanMo2Sources sources,ICollection<BuildQualityWarning> warnings)
    {
        var checkpoint=sources.StagingCheckpoint();
        try{
            var materials=Apply(selected,actor,captured,races,index);var staged=new List<LeanSource>();
            foreach(string key in materials.Select(x=>x.TextureSet).Distinct(StringComparer.OrdinalIgnoreCase))foreach(string tag in new[]{"TX00","TX01","TX02","TX03","TX04","TX05","TX06","TX07","TX08"})foreach(string path in index.Get(key).Text(tag).Where(x=>x.Length>0)){string value=path.Replace('\\','/');string reference=VirtualPath.Normalize(value.StartsWith("textures/",StringComparison.OrdinalIgnoreCase)?value:"textures/"+value);if(!reference.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Requested skin calls an unsupported texture asset");staged.Add(sources.Stage(reference,"requested skin material "+key,new(),followDependencies:false));}
            foreach(var source in staged)sources.VerifyRetained(source);
            warnings.Add(BestEffortBuild.Finding("requested-skin-head","Explicit body skin selected; exported head and tint remain authoritative","appearance-choice","The requested body, hands and feet skin does not replace exported head or teeth. A visible head/body texture difference is possible."));return captured with{RequestedMaterials=materials};
        }catch(Exception e){sources.RollbackStaging(checkpoint);if(e is HostProtectionException or OperationCanceledException)throw;if(e is not (IOException or InvalidDataException or UnauthorizedAccessException))throw;warnings.Add(BestEffortBuild.Finding("requested-skin-unavailable",e.Message,"appearance-fallback","The requested skin could not be applied. Captured/default skin is retained."));return captured;}
    }
    static (SkinMaterial[] Materials,string Proof) Resolve(string key,LeanActor actor,LeanSkin captured,HashSet<string> races,LeanRecordIndex index,LeanRecord quest)
    {
        var armor=index.Get(key);if(armor.Type!="ARMO")throw new InvalidDataException("Selected skin binding is not armor");
        List<string> pins=[quest.Canonical,quest.Backing,quest.Sha256,armor.Canonical,armor.Backing,armor.Sha256,actor.Sex.ToString(),actor.Race];
        Dictionary<int,string> textures=[];
        foreach(string addon in armor.Refs("MODL")){
            var a=index.Get(addon);if(a.Type!="ARMA")throw new InvalidDataException("Selected skin addon is unavailable");
            if(!a.Refs("RNAM").Concat(a.Refs("MODL")).Any(races.Contains))continue;
            if(LeanModelInputs.Biped(a,actor.Sex,false).Paths.Length==0)continue;
            int part;try{part=Part(a);if(part==0)continue;}catch(InvalidDataException){continue;}
            var links=a.Refs(actor.Sex==1?"NAM1":"NAM0");if(links.Length!=1||textures.ContainsKey(part))throw new InvalidDataException("Selected skin part has missing or conflicting textures");
            var tx=index.Get(links[0]);if(tx.Type!="TXST")throw new InvalidDataException("Selected skin texture set unavailable");
            textures.Add(part,tx.Canonical);pins.Add(a.Backing);pins.Add(a.Sha256);pins.Add(tx.Backing);pins.Add(tx.Sha256);
        }
        List<SkinMaterial> output=[];
        foreach(string addon in captured.Addons){var a=index.Get(addon);int part=Part(a);if(part==0)continue;if(!textures.TryGetValue(part,out string? tx))throw new InvalidDataException("Selected skin does not cover captured body, hands and feet");output.Add(new(addon,tx));pins.Add(a.Sha256);}
        if(output.Count==0)throw new InvalidDataException("No captured skin parts available");
        return(output.ToArray(),BoundedBinary.Digest(Encoding.UTF8.GetBytes(string.Join('\n',pins))));
    }
    public static SkinChoice[] Catalog(LeanActor actor,LeanRecordIndex index)
    {
        if(!index.Order.Contains(Plugin,StringComparer.OrdinalIgnoreCase))return [];
        var quest=index.Get(Plugin+"|000A08");if(quest.Type!="QUST")return [];
        var races=LeanCharacterInputs.Resolve(actor,k=>index.Get(k)).ArmorRaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var captured=LeanSkinInputs.Resolve(k=>index.Get(k),actor,races,new());List<SkinChoice> result=[];
        foreach(string key in Bindings(quest))try{
            var resolved=Resolve(key,actor,captured,races,index,quest);var armor=index.Get(key);
            string label=armor.Text("EDID").SingleOrDefault()??key;label=label.Replace("RSV_SkinNaked","").Replace("Fantasy"," Fantasy ");
            if(label.Length==0)label="RSV (race applicable)";
            result.Add(new(key,label,resolved.Proof));
        }catch(InvalidDataException){/* Inapplicable or incomplete choices are unavailable. */}
        return result.OrderBy(x=>x.Label,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static SkinMaterial[] Apply(RequestedSkin selected,LeanActor actor,LeanSkin captured,HashSet<string> races,LeanRecordIndex index)
    {
        var choice=Catalog(actor,index).SingleOrDefault(x=>x.Canonical.Equals(selected.Canonical,StringComparison.OrdinalIgnoreCase));
        if(choice==null||choice.Proof!=selected.Proof)throw new InvalidDataException("The requested skin changed or is unavailable. Review Skin in Advanced settings.");
        return Resolve(choice.Canonical,actor,captured,races,index,index.Get(Plugin+"|000A08")).Materials;
    }
}
