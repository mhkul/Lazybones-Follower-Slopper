"""Bounded read-only validation of the actual SSE CharGen export variant.

Uses T02's pinned nif.xml format. Dynamic positions are in BSDynamicTriShape's
Vector4 tail; packed skin-partition bytes are NOT assumed to contain positions.
This is not a complete NIF graph/skin validator or a writer.
"""
from pathlib import Path
import struct, hashlib, json, math, zlib, sys

def sha(b): return hashlib.sha256(b).hexdigest().upper()
class Reader:
    def __init__(self,b): self.b=b; self.i=0
    def read(self,n):
        assert n>=0 and self.i+n<=len(self.b),(self.i,n,len(self.b))
        r=self.b[self.i:self.i+n];self.i+=n;return r
    def get(self,fmt):
        r=struct.unpack('<'+fmt,self.read(struct.calcsize('<'+fmt)))
        return r[0] if len(r)==1 else r
    def string(self,fmt='I'): return self.read(self.get(fmt)).rstrip(b'\0').decode('utf8')

def head(path):
    b=path.read_bytes();r=Reader(b);r.i=b.index(b'\n')+1
    version,endian,user,n,bs=r.get('IBIII')
    assert (version,endian,user,bs)==(0x14020007,1,12,100)
    metadata=[r.string('B') for _ in range(3)]
    types=[r.string() for _ in range(r.get('H'))]
    indices=[r.get('H') for _ in range(n)];sizes=[r.get('I') for _ in range(n)]
    ns,ml=r.get('II');strings=[r.string() for _ in range(ns)]
    groups=[r.get('I') for _ in range(r.get('I'))]
    block_offsets=[];blocks=[]
    for i,size in zip(indices,sizes):
        block_offsets.append(r.i);blocks.append((types[i],r.read(size)))
    roots=[r.get('i') for _ in range(r.get('I'))]
    assert r.i==len(b) and roots and all(0<=x<n for x in roots)
    shapes=[];textures=[];nodes=[]
    for index,(typ,body) in enumerate(blocks):
        s=Reader(body)
        if typ in ['NiNode','BSFadeNode']:
            nameid=s.get('I');extras=[s.get('i') for _ in range(s.get('I'))];controller=s.get('i')
            flag_offset=block_offsets[index]+s.i;flags=s.get('I');transform=s.get('13f')
            nodes.append(dict(block=index,type=typ,name=strings[nameid] if nameid!=0xffffffff else '',
                flags=f'{flags:08X}',flagsByteOffset=flag_offset,transform=transform))
        if typ=='BSShaderTextureSet':
            textures.append(dict(block=index,paths=[s.string() for _ in range(s.get('I'))]))
            assert s.i==len(body)
        if typ!='BSDynamicTriShape': continue
        nameid=s.get('I');name=strings[nameid]
        extras=[s.get('i') for _ in range(s.get('I'))];controller=s.get('i')
        flags=s.get('I');transform=s.get('13f');collision=s.get('i');sphere=s.get('4f')
        skin,shader,alpha=s.get('iii');desc=s.get('Q');nt,nv=s.get('HH');ds=s.get('I')
        stride=(desc&15)*4;raw=b'';partition=None;partition_info=None
        if ds:
            assert ds==stride*nv+nt*6
            raw=s.read(stride*nv);s.read(nt*6)
        elif skin>=0:
            assert blocks[skin][0] in ['NiSkinInstance','BSDismemberSkinInstance']
            partition=struct.unpack_from('<i',blocks[skin][1],4)[0]
            assert blocks[partition][0]=='NiSkinPartition'
            pr=Reader(blocks[partition][1]);np,pds,pvs=pr.get('III');pdesc=pr.get('Q')
            assert pvs==stride and pds%pvs==0 and pds//pvs==nv
            raw=pr.read(pds)
            partition_info=dict(block=partition,count=np,vertexCount=pds//pvs,
                vertexDescriptor=f'{pdesc:016X}',packedVertexHash=sha(raw),
                topologyTailHash=sha(pr.read(len(pr.b)-pr.i)))
        # These exports have no optional particle payload. Refuse another variant.
        particle=s.get('I');assert particle==0,particle
        dynamic_size=s.get('I');assert dynamic_size==nv*16
        dynamic=s.read(dynamic_size);assert s.i==len(body)
        vec=list(struct.iter_unpack('<4f',dynamic));assert len(vec)==nv and nv>0
        assert all(math.isfinite(x) and abs(x)<1e6 for v in vec for x in v[:3])
        for ref in extras+[controller,collision,skin,shader,alpha]:assert ref==-1 or 0<=ref<n
        assert blocks[shader][0]=='BSLightingShaderProperty'
        pos=b''.join(struct.pack('<3f',*v[:3]) for v in vec)
        shapes.append(dict(block=index,type=typ,name=name,vertices=nv,trianglesInShape=nt,
            dynamicBytes=dynamic_size,dynamicHash=sha(dynamic),positionHash=sha(pos),
            bounds=[[min(v[a] for v in vec),max(v[a] for v in vec)] for a in range(3)],
            packedHash=sha(raw),skin=skin,partition=partition_info,shader=shader,alpha=alpha,
            vertexDescriptor=f'{desc:016X}',flags=f'{flags:08X}',transform=transform))
    assert shapes and len(shapes)==sum(t=='BSDynamicTriShape' for t,_ in blocks)
    return dict(path=str(path.resolve()),sha256=sha(b),bytes=len(b),version=version,
        userVersion=user,bsVersion=bs,metadata=metadata,roots=roots,blockTypes=[t for t,_ in blocks],
        strings=strings,shapes=shapes,textures=textures,nodes=nodes,
        limits='Block framing, dynamic vertex arrays, selected links/materials only; topology tails are hash-compared, not fully decoded. Skin graph/visual correctness remain separate.')

def dds(path,preview):
    b=path.read_bytes();assert b[:4]==b'DDS '
    h=struct.unpack('<31I',b[4:128]);size,flags,height,width,pitch,depth,mips=h[:7]
    assert size==124 and h[18]==32 and h[19]==65 and h[20]==0 and h[21]==32
    assert h[22:26]==(255,65280,16711680,4278190080)
    assert pitch==width*4 and mips==1 and len(b)==128+width*height*4
    pixels=b[128:]
    def chunk(kind,data):return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data)&0xffffffff)
    scan=b''.join(b'\0'+pixels[i*width*4:(i+1)*width*4] for i in range(height))
    png=b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',width,height,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(scan))+chunk(b'IEND',b'')
    preview.write_bytes(png)
    return dict(path=str(path.resolve()),sha256=sha(b),bytes=len(b),width=width,height=height,
        format='uncompressed RGBA8',mipCount=mips,payloadSHA256=sha(pixels),
        uniqueColors=len({pixels[i:i+4] for i in range(0,len(pixels),4)}),preview=str(preview.resolve()))

if __name__ == '__main__':
    raise SystemExit('Historical diagnostic CLI is disabled. Use the application build stages.')
