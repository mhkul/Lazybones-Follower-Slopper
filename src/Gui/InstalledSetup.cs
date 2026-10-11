using AutoFollower;
using Microsoft.Win32;

namespace AutoFollower.Gui;

internal sealed record InstalledSetup(string Game, ActiveMo2? Mo2, PhysicalConfiguration? Physical, SelectedEnvironment? Snapshot)
{
    internal static InstalledSetup Virtual(ActiveMo2 mo2) => new(mo2.Game,mo2,null,null);
    internal static InstalledSetup Direct(string game, PhysicalConfiguration? config = null)
    {
        PhysicalEnvironment.RequireDirect(); config ??= PhysicalEnvironment.Default;
        using var sources = new LeanMo2Sources(Path.Combine(game,"Data"),Path.Combine(Path.GetTempPath(),"unused-"+Guid.NewGuid()),new(),CancellationToken.None);
        var environment = PhysicalEnvironment.Read(game,config,sources);
        return new(Path.GetFullPath(game),null,config,environment);
    }
    internal static InstalledSetup? Discover(out string status)
    {
        if(LeanMo2Sources.HasContext)
        {
            var mo2=ActiveMo2.TryRead(out status);
            return mo2==null?null:Virtual(mo2);
        }
        try
        {
            using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition");
            string? game=key?.GetValue("Installed Path") as string;
            if(string.IsNullOrWhiteSpace(game)) { status="Choose your Skyrim folder for physical/deployed Data.";return null; }
            var setup=Direct(game);status="Physical Data · "+setup.Game;return setup;
        }
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException){status="Physical setup: "+e.Message;return null;}
    }
    internal void Revalidate()
    {
        if(Mo2!=null){Mo2.Revalidate();return;}
        PhysicalEnvironment.RequireDirect();
        using var sources=new LeanMo2Sources(Path.Combine(Game,"Data"),Path.Combine(Path.GetTempPath(),"unused-"+Guid.NewGuid()),new(),CancellationToken.None);
        Snapshot!.RequireCurrent(sources);
    }
    internal LeanBuildRequest Request(FollowerInputPreview preview, ApplicationTools tools, FollowerSettings settings, LeanOutputSelection outputs, bool available, string root) => new(
        preview.Selection.Save,preview.Selection.Cosave,preview.Selection.HeadNif,preview.Selection.TintDds,preview.Selection.Jslot,preview.Selection.ExportAssociation,preview.Selection.TintReference,
        Mo2?.InstanceIni??"",Mo2?.Profile??"",Game,Mo2?.Executable??"",Python:tools.Python,PluginWriter:tools.Writer,Settings:settings,Outputs:outputs,ExpectedObodyPresent:available,StagesDirectory:tools.Stages,OutputRoot:root,Store:preview.Selection.Store,InputRuntime:preview.Selection.InputRuntime,Physical:Physical);
}
