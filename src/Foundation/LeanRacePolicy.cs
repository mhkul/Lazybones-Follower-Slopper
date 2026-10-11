namespace AutoFollower;

public static class LeanRacePolicy
{
    public const string Private="private",Existing="existing";
    public static string Require(string mode)=>mode is Private or Existing?mode:throw new InvalidDataException("Choose Generate new custom race or Use existing race.");
    public static bool IsExisting(ResolvedFollowerSettings settings)=>Require(settings.Values.RaceMode)==Existing;
}
