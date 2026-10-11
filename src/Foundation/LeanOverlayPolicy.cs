namespace AutoFollower;

public sealed record LeanOverlayOmission(string Node,string Scope,string? Registration,int Values);
public sealed record LeanOverlayNotice(string Warning,bool PlayerRegistration,LeanOverlayOmission[] OmittedOperations)
{
    public bool DetectionExhaustive=>false;
}

// D124: omit only RaceMenu's named overlay-node operations. Binary decoding and
// ordinary exported-head dependencies keep their existing validation paths.
public static class LeanOverlayPolicy
{
    public const string Warning="RaceMenu overlays are not fully supported yet and may be missing from this follower.";
    public static bool OverlayNode(string? name)
    {
        if(name==null)return false;
        foreach(string part in new[]{"Face","Hair","Body","Hands","Feet"})foreach(string kind in new[]{"Ovl","SOvl"}){
            string prefix=part+" ["+kind;
            if(!name.StartsWith(prefix,StringComparison.Ordinal)||!name.EndsWith(']'))continue;
            var index=name.AsSpan(prefix.Length,name.Length-prefix.Length-1);
            if(index.Length>0&&index.Length<=10&&index.ToString().All(c=>c is >= '0' and <= '9')&&uint.TryParse(index,out _))return true;
        }
        return false;
    }
    // ACTM's NOTM parent is the physical scene node; NOEN is an arbitrary
    // transform registration name. A name alone must not waive ordinary work.
    public static bool Deferred(LeanSkeeNode node)=>node.Scope=="material"?OverlayNode(node.Node):
        node.Scope=="transform"&&OverlayNode(node.Registration);
    public static LeanOverlayNotice Describe(LeanSkeeState state)=>new(Warning,state.PlayerOverlay,
        state.Nodes.Where(Deferred).Select(n=>new LeanOverlayOmission(n.Node,n.Scope,n.Registration,n.Values.Length)).ToArray());
}
