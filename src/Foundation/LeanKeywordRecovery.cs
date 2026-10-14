namespace AutoFollower;

public sealed record LeanKeywordRemoval(string Consumer,string Keyword,string SourceIdentity);
public sealed record LeanKeywordCandidate(string Consumer,string GeneratedRecord,string ConsumerName,string Keyword,
    string? EditorId,string Provider,string Status,string SourceIdentity)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public LeanKeywordRemoval Removal=>new(Consumer,Keyword,SourceIdentity);
}
public static class LeanKeywordRecovery
{
    public static void Respond(string output,Func<LeanKeywordCandidate[],LeanKeywordRemoval[]?>? choose,CancellationToken token)
    {
        if(choose==null)return;
        string request=SafePaths.Below(output,"keyword-recovery-choice.json"),response=SafePaths.Below(output,"keyword-recovery-response.json");
        if(!File.Exists(request)||File.Exists(response))return;
        token.ThrowIfCancellationRequested();
        var candidates=System.Text.Json.JsonSerializer.Deserialize<LeanKeywordCandidate[]>(WorkingStorage.ReadBytes(request,checked((int)WorkingStorage.FileLimit)),Wire.Options)??throw new InvalidDataException("Keyword recovery candidates missing.");
        var selected=choose(candidates);token.ThrowIfCancellationRequested();
        if(selected==null)throw new OperationCanceledException(token);
        var removals=Select(candidates,selected);
        string temporary=SafePaths.Below(output,"keyword-recovery-response.tmp");
        using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))System.Text.Json.JsonSerializer.Serialize(file,removals,Wire.Options);
        File.Move(temporary,response);
    }
    public static LeanKeywordRemoval[] Select(LeanKeywordCandidate[] candidates,LeanKeywordRemoval[]? selected)
    {
        var result=(selected??[]).Distinct().ToArray();
        if(result.Any(r=>!candidates.Any(c=>c.Removal==r)))throw new InvalidDataException("Keyword recovery selection changed; review current generated assignments.");
        return result;
    }
}
