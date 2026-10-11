namespace AutoFollower;

public sealed class FollowerInputException(string input,string path,Exception cause,bool presetReplacement=false)
 : IOException(input+" '"+Path.GetFileName(path)+"': "+cause.Message,cause)
{
 public bool PresetReplacement {get;}=presetReplacement;
}

public static class FollowerInputErrors
{
 public static string LandingMessage(Exception error,string preset)
 {
  if(error is AggregateException aggregate)error=aggregate.GetBaseException();
  return error.Message+(error is FollowerInputException {PresetReplacement:true}&&!string.IsNullOrWhiteSpace(preset)
   ? "\r\nUse Change RaceMenu preset to select the matching preset for this save and head." : "");
 }
}
