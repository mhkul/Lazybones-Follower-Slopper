using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutoFollower;

public sealed class LeanReadMetrics { public long BytesRead {get;set;} public long BytesCopied {get;set;} public int FilesCopied {get;set;} public HashSet<string> FilesRead {get;}=new(StringComparer.OrdinalIgnoreCase); }
public sealed record LeanSource(string Reference,string Backing,string Sha256,long Bytes,string Consumer,string Kind,
    string? ArchiveMember=null,long? ArchiveOffset=null,int? ArchiveStoredBytes=null,string? Activation=null,string? WinnerBasis=null,bool RuntimeAsset=true,string[]? RequiredReferences=null,LeanExportInputs.TextureRepair[]? TextureRepairs=null);
public sealed record LeanLookupPolicy(bool Supported,string Basis,string Mo2Version,string GameVersion,
    string EffectiveInvalidation,string? ConfiguredInvalidation,string IniQueryPath)
{
    // The installed 1.5.97 Setting at RVA 0x1def4a0 has bool data=1.
    // MO2 2.5.0 maps profile INIs; USVFS does not merge initweaks.ini.
    // A missing key preserves the engine default. See reports/T09F.md.
    public static LeanLookupPolicy Evaluate(string mo2,string game,string? configured,string query)=>
        new(configured==null||configured=="1",
            "Selected installed Data view: loose-over-archive and later active-plugin archive priority; effective invalidation must be enabled or omitted (engine default=1 assumption). This policy was verified on Skyrim 1.5.97/MO2 2.5.0; other versions remain unverified and are permitted for compatibility feedback. Explicit disabled/unknown overrides reject.",
            mo2,game,configured??"1",configured,query);
}
public sealed class LeanSourceAdmissionException(string message):IOException(message);
public sealed class LeanMo2Sources(string data,string output,LeanReadMetrics metrics,CancellationToken token) : IDisposable
{
    readonly SelectedDataView fileView=new(Path.GetFullPath(data),"selected-process-file-view","Required final backing handles; production admission requires selected MO2 context and validated lookup policy");
    bool physical;
    public void Bind(SelectedEnvironment environment)
    {physical=environment.Manager=="Physical";if(environment.Manager is not ("MO2" or "Physical")||!environment.FileView.Directory.Equals(fileView.Directory,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Selected Data view differs from the admitted MO2 source context.");}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint size,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern uint GetModuleFileName(IntPtr module,StringBuilder path,int size);
    public static bool HasContext=>GetModuleHandle("usvfs_x64.dll")!=IntPtr.Zero;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern uint GetPrivateProfileString(string section,string key,string missing,StringBuilder value,int size,string file);
    public static string? EffectiveInvalidation(string ini){var value=new StringBuilder(128);GetPrivateProfileString("Archive","bInvalidateOlderFiles","__missing__",value,value.Capacity,ini);return value.ToString()=="__missing__"?null:value.ToString();}
    public static object RequireContext(string expectedMo2)
    {
        IntPtr module=GetModuleHandle("usvfs_x64.dll");if(module==IntPtr.Zero)throw new InvalidDataException("Launch lean-build through the selected user's MO2");
        var path=new StringBuilder(32768);if(GetModuleFileName(module,path,path.Capacity)==0)throw new IOException("USVFS module path unavailable");
        string expected=Path.Combine(Path.GetDirectoryName(expectedMo2)!,"usvfs_x64.dll");if(!Path.GetFullPath(path.ToString()).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unexpected USVFS module; use unmodified selected MO2");
        var server=Process.GetProcessesByName("ModOrganizer").Where(p=>{try{return p.MainModule?.FileName?.Equals(expectedMo2,StringComparison.OrdinalIgnoreCase)==true;}catch{return false;}}).Select(p=>new {p.Id,Creation=p.StartTime.ToUniversalTime(),Path=p.MainModule!.FileName}).ToArray();
        if(server.Length!=1)throw new InvalidDataException("Selected user MO2 process identity ambiguous");
        return new {Module=path.ToString(),Server=server[0],Mo2Version=FileVersionInfo.GetVersionInfo(expectedMo2).FileVersion,UserMo2Owned=false,Authority="Normal MO2 launch module and current selected-profile metadata; each required source uses final read-only backing handle"};
    }
    public FileStream Open(string path)
    {
        token.ThrowIfCancellationRequested();if(File.Exists(Path.Combine(output,"stop")))throw new OperationCanceledException(token);var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try{string final=Backing(f);SafePaths.NoLinks(final);if(final.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source aliases owned output");metrics.FilesRead.Add(final);return f;}catch{f.Dispose();throw;}
    }
    public static string Backing(FileStream f){var path=new StringBuilder(32768);uint size=GetFinalPathNameByHandle(f.SafeFileHandle,path,(uint)path.Capacity,0);if(size==0||size>=path.Capacity)throw new IOException("Required source has no final backing path");string result=path.ToString();return result.StartsWith("\\\\?\\UNC\\")?"\\\\"+result[8..]:result.StartsWith("\\\\?\\")?result[4..]:result;}
    public byte[] At(FileStream f,long offset,int size){token.ThrowIfCancellationRequested();if(File.Exists(Path.Combine(output,"stop")))throw new OperationCanceledException(token);if(size<0||size>BoundedBinary.Limit||offset<0||offset>f.Length-size)throw new InvalidDataException("Required source range exceeds file");f.Position=offset;byte[] bytes=new byte[size];f.ReadExactly(bytes);metrics.BytesRead+=size;return bytes;}
    public byte[] Read(string path){using var f=Open(path);if(f.Length>BoundedBinary.Limit)throw new InvalidDataException("Required input exceeds bounded size");return At(f,0,(int)f.Length);}
    public string Virtual(string reference)=>fileView.Resolve(reference);
    public (byte[] Bytes,string Backing) ReadLooseCandidate(string reference){if(lookupPolicy?.Supported!=true)throw new LeanSourceAdmissionException("Selected loose metadata lookup unsupported");using var f=Open(Virtual(reference));return(At(f,0,checked((int)f.Length)),Backing(f));}
    public List<LeanSource> Sources {get;}=[];
    public void VerifyRetained(LeanSource source)
    {
        byte[] raw;
        if(source.ArchiveMember==null){
            using var file=Open(Virtual(source.Reference));
            if(!Backing(file).Equals(source.Backing,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Retained provider winner changed: "+source.Reference+"; refresh the dependency preview");
            raw=At(file,0,checked((int)file.Length));
        }else{
            if(File.Exists(Virtual(source.Reference)))throw new InvalidDataException("Retained archive winner replaced by loose source: "+source.Reference+"; refresh the dependency preview");
            using var file=Open(source.Backing);
            if(!Backing(file).Equals(source.Backing,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Retained archive backing changed: "+source.Reference);
            using var archive=new BsaReader(source.Backing,n=>metrics.BytesRead+=n);raw=archive.Extract(source.ArchiveMember);
        }
        if(raw.LongLength!=source.Bytes||BoundedBinary.Digest(raw)!=source.Sha256)throw new InvalidDataException("Retained source identity changed: "+source.Reference+"; refresh the dependency preview");
    }
    readonly Dictionary<string,LeanSource> staged=new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Backing,string Activation,bool Proven,int PluginOrder,int IniOrder,BsaReader Reader)> archives=[];
    string[] archivePlugins=[],iniArchiveOrder=[];HashSet<string> iniArchives=new(StringComparer.OrdinalIgnoreCase),profileArchives=new(StringComparer.OrdinalIgnoreCase);bool archiveIndexed;
    LeanLookupPolicy? lookupPolicy;
    public void ConfigureArchives(string[] plugins,IEnumerable<string> activated,IEnumerable<string> selected,LeanLookupPolicy? policy=null){archivePlugins=plugins;iniArchiveOrder=activated.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();iniArchives.UnionWith(iniArchiveOrder);profileArchives.UnionWith(selected);lookupPolicy=policy;}
    void IndexArchives()
    {
        if(archiveIndexed)return;archiveIndexed=true;
        foreach(string virtualArchive in Directory.EnumerateFiles(data,"*.bsa",SearchOption.TopDirectoryOnly)){
            token.ThrowIfCancellationRequested();string name=Path.GetFileName(virtualArchive);string[] bindings=archivePlugins.Where(x=>name.Equals(Path.GetFileNameWithoutExtension(x)+".bsa",StringComparison.OrdinalIgnoreCase)||name.Equals(Path.GetFileNameWithoutExtension(x)+" - Textures.bsa",StringComparison.OrdinalIgnoreCase)).ToArray();string? plugin=bindings.Length==1?bindings[0]:null;
            bool known=iniArchives.Contains(name)||plugin!=null;string activation=iniArchives.Contains(name)?"effective profile INI archive list":plugin!=null?"active plugin "+plugin:"unproved archive activation";
            if(!known&&bindings.Length==0&&!profileArchives.Contains(name)&&!archivePlugins.Any(x=>name.StartsWith(Path.GetFileNameWithoutExtension(x)+" - ",StringComparison.OrdinalIgnoreCase)))continue;
            int archiveOrder=plugin!=null&&!iniArchives.Contains(name)?Array.IndexOf(archivePlugins,plugin):-1;
            // MO2 2.5.0 DirectoryEntry::addFromAllBSAs associates "Skyrim - "
            // with Skyrim.esm. Admit only the vanilla names documented by its
            // release-era SkyrimSEDataArchives and present in the selected INI.
            if(iniArchives.Contains(name)&&VanillaArchive(name)){archiveOrder=Array.IndexOf(archivePlugins,"Skyrim.esm");activation+="; vanilla Skyrim.esm archive association @"+archiveOrder;}
            int iniOrder=Array.FindIndex(iniArchiveOrder,x=>x.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(iniOrder>=0)activation+="; selected INI ordinal @"+iniOrder;
            using var f=Open(virtualArchive);string backing=Backing(f);var reader=new BsaReader(backing,n=>{token.ThrowIfCancellationRequested();if(File.Exists(Path.Combine(output,"stop")))throw new OperationCanceledException(token);metrics.BytesRead+=n;});archives.Add((backing,activation,known,archiveOrder,iniOrder,reader));
        }
    }
    public static bool VanillaArchive(string name)=>new[]{"Skyrim - Textures0.bsa","Skyrim - Textures1.bsa","Skyrim - Textures2.bsa","Skyrim - Textures3.bsa","Skyrim - Textures4.bsa","Skyrim - Textures5.bsa","Skyrim - Textures6.bsa","Skyrim - Textures7.bsa","Skyrim - Textures8.bsa","Skyrim - Meshes0.bsa","Skyrim - Meshes1.bsa","Skyrim - Voices_en0.bsa","Skyrim - Sounds.bsa","Skyrim - Interface.bsa","Skyrim - Animations.bsa","Skyrim - Shaders.bsa","Skyrim - Misc.bsa"}.Contains(name,StringComparer.OrdinalIgnoreCase);
    public LeanSource Stage(string reference,string consumer,Queue<(string Reference,string Consumer)> pending,string? supplied=null,byte[]? selectedBytes=null,bool followDependencies=true,Action<byte[]>? consume=null,Action<LeanExportInputs.Nif>? inspectNif=null,bool stagePayload=true,string? suppliedKind=null,string? suppliedBasis=null)
    {
        reference=VirtualPath.Normalize(reference);if(staged.TryGetValue(reference,out var prior))return prior;
        byte[] bytes;string backing;string? member=null,activation=null,archiveBasis=null;long? memberOffset=null;int? storedBytes=null;
        try{using var f=Open(supplied??Virtual(reference));backing=Backing(f);
            if(supplied==null&&lookupPolicy?.Supported!=true)throw new LeanSourceAdmissionException("Required loose/archive winner remains unsupported: "+reference+"; loose backing="+backing+"; effective invalidation="+(lookupPolicy?.EffectiveInvalidation??"missing")+"; MO2="+(lookupPolicy?.Mo2Version??"unproved")+"; no asset payload admitted");
            if(f.Length>BoundedBinary.Limit)throw new InvalidDataException("Asset exceeds supported bound");bytes=selectedBytes??At(f,0,(int)f.Length);}
        catch(IOException e)when(supplied==null&&e is FileNotFoundException or DirectoryNotFoundException){
            try{IndexArchives();}catch(InvalidDataException cause){throw new InvalidDataException("Index candidate archives for lookup '"+reference+"' ("+consumer+"): "+cause.Message,cause);}
            var matches=archives.Where(x=>x.Reader.Members.ContainsKey(reference)).ToArray();
            if(matches.Length==0)throw new FileNotFoundException("Required loose/archive member missing",reference);
            // Retained T05 attempt14 establishes later INI-list archive precedence.
            // This is separate from plugin pairing; arbitrary mixed/tied archives
            // still need the existing distinct proven plugin-order route.
            bool iniOrdered=matches.Length>1&&lookupPolicy?.Supported==true&&matches.All(x=>x.Proven&&x.IniOrder>=0)&&matches.Select(x=>x.IniOrder).Distinct().Count()==matches.Length;
            bool ordered=iniOrdered||matches.Length>1&&lookupPolicy?.Supported==true&&matches.All(x=>x.Proven&&x.PluginOrder>=0)&&matches.Select(x=>x.PluginOrder).Distinct().Count()==matches.Length;
            if((matches.Length!=1||!matches[0].Proven)&&!ordered)throw new InvalidDataException("Required archive member winner remains unsupported: "+reference+"; matches="+string.Join(';',matches.Select(x=>x.Backing+" ["+x.Activation+"]")));
            var match=iniOrdered?matches.MaxBy(x=>x.IniOrder):ordered?matches.MaxBy(x=>x.PluginOrder):matches[0];var entry=match.Reader.Members[reference];bytes=match.Reader.Extract(reference);backing=match.Backing;member=reference;memberOffset=entry.Offset;storedBytes=entry.Bytes;activation=match.Activation;
            archiveBasis=iniOrdered?"Loose lookup absent; later selected effective INI archive wins (SResourceArchiveList then SResourceArchiveList2): "+string.Join("; ",matches.OrderBy(x=>x.IniOrder).Select(x=>x.Activation+" -> "+x.Backing)):ordered?"Loose lookup absent; later active-plugin order wins among exact paired archives: "+string.Join("; ",matches.OrderBy(x=>x.PluginOrder).Select(x=>x.Activation+" @"+x.PluginOrder+" -> "+x.Backing)):"Only exact member in proven active archives; loose lookup absent";
        }
        LeanExportInputs.Nif? nif=null;
        if(Path.GetExtension(reference).Equals(".nif",StringComparison.OrdinalIgnoreCase))try{nif=LeanExportInputs.ReadNif(bytes);}catch(InvalidDataException e){throw new InvalidDataException("Required NIF "+reference+" from "+backing+(member==null?"":" :: "+member)+" ("+consumer+"): "+e.Message,e);}
        try{consume?.Invoke(bytes);}catch(InvalidDataException e){throw new InvalidDataException("Read required asset "+reference+" from "+backing+(member==null?"":" :: "+member)+" ("+consumer+"): "+e.Message,e);}
        string digest=BoundedBinary.Digest(bytes);var source=new LeanSource(reference,backing,digest,bytes.Length,consumer,member!=null?"Active BSA exact member":supplied==null?(physical?"Physical/deployed final loose backing":"Selected Data read-only final loose backing"):suppliedKind??"Declared generated export",member,memberOffset,storedBytes,activation,member!=null?archiveBasis:supplied==null?lookupPolicy!.Basis:suppliedBasis??"Explicit selected generated export; independent of installed loose/archive priority",stagePayload,nif?.Dependencies.Select(LeanPackagingChoices.Ref).Distinct().ToArray(),nif?.TextureRepairs);
        if(stagePayload){string target=SafePaths.Below(output,"assets/"+reference);Directory.CreateDirectory(Path.GetDirectoryName(target)!);using(var dest=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))dest.Write(bytes);metrics.BytesCopied+=bytes.Length;metrics.FilesCopied++;}staged.Add(reference,source);Sources.Add(source);
        if(nif!=null){try{inspectNif?.Invoke(nif);}catch(InvalidDataException e){throw new InvalidDataException("Inspect required NIF "+reference+" from "+backing+(member==null?"":" :: "+member)+" ("+consumer+"): "+e.Message,e);}if(followDependencies)foreach(var dep in nif.Dependencies)pending.Enqueue((dep,"NIF dependency of "+reference));}
        return source;
    }
    public void Dispose(){foreach(var item in archives)item.Reader.Dispose();}
}

public sealed record LeanRecord(string Canonical,string Type,string Plugin,string Backing,long Offset,int Bytes,uint Flags,string[] Masters,Dictionary<string,List<byte[]>> Fields,string Sha256,LeanGroup[]? Groups=null)
{
    public string[] Text(string tag)=>Fields.GetValueOrDefault(tag,[]).Select(x=>Encoding.UTF8.GetString(x).TrimEnd('\0')).ToArray();
    public string? Ref(uint id){if(id==0)return null;int slot=(int)(id>>24);if(slot>Masters.Length)throw new InvalidDataException("Record reference master index invalid");return (slot<Masters.Length?Masters[slot]:Plugin)+"|"+(id&0xffffff).ToString("X6");}
    public string[] Refs(string tag)=>Fields.GetValueOrDefault(tag,[]).SelectMany(x=>{if(x.Length%4!=0)throw new InvalidDataException("Record reference array malformed");return Enumerable.Range(0,x.Length/4).Select(i=>Ref(BitConverter.ToUInt32(x,i*4))).Where(y=>y!=null).Select(y=>y!);}).ToArray();
}
public sealed class LeanRecordIndex(LeanMo2Sources sources,CancellationToken token)
{
    LeanLocalizedStrings? localized;
    public void SetLanguage(string language)=>localized=new(sources,language);
    record Row(string Plugin,string Backing,long Offset,int Size,uint Flags,string Type,string[] Masters,LeanGroup[] Groups);
    readonly Dictionary<string,Row> winners=new(StringComparer.OrdinalIgnoreCase),origins=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,LeanRecord> records=new(StringComparer.OrdinalIgnoreCase);
    // These groups have concrete head/material/equipment record consumers.
    static readonly HashSet<string> types=["NPC_","RACE","HDPT","ARMO","ARMA","TXST","WEAP","STAT","AMMO","PROJ","CELL","WRLD","FLST","ENCH","MGEF","CLAS","CSTY","PERK","SPEL","VTYP","FACT","SNDR","SNCT","SOPM","KYWD"];
    readonly Dictionary<string,byte[]> rawRecords=new(StringComparer.OrdinalIgnoreCase),headers=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,(long Bytes,long WriteTime)> pluginIdentity=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,string> pluginBackings=new(StringComparer.OrdinalIgnoreCase);
    bool playerReferenceIndexed;
    public int HeadersRead {get;private set;}
    public string[] Order {get;private set;}=[];
    public static Dictionary<string,List<byte[]>> Fields(byte[] payload)
    {
        var r=new BoundedBinary(payload);Dictionary<string,List<byte[]>> fields=[];int? extended=null;
        while(r.Remaining>0){string tag=Encoding.ASCII.GetString(r.Take(4));int count=r.U16();if(tag=="XXXX"){if(count!=4||extended!=null)throw new InvalidDataException("Record extended field malformed");extended=r.Count();continue;}if(extended!=null){count=extended.Value;extended=null;}if(!fields.TryGetValue(tag,out var list)){list=[];fields.Add(tag,list);}list.Add(r.Take(count));}if(extended!=null)throw new InvalidDataException("Dangling record extended field");return fields;
    }
    public void Scan(string[] order,bool playerScaleDefault=false,IReadOnlyDictionary<string,string>? suppliedPaths=null)
    {
        Order=order;playerReferenceIndexed=playerScaleDefault;
        foreach(string plugin in order){token.ThrowIfCancellationRequested();using var f=sources.Open(suppliedPaths?.GetValueOrDefault(plugin)??sources.Virtual(plugin));string backing=LeanMo2Sources.Backing(f);byte[] header=sources.At(f,0,24);if(Encoding.ASCII.GetString(header,0,4)!="TES4")throw new InvalidDataException("Plugin TES4 header absent: "+plugin);int size=checked((int)BitConverter.ToUInt32(header,4));byte[] headPayload=sources.At(f,24,size);var fields=Fields(headPayload);string[] masters=fields.GetValueOrDefault("MAST",[]).Select(x=>Encoding.UTF8.GetString(x).TrimEnd('\0')).ToArray();
            headers[backing]=header.Concat(headPayload).ToArray();pluginBackings[plugin]=backing;pluginIdentity[plugin]=(f.Length,new FileInfo(backing).LastWriteTimeUtc.Ticks);
            void ScanGroup(long start,long end,LeanGroup[] groups){long at=start;while(at<end){token.ThrowIfCancellationRequested();byte[] h=sources.At(f,at,24);HeadersRead++;string tag=Encoding.ASCII.GetString(h,0,4);uint bytes=BitConverter.ToUInt32(h,4),flags=BitConverter.ToUInt32(h,8),id=BitConverter.ToUInt32(h,12);
                if(tag=="GRUP"){if(bytes<24||at+bytes>end)throw new InvalidDataException("Plugin group bounds invalid");int group=BitConverter.ToInt32(h,12);string label=Encoding.ASCII.GetString(h,8,4);if((group==0&&types.Contains(label))||group is 1 or 2 or 3 or 4 or 5||(playerScaleDefault&&group is 6 or 8))ScanGroup(at+24,at+bytes,[..groups,new(group,BitConverter.ToUInt32(h,8))]);at+=bytes;}
                else{if(at+24+bytes>end)throw new InvalidDataException("Plugin record bounds invalid");if(types.Contains(tag)||(playerScaleDefault&&tag=="ACHR"&&(id&0xffffff)==0x14)){int slot=(int)(id>>24);if(slot>masters.Length)throw new InvalidDataException("Record owner index invalid");string key=(slot<masters.Length?masters[slot]:plugin)+"|"+(id&0xffffff).ToString("X6");if(tag!="ACHR"||key.Equals("Skyrim.esm|000014",StringComparison.OrdinalIgnoreCase)){var row=new Row(plugin,backing,at,checked((int)bytes+24),flags,tag,masters,groups);winners[key]=row;origins.TryAdd(key,row);}}at+=24+bytes;}
            }if(at!=end)throw new InvalidDataException("Plugin group framing mismatch");}ScanGroup(24+size,f.Length,[]);
        }
    }
    public LeanRecord Get(string canonical,bool origin=false)=>ReadRecord(canonical,origin,false);
    public string[] RaceDefaultHeadParts(string canonical,ushort sex)
    {
        var race=Get(canonical);if(race.Type!="RACE"||sex>1)throw new InvalidDataException("Retained head closure requires a selected-sex RACE");
        // Reuse the already-read winner bytes. HEAD is repeated in sex-specific
        // NAM0 sections; the field dictionary alone loses those boundaries.
        byte[] raw=rawRecords["winner/"+canonical],payload=raw[24..];
        if((BitConverter.ToUInt32(raw,8)&0x40000)!=0)payload=BoundedBinary.Inflate(payload[4..],checked((int)BitConverter.ToUInt32(payload)));
        var q=new BoundedBinary(payload);int? extended=null;bool section=false,selected=false;List<string> heads=[];
        while(q.Remaining>0){
            string tag=Encoding.ASCII.GetString(q.Take(4));int size=q.U16();
            if(tag=="XXXX"){if(size!=4||extended!=null)throw new InvalidDataException("Race default head extended field malformed");extended=q.Count();continue;}
            if(extended!=null){size=extended.Value;extended=null;}byte[] value=q.Take(size);
            if(tag=="NAM0"){section=true;selected=false;}
            else if(section&&tag is "MNAM" or "FNAM")selected=tag==(sex==0?"MNAM":"FNAM");
            else if(section&&selected&&tag=="HEAD"){
                if(value.Length!=4)throw new InvalidDataException("Race default HEAD framing unsupported: "+canonical);
                string key=race.Ref(BitConverter.ToUInt32(value))??throw new InvalidDataException("Null race default HEAD: "+canonical);heads.Add(key);
            }
        }
        if(extended!=null)throw new InvalidDataException("Dangling race default head field");return heads.ToArray();
    }
    LeanRecord ReadRecord(string canonical,bool origin,bool allowDeleted)
    {
        string cache=(origin?"origin/":"winner/")+canonical;
        if(!(origin?origins:winners).TryGetValue(canonical,out var row))throw new InvalidDataException("Required active record missing: "+canonical);
        if(!allowDeleted&&(row.Flags&0x20)!=0)throw new InvalidDataException("Required winning record is deleted: "+canonical);
        if(records.TryGetValue(cache,out var record))return record;
        try{
        using var f=sources.Open(row.Backing);byte[] raw=sources.At(f,row.Offset,row.Size),payload=raw[24..];if((row.Flags&0x40000)!=0)payload=BoundedBinary.Inflate(payload[4..],checked((int)BitConverter.ToUInt32(payload)));
        string digest=BoundedBinary.Digest(raw);
        if((BitConverter.ToUInt32(headers[row.Backing],8)&0x80)!=0){payload=(localized??=new(sources,"english")).Inline(row.Plugin,row.Type,payload);byte[] edited=raw[..24];BitConverter.GetBytes(payload.Length).CopyTo(edited,4);BitConverter.GetBytes(row.Flags&~0x40000u).CopyTo(edited,8);raw=edited.Concat(payload).ToArray();}
        record=new(canonical,row.Type,row.Plugin,row.Backing,row.Offset,row.Size,row.Flags,row.Masters,Fields(payload),digest,row.Groups);records.Add(cache,record);rawRecords.Add(cache,raw);return record;
        }catch(InvalidDataException e){throw new InvalidDataException("Read "+row.Type+" record "+canonical+" from "+row.Plugin+" ("+row.Backing+", record offset="+row.Offset+"): "+e.Message,e);}
    }
    string? RowWorld(Row row){var world=row.Groups.SingleOrDefault(g=>g.Type==1);if(world==null)return null;int slot=(int)(world.Label>>24);if(slot>row.Masters.Length)throw new InvalidDataException("WRLD group master index invalid");return (slot<row.Masters.Length?row.Masters[slot]:row.Plugin)+"|"+(world.Label&0xffffff).ToString("X6");}
    LeanRecord LocationGet(string key,bool origin=false){var record=Get(key,origin);if(!origin&&record.Type=="CELL"&&(record.Flags&0x4000)!=0){var inherited=Get(key,true);var fields=new Dictionary<string,List<byte[]>>(record.Fields);foreach(string tag in new[]{"DATA","XCLC"})if(!fields.ContainsKey(tag)&&inherited.Fields.TryGetValue(tag,out var values))fields[tag]=values;record=record with{Fields=fields};}return record;}
    public string? PhysicalCell(string world,int x,int y)
    {
        uint block=LeanPlacementPolicy.XY(LeanPlacementPolicy.Divide(x,32),LeanPlacementPolicy.Divide(y,32)),sub=LeanPlacementPolicy.XY(LeanPlacementPolicy.Divide(x,8),LeanPlacementPolicy.Divide(y,8));List<string> matches=[];
        foreach(var p in winners.Where(p=>p.Value.Type=="CELL"&&(p.Value.Flags&0x400)==0&&LeanPlacementPolicy.Same(RowWorld(p.Value),world)&&p.Value.Groups.Any(g=>g.Type==4&&g.Label==block)&&p.Value.Groups.Any(g=>g.Type==5&&g.Label==sub))){
            bool deleted=(p.Value.Flags&0x20)!=0;
            var record=deleted?ReadRecord(p.Key,false,true):LocationGet(p.Key);
            // A stripped deleted override may omit XCLC. Its original grid can
            // establish relevance only; it never supplies a usable destination.
            if(deleted&&!record.Fields.ContainsKey("XCLC"))record=ReadRecord(p.Key,true,true);
            if(LeanPlacementPolicy.Coordinates(record)!=(x,y))continue;
            if(deleted)Get(p.Key); // Reject a deleted target, never call it absent.
            matches.Add(p.Key);
        }
        if(matches.Count>1)throw new InvalidDataException("Physical exterior CELL ambiguous for "+world+" grid "+x+","+y);
        return matches.SingleOrDefault();
    }
    public string PersistentCell(string world){var candidates=winners.Where(p=>p.Value.Type=="CELL"&&(p.Value.Flags&0x400)!=0&&LeanPlacementPolicy.Same(RowWorld(p.Value),world)&&!p.Value.Groups.Any(g=>g.Type is 4 or 5)).Select(p=>p.Key).ToArray();if(candidates.Length!=1)throw new InvalidDataException("Persistent CELL missing/ambiguous for worldspace "+world);return candidates[0];}
    public LeanPlacement ResolvePlacement(Transform saved)=>LeanPlacementPolicy.Resolve(saved,LocationGet,PhysicalCell,PersistentCell);
    public LeanRecord? PlayerReference(){if(!playerReferenceIndexed)throw new InvalidDataException("Selected player reference default was not indexed");return winners.ContainsKey("Skyrim.esm|000014")?Get("Skyrim.esm|000014"):null;}
    // Compact binary overlays reuse already-read required ranges. They retain
    // the real plugin/master header and records, never a legacy capture schema.
    // Base player ACHR proves an unchanged scale only. Keep its range receipt;
    // the generated NPC never links to it or needs a flat ACHR overlay.
    public List<BuildQualityWarning> QualityWarnings {get;}=[];
    public void CaptureKeywords(bool bestEffort=false){
        // Only already-consumed record KWDA arrays need this supporting proof.
        // The FormKey's origin can differ from the selected defining plugin.
        foreach(string key in records.Where(x=>!x.Key.StartsWith("origin/",StringComparison.Ordinal)).SelectMany(x=>x.Value.Refs("KWDA")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()){
            try{var keyword=Get(key);if(keyword.Type!="KYWD")throw new InvalidDataException("Required keyword link is not KYWD: "+key);}
            catch(InvalidDataException e)when(bestEffort){QualityWarnings.Add(BestEffortBuild.Finding("keyword-capture",e.Message,"plugin-quality","A selected keyword could not be resolved. The original link remains; behavior or loading may fail."));}
        }
    }
    public object EmitInputs(string output,bool bestEffort=false){
        CaptureKeywords(bestEffort);
        string masterFolder=SafePaths.Below(output,"master-headers");Directory.CreateDirectory(masterFolder);
        foreach(var pair in pluginBackings){SafePaths.NoLinks(pair.Value);var info=new FileInfo(pair.Value);var original=pluginIdentity[pair.Key];if(!info.Exists||info.Length!=original.Bytes||info.LastWriteTimeUtc.Ticks!=original.WriteTime){if(!bestEffort)throw new InvalidDataException("Selected plugin changed: "+pair.Key);QualityWarnings.Add(BestEffortBuild.Finding("record-source-readback","Selected plugin changed or became unavailable after capture: "+pair.Key));}File.WriteAllBytes(SafePaths.Below(masterFolder,pair.Key),headers[pair.Value]);}
        List<object> overlays=[];int n=0;foreach(var group in records.Where(x=>x.Value.Type is not ("ACHR" or "CELL" or "WRLD")&&!x.Key.StartsWith("origin/",StringComparison.Ordinal)).GroupBy(x=>x.Value.Backing)){
        byte[] Group(byte[] label,int kind,byte[] body){using var mem=new MemoryStream();using var w=new BinaryWriter(mem);w.Write(Encoding.ASCII.GetBytes("GRUP"));w.Write(24+body.Length);w.Write(label);w.Write(kind);w.Write(new byte[8]);w.Write(body);return mem.ToArray();}
        using var data=new MemoryStream();byte[] inlineHeader=(byte[])headers[group.Key].Clone();BitConverter.GetBytes(BitConverter.ToUInt32(inlineHeader,8)&~0x80u).CopyTo(inlineHeader,8);data.Write(inlineHeader);foreach(var typed in group.GroupBy(x=>x.Value.Type)){byte[] body=typed.SelectMany(x=>rawRecords[x.Key]).ToArray();if(typed.Key=="CELL"){if(typed.Count()!=1)throw new InvalidDataException("Bounded single origin CELL overlay required");uint id=uint.Parse(typed.Single().Value.Canonical.Split('|')[1],System.Globalization.NumberStyles.HexNumber);body=Group(BitConverter.GetBytes(id%10),2,Group(BitConverter.GetBytes(id/10%10),3,body));}data.Write(Group(Encoding.ASCII.GetBytes(typed.Key),0,body));}
        string target=SafePaths.Below(output,"record-inputs/"+(n++)+"/"+group.First().Value.Plugin);Directory.CreateDirectory(Path.GetDirectoryName(target)!);byte[] bytes=data.ToArray();File.WriteAllBytes(target,bytes);overlays.Add(new{Source=group.Key,Overlay=new{Path=target,Bytes=bytes.Length,Sha256=BoundedBinary.Digest(bytes)}});
    }return new{Overlays=overlays,MasterPaths=pluginBackings,MasterHeaderFolder=masterFolder,MasterEvidence=pluginBackings.ToDictionary(x=>x.Key,x=>(object)new{Backing=x.Value,Bytes=pluginIdentity[x.Key].Bytes,WriteTime=pluginIdentity[x.Key].WriteTime,HeaderBytes=headers[x.Value].Length,HeaderSha256=BoundedBinary.Digest(headers[x.Value])},StringComparer.OrdinalIgnoreCase)};}
    public object[] ConsumedRecords()=>records.Values.Select(x=>(object)new{x.Canonical,x.Type,x.Plugin,x.Backing,x.Offset,x.Bytes,x.Sha256}).ToArray();
}
