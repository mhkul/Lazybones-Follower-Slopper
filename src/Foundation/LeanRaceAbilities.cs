namespace AutoFollower;

// Private races copy their source-local abilities, not unrelated host gameplay.
// Every supported link is resolved through the selected winning record index.
public static class LeanRaceAbilities
{
 public static string[] Resolve(LeanRecord race,Func<string,LeanRecord> get,bool existing)
 {
  if(existing)return [];
  string owner=race.Canonical.Split('|')[0];
  if(owner.Equals("Skyrim.esm",StringComparison.OrdinalIgnoreCase))return [];
  HashSet<string> required=new(StringComparer.OrdinalIgnoreCase);
  void Visit(string key){
   if(!key.StartsWith(owner+"|",StringComparison.OrdinalIgnoreCase)||!required.Add(key))return;
   var record=get(key);
   if(record.Type is not ("SPEL" or "MGEF"))throw new InvalidDataException("Private race ability requires unsupported source-local record: "+key+" / "+record.Type);
   if(record.Fields.ContainsKey("VMAD")||record.Fields.ContainsKey("CTDA"))throw new InvalidDataException("Private race ability requires script/condition dependency decoding: "+key+"; no partial copy");
   foreach(string tag in new[]{"MDOB","KWDA",record.Type=="SPEL"?"ETYP":"ESCE"})foreach(string child in record.Refs(tag))Visit(child);
   if(record.Type=="SPEL"){
    foreach(string child in record.Refs("EFID"))Visit(child);
    foreach(byte[] data in record.Fields.GetValueOrDefault("SPIT",[])){if(data.Length!=36)throw new InvalidDataException("Private ability SPIT framing unsupported: "+key);var child=record.Ref(BitConverter.ToUInt32(data,32));if(child!=null)Visit(child);}
   }else{
    foreach(byte[] data in record.Fields.GetValueOrDefault("DATA",[])){if(data.Length!=152)throw new InvalidDataException("Private ability MGEF DATA framing unsupported: "+key);foreach(int offset in new[]{8,24,32,36,72,76,92,96,100,108,116,120,124,128,132,136}){var child=record.Ref(BitConverter.ToUInt32(data,offset));if(child!=null)Visit(child);}}
    foreach(byte[] data in record.Fields.GetValueOrDefault("SNDD",[])){if(data.Length%8!=0)throw new InvalidDataException("Private ability sound framing unsupported: "+key);for(int offset=4;offset<data.Length;offset+=8){var child=record.Ref(BitConverter.ToUInt32(data,offset));if(child!=null)Visit(child);}}
   }
  }
  foreach(string key in race.Refs("SPLO"))Visit(key);
  return required.Order(StringComparer.OrdinalIgnoreCase).ToArray();
 }
}
