using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutoFollower;

public sealed record LeanAddon(string Canonical,bool RaceApplicable,string[] ThirdPerson,string[] FirstPerson,string ThirdPersonBasis="selected-sex",string FirstPersonBasis="selected-sex");
public sealed record LeanEquipment(string Canonical,int Count,LeanAddon[] Addons,string[] Ground,string GroundBasis,string Type="ARMO");
public sealed record LeanHeadPhysicsModel(string Canonical,string EditorId,string Reference);
public sealed record LeanInputPlan(string Schema,LeanActor SavedActor,LeanAppearance Appearance,
    LeanEquipment[] Equipment,object? Body,object[] Records,List<LeanSource> Sources,object InputPins,
    List<LeanGap> Gaps,bool PlacementOriginInterior,int IndexHeaders,int ActivePlugins,bool LegacyCaptureReceiptRequired=false,LeanSkin? Skin=null,LeanSkeeState? PersistedAppearance=null,LeanAppearanceBinding[]? AppearanceBindings=null,object[]? HostResources=null,LeanAssetChoice[]? AssetChoices=null,ResolvedFollowerSettings? FollowerSettings=null,LeanOutputPlan? Outputs=null,LeanPlacement? Placement=null,LeanOverlayNotice? OverlayNotice=null,LeanMaterialConflict[]? MaterialConflicts=null,string[]? RuntimeAssignmentWarnings=null,LeanHeadMorphExclusion[]? HeadMorphExclusions=null,LeanHeadPhysicsModel[]? HeadPhysicsModels=null,LeanHeadIdentityBinding[]? HeadIdentityBindings=null,string[]? PrivateRaceRecords=null,string[]? PrivateSoundRecords=null,LeanHeadClosurePart[]? RetainedHeadClosure=null,string[]? AssetRepairWarnings=null,LeanEquipmentOmission[]? EquipmentOmissions=null,LeanMissingReference[]? MissingReferences=null,BuildQualityWarning[]? BuildWarnings=null,LeanKeywordRemoval[]? KeywordRemovals=null,bool KeywordRecoveryInteractive=false);

// Reuses bounded editors and package stages with the consumed compact plan.
public static class LeanGenerationStage
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    public static JsonElement Package(string output,string python,CancellationToken token,string writer="",string? stages=null,Func<LeanKeywordCandidate[],LeanKeywordRemoval[]?>? chooseKeywords=null)
    {
        token.ThrowIfCancellationRequested();var tools=ApplicationTools.Resolve(python,writer,stages);tools.Require();writer=tools.Writer;string script=Path.Combine(tools.Stages,"lean_build_stage.py");
        SafePaths.NoLinks(script);SafePaths.NoLinks(writer);if(!File.Exists(writer))throw new InvalidDataException("Required compiled lean plugin writer missing: "+writer);
        var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=output,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("-B");start.ArgumentList.Add(script);start.ArgumentList.Add(Path.Combine(output,"input-plan.json"));start.ArgumentList.Add(writer);
        if(File.Exists(Path.Combine(output,"stop")))throw new OperationCanceledException(token);
        File.WriteAllText(SafePaths.Below(output,"package-stage-launch.json"),"{\"launchIntent\":true}");
        using var launch=RuntimeStorage.Launch(start); var child=launch.Process;
        if(!GetProcessTimes(child.SafeHandle,out long created,out _,out _,out _)){if(!child.HasExited)child.Kill(entireProcessTree:true);throw new IOException("Owned package child identity unavailable");}
        File.WriteAllText(Path.Combine(output,"package-stage-process.json"),JsonSerializer.Serialize(new{child.Id,CreatedFileTime=created,Executable=Path.GetFullPath(python),OwnedBy=Environment.ProcessId,UserMo2Owned=false,OwnedDescendantsOnly=true},Wire.Options));var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();bool cancelled=false;long peak=0;
        try{while(!child.WaitForExit(100)){try{child.Refresh();peak=Math.Max(peak,child.PeakWorkingSet64);}catch(InvalidOperationException)when(child.HasExited){}if(token.IsCancellationRequested||File.Exists(Path.Combine(output,"stop"))){cancelled=true;child.Kill(entireProcessTree:true);child.WaitForExit();break;}LeanKeywordRecovery.Respond(output,chooseKeywords,token);}}
        finally{if(!child.HasExited){child.Kill(entireProcessTree:true);child.WaitForExit();}}
        cancelled|=token.IsCancellationRequested||File.Exists(Path.Combine(output,"stop"));File.WriteAllText(Path.Combine(output,"package-stage.log"),stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult());File.WriteAllText(Path.Combine(output,"package-stage-exit.json"),JsonSerializer.Serialize(new{child.Id,child.ExitCode,Cancelled=cancelled,PeakWorkingSetBytes=peak,OwnedDescendantsOnly=true,UserMo2Owned=false},Wire.Options));if(cancelled)throw new OperationCanceledException(token);
        if(child.ExitCode!=0)
        {
            string detail=StageFailureDiagnostics.PackageError(output);
            var exception=new InvalidDataException("Required body/ESPFE/package stage failed: "+detail);
            StageFailureDiagnostics.Attach(exception,output,"package",child.ExitCode);
            throw exception;
        }
        using var result=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output,"package-stage-result.json")));if(!BestEffortBuild.DeliveredStatus(result.RootElement.GetProperty("status").GetString()))throw new InvalidDataException("Package stage has no structural success receipt");
        WorkingStorage.PackageReady(output,result.RootElement.GetProperty("distribution"));
        return result.RootElement.Clone();
    }
    public static object PrivateHead(string output,string python,CancellationToken token,string? stages=null)
    {
        token.ThrowIfCancellationRequested();if(File.Exists(Path.Combine(output,"stop")))throw new OperationCanceledException(token);
        var tools=ApplicationTools.Resolve(python,stages:stages);string script=Path.Combine(tools.Stages,"lean_head_stage.py");
        SafePaths.NoLinks(script);if(!File.Exists(script))throw new FileNotFoundException("Application head stage missing",script);
        SafePaths.NoLinks(script);SafePaths.NoLinks(python);
        var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=output,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("-B");start.ArgumentList.Add(script);start.ArgumentList.Add(Path.Combine(output,"input-plan.json"));
        token.ThrowIfCancellationRequested();
        File.WriteAllText(SafePaths.Below(output,"head-stage-launch.json"),"{\"launchIntent\":true}");
        using var launch=RuntimeStorage.Launch(start); var process=launch.Process;
        if(!GetProcessTimes(process.SafeHandle,out long created,out _,out _,out _)){if(!process.HasExited)process.Kill(entireProcessTree:false);throw new IOException("Owned child identity unavailable");}
        File.WriteAllText(Path.Combine(output,"head-stage-process.json"),JsonSerializer.Serialize(new{process.Id,Executable=Path.GetFullPath(python),CreatedFileTime=created,OwnedBy=Environment.ProcessId,UserMo2Owned=false},Wire.Options));
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        bool cancelled=false;long peak=0;
        try{
            while(!process.WaitForExit(100)){
                // Memory is optional measurement. A short stage can exit
                // between WaitForExit and the getter; that is not a build error.
                try{process.Refresh();peak=Math.Max(peak,process.PeakWorkingSet64);}catch(InvalidOperationException)when(process.HasExited){}
                if(token.IsCancellationRequested||File.Exists(Path.Combine(output,"stop"))){cancelled=true;process.Kill(entireProcessTree:false);process.WaitForExit();break;}
            }
        }finally{if(!process.HasExited){process.Kill(entireProcessTree:false);process.WaitForExit();}}
        cancelled|=token.IsCancellationRequested||File.Exists(Path.Combine(output,"stop"));
        File.WriteAllText(Path.Combine(output,"head-stage.log"),stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(output,"head-stage-exit.json"),JsonSerializer.Serialize(new{process.Id,process.ExitCode,Cancelled=cancelled,PeakWorkingSetBytes=peak,UserMo2Owned=false},Wire.Options));
        if(cancelled)throw new OperationCanceledException(token);
        if(process.ExitCode!=0)
        {
            var failure=new InvalidDataException("Private head stage failed; see owned head-stage.log");
            StageFailureDiagnostics.Attach(failure,output,"private-head",process.ExitCode);
            throw failure;
        }
        using var result=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output,"private-head-result.json")));
        return result.RootElement.Clone();
    }
}
