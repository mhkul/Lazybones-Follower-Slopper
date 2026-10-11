"""Recover retained-mode SMP bone frames from the selected export's skin binds.

RaceMenu's flat identity nodes carry bone names, not the physical rest frames.
FSMP raw FaceGeom recovery clones these nodes. Restore only missing identity
nodes whose exported inverse binds prove the captured source head-relative pose.
No source-local substitution, hierarchy change, geometry or skin-data edit.
"""
import hashlib,math,struct
from nif_refs import Cursor
from head_physics import nodes,root

IDENTITY=[[float(i==j) for j in range(4)] for i in range(4)]
TOLERANCE=0.001
def multiply(a,b):
    return [[sum(a[i][k]*b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def inverse(m):
    a=[list(row)+[float(i==j) for j in range(4)] for i,row in enumerate(m)]
    for c in range(4):
        p=max(range(c,4),key=lambda r:abs(a[r][c]));a[c],a[p]=a[p],a[c]
        if abs(a[c][c])<1e-8:raise ValueError('SMP skin bind frame is singular')
        d=a[c][c];a[c]=[v/d for v in a[c]]
        for r in range(4):
            if r!=c:
                d=a[r][c];a[r]=[a[r][j]-d*a[c][j] for j in range(8)]
    return [row[4:] for row in a]
def difference(a,b):return max(abs(a[i][j]-b[i][j]) for i in range(4) for j in range(4))
def matrix(t):
    if not all(math.isfinite(v) for v in t) or t[12]<=0:raise ValueError('SMP bone transform is nonfinite or has unsupported scale')
    return [[t[3+i*3+j]*t[12] for j in range(3)]+[t[i]] for i in range(3)]+[[0,0,0,1]]
def skin_matrix(t):return matrix(t[9:12]+t[:9]+t[12:])
def node_transform(m):
    scale=math.sqrt(sum(m[i][0]**2 for i in range(3)))
    if scale<1e-8:raise ValueError('SMP recovered frame has zero scale')
    r=[[m[i][j]/scale for j in range(3)] for i in range(3)]
    if max(abs(sum(r[k][i]*r[k][j] for k in range(3))-float(i==j)) for i in range(3) for j in range(3))>0.0001:
        raise ValueError('SMP recovered frame is not a uniform rigid transform')
    det=r[0][0]*(r[1][1]*r[2][2]-r[1][2]*r[2][1])-r[0][1]*(r[1][0]*r[2][2]-r[1][2]*r[2][0])+r[0][2]*(r[1][0]*r[2][1]-r[1][1]*r[2][0])
    if abs(det-1)>0.0001:raise ValueError('SMP recovered frame has unsupported reflection')
    return [m[i][3] for i in range(3)]+[v for row in r for v in row]+[scale]
def unique_names(tree):
    names={n['name']:i for i,n in tree.items() if n['name']}
    if len(names)!=sum(bool(n['name']) for n in tree.values()):raise ValueError('SMP bind frame node names are ambiguous')
    return names
def world_frames(tree,r):
    frames={};parents={}
    def visit(i,parent):
        if i in frames:raise ValueError('SMP bind source has a cycle or duplicate attachment')
        frames[i]=multiply(parent,matrix(tree[i]['transform']))
        for c in tree[i]['children']:
            if c in tree:parents[c]=i;visit(c,frames[i])
    visit(r,IDENTITY);return frames,parents
def skin_binds(model,tree):
    for kind,raw in zip(model.indices,model.blocks):
        if model.types[kind] not in ('NiSkinInstance','BSDismemberSkinInstance'):continue
        q=Cursor(raw);data,partition,skeleton_root=q.unpack('3i');bones=[q.unpack('i') for _ in range(q.unpack('I'))]
        if data<0 or data>=len(model.blocks) or model.types[model.indices[data]]!='NiSkinData':raise ValueError('SMP inverse bind data is absent')
        q=Cursor(model.blocks[data]);skin=q.unpack('13f');count=q.unpack('I');weights=q.unpack('B')
        if count!=len(bones) or weights not in (0,1):raise ValueError('SMP inverse bind framing is unsupported')
        result=[]
        for bone in bones:
            if bone not in tree:raise ValueError('SMP skin links an unsupported bone')
            t=q.unpack('13f');q.take(16);n=q.unpack('H');q.take(n*6 if weights else 0)
            result.append((tree[bone]['name'],skin_matrix(t)))
        if q.pos!=len(q.data):raise ValueError('SMP inverse bind data has trailing fields')
        yield data,skin_matrix(skin),result

def restore(model,inputs,skeleton):
    if not inputs:return dict(required=False,runtimeVerified=False)
    if len(inputs)!=1:raise ValueError('Retained SMP bind frames require one captured physics source tree')
    tree=nodes(model);names=unique_names(tree);installed=unique_names(nodes(skeleton));source=inputs[0];src=source['nodes'];src_names=unique_names(src)
    missing=set(model.skin_bones())-set(installed)
    if not missing:return dict(required=False,alreadyBound=True,runtimeVerified=False)
    if missing-set(src_names):raise ValueError('Captured SMP source lacks required retained hair bones: '+str(sorted(missing-set(src_names))))
    anchor='NPC Head [Head]'
    if anchor not in names or anchor not in src_names or anchor not in installed:raise ValueError('Retained SMP has no unique installed/source/export head anchor')
    head=names[anchor];frames,parents=world_frames(tree,root(model));source_frames,source_parents=world_frames(src,source['root'])
    source_inverse=inverse(source_frames[src_names[anchor]])
    recovered={};skins={};residual=0;source_error=0
    for data,skin,binds in skin_binds(model,tree):
        selected=[(n,m) for n,m in binds if n in missing]
        if not selected:continue
        anchors=[m for n,m in binds if n==anchor]
        if len(anchors)!=1:raise ValueError('SMP hair skin lacks a unique inverse bind head frame')
        for name,bind in selected:
            # Both inverse binds share the same skin space; their product
            # recovers bone-to-head directly, without a global/root offset.
            frame=multiply(anchors[0],inverse(bind))
            expected=multiply(source_inverse,source_frames[src_names[name]])
            err=difference(frame,expected);source_error=max(source_error,err)
            if err>TOLERANCE:raise ValueError('Exported SMP bind pose differs from captured source: '+name)
            if name in recovered and difference(frame,recovered[name])>TOLERANCE:raise ValueError('SMP repeated inverse bind frames disagree: '+name)
            recovered[name]=frame;skins.setdefault(name,[]).append(data)
            residual=max(residual,difference(multiply(frame,bind),anchors[0]))
    if set(recovered)!=missing:raise ValueError('Retained SMP skin bind frame coverage is incomplete')
    changes=[];original=list(model.blocks)
    for name in sorted(missing):
        i=names[name];node=tree[i];expected=multiply(frames[head],recovered[name]);old=frames[i]
        if difference(old,expected)<=TOLERANCE:continue
        # Authored nonidentity poses cannot safely be treated as placeholders.
        if parents.get(i)!=root(model) or difference(matrix(node['transform']),IDENTITY)>1e-7 or difference(frames[head],IDENTITY)>1e-7:
            raise ValueError('Retained SMP export has a non-placeholder bone pose: '+name)
        if node['extras'] or node['controller']!=-1 or node['collision']!=-1 or node['children'] or node['effects']:
            raise ValueError('Retained SMP placeholder bone has unsupported attached mechanisms: '+name)
        transform=node_transform(expected);at=16+4*len(node['extras']);raw=model.blocks[i]
        model.blocks[i]=raw[:at]+struct.pack('<13f',*transform)+raw[at+52:]
        changes.append(dict(name=name,block=i,skinDataBlocks=skins[name],original=list(node['transform']),replacement=transform))
    changed={x['block'] for x in changes};after,unused=world_frames(nodes(model),root(model))
    for name in missing:
        if difference(after[names[name]],multiply(frames[head],recovered[name]))>TOLERANCE:raise ValueError('Restored SMP bind frame readback differs')
    for i,(a,b) in enumerate(zip(original,model.blocks)):
        if i not in changed and a!=b:raise ValueError('Retained SMP changed a protected geometry/skin/material block')
        if i in changed and (a[:16]!=b[:16] or a[68:]!=b[68:]):raise ValueError('Retained SMP changed a field outside the proved bone frame')
    return dict(required=bool(changes),sourceReference=source['reference'],sourceSha256=hashlib.sha256(source['model'].original).hexdigest().upper(),
        missingInstalledBones=len(missing),restoredBoneFrames=changes,maximumSourceRelativeError=source_error,maximumSkinFrameResidual=residual,
        geometryWeightsBindDataXmlHierarchyAndFlagsPreserved=True,frameAuthority='Exact exported inverse skin binds, cross-checked against captured winning source head-relative frames',runtimeVerified=False)
