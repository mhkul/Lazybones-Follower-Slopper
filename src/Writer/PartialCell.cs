using System.Buffers.Binary;
using Mutagen.Bethesda.Plugins;
// Mutagen writes required CELL DATA even for partial containers. xEdit's partial
// representation keeps only EDID. Rebuild enclosing sizes; preserve all other bytes.
static class PartialCell {
 public static void Trim(string path,FormKey? expectedCell=null,FormKey? expectedWorld=null) {
  var data=File.ReadAllBytes(path);int count=0,worlds=0;
  var masters=new List<string>();int headEnd=24+BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));for(int q=24;q<headEnd;){int n=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(q+4));if(System.Text.Encoding.ASCII.GetString(data,q,4)=="MAST")masters.Add(System.Text.Encoding.UTF8.GetString(data,q+6,n).TrimEnd('\0'));q+=6+n;}
  FormKey Key(byte[] header){uint id=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));int slot=(int)(id>>24);if(slot>=masters.Count)throw new Exception("Location container has no originating master");return new(ModKey.FromFileName(masters[slot]),id&0xffffff);}
  expectedCell??=new FormKey(ModKey.FromFileName("Skyrim.esm"),0x16DF2);
  byte[] Range(int start,int end) {
   using var result=new MemoryStream();
   for(int p=start;p<end;) {
    if(p+24>end)throw new Exception("Truncated record/group");
    var header=data[p..(p+24)];var size=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
    var tag=System.Text.Encoding.ASCII.GetString(header,0,4);
    var next=p+(tag=="GRUP"?size:size+24);
    if(next>end||next<=p)throw new Exception("Invalid record/group bounds");
    byte[] body;
    if(tag=="GRUP")body=Range(p+24,next);
    else if(tag is "CELL" or "WRLD") {
     if(tag=="CELL"){if(Key(header)!=expectedCell||(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))&~0x400u)!=0x4000)throw new Exception("Unexpected partial CELL");count++;}
     else{if(expectedWorld==null||Key(header)!=expectedWorld||BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))!=0x4000)throw new Exception("Unexpected WRLD container");worlds++;}
     using var fields=new MemoryStream();
     for(int q=p+24;q<next;) {
      var n=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(q+4));
      if(q+6+n>next)throw new Exception("Invalid CELL subrecord");
      if(System.Text.Encoding.ASCII.GetString(data,q,4)=="EDID")fields.Write(data,q,6+n);
      q+=6+n;
     }
     body=fields.ToArray();
    } else body=data[(p+24)..next];
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4),body.Length+(tag=="GRUP"?24:0));
    result.Write(header);result.Write(body);p=next;
   }
   return result.ToArray();
  }
  var revised=Range(0,data.Length);if(count!=1||worlds!=(expectedWorld==null?0:1))throw new Exception("Exactly the intended minimal CELL/WRLD containers required");File.WriteAllBytes(path,revised);
 }
}
