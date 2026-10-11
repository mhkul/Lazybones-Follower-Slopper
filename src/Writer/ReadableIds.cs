using System.Globalization;
using System.Text;
using AutoFollower;

// Owned record labels only. Captured HDPT labels and originating CELL/WRLD
// labels retain their source identities because FaceGen and placement use them.
static class ReadableIds
{
 public static string Stem(string value,int maximum)
 {
  var result=new StringBuilder();bool word=true;
  foreach(var c in value.Normalize(NormalizationForm.FormD))
  {
   if(CharUnicodeInfo.GetUnicodeCategory(c)==UnicodeCategory.NonSpacingMark)continue;
   if(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
   {result.Append(word?char.ToUpperInvariant(c):c);word=false;}
   else if(c>127){result.Append("U"+((int)c).ToString("X4"));word=true;}
   else word=true;
  }
  if(result.Length==0)result.Append("Follower");
  return result.ToString()[..Math.Min(maximum,result.Length)];
 }
 public static string For(string plugin,string role,string? label=null,uint? local=null)
 {
  string identity=plugin.ToLowerInvariant();
  string suffix=BoundedBinary.Digest(Encoding.UTF8.GetBytes(identity))[..8];
  string value="LBFS_"+Stem(Path.GetFileNameWithoutExtension(identity),48)+"_"+Stem(role,18);
  if(!string.IsNullOrEmpty(label))value+="_"+Stem(label,30);
  if(local!=null)value+="_"+local.Value.ToString("X3");
  return value+"_"+suffix;
 }
}
