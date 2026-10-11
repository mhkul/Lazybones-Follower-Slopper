namespace AutoFollower;

public sealed record LeanMissingReference(string Reference,string Status,string[] Consumers,string[] Equipment,string Warning);
public static class LeanMissingReferences
{
    public static LeanMissingReference[] Admit(List<LeanGap> gaps,List<LeanSource> sources,List<LeanEquipment> equipment,IEnumerable<string> indispensable, IEnumerable<(string Reference,string Consumer)> calls,Func<string,string>? name=null,Func<string,string>? requiredReason=null)
    {
        var required=indispensable.Select(LeanPackagingChoices.Ref).ToHashSet(StringComparer.OrdinalIgnoreCase);List<LeanMissingReference> admitted=[];
        foreach(var gap in gaps.ToArray()){
            if(gap.Evidence!="Required loose/archive member missing in selected MO2")continue;
            string reference=LeanPackagingChoices.Ref(gap.Input);if(required.Contains(reference)){
                gaps[gaps.IndexOf(gap)]=gap with{NextAction=requiredReason?.Invoke(reference)??"Generation requires readable bytes for this exact input; retaining only the reference cannot construct the requested output. Restore this input or exclude affected optional equipment."};continue;
            }
            VirtualPath.Normalize(reference);
            var consumers=sources.Where(s=>(s.RequiredReferences??[]).Contains(reference,StringComparer.OrdinalIgnoreCase)).Select(s=>"NIF dependency of "+s.Reference).Concat(calls.Where(c=>LeanPackagingChoices.Ref(c.Reference)==reference).Select(c=>c.Consumer)).Distinct().ToArray();
            if(consumers.Length==0)continue;
            var affected=equipment.Where(e=>LeanEquipmentRecovery.Closure(LeanEquipmentRecovery.Roots(e),sources).Contains(reference)||consumers.Any(c=>c.StartsWith(e.Canonical,StringComparison.OrdinalIgnoreCase))).Select(e=>e.Canonical).ToArray();
            admitted.Add(new(reference,"Unavailable in selected loose/active-archive view; original safe reference preserved",consumers,affected,"Missing asset "+reference+". Affected: "+(affected.Length>0?string.Join("; ",affected.Select(e=>(name?.Invoke(e)??e)+" ("+e+")")):string.Join("; ",consumers))+"; kept the original reference and all available inputs. No replacement/provider is packaged. Appearance, audio or behavior may differ. Consumers: "+string.Join("; ",consumers)));
            gaps.Remove(gap);
        }
        return admitted.ToArray();
    }
}
