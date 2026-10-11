// The binary serializer normalizes signed zero. Restore only numerically equal
// DATA values so the selected save's six float bit patterns survive serialization.
static class SavedTransform
{
 public static void Preserve(string path,float[] values)
 {
  if(values.Length!=6)throw new InvalidDataException("Six saved transform values required");
  byte[] bytes=File.ReadAllBytes(path);int count=0;
  void Range(int start,int end){for(int at=start;at<end;){
   if(at+24>end)throw new InvalidDataException("Saved transform record header truncated");
   string tag=System.Text.Encoding.ASCII.GetString(bytes,at,4);int size=BitConverter.ToInt32(bytes,at+4),next=checked(at+size+(tag=="GRUP"?0:24));
   if(size<0||next>end||next<=at)throw new InvalidDataException("Saved transform record bounds invalid");
   if(tag=="GRUP")Range(at+24,next);
   else if(tag=="ACHR"){
    if((BitConverter.ToUInt32(bytes,at+12)&0xffffff)!=0x801||(BitConverter.ToUInt32(bytes,at+8)&0x40000)!=0)throw new InvalidDataException("Unexpected placed actor framing");
    int fields=0;for(int p=at+24;p<next;){if(p+6>next)throw new InvalidDataException("Placed actor field truncated");int n=BitConverter.ToUInt16(bytes,p+4);if(p+6+n>next)throw new InvalidDataException("Placed actor field bounds invalid");
     if(System.Text.Encoding.ASCII.GetString(bytes,p,4)=="DATA"){if(n!=24)throw new InvalidDataException("Placed actor DATA must contain six floats");for(int i=0;i<6;i++){int offset=p+6+i*4;if(BitConverter.ToSingle(bytes,offset)!=values[i])throw new InvalidDataException("Generated placement differs from saved numeric transform");BitConverter.GetBytes(values[i]).CopyTo(bytes,offset);}fields++;}p+=6+n;
    }if(fields!=1)throw new InvalidDataException("Unique placed actor DATA required");count++;
   }at=next;
  }}
  Range(0,bytes.Length);if(count!=1)throw new InvalidDataException("Unique saved placed actor required");File.WriteAllBytes(path,bytes);
 }
}
