using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AutoFollower;

public sealed record LeanBuildRequest(string Save,string Cosave,string HeadNif,string TintDds,string Jslot,
    string ExportAssociation,string TintReference,string InstanceIni,string Profile,string Game,
    string Mo2,string? PresetXml=null,string Python="",string PluginWriter="",LeanAssetSelection[]? AssetSelections=null,FollowerSettings? Settings=null,string? PresetSha256=null,string? PresetBacking=null,LeanOutputSelection? Outputs=null,bool? ExpectedObodyPresent=null,LeanBodyProjectSelection[]? BodyProjectSelections=null,string? StagesDirectory=null,string? OutputRoot=null,string Store="unspecified",string InputRuntime="",PhysicalConfiguration? Physical=null,string[]? ExcludedEquipment=null,LeanKeywordRemoval[]? KeywordRemovals=null);
public sealed record LeanGap(string Input,string Evidence,string NextAction);
public static class LeanExportBuilder
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool CreateDirectory(string path,IntPtr security);
    public static int Run(string requestPath,string output,CancellationToken token,bool previewOnly=false,Func<LeanAssetChoice[],LeanAssetSelection[]?>? chooseAssets=null,Func<FollowerSettingsContext,FollowerSettings>? chooseSettings=null,FollowerSavePreview? savePreview=null,FollowerInputPreview? inputPreview=null,Func<LeanBodyProjectChoice,LeanBodyProjectSelection?>? chooseBodyProject=null,Func<LeanEquipmentRecoveryChoice,bool>? recoverEquipment=null,Func<LeanKeywordCandidate[],LeanKeywordRemoval[]?>? chooseKeywords=null)
    {
        requestPath=Path.GetFullPath(requestPath);output=Path.GetFullPath(output);
        // The checkpoint runner is thin and uses a new explicit task destination;
        // no cleanup, source permissions, game or legacy capture helper is invoked.
        SafePaths.NoLinks(Path.GetDirectoryName(output)!);SafePaths.NoLinks(requestPath);token.ThrowIfCancellationRequested();
        var request=JsonSerializer.Deserialize<LeanBuildRequest>(File.ReadAllText(requestPath),Wire.Options)??throw new InvalidDataException("Lean build request missing");
        foreach(var field in new[]{(nameof(request.Save),request.Save),(nameof(request.Cosave),request.Cosave),(nameof(request.HeadNif),request.HeadNif),(nameof(request.TintDds),request.TintDds),(nameof(request.Jslot),request.Jslot),(nameof(request.Game),request.Game),(nameof(request.ExportAssociation),request.ExportAssociation),(nameof(request.TintReference),request.TintReference)})
            if(string.IsNullOrWhiteSpace(field.Item2))throw new InvalidDataException("Required request field missing: "+field.Item1+"; supply the selected input/environment value");
        if(request.Physical==null&&new[]{request.InstanceIni,request.Profile,request.Mo2}.Any(string.IsNullOrWhiteSpace))throw new InvalidDataException("Required MO2 selection missing.");
        if(request.Physical!=null)PhysicalEnvironment.RequireDirect();
        // No variant means no build work. Reject before resolving tools or
        // reading environment configuration; no output can be allocated.
        if(request.Outputs is {Prebuilt:false,Obody:false})throw new InvalidDataException("Select at least one output: Prebuilt or OBody NG.");
        var tools=ApplicationTools.Resolve(request.Python,request.PluginWriter,request.StagesDirectory);
        request=request with{Python=tools.Python,PluginWriter=tools.Writer,StagesDirectory=tools.Stages};
        var protection=BuildDestinations.Protection(request,tools);
        BuildDestinations.RequireNew(output,request.OutputRoot??Path.GetDirectoryName(output)!,
            protection.Files.Append(requestPath),protection.Directories);
        if(!previewOnly)tools.Require();
        var runtime=SkyrimRuntimeProfile.Require(request.Store,FileVersionInfo.GetVersionInfo(Path.Combine(request.Game,"SkyrimSE.exe")).FileVersion??"");
        if(SkyrimRuntimeProfile.ForInputs(request.Store,request.InputRuntime)!=runtime)throw new InvalidDataException("Selected input profile differs from the admitted build environment; refresh inputs before building.");
        Console.WriteLine(runtime.Notice);
        Console.WriteLine(LeanOverlayPolicy.Warning);
        SafePaths.NoLinks(request.Game);
        foreach(string input in new[]{request.Save,request.Cosave,request.HeadNif,request.TintDds,request.Jslot,request.InstanceIni,request.Mo2,request.Python}.Where(x=>!string.IsNullOrWhiteSpace(x)).Concat(request.PresetXml==null?[]:new[]{request.PresetXml})){SafePaths.NoLinks(input);if(Path.GetFullPath(input).StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Output/source collision");}
        if(!Path.GetFileNameWithoutExtension(request.Save).Equals(Path.GetFileNameWithoutExtension(request.Cosave),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Save/co-save stems differ");
        if(!CreateDirectory(output,IntPtr.Zero))throw new IOException("Unique owned output could not be created: "+Marshal.GetLastWin32Error());
        using(var marker=new FileStream(Path.Combine(output,"owned-output.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(marker,new{Kind="T09B lean export build",BuildId=Guid.NewGuid(),Process=Environment.ProcessId,Executable=Process.GetCurrentProcess().MainModule!.FileName,CreatedFileTime=Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc(),CreatedUtc=DateTime.UtcNow,Request=requestPath,GameLaunched=false},Wire.Options);
        using var storageLease=WorkingStorage.Begin(output,requestPath);
        var total=Stopwatch.StartNew();var metrics=new LeanReadMetrics();using var sources=new LeanMo2Sources(Path.Combine(request.Game,"Data"),output,metrics,token);List<object> phases=[];List<LeanGap> gaps=[];List<BuildQualityWarning> qualityWarnings=[];
        void Write(string name,object value)=>File.WriteAllText(Path.Combine(output,name),JsonSerializer.Serialize(value,Wire.Options));
        void Phase(string name,Stopwatch clock,long read,int files)=>phases.Add(new{Name=name,Milliseconds=clock.ElapsedMilliseconds,BytesRead=metrics.BytesRead-read,FilesOpened=metrics.FilesRead.Count-files});
        try{
            object context;
            if(request.Physical==null)context=LeanMo2Sources.RequireContext(request.Mo2);else{PhysicalEnvironment.RequireDirect();context=new{Kind="Physical/deployed",NoMo2=true,Game=request.Game};}
            var phase=Stopwatch.StartNew();long prior=metrics.BytesRead;int opened=metrics.FilesRead.Count;
            byte[] saveBytes=sources.Read(request.Save),cosaveBytes=sources.Read(request.Cosave),headBytes=sources.Read(request.HeadNif),tintBytes=sources.Read(request.TintDds),slotBytes=sources.Read(request.Jslot);
            var selectedBytes=new[]{saveBytes,cosaveBytes,headBytes,tintBytes,slotBytes};var selectedInputs=FollowerInputSelection.FromRequest(request);
            var inputFingerprints=selectedInputs.Paths.Select((path,i)=>new FollowerInputPin(Path.GetFullPath(path),BoundedBinary.Digest(selectedBytes[i]),selectedBytes[i].Length)).ToArray();
            if(inputPreview!=null){FollowerInputPreparation.RequireCurrent(inputPreview,selectedInputs,inputFingerprints);savePreview=inputPreview.SavePreview;}
            T ReadInput<T>(string operation,string path,Func<T> read){try{return read();}catch(InvalidDataException e){throw new InvalidDataException(operation+" '"+path+"': "+e.Message,e);}}
            var saved=savePreview==null?ReadInput("Read selected save",request.Save,()=>LeanSaveReader.Read(saveBytes,request.Save,token,runtime.Formats)):FollowerSettingsPolicy.ReusePreview(savePreview,request.Save,inputFingerprints[0].Sha256,runtime.Formats);
            var cosave=inputPreview?.Cosave??ReadInput("Read selected co-save",request.Cosave,()=>LeanExportInputs.Cosave(cosaveBytes,saved.Plugins,saved.Actor.Sex,runtime.Formats));
            var appearance=inputPreview?.Appearance??ReadInput("Read selected export bundle",string.Join("; ",new[]{request.HeadNif,request.TintDds,request.Jslot}),()=>LeanExportInputs.Bundle(headBytes,tintBytes,slotBytes,saved,request.TintReference,request.ExportAssociation,bestEffort:true));
            var compatibility=new{Notice=runtime.Notice,ExecutableVersion=runtime.Version,ManagerVersion=request.Physical==null?FileVersionInfo.GetVersionInfo(request.Mo2).FileVersion??"":"physical/deployed",Store=runtime.Store,StoreDetected=false,ProducerSkse=cosave.ProducerSkse.ToString("X8"),ProducerRuntime=cosave.ProducerRuntime.ToString("X8"),Formats=SkyrimInputFormats.Common,RequiredMaterialLayouts=new[]{2,3},OtherEnvironmentVerified=false};
            Write("compatibility.json",compatibility);
            LeanAppearanceAssessment materials;
            try{materials=LeanAppearanceAssociations.Assess(LeanExportInputs.ReadNif(headBytes),cosave.Skee,bestEffort:true);}
            catch(InvalidDataException e){
                qualityWarnings.Add(BestEffortBuild.Finding("material-association",e.Message));
                materials=new([],[]);
            }
            qualityWarnings.AddRange(materials.QualityWarnings??[]);
            qualityWarnings.AddRange(appearance.QualityWarnings??[]);
            Write("material-assessment.json",materials);
            var settingsContext=FollowerSettingsPolicy.Context(saved.Actor);
            var selectedSettings=chooseSettings==null?request.Settings:chooseSettings(settingsContext);token.ThrowIfCancellationRequested();
            var followerSettings=FollowerSettingsPolicy.Resolve(selectedSettings,saved.Actor);
            Write("follower-settings.json",new{Context=settingsContext,Resolved=followerSettings});
            var pins=inputFingerprints;
            Phase("read selected saved state and export bundle",phase,prior,opened);token.ThrowIfCancellationRequested();
            phase.Restart();prior=metrics.BytesRead;opened=metrics.FilesRead.Count;
            SelectedEnvironment environment; string[] order;
            if(request.Physical!=null){environment=PhysicalEnvironment.Read(request.Game,request.Physical,sources,saved.Plugins.Entries.Select(x=>x.Name).ToArray(),request.Store);order=environment.ActivePlugins;Write("selected-environment.json",environment);Write("source-admission.json",new{Kind="Physical",environment.Pins,environment.SourceAccess});}
            else {
            var environmentPins=new List<EnvironmentFilePin>();
            EnvironmentFilePin PinEnvironment(string path){var pin=SelectedEnvironment.Pin(sources,path);environmentPins.Add(pin);return pin;}
            PinEnvironment(request.InstanceIni);
            var ini=Mo2Configuration.Read(Encoding.UTF8.GetString(sources.Read(request.InstanceIni)),request.InstanceIni);string selected=ini.GetValueOrDefault("General/selected_profile")??"";
            if(selected!=request.Profile)throw new InvalidDataException("Selected MO2 profile changed; no profile switch is performed");
            string game=ini.GetValueOrDefault("General/gamePath")??"";if(!Path.GetFullPath(game).Equals(Path.GetFullPath(request.Game),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Selected MO2 game differs");
            SkyrimRuntimeProfile.Require(request.Store,FileVersionInfo.GetVersionInfo(Path.Combine(request.Game,"SkyrimSE.exe")).FileVersion??"");
            string baseDir=ini.GetValueOrDefault("Settings/base_directory")??throw new InvalidDataException("MO2 base directory missing");
            string profiles=(ini.GetValueOrDefault("Settings/profiles_directory")??Path.Combine(baseDir,"profiles")).Replace("%BASE_DIR%",baseDir,StringComparison.OrdinalIgnoreCase),profile=Path.GetFullPath(Path.Combine(profiles,request.Profile));
            string[] Lines(string path){PinEnvironment(path);return Encoding.UTF8.GetString(sources.Read(path)).TrimStart('\uFEFF').Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0&&!x.StartsWith('#')&&!x.StartsWith(';')).ToArray();}
            string ccc=Path.Combine(request.Game,"Skyrim.ccc");string[] cc=File.Exists(ccc)?Lines(ccc):[];if(!File.Exists(ccc))PinEnvironment(ccc);
            bool PresentPlugin(string name){string path=sources.Virtual(name);bool exists=File.Exists(path);if(!exists)PinEnvironment(path);return exists;}
            order=SelectedPluginOrder.Resolve(Lines(Path.Combine(profile,"plugins.txt")),Lines(Path.Combine(profile,"loadorder.txt")),cc,PresentPlugin,"Selected MO2 profile '"+request.Profile+"'");
            var active=order.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Saved IDs use their saved table; winning inputs use this active
            // environment. Unrelated old save plugins are not output inputs.
            // Every required supporting record/dependency still resolves below
            // through LeanRecordIndex over the selected active order only.
            var requiredSaved=saved.Actor.HeadParts.Concat(saved.Actor.Worn.Select(x=>x.Canonical)).Append(saved.Actor.Race).Append(saved.Actor.FaceTextureSet).Append(saved.Actor.Placement.Cell)
                .Where(x=>x.Contains('|')).Select(x=>x[..x.LastIndexOf('|')]).Distinct(StringComparer.OrdinalIgnoreCase).Where(x=>!active.Contains(x)).ToArray();
            if(requiredSaved.Length>0)throw new InvalidDataException("Required saved input plugins are inactive in selected MO2: "+string.Join(", ",requiredSaved));
            string query=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"My Games","Skyrim Special Edition","Skyrim.ini");
            PinEnvironment(Path.Combine(profile,"settings.ini"));
            string settingsPath=Path.Combine(profile,"settings.ini");
            bool localInis=Mo2Configuration.LocalInis(ini,File.Exists(settingsPath)?Encoding.UTF8.GetString(sources.Read(settingsPath)):null);
            var liveProfile=Mo2ProfileEvidence.Read();liveProfile.Require(profile,localInis);
            foreach(string name in new[]{"plugins.txt","loadorder.txt"})PinEnvironment(Mo2ProfileEvidence.PluginQuery(name));
            if(File.Exists(Path.Combine(request.Game,"Skyrim.ini")))throw new InvalidDataException("Game-directory Skyrim.ini override remains unsupported by the selected source rule");
            Dictionary<string,string> effectiveIni=new(StringComparer.OrdinalIgnoreCase);List<object> iniEvidence=[];string? configured=null;
            void ReadPolicyIni(string path,string? expected,bool primary){
                using var file=sources.Open(path);string backing=LeanMo2Sources.Backing(file);
                if(expected!=null&&!Path.GetFullPath(backing).Equals(Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Normal MO2 INI mapping does not match selected profile: "+path+" -> "+backing);
                byte[] bytes=sources.At(file,0,checked((int)file.Length));var values=ParseIni(Encoding.UTF8.GetString(bytes));
                string? value=LeanMo2Sources.EffectiveInvalidation(path);
                // Reject disabling/unknown values in any potential override layer;
                // do not guess ordering for conflicting plugin/custom INIs.
                if(value!=null&&(configured==null||value!="1"))configured=value;
                if(primary)effectiveIni=values;
                else if(values.Keys.Any(x=>x.StartsWith("Archive/sResourceArchiveList",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Archive-list override outside primary Skyrim.ini remains unsupported: "+backing);
                environmentPins.Add(new(Path.GetFullPath(path),backing,BoundedBinary.Digest(bytes),bytes.Length));
                iniEvidence.Add(new{Path=path,Backing=backing,Sha256=BoundedBinary.Digest(bytes),Bytes=bytes.Length,ConfiguredInvalidation=value});
            }
            foreach(string name in new[]{"Skyrim.ini","SkyrimPrefs.ini","SkyrimCustom.ini"}){
                string path=Path.Combine(Path.GetDirectoryName(query)!,name),expected=localInis?Path.Combine(profile,name):path;
                if(name=="Skyrim.ini"||File.Exists(expected)||File.Exists(path))ReadPolicyIni(path,expected,name=="Skyrim.ini");else PinEnvironment(path);
            }
            // Active plugin INIs can alter game settings. Only these concrete
            // potential policy inputs are read, through the selected VFS.
            foreach(string plugin in order){string path=sources.Virtual(Path.ChangeExtension(plugin,".ini"));if(File.Exists(path))ReadPolicyIni(path,null,false);else PinEnvironment(path);}
            var lookup=LeanLookupPolicy.Evaluate(FileVersionInfo.GetVersionInfo(request.Mo2).FileVersion??"",FileVersionInfo.GetVersionInfo(Path.Combine(request.Game,"SkyrimSE.exe")).FileVersion??"",configured,query);
            Write("source-admission.json",new{Policy=lookup,IniSources=iniEvidence,GeneratedTweaksAreEffectiveInput=false});
            string[] archiveNames=new[]{"Archive/sResourceArchiveList","Archive/sResourceArchiveList2"}.SelectMany(x=>(effectiveIni.GetValueOrDefault(x)??"").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).ToArray();string archivesFile=Path.Combine(profile,"archives.txt");sources.ConfigureArchives(order,archiveNames,File.Exists(archivesFile)?Lines(archivesFile):[],lookup);
            PinEnvironment(archivesFile);PinEnvironment(Path.Combine(profile,"modlist.txt"));
            environment=SelectedEnvironment.Create(request.Game,request.InstanceIni,request.Profile,profile,query,order,request.Store,environmentPins.ToArray());
            sources.Bind(environment);environment.RequireCurrent(sources);Write("selected-environment.json",environment);
            }
            var index=new LeanRecordIndex(sources,token);index.SetLanguage(ParseIni(Encoding.UTF8.GetString(sources.Read(environment.IniQuery))).GetValueOrDefault("General/sLanguage")??"english");index.Scan(order,!saved.Actor.ScaleFromChangeForm);Phase("index required record groups in active plugins",phase,prior,opened);
            saved=saved with{Actor=LeanCharacterInputs.ResolveSavedDefaults(saved.Actor,key=>index.Get(key),index.PlayerReference)};
            // Automatic scale must consume the selected base reference once its
            // winner is known. An explicit GUI/request scale remains an override.
            followerSettings=FollowerSettingsPolicy.Resolve(selectedSettings,saved.Actor);
            Write("follower-settings.json",new{Context=FollowerSettingsPolicy.Context(saved.Actor),Resolved=followerSettings});
            phase.Restart();prior=metrics.BytesRead;opened=metrics.FilesRead.Count;
            FollowerSettingsPolicy.RequireRecords(followerSettings,(key,origin)=>index.Get(key,origin));
            FollowerSettingsPolicy.RequireNameAvailable(followerSettings,name=>order.Contains(name,StringComparer.OrdinalIgnoreCase)||File.Exists(sources.Virtual(name)));
            Phase("validate selected follower settings records",phase,prior,opened);
            phase.Restart();prior=metrics.BytesRead;opened=metrics.FilesRead.Count;Queue<(string Reference,string Consumer)> pending=[];List<LeanEquipment> equipment=[];
            var race=index.Get(saved.Actor.Race);var character=LeanCharacterInputs.Resolve(saved.Actor,key=>index.Get(key));HashSet<string> races=new(character.ArmorRaces,StringComparer.OrdinalIgnoreCase);
            var privateRaceRecords=LeanRaceAbilities.Resolve(race,key=>index.Get(key),LeanRacePolicy.IsExisting(followerSettings));
            Write("character-inputs.json",character);
            var npc=index.Get("Skyrim.esm|000007");LeanPlacement? resolvedPlacement=null;bool interior=false;
            try{resolvedPlacement=index.ResolvePlacement(saved.Actor.Placement);saved=saved with{Actor=saved.Actor with{Placement=resolvedPlacement.Transform}};interior=resolvedPlacement.Interior;}
            catch(Exception e)when(e is InvalidDataException or InvalidOperationException or OverflowException){gaps.Add(new("saved placement "+saved.Actor.Placement.Cell,e.Message,"Resolve this exact CELL/WRLD/group/transform input; no relocation or location substitution"));}
            Write("placement-inputs.json",new{Resolved=resolvedPlacement,Saved=saved.Actor.Placement});
            HashSet<string> materialRecords=new(StringComparer.OrdinalIgnoreCase);List<LeanRecord> hostEffects=[];
            void Material(string key){if(!materialRecords.Add(key))return;var record=index.Get(key);if(record.Type=="TXST")foreach(string tag in new[]{"TX00","TX01","TX02","TX03","TX04","TX05","TX06","TX07","TX08"})foreach(string value in record.Text(tag).Where(x=>x.Length>0))pending.Enqueue(("textures/"+value,"effective TXST "+key));else if(record.Type=="FLST")foreach(string child in record.Refs("LNAM"))Material(child);else if(record.Type=="ENCH"){
                foreach(string child in record.Refs("EFID"))if(!child.StartsWith("Skyrim.esm|",StringComparison.OrdinalIgnoreCase))Material(child);
                foreach(byte[] data in record.Fields.GetValueOrDefault("ENIT",[])){if(data.Length!=36)throw new InvalidDataException("Required enchantment ENIT framing unsupported: "+key);foreach(int offset in new[]{28,32}){string? child=record.Ref(BitConverter.ToUInt32(data,offset));if(child!=null&&!child.StartsWith("Skyrim.esm|",StringComparison.OrdinalIgnoreCase))Material(child);}}
            }else if(record.Type=="MGEF"){
                // Gameplay magic effects, including VMAD scripts and effect art,
                // stay bound to their existing host record/master. No cloned
                // runtime or partial effect-asset reconstruction is emitted.
                hostEffects.Add(record);
            }else throw new InvalidDataException("Required material/effect record needs a specific asset/reference consumer: "+key+" "+record.Type);}
            void Alternate(LeanRecord record,string tag){foreach(byte[] bytes in record.Fields.GetValueOrDefault(tag,[])){var q=new BoundedBinary(bytes);int count=q.Count(10000);for(int a=0;a<count;a++){q.Take(q.Count(1048576));uint id=q.U32();q.U32();string key=record.Ref(id)??throw new InvalidDataException("Null alternate TXST");Material(key);}q.End("alternate texture field "+tag+" of "+record.Type+" "+record.Canonical+" from "+record.Plugin+" ("+record.Backing+", record offset="+record.Offset+")");}}
            HashSet<string> heads=new(StringComparer.OrdinalIgnoreCase);List<LeanHeadMorphExclusion> headMorphExclusions=[];List<LeanHeadPhysicsModel> headPhysicsModels=[];List<LeanHeadIdentityBinding> headIdentityBindings=[];
            var finishedHead=LeanExportInputs.ReadNif(headBytes);
            HashSet<string> hairParts=new(StringComparer.OrdinalIgnoreCase);
            void HairTree(string key){if(!hairParts.Add(key))return;foreach(string child in index.Get(key).Refs("HNAM"))HairTree(child);}
            foreach(string key in saved.Actor.HeadParts){var part=index.Get(key);var type=part.Fields.GetValueOrDefault("PNAM",[]);var flag=part.Fields.GetValueOrDefault("DATA",[]);if(type.Count==1&&type[0].Length==4&&BitConverter.ToUInt32(type[0])==3&&flag.Count==1&&flag[0].Length==1&&(flag[0][0]&8)==0)HairTree(key);}
            void Head(string head){if(!heads.Add(head))return;var record=index.Get(head);if(record.Type!="HDPT")throw new InvalidDataException("Saved/extra head reference is not HDPT: "+head);
                string[] models=record.Text("MODL").Where(x=>x.Length>0).ToArray();bool staticHair=false;
                foreach(string model in models)headPhysicsModels.Add(new(head,record.Text("EDID").Single(),LeanModelInputs.Reference(model)));
                if(models.Length==1){LeanExportInputs.Nif? installed=null;sources.Stage("meshes/"+models[0],"saved HDPT model "+head,pending,inspectNif:n=>installed=n);if(installed!=null){staticHair=LeanHeadMorphInputs.StaticExtraHair(record,installed,finishedHead,hairParts.Contains(head));var identity=LeanHeadMorphInputs.BindIdentity(record,installed,finishedHead,LeanRacePolicy.IsExisting(followerSettings));if(identity!=null)headIdentityBindings.Add(identity);}}
                else foreach(string path in models)pending.Enqueue(("meshes/"+path,"saved HDPT model "+head));
                foreach(var part in LeanHeadMorphInputs.Parts(record).Where(x=>x.Path.Length>0)){
                    if(staticHair&&part.Type==1){headMorphExclusions.Add(new(head,part.Type,LeanModelInputs.Reference(part.Path),record.Text("EDID").Single(),"Extra Hair; installed and exact EDID-bound finished shapes are static BSTriShape with bone skinning. Expression vertex TRI has no dynamic facial target; SMP meshes/bones/XML/textures stay required."));continue;}
                    pending.Enqueue(("meshes/"+part.Path,"saved HDPT morph "+head));
                }
                foreach(string key in record.Refs("TNAM"))Material(key);foreach(string extra in record.Refs("HNAM"))Head(extra);}
            foreach(string head in saved.Actor.HeadParts)Head(head);
            LeanHeadClosurePart[]? retainedHeadClosure=null;
            if(LeanRacePolicy.IsExisting(followerSettings)){
                var outputNames=LeanHeadMorphInputs.ShapeNames(finishedHead).Select(name=>headIdentityBindings.FirstOrDefault(x=>x.ExportedShapeName==name)?.OutputShapeName??name);
                retainedHeadClosure=LeanRetainedHeadClosure.Resolve(appearance.HeadParts.EnumerateArray().Select(x=>x.GetProperty("canonical").GetString()!),index.RaceDefaultHeadParts(saved.Actor.Race,saved.Actor.Sex),key=>index.Get(key),headIdentityBindings,outputNames);
                Write("retained-head-closure.json",retainedHeadClosure);
            }
            Write("head-morph-inputs.json",headMorphExclusions);
            string[] buildWarnings=LeanRuntimeAssignmentPolicy.Warnings(cosave.Skee).Concat(headMorphExclusions.Count>0?new[]{"Static SMP hair strands retain their installed geometry and physics. Unconsumed expression TRI entries on proven static extra Hair shapes are not emitted; dynamic hairline and ordinary facial morphs remain required."}:Array.Empty<string>()).ToArray();
            if(saved.Actor.FaceTextureSet.Length>0&&index.Get(saved.Actor.FaceTextureSet).Type!="TXST")throw new InvalidDataException("Saved/exported face-texture identity is not an active TXST");
            var exportedIndices=appearance.HeadParts.EnumerateArray().Select(x=>x.GetProperty("savedIndex").GetInt32()).ToHashSet();
            for(int i=0;i<saved.Actor.HeadParts.Length;i++){
                var record=index.Get(saved.Actor.HeadParts[i]);if(!record.Fields.TryGetValue("DATA",out var flags)||flags.Count!=1||flags[0].Length!=1)throw new InvalidDataException("Saved HDPT flags unavailable: "+record.Canonical);
                bool extra=(flags[0][0]&8)!=0;if(exportedIndices.Contains(i)==extra)qualityWarnings.Add(BestEffortBuild.Finding("head-coverage","JSlot primary/extra head coverage contradicts selected HDPT: "+record.Canonical));
            }
            foreach(var texture in appearance.FaceTextures.EnumerateArray())pending.Enqueue(("textures/"+texture.GetProperty("texture").GetString(),"JSlot consumed face texture"));
            if(!LeanRacePolicy.IsExisting(followerSettings)){
                string skinTintTexture=VirtualPath.Normalize(appearance.TintLighting.GetProperty("texture").GetString()!);
                if(!skinTintTexture.StartsWith("textures/",StringComparison.OrdinalIgnoreCase))skinTintTexture="textures/"+skinTintTexture;
                pending.Enqueue((skinTintTexture,"captured private-race skin-tone tint asset"));
            }
            var equipmentCatalog=LeanEquipmentChoices.Catalog(saved.Actor,index);
            var excludedEquipment=LeanEquipmentChoices.Exclusions(equipmentCatalog,request.ExcludedEquipment);
            LeanEquipmentOmission[] equipmentOmissions=equipmentCatalog.Where(c=>excludedEquipment.Contains(c.Canonical)).Select(c=>new LeanEquipmentOmission(c.Canonical,c.Name,[],"Voluntarily omitted equipment "+c.Name+" ("+c.Canonical+"). Its exclusive output inputs and links are excluded.")).ToArray();
            Write("equipment-choices.json",new{Catalog=equipmentCatalog,Excluded=excludedEquipment,Default="Include all",equipmentOmissions});
            foreach(var item in saved.Actor.Worn){if(excludedEquipment.Contains(item.Canonical))continue;var armor=index.Get(item.Canonical);if(LeanModelInputs.MeshlessWeapon(armor))continue;
                if(armor.Type=="WEAP"){
                    List<string> models=[];
                    foreach(string tag in new[]{"MODL","MOD3"})foreach(string path in armor.Text(tag).Where(x=>x.Length>0)){models.Add(path);pending.Enqueue(("meshes/"+path,item.Canonical+" weapon "+tag));}
                    Alternate(armor,"MODS");Alternate(armor,"MO3S");
                    foreach(string first in armor.Refs("WNAM")){var stat=index.Get(first);if(stat.Type!="STAT")throw new InvalidDataException("Weapon first-person model is not STAT: "+first);foreach(string path in stat.Text("MODL").Where(x=>x.Length>0)){models.Add(path);pending.Enqueue(("meshes/"+path,item.Canonical+" first-person STAT "+first));}Alternate(stat,"MODS");}
                    foreach(string key in armor.Refs("EITM"))if(!key.StartsWith("Skyrim.esm|",StringComparison.OrdinalIgnoreCase))Material(key);
                    equipment.Add(new(item.Canonical,item.Count,[],models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),"Original WEAP MODL/MOD3 and WNAM STAT calls retained","WEAP"));continue;
                }
                if(armor.Type=="AMMO"){
                    string[] models=LeanAmmoInputs.Resolve(armor,key=>index.Get(key),pending,Alternate,Material);
                    if(models.Length>0)equipment.Add(new(item.Canonical,item.Count,[],models,"Original AMMO model and DATA projectile models retained","AMMO"));
                    continue;
                }
                if(armor.Type!="ARMO"){gaps.Add(new("worn "+item.Canonical,"Worn record type "+armor.Type+" requires its applicable model adapter","Add only this concrete record-model consumer"));continue;}
                List<LeanAddon> addons=[];foreach(string addon in armor.Refs("MODL")){var ar=index.Get(addon);if(ar.Type!="ARMA")throw new InvalidDataException("Armor addon type invalid");bool applies=ar.Refs("RNAM").Concat(ar.Refs("MODL")).Any(races.Contains);var worldModel=LeanModelInputs.Biped(ar,saved.Actor.Sex,false);var firstModel=LeanModelInputs.Biped(ar,saved.Actor.Sex,true);string[] world=worldModel.Paths,perspective=firstModel.Paths;
                    if(applies){foreach(string path in world.Concat(perspective)){pending.Enqueue(("meshes/"+path,item.Canonical+"/"+addon+" applicable worn model"));if((path.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)||path.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase))&&ar.Fields.TryGetValue("DNAM",out var dnam)&&dnam.Count==1&&dnam[0].Length>3&&(dnam[0][saved.Actor.Sex==1?3:2]&2)!=0)pending.Enqueue(("meshes/"+path[..^6]+(path.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)?"_0.nif":"_1.nif"),item.Canonical+" weight-slider counterpart"));}
                        foreach(string key in ar.Refs(saved.Actor.Sex==1?"NAM1":"NAM0").Concat(ar.Refs(saved.Actor.Sex==1?"NAM3":"NAM2")))Material(key);Alternate(ar,worldModel.Basis.Contains("fallback")?"MO2S":saved.Actor.Sex==1?"MO3S":"MO2S");Alternate(ar,firstModel.Basis.Contains("fallback")?"MO4S":saved.Actor.Sex==1?"MO5S":"MO4S");
                    }
                    if(applies&&(world.Length>0||perspective.Length>0))addons.Add(new(addon,true,world,perspective,worldModel.Basis,firstModel.Basis));}
                if(addons.Count==0)continue;string[] female=armor.Text("MOD4").Where(x=>x.Length>0).ToArray(),male=armor.Text("MOD2").Where(x=>x.Length>0).ToArray();string[] ground=male.Concat(female).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();foreach(string path in ground)pending.Enqueue(("meshes/"+path,item.Canonical+" emitted original ARMO ground/inventory models"));if(male.Length>0)Alternate(armor,"MODS");if(female.Length>0)Alternate(armor,"MO4S");foreach(string key in armor.Refs("EITM"))if(!key.StartsWith("Skyrim.esm|",StringComparison.OrdinalIgnoreCase))Material(key);equipment.Add(new(item.Canonical,item.Count,addons.ToArray(),ground,"D54 original ARMO MOD2/MOD4 calls retained"));
            }
            var privateSoundRecords=LeanWeaponSounds.Resolve(equipment.Where(e=>e.Type is "WEAP" or "AMMO").Select(e=>index.Get(e.Canonical)),key=>index.Get(key),pending);
            Write("weapon-sound-inputs.json",new{Records=privateSoundRecords,Basis="Selected weapon-called mod sound descriptors, category/output closure and exact audio winners; no behavior substitution"});
            LeanSkin? skin=null;try{skin=LeanSkinInputs.Resolve(key=>index.Get(key),saved.Actor,races,pending);}catch(InvalidDataException e){gaps.Add(new("effective body skin",e.Message,"Resolve the named ordinary selected skin/addon/model from active record winners; no invented skin or historical capture substitution"));}
            if(skin!=null)foreach(string addon in skin.Addons){var ar=index.Get(addon);foreach(string key in ar.Refs(saved.Actor.Sex==1?"NAM1":"NAM0").Concat(ar.Refs(saved.Actor.Sex==1?"NAM3":"NAM2")))Material(key);var model=LeanModelInputs.Biped(ar,saved.Actor.Sex,false);Alternate(ar,model.Basis.Contains("fallback")?"MO2S":saved.Actor.Sex==1?"MO3S":"MO2S");}
            var overlayNotice=LeanOverlayPolicy.Describe(cosave.Skee);
            var bindings=materials.Bindings;
            foreach(var binding in bindings)pending.Enqueue((binding.Reference,"persisted SKEE "+binding.Node+" slot "+binding.Slot));
            foreach(var conflict in materials.Conflicts)pending.Enqueue((conflict.SavedReference,"disclosed saved material conflict "+conflict.Node+"; finished export retained"));
            pending.Enqueue((character.Skeleton,"winning selected-sex race skeleton"));
            bool obodyInstalled=File.Exists(sources.Virtual("SKSE/Plugins/OBody.dll"));
            var outputs=LeanOutputPolicy.Resolve(request.Outputs,obodyInstalled,request.ExpectedObodyPresent,bestEffort:true);
            if(outputs.Obody&&!obodyInstalled)qualityWarnings.Add(BestEffortBuild.Finding("obody-dependency","Selected environment contains no OBody host","compatibility-unavailable","The requested OBody branch preserves captured meshes, but OBody is unavailable in the selected environment. No working preset application is claimed."));
            if(request.ExpectedObodyPresent.HasValue&&request.ExpectedObodyPresent.Value!=obodyInstalled)qualityWarnings.Add(BestEffortBuild.Finding("obody-presence","OBody presence differs from input preparation; current selected-environment evidence is used"));
            bool capturedObody=LeanOutputPolicy.CapturedObody(outputs,obodyInstalled,cosave);
            var outputPlan=LeanOutputPolicy.Plan(outputs,obodyInstalled,capturedObody);
            if(obodyInstalled)sources.Stage("SKSE/Plugins/OBody.dll","selected environment OBody presence proof",pending,followDependencies:false,stagePayload:false);
            Write("output-selection.json",outputPlan);
            string? preset=cosave.Preset;
            byte[]? authored=null;LeanAssignedPreset? assignedPreset=null;object? body=null;try{if(capturedObody){if(preset==null)gaps.Add(new("OBody assignment","OBody installed but supported saved registry has no player preset","Supply supported assignment evidence; do not infer from morphs"));else{
                    if(request.PresetXml==null)gaps.Add(new("OBody XML","Saved assignment="+preset+"; authored XML not selected","Select the actual preset XML; JSlot bodyMorphs are diagnostics"));
                    else{using(var xmlFile=sources.Open(request.PresetXml)){if(request.PresetBacking!=null&&!LeanMo2Sources.Backing(xmlFile).Equals(request.PresetBacking,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Selected authored preset winner changed; start a fresh build");authored=sources.At(xmlFile,0,checked((int)xmlFile.Length));}if(request.PresetSha256!=null&&BoundedBinary.Digest(authored)!=request.PresetSha256)throw new InvalidDataException("Selected authored preset XML changed; start a fresh build");assignedPreset=LeanAssignedPreset.Admit(authored,preset,qualityWarnings);authored=assignedPreset?.Bytes;if(authored!=null)body=new{AssignedPreset=preset,Xml=request.PresetXml,Sha256=BoundedBinary.Digest(authored)};}}} }catch(Exception e)when(e is InvalidDataException or System.Xml.XmlException or FileNotFoundException or DirectoryNotFoundException){
                qualityWarnings.Add(BestEffortBuild.Finding("assigned-preset",e.Message,"prebuilt-fallback","The saved preset could not be recovered from the selected XML. Captured installed meshes remain available; no preset bake or OBody assignment is claimed."));
                authored=null;assignedPreset=null;body=null;
            }
            Phase("derive applicable required inputs",phase,prior,opened);
            phase.Restart();prior=metrics.BytesRead;opened=metrics.FilesRead.Count;pending.Enqueue(("meshes/lean-export/head.nif","declared exported finished head"));pending.Enqueue((appearance.TintReference,"declared composed export tint"));
            bool EquipmentConsumer(string consumer)=>equipment.Any(e=>consumer.StartsWith(e.Canonical,StringComparison.OrdinalIgnoreCase));
            string[] coreRoots=pending.Where(x=>!EquipmentConsumer(x.Consumer)).Select(x=>x.Reference).Concat(sources.Sources.Where(s=>!EquipmentConsumer(s.Consumer)&&!s.Consumer.StartsWith("NIF dependency of ",StringComparison.Ordinal)).Select(s=>s.Reference)).ToArray();
            List<(string Reference,string Consumer)> assetCalls=[];
            Dictionary<string,string?> bodyTris=new(StringComparer.OrdinalIgnoreCase);Dictionary<string,Dictionary<string,int>> modelShapes=new(StringComparer.OrdinalIgnoreCase);Dictionary<string,Dictionary<string,(HashSet<string> Sliders,int Max)>> triInputs=new(StringComparer.OrdinalIgnoreCase);HashSet<string> visited=new(StringComparer.OrdinalIgnoreCase);while(pending.Count>0){token.ThrowIfCancellationRequested();if(File.Exists(Path.Combine(output,"stop")))throw new OperationCanceledException(token);var (reference,consumer)=pending.Dequeue();assetCalls.Add((reference,consumer));string normalized=VirtualPath.Normalize(reference);if(!visited.Add(normalized))continue;
                bool head=normalized=="meshes/lean-export/head.nif",tint=normalized.Equals(appearance.TintReference,StringComparison.OrdinalIgnoreCase);
                try{sources.Stage(normalized,consumer,pending,head?request.HeadNif:tint?request.TintDds:null,head?headBytes:tint?tintBytes:null,followDependencies:true,consume:b=>{if(bodyTris.Values.Any(x=>x!=null&&x.Equals(normalized,StringComparison.OrdinalIgnoreCase)))triInputs[normalized]=LeanMorphInputs.Tri(b);},inspectNif:n=>{bodyTris[normalized]=LeanBodyInputs.BodyTri(n);modelShapes[normalized]=LeanMorphInputs.Shapes(n);});}catch(InvalidDataException e)when(e.InnerException is InvalidDataException cause&&(cause.Message.StartsWith("Creator texture path",StringComparison.Ordinal)||cause.Message.StartsWith("Malformed creator texture path",StringComparison.Ordinal))){gaps.Add(new(normalized,"Unsupported embedded creator texture reference: "+e.Message,"Repair this input or explicitly exclude only its affected optional equipment"));}catch(LeanSourceAdmissionException e){gaps.Add(new(normalized,e.Message,"Establish supported effective loose/archive lookup settings for this selected environment before admitting this source"));}catch(FileNotFoundException){gaps.Add(new(normalized,"Required loose/archive member missing in selected MO2","Resolve the actual called asset without substituting historical payloads"));}catch(InvalidDataException e)when(e.Message.StartsWith("Required archive member winner remains unsupported",StringComparison.Ordinal)){gaps.Add(new(normalized,e.Message,"Establish only this concrete activation/member precedence; no matching-byte winner inference"));}
            }Phase("resolve and stage unique established assets",phase,prior,opened);
            // These actual operations need readable geometry/morph/config inputs;
            // missing other called assets are explicitly warned, not substituted.
            var indispensable=skin?.Models??Array.Empty<string>();
            string RequiredReason(string reference){
                bool Is(string value)=>LeanPackagingChoices.Ref(value).Equals(reference,StringComparison.OrdinalIgnoreCase);
                string operation=Is(character.Skeleton)?"bind captured skin/SMP weighted bones to the selected skeleton":
                    Is("meshes/lean-export/head.nif")?"preserve and emit the selected finished FaceGen geometry":
                    Is(appearance.TintReference)?"emit the selected composed FaceTint image":
                    headPhysicsModels.Any(h=>Is(h.Reference))?"bind selected headpart identities and compose their captured SMP geometry":
                    bodyTris.Values.Any(x=>x!=null&&Is(x))?"verify captured BODYTRI shape/vertex compatibility and applied-preset targets":
                    sources.Sources.Any(s=>(s.TextureRepairs??[]).Any(r=>Is(r.Reference)))?"prove the exact selected texture winner before repairing a creator-absolute reference":
                    indispensable.Any(Is)?"construct and validate the captured body skin geometry":
                    "inspect captured outfit geometry/morph targets and select the required XML prebuilt route";
                if(reference.EndsWith(".xml",StringComparison.OrdinalIgnoreCase))operation="compose and validate the captured head SMP physics/bone configuration";
                return "Cannot "+operation+" without readable bytes for "+reference+". Keeping only the original reference cannot perform this operation; restore the exact input or exclude affected optional equipment.";
            }
            var missingReferences=LeanMissingReferences.Admit(gaps,sources.Sources,equipment,
                Array.Empty<string>(),assetCalls,key=>index.Get(key).Text("FULL").FirstOrDefault(x=>x.Length>0)??key,RequiredReason);
            Write("missing-references.json",missingReferences);
            phase.Restart();prior=metrics.BytesRead;opened=metrics.FilesRead.Count;
            List<object> hostResources=hostEffects.Select(e=>(object)new{Reference=e.Canonical,Status="RequiredHostRecord",Authority=e,Basis="Original gameplay magic effect and its scripts/art remain supplied by the required origin master; no packaged runtime"}).ToList();var originRace=index.Get(saved.Actor.Race,origin:true);foreach(string path in race.Text("MODL")){
                string reference=LeanModelInputs.Reference(path);
                // D54's accepted review explicitly retains these unchanged
                // Skyrim origin NAM1/MODL fields as informational, non-blocking.
                // Their absent provider is not a new required extraction input.
                if(originRace.Text("MODL").Contains(path,StringComparer.OrdinalIgnoreCase)&&(reference.Equals("meshes/Actors/Character/UpperBodyHumanMale.egt",StringComparison.OrdinalIgnoreCase)||reference.Equals("meshes/Actors/Character/UpperBodyHumanFemale.egt",StringComparison.OrdinalIgnoreCase))){hostResources.Add(new{Reference=reference.ToLowerInvariant(),Status="InformationalNonBlocking",InheritedVanillaField=new{originRace.Canonical,originRace.Plugin,originRace.Backing,originRace.Offset,originRace.Sha256,Reference=path},Basis="D54/D57: unchanged known legacy human EGT fields retained, including copies in custom race origins; no required finished-export/physics consumer or demonstrated defect"});continue;}
                try{var host=sources.Stage(reference,"inherited race game-system; host resource rather than private payload",pending,followDependencies:false,stagePayload:false);hostResources.Add(new{Reference=reference.ToLowerInvariant(),Status="Resolved",Authority=host,InheritedVanillaField=new{originRace.Canonical,originRace.Plugin,originRace.Backing,originRace.Offset,originRace.Sha256,Reference=path}});}catch(Exception e)when(e is FileNotFoundException or InvalidDataException or DirectoryNotFoundException){gaps.Add(new(reference,e.Message,"Establish the selected host game-system backing source; no unclassified race reference"));}
            }
            if(obodyInstalled&&assignedPreset!=null&&authored!=null&&preset!=null&&skin!=null){try{
                var sliders=assignedPreset.SliderNames.ToHashSet(StringComparer.Ordinal);
                string[] outfitModels=equipment.SelectMany(e=>e.Addons.SelectMany(a=>a.ThirdPerson.Concat(a.FirstPerson))).Select(x=>VirtualPath.Normalize("meshes/"+x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string[] primary=skin.Models.Concat(outfitModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string[] targets=primary.Concat(primary.Where(x=>x.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)||x.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase)).Select(x=>x[..^6]+(x.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)?"_0.nif":"_1.nif")).Where(bodyTris.ContainsKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();List<LeanBodyModel> models=[];
                foreach(string target in targets){if(!bodyTris.TryGetValue(target,out string? tri))throw new InvalidDataException("Required called body/outfit mesh is not established: "+target);LeanMorphShape[] compatible=[];if(tri!=null){if(!triInputs.TryGetValue(tri,out var morphs))throw new InvalidDataException("Required called BODYTRI is not established: "+target+" -> "+tri);compatible=LeanMorphInputs.Compatible(modelShapes[target],morphs);}
                    bool eligible=(skin.ObodyBodyModels??[]).Contains(target,StringComparer.OrdinalIgnoreCase)||outfitModels.Contains(target,StringComparer.OrdinalIgnoreCase)||(target.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase)||target.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase))&&outfitModels.Contains(target[..^6]+(target.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)?"_0.nif":"_1.nif"),StringComparer.OrdinalIgnoreCase);
                    bool affected=eligible&&compatible.Any(x=>x.Sliders.Any(sliders.Contains));
                    models.Add(new(target,tri,compatible,affected,!eligible?"T12C user policy: captured non-body skin remains as-is in every output; only preset-affected bodies and outfits build":affected?"Applicable installed BODYTRI shape/vertex ranges and assigned XML slider intersection":"No matching assigned-preset morph target; as-is"));}
                var projectChoices=new List<object>();
                LeanBodyProjectSelection? ChooseProject(LeanBodyProjectChoice choice){var selection=chooseBodyProject!(choice);projectChoices.Add(new{Choice=choice,Selection=selection});Write("body-project-choices.json",projectChoices);return selection;}
                try{body=LeanBodyInputs.Prepare(sources,request,preset,authored,models.ToArray(),pending,outputs.Prebuilt,chooseBodyProject==null?null:ChooseProject,token);}
                catch(LeanMissingPrebuiltProjectsException failure){
                    qualityWarnings.Add(BestEffortBuild.Finding("prebuilt-inputs",failure.Message,"prebuilt-fallback","The requested preset bake is unavailable for some targets. Captured installed meshes are preserved; the shape may differ and OBody may be needed."));
                    body=LeanBodyInputs.Prepare(sources,request,preset,authored,models.ToArray(),pending,false,null,token);
                }
            }catch(Exception e)when(e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException){
                qualityWarnings.Add(BestEffortBuild.Finding("prebuilt-inputs",e.Message,"prebuilt-fallback","The requested preset bake could not be prepared. Captured installed meshes are preserved; the shape may differ and OBody may be needed."));
                string xmlRef="metadata/authored-preset.xml";
                if(!sources.Sources.Any(s=>s.Reference.Equals(xmlRef,StringComparison.OrdinalIgnoreCase)))sources.Stage(xmlRef,"selected authored OBody preset",pending,request.PresetXml,authored,followDependencies:false);
                body=new LeanBodyPlan(preset,request.PresetXml!,BoundedBinary.Digest(authored),xmlRef,[],[],"Captured installed fallback; requested preset bake unavailable",new{Fallback=true},[],[]);
            }}
            Phase("resolve required XML prebuilt projects and tool",phase,prior,opened);
            foreach(var selection in request.AssetSelections??[])if(selection.Mode==LeanPackagingChoices.Retain&&selection.Id.StartsWith("shared/",StringComparison.OrdinalIgnoreCase)){
                string reference=selection.Id[7..];var missing=gaps.FirstOrDefault(x=>x.Input.Equals(reference,StringComparison.OrdinalIgnoreCase));
                if(missing!=null)qualityWarnings.Add(BestEffortBuild.Finding("retained-provider",selection.Id+": "+missing.Evidence,"asset-quality","A selected retained dependency is unavailable. Its original reference remains; install the required provider before using the follower."));
            }
            string[] repairWarnings=sources.Sources.Where(s=>s.Reference.StartsWith("meshes/",StringComparison.OrdinalIgnoreCase)&&(s.TextureRepairs?.Length??0)>0).Select(s=>"Repaired creator texture paths in "+s.Reference+" using exact selected Data sources; geometry, shader settings and texture bytes preserved.").ToArray();
            var selections=request.AssetSelections;
            // T11 can present individual choices during this same owned build,
            // keeping the already-staged payload instead of a second discovery/copy.
            string? existingSkeleton=LeanRacePolicy.IsExisting(followerSettings)?character.Skeleton:null;
            if(chooseAssets!=null){selections=chooseAssets(LeanPackagingChoices.Resolve(sources.Sources,body,appearance.TintReference,selections,outputPlan.Variants,existingSkeleton,missingReferences));token.ThrowIfCancellationRequested();}
            LeanAssetChoice[] choices=LeanPackagingChoices.Resolve(sources.Sources,body,appearance.TintReference,selections,outputPlan.Variants,existingSkeleton,missingReferences);
            // Capture final dependency-quality findings before serializing the
            // plan, so the same warnings reach the recipient package and GUI.
            foreach(var choice in choices.Where(x=>x.Mode==LeanPackagingChoices.Retain))try{sources.VerifyRetained(choice.Provider!);}catch(Exception e)when(e is IOException or InvalidDataException && e is not HostProtectionException){qualityWarnings.Add(BestEffortBuild.Finding("retained-provider",choice.Id+": "+e.Message,"asset-quality","A retained dependency could not be reverified. Its original reference remains; the required installed provider may be unavailable or changed."));}
            try{environment.RequireCurrent(sources);}catch(InvalidDataException e){qualityWarnings.Add(BestEffortBuild.Finding("environment-readback",e.Message));}
            index.CaptureKeywords(bestEffort:true);
            Write("record-inputs.json",index.EmitInputs(output,bestEffort:true));qualityWarnings.AddRange(index.QualityWarnings);
            qualityWarnings.AddRange(gaps.Select(g=>BestEffortBuild.Finding("selected-input",g.Evidence)));
            Write("dependency-preview.json",new{BuildWarnings=qualityWarnings,Schema="lean-asset-choices/v1",Assets=choices,HostResources=hostResources,OverlayNotice=overlayNotice,MaterialConflicts=materials.Conflicts,RuntimeAssignmentWarnings=buildWarnings,AssetRepairWarnings=repairWarnings,MissingReferences=missingReferences,EquipmentOmissions=equipmentOmissions,RuntimeAssignmentOmission=LeanSkinInputs.RuntimeAssignmentOmission,DefaultMode=LeanPackagingChoices.Standalone,Gaps=gaps});
            Write("input-plan.json",new LeanInputPlan("lean-export-input-plan/v1",saved.Actor,appearance,equipment.Where(x=>x.Addons.Length>0||x.Type is "WEAP" or "AMMO").ToArray(),body,index.ConsumedRecords(),sources.Sources,pins,gaps,interior,index.HeadersRead,order.Length,Skin:skin,PersistedAppearance:cosave.Skee,AppearanceBindings:bindings,HostResources:hostResources.ToArray(),AssetChoices:choices,FollowerSettings:followerSettings,Outputs:outputPlan,Placement:resolvedPlacement,OverlayNotice:overlayNotice,MaterialConflicts:materials.Conflicts,RuntimeAssignmentWarnings:buildWarnings,HeadMorphExclusions:headMorphExclusions.ToArray(),HeadPhysicsModels:headPhysicsModels.ToArray(),HeadIdentityBindings:headIdentityBindings.ToArray(),PrivateRaceRecords:privateRaceRecords,PrivateSoundRecords:privateSoundRecords,RetainedHeadClosure:retainedHeadClosure,AssetRepairWarnings:repairWarnings,EquipmentOmissions:equipmentOmissions,MissingReferences:missingReferences,BuildWarnings:qualityWarnings.ToArray(),KeywordRemovals:request.KeywordRemovals,KeywordRecoveryInteractive:chooseKeywords!=null));
            if(previewOnly){Write("result.json",new{Status=gaps.Count==0?"dependency-preview-ready":"unsupported-required-input",Compatibility=compatibility,OverlayNotice=overlayNotice,MaterialConflicts=materials.Conflicts,RuntimeAssignmentWarnings=buildWarnings,AssetRepairWarnings=repairWarnings,MissingReferences=missingReferences,EquipmentOmissions=equipmentOmissions,Outputs=outputPlan,Placement=resolvedPlacement,AssetChoices=choices,FollowerSettings=followerSettings,Gaps=gaps,Phases=phases,TotalMilliseconds=total.ElapsedMilliseconds,SourceFilesRead=metrics.FilesRead.Count,SourceBytesRead=metrics.BytesRead,metrics.FilesCopied,metrics.BytesCopied,RuntimeVerified=false});return gaps.Count==0?0:3;}
            phase.Restart();object? headStage=null;
            if(sources.Sources.Any(s=>s.Reference.Equals("meshes/lean-export/head.nif",StringComparison.OrdinalIgnoreCase))&&sources.Sources.Any(s=>s.Reference.Equals(appearance.TintReference,StringComparison.OrdinalIgnoreCase)))headStage=LeanGenerationStage.PrivateHead(output,request.Python,token,request.StagesDirectory);
            else gaps.Add(new("private head dependency closure","One or more head-called dependencies have no established staged source","Resolve the named head source gaps before private generation"));
            phases.Add(new{Name="generate private head with existing reference editor",Milliseconds=phase.ElapsedMilliseconds,Detail=headStage});
            JsonElement? packageStage=null;if(headStage!=null){phase.Restart();packageStage=LeanGenerationStage.Package(output,request.Python,token,request.PluginWriter,request.StagesDirectory,chooseKeywords);phases.Add(new{Name="fresh body/private assets/ESPFE/ZIP readback",Milliseconds=phase.ElapsedMilliseconds,Detail=packageStage});
            }
            var deliveryWarnings=packageStage.HasValue?packageStage.Value.GetProperty("buildWarnings").Deserialize<BuildQualityWarning[]>(Wire.Options)??[]:Array.Empty<BuildQualityWarning>();
            deliveryWarnings=deliveryWarnings.Concat(qualityWarnings).Distinct().ToArray();
            long headRead=headStage is JsonElement hr?hr.GetProperty("bytesRead").GetInt64():0,generatedBytes=headStage is JsonElement hg?hg.GetProperty("generatedBytes").GetInt64():0;
            int stageFiles=headStage is JsonElement hf?hf.GetProperty("filesRead").GetInt32():0;
            long packageRead=packageStage?.GetProperty("bytesRead").GetInt64()??0;int packageFiles=packageStage?.GetProperty("filesRead").GetInt32()??0;generatedBytes+=packageStage?.GetProperty("generatedBytes").GetInt64()??0;
            Write("result.json",new{Status=packageStage.HasValue?(deliveryWarnings.Length>0?BestEffortBuild.WarningStatus:packageStage.Value.GetProperty("status").GetString()):"unsupported-required-input",BuildWarnings=deliveryWarnings,ValidationFailures=packageStage.HasValue&&packageStage.Value.TryGetProperty("validationFailures",out var typedFailures)?(object)typedFailures:Array.Empty<object>(),Compatibility=compatibility,OverlayNotice=overlayNotice,MaterialConflicts=materials.Conflicts,RuntimeAssignmentWarnings=buildWarnings,AssetRepairWarnings=repairWarnings,MissingReferences=missingReferences,EquipmentOmissions=equipmentOmissions,Outputs=outputPlan,Placement=resolvedPlacement,Distribution=packageStage?.GetProperty("distribution"),AssetChoices=choices,FollowerSettings=followerSettings,Packages=packageStage.HasValue?(object)packageStage.Value.GetProperty("packages"):Array.Empty<object>(),PrivateHead=headStage,Gaps=gaps,Context=context,Phases=phases,TotalMilliseconds=total.ElapsedMilliseconds,SourceFilesRead=metrics.FilesRead.Count,SourceBytesRead=metrics.BytesRead,GenerationFilesRead=stageFiles+packageFiles,GenerationBytesRead=headRead+packageRead,TotalInstrumentedBytesRead=metrics.BytesRead+headRead+packageRead,metrics.FilesCopied,metrics.BytesCopied,GeneratedBytes=generatedBytes,PeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64,MemoryScope="Runner peak; owned child peaks separately in stage-exit receipts, not simultaneous aggregate",ExternalToolIoInstrumented=false,SavedChangeFormsScanned=saved.ChangeFormsScanned,saved.PlayerPrefixBytes,saved.PlayerTailSkipped,RawCosaveCatalogProduced=false,MorphDiagnosticsDecoded=false,GameLaunched=false,OriginalInputsWritten=false,LegacyProtectionInvoked=false,RuntimeVerified=false,RuntimeAssignmentOmission=LeanSkinInputs.RuntimeAssignmentOmission});
            return packageStage.HasValue?0:3;
        }catch(Exception e){e.Data["LeanOutputOwned"]=true;var parserDetails=new List<string>();var diagnosticDetails=new List<string>();for(Exception? cause=e;cause!=null;cause=cause.InnerException){if(cause.Data["InputParseDetails"] is string detail)parserDetails.Add(detail);if(cause.Data["StageDiagnostics"] is string stageDetail)diagnosticDetails.Add(stageDetail);}Write("result.json",new{Status=e is OperationCanceledException?"cancelled":"failed",Error=e.GetType().Name+": "+e.Message,Exception=e.ToString(),ParserDetails=parserDetails,DiagnosticDetails=diagnosticDetails,Phases=phases,TotalMilliseconds=total.ElapsedMilliseconds,metrics.BytesRead,FilesRead=metrics.FilesRead.Count,metrics.BytesCopied,OutputPreserved=true,Retry="Use a new output path",GameLaunched=false,UserMo2Owned=false});if(e is OperationCanceledException)return 130;throw;}
    }
    internal static Dictionary<string,string> ParseIni(string text)
    {
        Dictionary<string,string> result=new(StringComparer.OrdinalIgnoreCase);string section="";foreach(string line in text.TrimStart('\uFEFF').Split('\n')){string s=line.Trim();if(s.StartsWith('[')&&s.EndsWith(']')){section=s[1..^1];continue;}int split=s.IndexOf('=');if(split<0)continue;string value=s[(split+1)..].Trim();if(value.StartsWith("@ByteArray(")&&value.EndsWith(')'))value=value[11..^1];result[section+"/"+s[..split].Trim()]=value.Replace("\\\\","\\");}return result;
    }
}
