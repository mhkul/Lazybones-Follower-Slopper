using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

if(args[0]=="validate-lean") {try {Validate.Run(args[1],args[2],args[3],args[4],args.ElementAtOrDefault(5));}catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}return;}

if(args[0]=="validate") {try {Validate.Run(args[1],args[2],args[3]);}catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}return;}
if(args[0]=="generate") {try {Generate.Run(args[1],args[2],args[3]);}catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}return;}
if(args[0]=="quests") {using var qm=SkyrimMod.CreateFromBinaryOverlay(args[1],SkyrimRelease.SkyrimSE);foreach(var qr in qm.Quests)Console.WriteLine(qr.FormKey+" "+qr.EditorID+" "+string.Join(',',qr.VirtualMachineAdapter?.Scripts.Select(s=>s.Name)??Array.Empty<string>()));return;}
if (args[0] == "reflect") {
    foreach(var name in args.Skip(1)) {
        var t=typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim."+name)!;
        if(t==null) { Console.WriteLine("Unknown "+name);continue; }
        Console.WriteLine(name);
        foreach(var p in t.GetProperties()) Console.WriteLine($"  {p.Name}: {p.PropertyType}");
        if(t.IsEnum) Console.WriteLine(string.Join(", ",Enum.GetNames(t)));
    }
    return;
}
var manifest=JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
if(args[0]=="small-inspect") {
    foreach(var ov in JsonDocument.Parse(File.ReadAllText(args[2])).RootElement.GetProperty("overlays").EnumerateArray()) {
        using var mo=SkyrimMod.CreateFromBinaryOverlay(ov.GetProperty("overlay").GetProperty("path").GetString()!,SkyrimRelease.SkyrimSE);
        foreach(var rr in mo.EnumerateMajorRecords()) {
            Console.WriteLine($"{rr.GetType().Name} {rr.FormKey} {rr.EditorID}");
            if(rr is IArmorGetter ar) Console.WriteLine($" template {ar.TemplateArmor} armature {string.Join(',',ar.Armature)} scripts {ar.VirtualMachineAdapter?.Scripts.Count}");
            if(rr is IArmorGetter arm && arm.VirtualMachineAdapter!=null) foreach(var sc in arm.VirtualMachineAdapter.Scripts) Console.WriteLine($" script {sc.Name} props "+string.Join(',',sc.Properties.Select(p=>p.Name+":"+(p is IScriptObjectPropertyGetter op?op.Object.FormKey.ToString():p.GetType().Name))));
            if(rr is IArmorAddonGetter aa) Console.WriteLine($" race {aa.Race} female {aa.WorldModel?.Female?.File} ftex {aa.SkinTexture?.Female} male {aa.WorldModel?.Male?.File}");
            if(rr is IRaceGetter ra) foreach(var p in typeof(IHeadDataGetter).GetProperties()) Console.WriteLine($"  head {p.Name}: {p.PropertyType}");
        }
    }
    return;
}
var slices=JsonDocument.Parse(File.ReadAllText(manifest.GetProperty("actor").GetProperty("canonicalRecordSource").GetProperty("path").GetString()!)).RootElement.GetProperty("slices");
foreach(var group in slices.EnumerateArray().GroupBy(s=>s.GetProperty("sourcePath").GetString()!)) {
    using var mod=SkyrimMod.CreateFromBinaryOverlay(group.Key,SkyrimRelease.SkyrimSE);
    foreach(var s in group) {
        var c=s.GetProperty("canonical");
        var key=new FormKey(ModKey.FromFileName(c.GetProperty("plugin").GetString()!),c.GetProperty("localId").GetUInt32());
        var rec=mod.EnumerateMajorRecords().First(r=>r.FormKey==key);
        Console.WriteLine($"{rec.GetType().Name} {key} {rec.EditorID} => "+string.Join(",", rec.EnumerateFormLinks().Select(l=>l.FormKey).Distinct()));
    }
}
