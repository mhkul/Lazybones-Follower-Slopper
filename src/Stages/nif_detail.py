"""Bounded SSE geometry/skin/partition reader, based on retained niftools nif.xml."""
from pathlib import Path
import hashlib,math,struct,sys
from inventory import P
sys.path.insert(0,str(P/'prototypes/T02'))
from nif_probe import Reader,nif

def detail(path):
 path=Path(path);base=nif(path);b=path.read_bytes();r=Reader(b);r.i=b.index(b'\n')+1
 assert r.get('IBIII')==(0x14020007,1,12,len(base['blockTypes']),100)
 for _ in range(3):r.string('B')
 types=[r.string() for _ in range(r.get('H'))];indices=[r.get('H') for _ in base['blockTypes']];sizes=[r.get('I') for _ in indices]
 count,maximum=r.get('II');strings=[r.string() for _ in range(count)];groups=[r.get('I') for _ in range(r.get('I'))]
 blocks=[(types[k],r.read(size)) for k,size in zip(indices,sizes)];assert r.get('I')==1;r.get('i');assert r.i==len(b)
 def ref(k,kind=None):
  assert 0<=k<len(blocks)
  typ,raw=blocks[k]
  if kind:assert typ in kind,(typ,kind)
  return raw
 def node(k):
  raw=ref(k,('NiNode','BSFadeNode'));key=struct.unpack_from('<I',raw)[0];return strings[key] if key!=0xffffffff else ''
 for s in base['shapes']:
  raw=ref(s['block']);q=Reader(raw);q.get('I');[q.get('i') for _ in range(q.get('I'))];q.get('iI13fi4fiii');desc=q.get('Q');nt,nv=q.get('HH');ds=q.get('I');stride=(desc&15)*4
  local=q.read(stride*nv) if ds else None;triangles=list(struct.iter_unpack('<HHH',q.read(nt*6))) if ds else None
  skin=Reader(ref(s['skin'],('NiSkinInstance','BSDismemberSkinInstance')));data,partition,skeleton,nb=skin.get('iiiI');bones=[node(skin.get('i')) for _ in range(nb)]
  parts=[]
  if blocks[s['skin']][0]=='BSDismemberSkinInstance':parts=[skin.get('HH') for _ in range(skin.get('I'))]
  assert skin.i==len(skin.b);node(skeleton)
  sk=Reader(ref(data,('NiSkinData',)));transform=sk.get('13f');assert sk.get('I')==nb;has=sk.get('B');weight_total=[0.0]*s['count'];skindata=[]
  for _ in range(nb):
   bt=sk.get('13f');sphere=sk.get('4f');number=sk.get('H');weights=[]
   if has:
    for _ in range(number):
     index,value=sk.get('Hf');assert index<s['count'] and math.isfinite(value) and 0<=value<=1.0001;weight_total[index]+=value;weights.append((index,value))
   skindata.append(dict(transform=bt,sphere=sphere,weights=weights))
  assert sk.i==len(sk.b)
  part=Reader(ref(partition,('NiSkinPartition',)));np,ds2,stride2,desc2=part.get('IIIQ');assert stride2==stride and ds2%stride==0;vertex_raw=part.read(ds2)
  if local:assert vertex_raw==local
  assert len(vertex_raw)//stride==s['count'];partition_summaries=[];part_faces=[]
  for _ in range(np):
   pv,pt,pb,strips,nw=part.get('HHHHH');palette=[part.get('H') for _ in range(pb)];assert max(palette,default=-1)<nb
   vmap=[part.get('H') for _ in range(pv)] if part.get('B') else list(range(pv));assert max(vmap,default=-1)<s['count']
   has_weights=part.get('B');pweights=[]
   if has_weights:
    for _ in range(pv):
     w=[part.get('f') for _ in range(nw)];assert all(math.isfinite(v) and 0<=v<=1.0001 for v in w);assert abs(sum(w)-1)<.005;pweights.append(w)
   lengths=[part.get('H') for _ in range(strips)];assert not lengths,'Strip topology unsupported'
   faces=[part.get('HHH') for _ in range(pt)] if part.get('B') else []
   bone_indices=part.get('B')
   if bone_indices:
    for _ in range(pv):assert max([part.get('B') for _ in range(nw)],default=-1)<pb
   lod,globalvb,vd=part.get('BBQ');copies=[part.get('HHH') for _ in range(pt)];assert all(max(t)<s['count'] for t in copies)
   if faces:
    # nifly Skin.cpp sets bMappedIndices=false for 12/100, independently
    # of the serialized globalVB flag: both triangle arrays use shape indices.
    assert faces==copies and all(max(t)<s['count'] for t in faces)
   part_faces.extend(copies);partition_summaries.append(dict(vertices=pv,triangles=pt,bones=pb,weightsPerVertex=nw,palette=palette,vertexMap=vmap,lod=lod,globalVB=globalvb))
  assert part.i==len(part.b)
  assert s['triangles'] in (0,len(part_faces)) if not ds else len(part_faces)==s['triangles']
  s['triangles']=len(part_faces)
  if triangles is not None:assert triangles==part_faces
  else:triangles=part_faces
  assert all(max(t)<s['count'] for t in triangles)
  assert all(math.isfinite(v) for p in s['vertices'] for v in p)
  legacy_nonunit=sum(abs(t-1)>=.005 for t in weight_total) if has else 0
  uv_off=((desc>>8)&15)*4;skin_off=((desc>>28)&15)*4;attributes=desc>>44
  uvs=[struct.unpack_from('<ee',vertex_raw,i*stride+uv_off) for i in range(s['count'])] if attributes&2 else []
  if attributes&64:
   for i in range(s['count']):
    weights=struct.unpack_from('<4e',vertex_raw,i*stride+skin_off);ids=struct.unpack_from('<4B',vertex_raw,i*stride+skin_off+8)
    assert all(math.isfinite(v) and 0<=v<=1.001 for v in weights) and abs(sum(weights)-1)<.005 and max(ids)<nb
  s.update(rawVertices=vertex_raw,uvs=uvs,bones=bones,parts=parts,skinData=skindata,partitionSummary=partition_summaries,faces=triangles,faceHash=hashlib.sha256(b''.join(struct.pack('<HHH',*t) for t in triangles)).hexdigest().upper(),skinHash=hashlib.sha256(ref(data)+ref(s['skin'])).hexdigest().upper(),uvOffset=uv_off,skinOffset=skin_off,legacyNonUnitSkinTotals=legacy_nonunit,legacySkinTotalRange=[min(weight_total,default=0),max(weight_total,default=0)] if has else None)
 base['blocks']=blocks;return base

if __name__=='__main__':
 d=detail(sys.argv[1]);print([(s['name'],s['count'],len(s['faces']),len(s['bones'])) for s in d['shapes']])
