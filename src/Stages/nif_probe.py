"""SSE 20.2.0.7/12/100 bounded read-only geometry probe, per niftools nif.xml."""
from pathlib import Path
import struct,json,math,hashlib,shutil
ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'reports/T02-evidence'
class Reader:
    def __init__(self,b): self.b=b;self.i=0
    def read(self,n): r=self.b[self.i:self.i+n];self.i+=n;assert len(r)==n;return r
    def get(self,fmt): n=struct.calcsize('<'+fmt);r=struct.unpack('<'+fmt,self.read(n));return r[0] if len(r)==1 else r
    def string(self,fmt='I'):return self.read(self.get(fmt)).rstrip(b'\0').decode('utf-8','replace')
def nif(p):
    b=p.read_bytes();r=Reader(b);line=b.index(b'\n')+1;r.i=line
    version=r.get('I');endian=r.get('B');user=r.get('I');n=r.get('I');bs=r.get('I')
    assert (version,endian,user,bs)==(0x14020007,1,12,100),(p,version,user,bs)
    author,process,export=[r.string('B') for _ in range(3)]
    types=[r.string() for _ in range(r.get('H'))]; inds=[r.get('H') for _ in range(n)]; sizes=[r.get('I') for _ in range(n)]
    ns,ml=r.get('II');strings=[r.string() for _ in range(ns)];groups=[r.get('I') for _ in range(r.get('I'))]
    blocks=[(types[t],r.read(size)) for t,size in zip(inds,sizes)]
    assert r.get('I')==1 # one root
    root=r.get('i');assert r.i==len(b)
    shapes=[];extra=[];textures=[];nodeNames=[]
    for idx,(typ,body) in enumerate(blocks):
        s=Reader(body)
        if typ in ['NiNode','BSFadeNode']:
            key=s.get('I');nodeNames.append(strings[key] if key!=0xffffffff else '')
        if typ=='BSShaderTextureSet':
            textures.append(dict(block=idx,paths=[s.string() for _ in range(s.get('I'))]));assert s.i==len(body)
        if typ=='NiStringExtraData':
            key,val=s.get('II');extra.append(dict(block=idx,name=strings[key],value=strings[val]));assert s.i==len(body)
        if typ in ['BSTriShape','BSDynamicTriShape','BSSubIndexTriShape']:
            nameidx=s.get('I');name=strings[nameidx] if nameidx!=0xffffffff else ''
            extras=[s.get('i') for _ in range(s.get('I'))];controller=s.get('i');flags=s.get('I')
            transform=s.get('13f');collision=s.get('i');sphere=s.get('4f');skin,shader,alpha=s.get('iii');desc=s.get('Q');nt,nv=s.get('HH');datasize=s.get('I')
            stride=(desc&15)*4;verts=[];tri=None;partition=None;raw=b''
            if datasize:
                raw=s.read(stride*nv);tri=s.read(nt*6)
                assert datasize==stride*nv+nt*6
            elif skin>=0:
                skinType,skinBody=blocks[skin]; assert skinType in ['NiSkinInstance','BSDismemberSkinInstance'],skinType
                partition=struct.unpack_from('<i',skinBody,4)[0];pt,pb=blocks[partition];assert pt=='NiSkinPartition'
                pr=Reader(pb);np,ds,vs=pr.get('III');vd=pr.get('Q');assert vs==stride and ds%vs==0
                raw=pr.read(ds);nv=ds//vs
            if raw: verts=[struct.unpack_from('<3f',raw,k*stride) for k in range(nv)]
            shapes.append(dict(block=idx,type=typ,name=name,extras=extras,skin=skin,partition=partition,shader=shader,alpha=alpha,vertices=verts,count=nv,triangles=nt,stride=stride,desc=f'{desc:016X}',vertexBytesHash=hashlib.sha256(raw).hexdigest(),nonPositionHash=hashlib.sha256(b''.join(raw[i+12:i+stride] for i in range(0,len(raw),stride))).hexdigest(),transform=transform))
    return dict(path=str(p),bytes=len(b),sha256=hashlib.sha256(b).hexdigest().upper(),version=version,userVersion=user,bsVersion=bs,author=author,process=process,export=export,strings=strings,blockTypes=[x[0] for x in blocks],nodeNames=nodeNames,textures=textures,extras=extra,shapes=shapes)
def summarise(d):
    for s in d['shapes']:
        vs=s.pop('vertices');s['bounds']=[[min(v[a] for v in vs),max(v[a] for v in vs)] for a in range(3)] if vs else None
    return d
if __name__ == '__main__':
    raise SystemExit('Historical diagnostic CLI is disabled. Use the application build stages.')
