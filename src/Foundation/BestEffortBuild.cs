using System.Xml.Linq;

namespace AutoFollower;

public sealed record BuildQualityWarning(string Schema,string Category,string Stage,bool Passed,string Warning,string Detail);
public static class BestEffortBuild
{
    public const string WarningStatus="built-with-warnings";
    public static bool DeliveredStatus(string? status)=>status is "structural-build-passed" or WarningStatus;
    public static BuildQualityWarning Finding(string stage,string detail,string category="input-quality",string? warning=null)=>new("Lazybones/output-quality-finding-v1",category,stage,false,warning??"Some selected inputs are incomplete or disagree. Available source content is preserved; appearance or behavior may differ.",detail);
}

public sealed class HostProtectionException(string message,Exception? inner=null):IOException(message,inner);

// Rejected bytes never cross the authored-preset preparation boundary.
public sealed class LeanAssignedPreset
{
    public byte[] Bytes { get; }
    public string[] SliderNames { get; }
    LeanAssignedPreset(byte[] bytes,XElement preset){Bytes=bytes.ToArray();SliderNames=preset.Elements("SetSlider").Select(x=>(string?)x.Attribute("name")??"").ToArray();}
    public static LeanAssignedPreset? Admit(byte[] bytes,string name,ICollection<BuildQualityWarning> warnings)
    {
        try{
            using var input=new MemoryStream(bytes,false);var document=XDocument.Load(input);
            var matches=document.Descendants("Preset").Where(x=>(string?)x.Attribute("name")==name).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("XML does not uniquely contain saved OBody preset");
            return new(bytes,matches[0]);
        }catch(Exception e)when(e is System.Xml.XmlException or InvalidDataException){
            warnings.Add(BestEffortBuild.Finding("assigned-preset",e.Message,"prebuilt-fallback","The saved preset could not be recovered from the selected XML. Captured installed meshes remain available; no preset bake or OBody assignment is claimed."));
            return null;
        }
    }
}
