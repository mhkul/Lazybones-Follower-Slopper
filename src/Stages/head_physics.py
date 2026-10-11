"""Preserve selected head-part SMP on finished FaceGen without editing geometry.

Only proven installed bone identities, unweighted kinematic anchor chains and
the unique called physics XML are copied. Unknown/conflicting mechanisms reject.
"""
import hashlib, json, math, re, struct, xml.etree.ElementTree as ET
from nif_refs import Nif, Cursor

PREFIX=re.compile(r'^hdtSSEPhysics_AutoRename_Head_[0-9A-Fa-f]{8} (.+)$')

def nodes(model):
    result={}
    for i,(kind,b) in enumerate(zip(model.indices,model.blocks)):
        if model.types[kind] not in ('NiNode','BSFadeNode'):continue
        q=Cursor(b);key=q.unpack('I');name='' if key==0xffffffff else model.strings[key].decode('utf-8')
        extras=[q.unpack('i') for _ in range(q.unpack('I'))];controller=q.unpack('i');flags=q.unpack('I');transform=q.unpack('13f');collision=q.unpack('i');child_at=q.pos
        children=[q.unpack('i') for _ in range(q.unpack('I'))];effects=[q.unpack('i') for _ in range(q.unpack('I'))]
        if q.pos!=len(b) or not all(math.isfinite(v) for v in transform):raise ValueError('Head physics node framing unsupported')
        result[i]=dict(name=name,extras=extras,controller=controller,flags=flags,transform=transform,collision=collision,children=children,effects=effects,child_at=child_at)
    return result

def root(model):
    q=Cursor(model.footer)
    if q.unpack('I')!=1:raise ValueError('SMP head requires one root')
    return q.unpack('i')

def physics_inputs(plan, output, normalize):
    found=[]
    for row in plan.get('headPhysicsModels') or []:
        ref=normalize(row['reference'],'exact');p=output/'assets'/ref
        model=Nif(p.read_bytes());links=[v for v in model.references() if v['field']=='HDT Skinned Mesh Physics Object']
        if not links:continue
        ns=nodes(model);r=root(model)
        if r not in ns:raise ValueError('SMP head-part root node is unsupported')
        called=[v for v in links if v['block'] in ns[r]['extras']]
        if not called:raise ValueError('Selected SMP head-part XML is not on its supported root node')
        if called:found.append(dict(row=row,reference=ref,model=model,nodes=ns,root=r,xml=normalize(called[0]['value'],'BODYTRI')))
        if len(called)>1:raise ValueError('Multiple SMP XML links on one head part')
    refs={v['xml'] for v in found}
    if len(refs)>1:raise ValueError('Multiple distinct head-part SMP XMLs need a supported composition; no file substituted')
    return found

def attach(model, inputs, xml_raw, xml_path):
    if not inputs:return dict(required=False)
    original=model.encode();before=Nif(original);ns=nodes(model);head_nodes={n['name']:i for i,n in ns.items() if n['name']}
    if len(head_nodes)!=sum(bool(n['name']) for n in ns.values()):raise ValueError('Duplicate finished head bone names')
    source=inputs[0];src_nodes=source['nodes'];by_name={n['name']:i for i,n in src_nodes.items() if n['name']}
    if len(by_name)!=sum(bool(n['name']) for n in src_nodes.values()):raise ValueError('Ambiguous installed SMP node names')
    source_names=set(by_name);renames=[]
    for name,index in list(head_nodes.items()):
        match=PREFIX.fullmatch(name)
        if not match:continue
        target=match[1]
        if target not in source_names or target in head_nodes:raise ValueError('SMP exported bone identity cannot be bound uniquely: '+name)
        raw=model.blocks[index];model.blocks[index]=struct.pack('<I',len(model.strings))+raw[4:];model.strings.append(target.encode('utf-8'));renames.append(dict(block=index,original=name,replacement=target));del head_nodes[name];head_nodes[target]=index
    xml=ET.fromstring(xml_raw);declared={e.get('name'):e for e in xml.iter('bone') if e.get('name')}
    constraints=[e for e in xml.iter() if e.tag.endswith('constraint') and e.get('bodyA') and e.get('bodyB')]
    needed={e.get(k) for e in constraints for k in ('bodyA','bodyB')}
    parents={}
    for i,n in src_nodes.items():
        for child in n['children']:
            if child in src_nodes:
                if child in parents:raise ValueError('Installed SMP bone has multiple parents')
                parents[child]=i
    additions=[]
    def add(name, chain=()):
        if name in head_nodes:return head_nodes[name]
        if name in chain or len(chain)>64:raise ValueError('SMP anchor chain cycle/depth')
        i=by_name[name];n=src_nodes[i];decl=declared.get(name)
        # Recover only original, explicitly kinematic anchors; no dynamic or
        # weighted substitute, generated transform or controller is invented.
        # Empty anchor declarations precede all bone-defaults and use SMP's
        # zero-mass initial BoneTemplate. Named/inherited templates stay closed.
        mass=decl.findtext('mass') if decl is not None else None
        empty_initial=decl is not None and not list(decl) and not decl.get('template') and list(xml).index(decl)<next((i for i,e in enumerate(xml) if e.tag=='bone-default'),len(xml))
        if decl is None or not (empty_initial or mass is not None and float(mass)==0) or n['controller']!=-1 or n['collision']!=-1 or n['extras'] or n['effects']:
            raise ValueError('Required missing SMP anchor is not a supported original kinematic node: '+name)
        parent=parents.get(i)
        if parent is None:raise ValueError('Required SMP anchor has no source parent: '+name)
        parent_name=src_nodes[parent]['name'];parent_index=add(parent_name,chain+(name,)) if parent_name not in head_nodes else head_nodes[parent_name]
        raw=struct.pack('<IIiI13fiII',len(model.strings),0,-1,n['flags'],*n['transform'],-1,0,0);model.strings.append(name.encode('utf-8'));index=model.append_block('NiNode',raw);head_nodes[name]=index
        pn=nodes(model)[parent_index];block=model.blocks[parent_index];at=pn['child_at'];count=len(pn['children']);model.blocks[parent_index]=block[:at]+struct.pack('<I',count+1)+block[at+4:at+4+count*4]+struct.pack('<i',index)+block[at+4+count*4:]
        additions.append(dict(name=name,sourceBlock=i,parent=parent_name,block=index,transform=list(n['transform'])));return index
    # Missing branches with no selected weighted node are source-authored
    # dormant declarations, like the existing backpack's absent lantern chain.
    weighted={PREFIX.sub(r'\1',n) for n in model.skin_bones()};needed_missing=needed-set(head_nodes)
    for name in sorted(needed_missing & source_names):add(name)
    missing=needed-set(head_nodes);touches=[dict(e.attrib) for e in constraints if (e.get('bodyA') in missing and e.get('bodyB') in weighted) or (e.get('bodyB') in missing and e.get('bodyA') in weighted)]
    if touches:raise ValueError('Required SMP constraint cannot bind a selected weighted bone: '+json.dumps(touches))
    r=root(model)
    if model.types[model.indices[r]]!='BSFadeNode':raise ValueError('Finished SMP head root is not BSFadeNode')
    existing=[e for e in model.references() if e['field']=='HDT Skinned Mesh Physics Object']
    if existing and any(e['value'].replace('\\','/').lower()!=xml_path.replace('\\','/').lower() for e in existing):raise ValueError('Finished head already calls conflicting SMP XML')
    if not existing:
        name_index=len(model.strings);model.strings.extend([b'HDT Skinned Mesh Physics Object',xml_path.encode('utf-8')]);extra=model.append_block('NiStringExtraData',struct.pack('<II',name_index,name_index+1));block=model.blocks[r];n=nodes(model)[r];count=len(n['extras']);model.blocks[r]=block[:4]+struct.pack('<I',count+1)+block[8:8+count*4]+struct.pack('<i',extra)+block[8+count*4:]
    # Existing opaque blocks, including every vertex, shader, skin/partition,
    # controller and transform, must remain byte-identical. Only NiNode name,
    # child links, and root extra-data list may differ.
    edited={v['block'] for v in renames}|{r}|{head_nodes[v['parent']] for v in additions}
    after=Nif(model.encode())
    for i,(a,b) in enumerate(zip(before.blocks,after.blocks)):
        if i not in edited and a!=b:raise ValueError('SMP changed protected original block '+str(i))
    for i in edited:
        if i>=len(before.blocks):continue
        a,b=nodes(before)[i],nodes(after)[i]
        for key in ('controller','flags','transform','collision','effects'):
            if a[key]!=b[key]:raise ValueError('SMP changed original node '+key)
        added_children=[v['block'] for v in additions if head_nodes[v['parent']]==i]
        if b['children']!=a['children']+added_children:raise ValueError('SMP changed existing children')
        if i!=r and b['extras']!=a['extras']:raise ValueError('SMP changed existing extra data')
        if i==r and b['extras'][:len(a['extras'])]!=a['extras']:raise ValueError('SMP changed root extra data')
    if after.strings[:len(before.strings)]!=before.strings:raise ValueError('SMP changed original strings')
    return dict(required=True,xml=xml_path,xmlInputSha256=hashlib.sha256(xml_raw).hexdigest().upper(),renamedBones=renames,originalKinematicAnchors=additions,dormantUnweightedMissingBones=sorted(missing),protectedOriginalBlocks=len(before.blocks)-len(edited),geometryWeightsShadersAndOriginalTransformsPreserved=True,runtimeVerified=False)
