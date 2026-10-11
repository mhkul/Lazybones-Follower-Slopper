using System.Text;

namespace AutoFollower;

public sealed record LeanAppearanceBinding(string Node,int ShapeBlock,int ShaderBlock,int TextureBlock,int Slot,string Reference,string Basis);
public sealed record LeanMaterialConflict(string Node,int ShaderType,int SavedSlot,string SavedReference,int ExportedSlot,string ExportedReference,string Warning);
public sealed record LeanAppearanceAssessment(LeanAppearanceBinding[] Bindings,LeanMaterialConflict[] Conflicts);
public static class LeanAppearanceAssociations
{
    public static LeanAppearanceBinding[] Bind(byte[] bytes,LeanSkeeState state)=>Assess(LeanExportInputs.ReadNif(bytes),state,false).Bindings;
    public static LeanAppearanceAssessment Assess(LeanExportInputs.Nif nif,LeanSkeeState state,bool finishedMouthAuthority=true)
    {
        List<LeanAppearanceBinding> bindings=[];List<LeanMaterialConflict> conflicts=[];
        foreach(var node in state.Nodes){
            if(LeanOverlayPolicy.Deferred(node)||LeanRuntimeAssignmentPolicy.Omitted(node))continue;
            if(node.Scope!="material"||node.Values.Any(v=>v.Key!=9||v.Type!=2||v.Index is <0 or >8))throw new InvalidDataException(node.Scope=="transform"
                ? "Unsupported saved skeleton adjustment on '"+node.Registration+"' (registration '"+node.Node+"'). This adjustment cannot be reproduced safely yet. Keep these inputs for compatibility review, or choose another character."
                : "Unsupported saved material operation on '"+node.Node+"' (keys "+string.Join(", ",node.Values.Select(v=>v.Key).Distinct())+"). Keep these inputs for compatibility review, or choose another character.");
            int[] shapes=Enumerable.Range(0,nif.Types.Length).Where(i=>nif.Types[i]=="BSDynamicTriShape"&&nif.Blocks[i].Length>=4&&BitConverter.ToUInt32(nif.Blocks[i])<nif.Strings.Length&&nif.Strings[BitConverter.ToUInt32(nif.Blocks[i])]==node.Node).ToArray();
            if(shapes.Length!=1)throw new InvalidDataException("Persisted SKEE material requires one matching exported dynamic shape: "+node.Node);
            int shape=shapes[0];var q=new BoundedBinary(nif.Blocks[shape]);q.U32();int extras=q.Count(100000);q.Take(checked(extras*4));q.Take(4+4+52+4+16+4);int shader=checked((int)q.U32());
            if(shader<0||shader>=nif.Types.Length||nif.Types[shader]!="BSLightingShaderProperty")throw new InvalidDataException("Persisted SKEE shape shader unavailable: "+node.Node);
            q=new(nif.Blocks[shader]);uint shaderType=q.U32();q.U32();extras=q.Count(100000);q.Take(checked(extras*4));q.Take(4);uint flags=q.U32();q.Take(4+16);int texture=checked((int)q.U32());
            if(texture<0||texture>=nif.Types.Length||nif.Types[texture]!="BSShaderTextureSet")throw new InvalidDataException("Persisted SKEE shader texture set unavailable: "+node.Node);
            q=new(nif.Blocks[texture]);int count=q.Count(32);string[] paths=Enumerable.Range(0,count).Select(_=>Encoding.UTF8.GetString(q.Take(q.Count(1048576)))).ToArray();q.End();
            foreach(var value in node.Values){
                string context=node.Node+" (shader "+shaderType+", saved slot "+value.Index+")";
                string expected=Texture(value.Text,context+" required texture");
                string raw=value.Index<paths.Length?paths[value.Index]:"";
                string? observed=raw.Length==0?null:Texture(raw,context+" exported texture");
                if(!string.Equals(observed,expected,StringComparison.OrdinalIgnoreCase)){
                    // T12G direct user decision: preserve this finished mouth's
                    // environment mask and disclose the saved specular conflict.
                    // These slots are NOT equivalent. No slot or shader is edited,
                    // and the saved reference still requires a selected source.
                    if(finishedMouthAuthority&&node.Node=="FemaleMouthHumanoidDefault"&&value.Index==7&&shaderType==1&&(flags&0x80)!=0&&observed==null&&paths.Length>5&&paths[5].Length>0&&Texture(paths[5],context+" environment mask").Equals(expected,StringComparison.OrdinalIgnoreCase)){
                        string warning="Mouth uses the selected finished export's environment mask (slot 5). The save's specular override (slot 7) is not reproduced; exported geometry and shader are preserved.";
                        conflicts.Add(new(node.Node,(int)shaderType,value.Index,expected,5,expected,warning));continue;
                    }
                    throw new InvalidDataException("Saved material differs from export: "+context+" expected '"+expected+"', observed '"+(observed??(value.Index>=paths.Length?"<absent slot>":"<empty>"))+"'. Select matching material inputs; a texture in another slot does not prove the same shader effect.");
                }
                bindings.Add(new(node.Node,shape,shader,texture,value.Index,expected,"Persisted SKEE key9 texture matches the same serialized exported material slot"));
            }
        }
        return new(bindings.ToArray(),conflicts.ToArray());
    }
    static string Texture(string? path,string context)
    {
        if(string.IsNullOrEmpty(path))throw new InvalidDataException(context+": required texture is empty.");
        string value=path.Replace('\\','/');if(value.StartsWith("textures/",StringComparison.OrdinalIgnoreCase))value=value[9..];
        try{return VirtualPath.Normalize("textures/"+value);}
        catch(InvalidDataException e){throw new InvalidDataException(context+": unsafe texture path '"+path+"'; "+e.Message,e);}
    }
}
