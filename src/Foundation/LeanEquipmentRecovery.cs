namespace AutoFollower;

public sealed record LeanEquipmentOmission(string Canonical,string Name,string[] FailedReferences,string Warning);
public sealed record LeanEquipmentRecoveryChoice(LeanEquipmentOmission[] Items,string[] FailedReferences,string? Explanation=null);

// Offer exclusion only when every missing asset is exclusively reachable from
// optional equipment. Core appearance/skin/shared paths remain hard errors.
public static class LeanEquipmentRecovery
{
    public static LeanEquipmentRecoveryChoice? PrebuiltProjects(string[] targets,List<LeanEquipment> equipment,List<LeanSource> sources,IEnumerable<string> coreRoots,Func<string,string> name)
    {
        var gaps=targets.Select(t=>new LeanGap(t,"Required loose/archive member missing in selected MO2","Compatible prebuilt BodySlide project unavailable for this affected model")).ToList();
        var choice=Offer(gaps,equipment,sources,coreRoots,name);
        if(choice==null)return null;
        return choice with {Items=choice.Items.Select(i=>i with {Warning="Omitted equipment "+i.Name+" ("+i.Canonical+"): compatible prebuilt projects unavailable for "+string.Join(';',i.FailedReferences)+". Explicit choice applies to every selected output."}).ToArray(),Explanation="Compatible prebuilt BodySlide projects are unavailable for these outfit models. Build without the listed items in every selected output, or cancel and choose OBody-only to keep them using their captured installed meshes. You can also restore compatible projects and retry Prebuilt. Output choices and preset values are preserved."};
    }
    public static HashSet<string> Closure(IEnumerable<string> roots,IEnumerable<LeanSource> sources)
    {
        var edges=sources.ToDictionary(s=>LeanPackagingChoices.Ref(s.Reference),s=>s.RequiredReferences??[],StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen=new(StringComparer.OrdinalIgnoreCase);Queue<string> pending=new(roots);
        while(pending.Count>0){string reference=LeanPackagingChoices.Ref(pending.Dequeue());if(!seen.Add(reference))continue;if(edges.TryGetValue(reference,out var calls))foreach(string child in calls)pending.Enqueue(child);}
        return seen;
    }
    public static string[] Roots(LeanEquipment e)=>e.Ground.Concat(e.Addons.SelectMany(a=>a.ThirdPerson.Concat(a.FirstPerson))).SelectMany(p=>{
        string r=LeanModelInputs.Reference(p);return r.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)||r.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase)?new[]{r,r[..^6]+(r.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)?"_0.nif":"_1.nif")}:new[]{r};
    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static LeanEquipmentRecoveryChoice? Offer(List<LeanGap> gaps,List<LeanEquipment> equipment,List<LeanSource> sources,IEnumerable<string> coreRoots,Func<string,string> name)
    {
        if(gaps.Count==0||gaps.Any(g=>g.Evidence!="Required loose/archive member missing in selected MO2"&&!g.Evidence.StartsWith("Unsupported embedded creator texture reference: ",StringComparison.Ordinal)))return null;
        var core=Closure(coreRoots,sources);var missing=gaps.Select(g=>LeanPackagingChoices.Ref(g.Input)).ToArray();
        if(missing.Any(core.Contains))return null;
        var affected=equipment.Select(e=>(Item:e,Calls:Closure(Roots(e),sources))).Where(x=>missing.Any(x.Calls.Contains)).ToArray();
        if(affected.Length==0||missing.Any(m=>!affected.Any(x=>x.Calls.Contains(m))))return null;
        return new(affected.Select(x=>{string label=name(x.Item.Canonical);var failed=missing.Where(x.Calls.Contains).ToArray();string causes=string.Join("; ",gaps.Where(g=>failed.Contains(LeanPackagingChoices.Ref(g.Input),StringComparer.OrdinalIgnoreCase)).Select(g=>g.Input+": "+g.Evidence+". "+g.NextAction));return new LeanEquipmentOmission(x.Item.Canonical,label,failed,"Omitted equipment "+label+" ("+x.Item.Canonical+"): "+causes+". Its outfit/inventory links and unneeded assets are excluded.");}).ToArray(),missing);
    }
}
