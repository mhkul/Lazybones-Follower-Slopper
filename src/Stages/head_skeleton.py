"""Preload selected SMP hair bones into an owned private skeleton.

Uses the exact captured physics head-part tree at existing named anchors. This
tests the pregen/FSMP flattened-head merge hypothesis; runtime remains unproved.
No original skeleton node, geometry, weight, controller or transform is replaced.
"""
import hashlib, struct
from nif_refs import Nif
from head_physics import nodes, root

def preload(model, head, inputs):
    if not inputs:return dict(required=False, runtimeVerified=False)
    if len(inputs)!=1:raise ValueError('SMP private skeleton requires one uniquely selected physics source tree')
    original=model.encode();before=Nif(original);dst=nodes(model);src=inputs[0]['nodes']
    def names(tree):
        result={x['name']:i for i,x in tree.items() if x['name']}
        if len(result)!=sum(bool(x['name']) for x in tree.values()):raise ValueError('Ambiguous SMP skeleton node names')
        return result
    targets=names(dst);source_names=names(src);missing=set(head.skin_bones())-set(targets)
    if not missing:return dict(required=False, alreadyBound=True, runtimeVerified=False)
    absent=missing-set(source_names)
    if absent:raise ValueError('Selected physics tree lacks required private head bones: '+str(sorted(absent)))
    parents={}
    for i,x in src.items():
        for child in x['children']:
            if child not in src:continue
            if child in parents:raise ValueError('SMP source bone has multiple parents')
            parents[child]=i
    reachable=set()
    def visit(i):
        if i in reachable:raise ValueError('Private skeleton node cycle or duplicate attachment')
        reachable.add(i)
        for child in dst[i]['children']:
            if child in dst:visit(child)
    visit(root(model))
    wanted=set();anchors={}
    def require(i,chain=()):
        if i in chain or len(chain)>128:raise ValueError('SMP source bone cycle/depth')
        name=src[i]['name']
        if name in targets:
            if targets[name] not in reachable:raise ValueError('SMP skeleton anchor is unreachable: '+name)
            anchors[i]=targets[name];return
        if not name or i not in parents:raise ValueError('SMP source bone lacks an existing skeleton anchor: '+name)
        x=src[i]
        if x['controller']!=-1 or x['collision']!=-1 or x['extras'] or x['effects']:
            raise ValueError('SMP source bone needs unsupported controller/collision/extra data: '+name)
        wanted.add(i);require(parents[i],chain+(i,))
    for name in sorted(missing):require(source_names[name])
    mapping=dict(anchors);added=[]
    # Allocate every required source bone first so source child order survives.
    for i in sorted(wanted):
        x=src[i];name_index=len(model.strings);model.strings.append(x['name'].encode('utf-8'))
        raw=struct.pack('<IIiI13fiII',name_index,0,-1,x['flags'],*x['transform'],-1,0,0)
        mapping[i]=model.append_block('NiNode',raw)
    changed=set()
    for i in sorted(wanted):
        x=src[i];children=[mapping[c] for c in x['children'] if c in wanted]
        b=model.blocks[mapping[i]];model.blocks[mapping[i]]=b[:72]+struct.pack('<I',len(children))+b''.join(struct.pack('<i',c) for c in children)+struct.pack('<I',0)
        parent=parents[i]
        if parent in anchors:
            index=anchors[parent];n=nodes(model)[index];b=model.blocks[index];at=n['child_at'];count=len(n['children'])
            model.blocks[index]=b[:at]+struct.pack('<I',count+1)+b[at+4:at+4+count*4]+struct.pack('<i',mapping[i])+b[at+4+count*4:];changed.add(index)
        added.append(dict(name=x['name'],sourceBlock=i,block=mapping[i],parent=src[parent]['name'],transform=list(x['transform']),sourceNodeSha256=hashlib.sha256(inputs[0]['model'].blocks[i]).hexdigest().upper()))
    after=Nif(model.encode());now=nodes(after)
    if missing-set(after.nodes()):raise ValueError('SMP private skeleton preload did not bind all required head bones')
    if after.strings[:len(before.strings)]!=before.strings or after.footer!=before.footer or after.groups!=before.groups:
        raise ValueError('SMP skeleton preload changed original strings/footer/groups')
    for i,(a,b) in enumerate(zip(before.blocks,after.blocks)):
        if i not in changed and a!=b:raise ValueError('SMP skeleton preload changed protected original block '+str(i))
        if i in changed:
            for key in ('name','extras','controller','flags','transform','collision','effects'):
                if dst[i][key]!=now[i][key]:raise ValueError('SMP skeleton preload changed original anchor '+key)
            expected=dst[i]['children']+[mapping[s] for s in sorted(wanted) if parents[s] in anchors and anchors[parents[s]]==i]
            if now[i]['children']!=expected:raise ValueError('SMP skeleton preload changed existing anchor children')
    for i in wanted:
        for key in ('name','controller','flags','transform','collision','extras','effects'):
            if src[i][key]!=now[mapping[i]][key]:raise ValueError('SMP skeleton preload changed source bone '+key)
        if now[mapping[i]]['children']!=[mapping[c] for c in src[i]['children'] if c in wanted]:raise ValueError('SMP skeleton preload changed source bone hierarchy')
    return dict(required=True, sourceReference=inputs[0]['reference'], sourceSha256=hashlib.sha256(inputs[0]['model'].original).hexdigest().upper(), inputSkeletonSha256=hashlib.sha256(original).hexdigest().upper(), outputSkeletonSha256=hashlib.sha256(after.encode()).hexdigest().upper(), hypothesis='Preload original source hair bone tree so FSMP finds required bones directly without its flattened-FaceGeom merge.', addedBones=added, originalAnchorBlocksWithAppendedChildren=sorted(changed), originalSkeletonBlocks=len(before.blocks), originalSkeletonNodesTransformsGeometryPreserved=True, sourceBoneHierarchyAndTransformsPreserved=True, allRequiredHeadBonesPreloaded=True, runtimeVerified=False)
