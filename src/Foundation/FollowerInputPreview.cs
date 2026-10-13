namespace AutoFollower;

public sealed record FollowerInputSelection(string Save,string Cosave,string HeadNif,string TintDds,string Jslot,string ExportAssociation,string TintReference,string InputRuntime="",string Store="unspecified")
{
    public static FollowerInputSelection FromRequest(LeanBuildRequest request)=>new(request.Save,request.Cosave,request.HeadNif,request.TintDds,request.Jslot,request.ExportAssociation,request.TintReference,request.InputRuntime,request.Store);
    [System.Text.Json.Serialization.JsonIgnore]public string[] Paths=>[Save,Cosave,HeadNif,TintDds,Jslot];
}
public sealed record FollowerInputPin(string Path,string Sha256,int Bytes);
public sealed record FollowerInputPreview(FollowerInputSelection Selection,FollowerSavePreview SavePreview,LeanCosave Cosave,LeanAppearance Appearance,FollowerInputPin[] Pins,LeanMaterialConflict[]? MaterialConflicts=null)
{
    public LeanOverlayNotice OverlayNotice=>LeanOverlayPolicy.Describe(Cosave.Skee);
    public string[] RuntimeAssignmentWarnings=>LeanRuntimeAssignmentPolicy.Warnings(Cosave.Skee);
}

// Landing-screen Continue: reads only the five chosen files, creates no output,
// and uses the existing input decoders. No environment/asset discovery here.
public static class FollowerInputPreparation
{
    public static FollowerInputPreview Prepare(FollowerInputSelection selection,CancellationToken token,Func<LeanExportInputs.Nif,string>? deriveTintReference=null)
    {
        var formats=SkyrimRuntimeProfile.ForInputs(selection.Store,selection.InputRuntime).Formats;
        if(string.IsNullOrWhiteSpace(selection.ExportAssociation))throw new InvalidDataException("Required export association missing");
        string[] paths=selection.Paths,extensions=[".ess",".skse",".nif",".dds",".jslot"];
        if(paths.Any(string.IsNullOrWhiteSpace))throw new InvalidDataException("Select a save/co-save, head NIF, composed tint DDS and JSlot before Continue");
        if(!Path.GetFileNameWithoutExtension(selection.Save).Equals(Path.GetFileNameWithoutExtension(selection.Cosave),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Save/co-save stems differ; select the matching co-save");
        List<byte[]> bytes=[];List<FollowerInputPin> pins=[];
        for(int i=0;i<paths.Length;i++){
            token.ThrowIfCancellationRequested();string path=Path.GetFullPath(paths[i]);SafePaths.NoLinks(path);
            if(!Path.GetExtension(path).Equals(extensions[i],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Selected input must use "+extensions[i]+": "+path);
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(input.Length>BoundedBinary.Limit)throw new InvalidDataException("Selected input exceeds supported byte bound: "+path);
            byte[] raw=new byte[checked((int)input.Length)];input.ReadExactly(raw);bytes.Add(raw);pins.Add(new(path,BoundedBinary.Digest(raw),raw.Length));
        }
        LeanSavedState saved;LeanCosave cosave;
        try{saved=LeanSaveReader.Read(bytes[0],pins[0].Path,token,formats);}
        catch(InvalidDataException e){throw new FollowerInputException("Save",selection.Save,e);}
        try{cosave=LeanExportInputs.Cosave(bytes[1],saved.Plugins,saved.Actor.Sex,formats);}
        catch(InvalidDataException e){throw new FollowerInputException("Co-save",selection.Cosave,e);}
        var head=LeanExportInputs.ReadNif(bytes[2]);
        string tintReference=deriveTintReference?.Invoke(head)??selection.TintReference;
        if(string.IsNullOrWhiteSpace(tintReference))throw new InvalidDataException("Selected head has no supported derived tint reference");
        selection=selection with{TintReference=tintReference};
        LeanAppearance appearance;
        try{appearance=LeanExportInputs.Bundle(head,bytes[3],bytes[4],saved,selection.TintReference,selection.ExportAssociation,bestEffort:true);}
        catch(InvalidDataException e)when(e.Message.StartsWith("JSlot",StringComparison.Ordinal)){throw new FollowerInputException("RaceMenu preset",selection.Jslot,e,true);}
        catch(Exception e)when(e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException){throw new InvalidDataException("Selected RaceMenu export bundle is malformed or missing required fields; replace the head NIF, tint DDS or JSlot with the matching export files",e);}
        LeanAppearanceAssessment materials;
        try{materials=LeanAppearanceAssociations.Assess(head,cosave.Skee,bestEffort:true);}
        catch(InvalidDataException e){throw new FollowerInputException("Co-save appearance",selection.Cosave,e);}
        token.ThrowIfCancellationRequested();
        var savePreview=new FollowerSavePreview(pins[0].Path,pins[0].Sha256,saved,FollowerSettingsPolicy.Context(saved.Actor),formats);
        return new(selection,savePreview,cosave,appearance,pins.ToArray(),materials.Conflicts);
    }
    public static void RequireCurrent(FollowerInputPreview preview,FollowerInputSelection selection,FollowerInputPin[] current)
    {
        if(preview.Selection.ExportAssociation!=selection.ExportAssociation||preview.Selection.TintReference!=selection.TintReference||preview.Selection.InputRuntime!=selection.InputRuntime||preview.Selection.Store!=selection.Store)throw new InvalidDataException("Export selection or input profile changed after Continue; return to the landing screen and refresh inputs");
        if(current.Length!=5||preview.Pins.Length!=5||selection.Paths.Length!=5)throw new InvalidDataException("Incomplete input preview");
        for(int i=0;i<5;i++)if(!Path.GetFullPath(selection.Paths[i]).Equals(preview.Pins[i].Path,StringComparison.OrdinalIgnoreCase)||!current[i].Path.Equals(preview.Pins[i].Path,StringComparison.OrdinalIgnoreCase)||current[i].Sha256!=preview.Pins[i].Sha256||current[i].Bytes!=preview.Pins[i].Bytes)
            throw new InvalidDataException("Selected input changed after Continue: "+selection.Paths[i]+"; return to the landing screen and refresh inputs");
    }
}
