using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace AutoFollower;

public sealed record LeanBodyProject(string Name,string DefinitionReference,string InstalledTarget,string OutputTarget,bool Relocated,string[] InputReferences);
public sealed record LeanBodyModel(string Reference,string? Tri,LeanMorphShape[] CompatibleShapes,bool Affected,string Basis);
public sealed record LeanBodyProjectOption(string Name,string DefinitionReference,string DefinitionSha256);
public sealed record LeanBodyProjectChoice(string Target,LeanBodyProjectOption[] Options);
public sealed record LeanBodyProjectSelection(string Target,string Name,string DefinitionReference,string DefinitionSha256);
public sealed record LeanBodyPlan(string AssignedPreset,string Xml,string Sha256,string XmlReference,LeanBodyProject[] Projects,string[] ModelTargets,string Rule,object Settings,LeanBodyModel[] Models,string[] ToolResources,LeanBodyProjectSelection[]? ProjectSelections=null);
public static class LeanBodyInputs
{
    const string RelocatedProject="CBBE 3BA Creation Club - 1st Person - Boiled Netch Leather Armour";
    static string Ref(string value)=>VirtualPath.Normalize(value.Replace('\\','/'));
    // OutputPath is a directory and installed projects may include a trailing
    // separator. Irrelevant unsafe project destinations are not build inputs.
    static string? ProjectOutput(XElement set){var folder=set.Element("OutputPath")?.Value;var file=set.Element("OutputFile")?.Value;if(folder==null||file==null)return null;try{return Ref(folder.TrimEnd('\\','/')+"/"+file+"_1.nif");}catch(InvalidDataException){return null;}}
    public static LeanBodyProjectSelection ResolveProjectChoice(LeanBodyProjectChoice choice,LeanBodyProjectSelection[]? selections=null,Func<LeanBodyProjectChoice,LeanBodyProjectSelection?>? choose=null,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var pinned=selections?.Where(s=>s.Target.Equals(choice.Target,StringComparison.OrdinalIgnoreCase)).ToArray()??[];
        if(pinned.Length>1)throw new InvalidDataException("Multiple prebuilt project selections for "+choice.Target);
        var selection=pinned.SingleOrDefault();
        if(selection==null&&choose!=null){selection=choose(choice);if(selection==null)throw new OperationCanceledException(token);token.ThrowIfCancellationRequested();}
        if(selection==null)throw new InvalidDataException("Choose the installed BodySlide project for "+choice.Target+": "+string.Join(';',choice.Options.Select(o=>o.Name))+". No project was selected automatically.");
        if(!selection.Target.Equals(choice.Target,StringComparison.OrdinalIgnoreCase)||choice.Options.Count(o=>o.Name==selection.Name&&o.DefinitionReference.Equals(selection.DefinitionReference,StringComparison.OrdinalIgnoreCase)&&o.DefinitionSha256==selection.DefinitionSha256)!=1)throw new InvalidDataException("Selected prebuilt project is stale, absent or ambiguous for "+choice.Target+"; choose from the current installed definitions");
        return selection;
    }
    public static string? BodyTri(LeanExportInputs.Nif nif){List<string> refs=[];for(int i=0;i<nif.Types.Length;i++)if(nif.Types[i]=="NiStringExtraData"){uint name=BitConverter.ToUInt32(nif.Blocks[i]),value=BitConverter.ToUInt32(nif.Blocks[i],4);if(nif.Strings[name]=="BODYTRI"){string path=nif.Strings[value].Replace((char)92,'/');refs.Add(Ref(path.StartsWith("meshes/",StringComparison.OrdinalIgnoreCase)?path:"meshes/"+path));}}if(refs.Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)throw new InvalidDataException("Multiple differing BODYTRI references require precise root applicability");return refs.FirstOrDefault();}
    public static LeanBodyPlan Prepare(LeanMo2Sources sources,LeanBuildRequest request,string preset,byte[] xml,LeanBodyModel[] models,Queue<(string Reference,string Consumer)> pending,bool prebuilt=true,Func<LeanBodyProjectChoice,LeanBodyProjectSelection?>? chooseProject=null,CancellationToken token=default)
    {
        string[] targets=models.Where(x=>x.Affected&&x.Reference.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)).Select(x=>x.Reference).ToArray();
        string settings="";var config=sources.Stage("SKSE/Plugins/skee64.ini","required body morph enable setting",pending,followDependencies:false,consume:b=>settings=Encoding.UTF8.GetString(b));
        string section="";List<string> enabled=[];foreach(string line in settings.Split('\n')){string text=line.Trim();if(text.StartsWith('[')&&text.EndsWith(']'))section=text[1..^1];int eq=text.IndexOf('=');if(section=="General"&&eq>0&&text[..eq].Trim().Equals("bEnableBodyMorph",StringComparison.OrdinalIgnoreCase))enabled.Add(text[(eq+1)..].Split(';')[0].Trim());}
        if(enabled.Count>1||enabled.Count==1&&enabled[0]!="1")throw new InvalidDataException("Selected SKEE body morph setting disabled/ambiguous; do not infer actual outfit application");
        string xmlRef="metadata/authored-preset.xml";sources.Stage(xmlRef,"selected authored OBody preset",pending,request.PresetXml,xml,followDependencies:false,
            suppliedKind:request.PresetBacking!=null?"Selected installed authored XML":null,
            suppliedBasis:request.PresetBacking!=null?"Assigned preset resolved in selected MO2; actual final read-only backing and authored-byte digest checked against build selection":null);
        // OBody-only uses the actual authored preset, without completing omitted
        // endpoints (explicit T11D user choice). No project/tool inputs are needed.
        if(prebuilt&&models.Any(x=>x.Affected&&!x.Reference.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase)&&!x.Reference.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Affected non-weighted model requires a concrete prebuilt output mapping: "+string.Join(';',models.Where(x=>x.Affected&&!x.Reference.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase)&&!x.Reference.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)).Select(x=>x.Reference)));
        if(!prebuilt||targets.Length==0)return new(preset,request.PresetXml!,BoundedBinary.Digest(xml),xmlRef,[],targets,"Captured winning meshes and compatible morphs; authored XML only; omitted endpoints left to installed OBody. No BodySlide inputs/build.",new{Skee=config,BodyMorphEnabled=true,DefaultUsed=enabled.Count==0},models,[]);
        // Read only definition metadata. All eligible projects participate in
        // the same target choice, including historical names and relocation.
        string root="CalienteTools/BodySlide/";Dictionary<string,(string Ref,byte[] Bytes,XElement Set)> selected=new(StringComparer.Ordinal);
        using var presetStream=new MemoryStream(xml,false);var presetDocument=XDocument.Load(presetStream);
        var authoredProject=presetDocument.Root?.Elements("Preset").Where(p=>(string?)p.Attribute("name")==preset).Select(p=>(string?)p.Attribute("set")).SingleOrDefault();
        bool RelocationEligible(string name)=>name==RelocatedProject||name==authoredProject;
        List<(string Target,string Ref,byte[] Bytes,XElement Set)> candidates=[];
        foreach(string file in Directory.EnumerateFiles(sources.Virtual(root+"SliderSets"),"*.osp",SearchOption.TopDirectoryOnly)){
            string reference=root+"SliderSets/"+Path.GetFileName(file);var candidate=sources.ReadLooseCandidate(reference);using var input=new MemoryStream(candidate.Bytes,false);XDocument doc;try{doc=XDocument.Load(input);}catch(System.Xml.XmlException){continue;}
            foreach(var set in doc.Root?.Elements("SliderSet")??[]){
                string name=(string?)set.Attribute("name")??"";string? destination=ProjectOutput(set);
                bool exact=destination!=null&&targets.Contains(destination,StringComparer.OrdinalIgnoreCase);
                string[] relocated=RelocationEligible(name)&&destination!=null?targets.Where(x=>Path.GetFileName(x).Equals(set.Element("OutputFile")!.Value+"_1.nif",StringComparison.OrdinalIgnoreCase)).ToArray():[];
                if(!exact&&relocated.Length!=1)continue;
                candidates.Add((exact?targets.Single(x=>x.Equals(destination,StringComparison.OrdinalIgnoreCase)):relocated[0],reference,candidate.Bytes,set));
            }
        }
        List<LeanBodyProjectSelection> projectSelections=[];
        foreach(var group in candidates.GroupBy(x=>x.Target,StringComparer.OrdinalIgnoreCase)){
            var entries=group.ToArray();var entry=entries[0];
            if(entries.Length>1||request.BodyProjectSelections?.Any(s=>s.Target.Equals(group.Key,StringComparison.OrdinalIgnoreCase))==true){
                var choice=new LeanBodyProjectChoice(group.Key,entries.Select(e=>new LeanBodyProjectOption((string?)e.Set.Attribute("name")??"",e.Ref,BoundedBinary.Digest(e.Bytes))).ToArray());
                var selection=ResolveProjectChoice(choice,request.BodyProjectSelections,chooseProject,token);
                var matches=entries.Where(e=>selection.Target.Equals(group.Key,StringComparison.OrdinalIgnoreCase)&&selection.Name==(string?)e.Set.Attribute("name")&&selection.DefinitionReference.Equals(e.Ref,StringComparison.OrdinalIgnoreCase)&&selection.DefinitionSha256==BoundedBinary.Digest(e.Bytes)).ToArray();
                if(matches.Length!=1)throw new InvalidDataException("Selected prebuilt project is stale, absent or ambiguous for "+group.Key+"; choose from the current installed definitions");
                entry=matches[0];projectSelections.Add(selection);
            }
            string name=(string?)entry.Set.Attribute("name")??throw new InvalidDataException("Required project name absent");if(!selected.TryAdd(name,(entry.Ref,entry.Bytes,entry.Set)))throw new InvalidDataException("Required project name collision: "+name);
        }
        if(request.BodyProjectSelections?.Any(s=>!projectSelections.Contains(s))==true)throw new InvalidDataException("A requested prebuilt project is not a current selectable target; refresh the project choices");
        if(targets.Any(x=>!candidates.Any(c=>c.Target.Equals(x,StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("Affected installed model has no supported prebuilt project: "+string.Join(';',targets.Where(x=>!candidates.Any(c=>c.Target.Equals(x,StringComparison.OrdinalIgnoreCase)))));
        List<LeanBodyProject> projects=[];HashSet<string> definitions=new(StringComparer.OrdinalIgnoreCase),inputs=new(StringComparer.OrdinalIgnoreCase);
        foreach(var selectedEntry in selected){string name=selectedEntry.Key;var entry=selectedEntry.Value;var set=entry.Set;
            string outputFile=set.Element("OutputFile")?.Value??throw new InvalidDataException("Required project OutputFile absent: "+name),outputPath=Ref((set.Element("OutputPath")?.Value??"").TrimEnd('\\','/'));if(!outputPath.StartsWith("meshes/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Project output outside meshes: "+name);
            string installed=Ref(outputPath+"/"+outputFile+"_1.nif");string[] matches=targets.Where(x=>x.Equals(installed,StringComparison.OrdinalIgnoreCase)).ToArray();bool relocate=false;
            if(matches.Length==0&&RelocationEligible(name)){matches=targets.Where(x=>Path.GetFileName(x).Equals(outputFile+"_1.nif",StringComparison.OrdinalIgnoreCase)).ToArray();relocate=matches.Length==1;}
            if(matches.Length==0)continue;
            if(matches.Length!=1)throw new InvalidDataException("Required project does not uniquely match the selected called model: "+name+" -> "+installed);
            if(definitions.Add(entry.Ref))sources.Stage(entry.Ref,"XML-driven required prebuilt definition",pending,selectedBytes:entry.Bytes,followDependencies:false);
            string folder=set.Element("DataFolder")?.Value??throw new InvalidDataException("Required project DataFolder absent: "+name),inputFile=set.Element("SourceFile")?.Value??throw new InvalidDataException("Required project SourceFile absent: "+name);List<string> refs=[Ref(root+"ShapeData/"+folder+"/"+inputFile)];
            foreach(var slider in set.Elements("Slider"))foreach(var data in slider.Elements("Data")){
                string value=data.Value.Replace('\\','/');int split=value.LastIndexOf('/');if(split<1)throw new InvalidDataException("Required slider dataset path unsupported: "+name);string dataFolder=folder;
                if((string?)data.Attribute("local")!="true"){string target=(string?)data.Attribute("target")??"";var shapes=set.Elements("Shape").Where(x=>(string?)x.Attribute("target")==target).ToArray();if(shapes.Length!=1)throw new InvalidDataException("Required project dataset target ambiguous: "+name);dataFolder=(string?)shapes[0].Attribute("DataFolder")??folder;}
                refs.Add(Ref(root+"ShapeData/"+dataFolder+"/"+value[..split]));
            }
            foreach(string reference in refs.Distinct(StringComparer.OrdinalIgnoreCase))if(inputs.Add(reference))sources.Stage(reference,"required prebuilt source/diff "+name,pending,followDependencies:false);
            projects.Add(new(name,entry.Ref,installed,matches[0],relocate,refs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        var tool=sources.Stage(root+"BodySlide x64.exe","isolated required prebuilt generator",pending,followDependencies:false);string? toolVersion=FileVersionInfo.GetVersionInfo(tool.Backing).FileVersion;
        // 5.7.1 constructs its hidden frame even for -gbuild. Stage only
        // its four startup XRCs and their named image dependencies, not res/**.
        HashSet<string> resources=new(StringComparer.OrdinalIgnoreCase),images=new(StringComparer.OrdinalIgnoreCase){root+"res/images/BodySlide.png"};
        foreach(string name in new[]{"BodySlide","BatchBuild","Settings","About"}){string reference=root+"res/xrc/"+name+".xrc";byte[] raw=[];sources.Stage(reference,"BodySlide5.7.1 headless startup XRC",pending,followDependencies:false,consume:b=>raw=b);resources.Add(reference);using var stream=new MemoryStream(raw,false);var document=XDocument.Load(stream);foreach(var image in document.Descendants().Where(x=>x.Name.LocalName is "bitmap" or "bitmap2")){string value=image.Value.Replace('\\','/');if(value.Length==0)continue;if(value.StartsWith("$(AppDir)/",StringComparison.Ordinal))value=value[10..];if(!value.StartsWith("res/",StringComparison.Ordinal))throw new InvalidDataException("Required tool XRC image path unsupported: "+value);images.Add(Ref(root+value));}}
        foreach(string reference in images){sources.Stage(reference,"BodySlide5.7.1 named startup image",pending,followDependencies:false);resources.Add(reference);}
        sources.Stage(root+"BuildSelection.xml","authored project zap selections; D51 intact garment",pending,followDependencies:false);
        if(targets.Any(x=>projects.All(p=>!p.OutputTarget.Equals(x,StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("Affected installed model has no supported prebuilt project: "+string.Join(';',targets.Where(x=>projects.All(p=>!p.OutputTarget.Equals(x,StringComparison.OrdinalIgnoreCase)))));
        return new(preset,request.PresetXml!,BoundedBinary.Digest(xml),xmlRef,projects.ToArray(),targets,"OBody4.4.3 ApplySliderSet/ApplyBodyMorphs; compatible installed shape/vertex ranges and assigned XML slider intersection establish each selected-sex skin/outfit target. Unaffected meshes remain as-is; runtime geometry is not decoded.",new{Skee=config,BodyMorphEnabled=true,DefaultUsed=enabled.Count==0,BodySlideVersion=toolVersion,VersionNotice="Verified tool evidence is limited to 5.7.1; other versions are permitted subject to concrete required startup/project/output format checks."},models,resources.Order().ToArray(),projectSelections.ToArray());
    }
}
