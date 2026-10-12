"""Compact export-plan adapter to the established T07 private NIF editor.

Produces a partial private head stage, never a follower/package. No historical
capture receipts, source maps or completed follower assets are accepted.
"""
from pathlib import Path
import hashlib, json, os, struct, sys, time
sys.dont_write_bytecode = True
PROJECT = Path(__file__).resolve().parents[2]
_runtime_lease = open(PROJECT / 'runtime-use.lock', 'rb') if (PROJECT / 'runtime-use.lock').is_file() else None
_storage_lease = None
sys.path.insert(0, str(PROJECT / 'prototypes/T07'))
sys.path.insert(0, str(PROJECT / 'prototypes/T02A'))
from nif_refs import Nif, Cursor, prove_only_references_changed
from head_validate import head
from follower_names import names
from head_physics import physics_inputs, attach as attach_physics
from head_bind_frames import restore as restore_bind_frames

def bind_identities(model, bindings):
    """Retain source HDPT names when the original race still supplies defaults.

    The Foundation proves the one dynamic source/export packed template and
    topology correspondence. Recheck that exact exported payload here; edit
    only its name reference, preserving every vertex, weight and material.
    """
    before=Nif(model.encode()); changes=[]
    for binding in bindings:
        if not binding.get('retainSourceIdentity'):continue
        old=binding['exportedShapeName'];new=binding['editorId']
        matches=[]
        for i,kind in enumerate(model.indices):
            if model.types[kind]!='BSDynamicTriShape':continue
            name=struct.unpack_from('<I',model.blocks[i])[0]
            if model.strings[name].decode('utf-8')==old:matches.append(i)
        if len(matches)!=1 or new==old:raise ValueError('Retained HDPT export identity is absent/ambiguous: '+old)
        if any(model.strings[struct.unpack_from('<I',b)[0]].decode('utf-8')==new for k,b in zip(model.indices,model.blocks) if model.types[k] in ('BSDynamicTriShape','BSTriShape','NiTriShape','BSSubIndexTriShape')):
            raise ValueError('Retained HDPT export identity collides: '+new)
        i=matches[0];q=Cursor(model.blocks[i]);q.unpack('I');q.take(q.unpack('I')*4);q.take(4+4+52+4+16);skin=q.unpack('i');q.take(8);descriptor=q.unpack('Q');q.take(4);inline=q.unpack('I')
        if inline!=0 or skin<0 or model.types[model.indices[skin]] not in ('NiSkinInstance','BSDismemberSkinInstance'):raise ValueError('Retained HDPT template framing changed')
        partition=struct.unpack_from('<i',model.blocks[skin],4)[0]
        if partition<0 or model.types[model.indices[partition]]!='NiSkinPartition':raise ValueError('Retained HDPT partition absent')
        p=Cursor(model.blocks[partition]);p.unpack('I');size=p.unpack('I');stride=p.unpack('I');desc=p.unpack('Q')
        if not stride or descriptor!=desc or stride!=(desc&15)*4 or size%stride:raise ValueError('Retained HDPT packed framing changed')
        packed=hashlib.sha256(p.take(size)).hexdigest().upper();topology=hashlib.sha256(p.take(len(p.data)-p.pos)).hexdigest().upper()
        if (packed,topology)!=(binding['packedSha256'],binding['topologySha256']):raise ValueError('Retained HDPT packed-template/topology proof differs')
        model.blocks[i]=struct.pack('<I',len(model.strings))+model.blocks[i][4:];model.strings.append(new.encode('utf-8'))
        changes.append(dict(block=i,canonical=binding['canonical'],original=old,replacement=new,packedSha256=packed,topologySha256=topology))
    changed={x['block'] for x in changes}
    for i,(a,b) in enumerate(zip(before.blocks,model.blocks)):
        differs=a[4:]!=b[4:] if i in changed else a!=b
        if differs:raise ValueError('Retained HDPT identity changed protected geometry/material block')
    if model.strings[:len(before.strings)]!=before.strings:raise ValueError('Retained HDPT identity changed original strings')
    return dict(changes=changes,onlyShapeNameReferencesChanged=True)

def norm(value, field='texture'):
    if field == 'texture':
        from nif_refs import texture_reference
        value = texture_reference(value)
    value = value.replace('\\', '/').strip()
    if value.startswith('/'): raise ValueError('Rooted NIF reference')
    while '//' in value: value=value.replace('//','/')
    if value.lower().startswith('data/'): value = value[5:]
    if field == 'texture' and not value.lower().startswith(('textures/', 'skse/')): value = 'textures/' + value
    if field == 'BODYTRI' and not value.lower().startswith('meshes/'): value = 'meshes/' + value
    if ':' in value or any(p in ('', '.', '..') for p in value.split('/')): raise ValueError('Unsafe NIF reference')
    return value.lower()

def run(plan_path):
    started = time.monotonic()
    plan_path = Path(plan_path).resolve(); output = plan_path.parent
    global _storage_lease
    _storage_lease = open(output / 'storage.lock', 'rb') if (output / 'storage.lock').is_file() else None
    plan = json.loads(plan_path.read_text(encoding='utf-8-sig'))
    if plan['schema'] != 'lean-export-input-plan/v1': raise ValueError('Unsupported compact plan')
    owner = json.loads((output / 'owned-output.json').read_text(encoding='utf-8-sig'))
    if owner['kind'] != 'T09B lean export build': raise ValueError('Unowned output')
    def check():
        if (output / 'stop').exists(): raise InterruptedError('Owned build cancelled')
    def owned(path):
        path = Path(path)
        if os.path.commonpath([output, path.resolve()]) != str(output): raise ValueError('Path escapes owned output')
        current = path
        while current != output:
            if current.is_symlink() or (hasattr(current, 'is_junction') and current.is_junction()): raise ValueError('Owned path is a link')
            current = current.parent
        return path
    check()
    sources = {norm(s['reference'], 'exact'): s for s in plan['sources']}
    missing = {norm(r['reference'], 'exact'):r for r in plan.get('missingReferences') or []}
    head_ref = 'meshes/lean-export/head.nif'
    original_path = owned(output / 'assets' / head_ref)
    original = original_path.read_bytes()
    if hashlib.sha256(original).hexdigest().upper() != sources[head_ref]['sha256']: raise ValueError('Staged head changed')
    model = Nif(original)
    # Validate dynamic arrays and selected links before the private edit. The
    # helper does not decode all topology/skin semantics or establish appearance.
    before = head(original_path)
    namespace = names(plan, owner)['namespace']
    tint_ref = norm(plan['appearance']['tintReference'], 'exact')
    refs = {norm(r['value'], r['field']) for r in model.references() if r['value']}
    physics = physics_inputs(plan, output, norm)
    refs.update(p['xml'] for p in physics)
    if tint_ref not in refs: raise ValueError('Composed tint is not called')
    mapping = {}
    choices = {r['reference']: r for r in plan.get('assetChoices', []) if r['layer'] == 'shared'}
    for ref in refs:
        if ref in missing: mapping[ref]=ref; continue
        if ref not in sources: raise ValueError('Head dependency unresolved: ' + ref)
        if ref == tint_ref: target = 'textures/' + namespace + '/exports/tint.dds'
        elif choices.get(ref, {}).get('mode') == 'Retain dependency': target = ref
        else:
            category, tail = ref.split('/', 1)
            if category not in ('meshes', 'textures'): raise ValueError('Unsupported private head dependency: ' + ref)
            target = category + '/' + namespace + '/' + tail
        mapping[ref] = target
    # Filesystem/manifest keys use '/', but serialized Skyrim resources use '\\'.
    changes = model.rewrite(lambda r: r['value'] if not r['value'] or norm(r['value'],r['field']) in missing else mapping[norm(r['value'], r['field'])].replace('/', '\\'))
    encoded = model.encode(); proof = prove_only_references_changed(original, encoded, changes)
    physics_proof = dict(required=False)
    if physics:
        ref=physics[0]['xml'];raw=owned(output/'assets'/sources[ref]['reference']).read_bytes()
        if hashlib.sha256(raw).hexdigest().upper()!=sources[ref]['sha256']:raise ValueError('Staged SMP XML changed')
        physics_proof=attach_physics(model,physics,raw,mapping[ref].replace('/','\\'));encoded=model.encode()
    bind_frames=dict(required=False,runtimeVerified=False)
    if physics and plan.get('followerSettings',{}).get('values',{}).get('raceMode','private')=='existing':
        skeleton_sources=[s for s in plan['sources'] if s.get('consumer')=='winning selected-sex race skeleton']
        if len(skeleton_sources)!=1:raise ValueError('Retained SMP requires one captured winning race skeleton')
        skeleton_source=skeleton_sources[0];skeleton_raw=owned(output/'assets'/skeleton_source['reference']).read_bytes()
        if hashlib.sha256(skeleton_raw).hexdigest().upper()!=skeleton_source['sha256']:raise ValueError('Staged retained race skeleton changed')
        bind_frames=restore_bind_frames(model,physics,Nif(skeleton_raw));encoded=model.encode()
    identity_proof=bind_identities(model,plan.get('headIdentityBindings') or []);encoded=model.encode()
    shape_names=[model.strings[struct.unpack_from('<I',b)[0]].decode('utf-8') for k,b in zip(model.indices,model.blocks) if model.types[k] in ('BSDynamicTriShape','BSTriShape','NiTriShape','BSSubIndexTriShape')]
    for part in plan.get('retainedHeadClosure') or []:
        if part['model'] and shape_names.count(part['outputShapeName'])!=1:raise ValueError('Retained race pregen name absent/ambiguous: '+part['outputShapeName'])
    target_ref = 'meshes/' + namespace + '/exports/head.nif'
    target_head = owned(output / 'private-head' / target_ref)
    target_head.parent.mkdir(parents=True, exist_ok=True)
    with target_head.open('xb') as stream: stream.write(encoded)
    after = head(target_head)
    signature = lambda h: [(s['name'], s['vertices'], s['positionHash'], s['packedHash'], s['dynamicHash'], s['transform']) for s in h['shapes']]
    aliases={x['original']:x['replacement'] for x in identity_proof['changes']}
    if [(aliases.get(s[0],s[0]),*s[1:]) for s in signature(before)] != signature(after): raise ValueError('Private head geometry changed')
    read_bytes = len(original) * 2 + len(encoded) + plan_path.stat().st_size + (output / 'owned-output.json').stat().st_size
    moved = []
    for ref, target in sorted(mapping.items()):
        if ref in missing:continue
        check(); source = sources[ref]; staged = owned(output / 'assets' / ref)
        with staged.open('rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest().upper()
        read_bytes += staged.stat().st_size
        if staged.stat().st_size != source['bytes'] or digest != source['sha256']: raise ValueError('Staged dependency changed: ' + ref)
        if choices.get(ref, {}).get('mode') == 'Retain dependency': continue
        dest = owned(output / 'private-head' / target); dest.parent.mkdir(parents=True, exist_ok=True)
        if dest.exists(): raise ValueError('Private head destination collision')
        # Owned-stage rename avoids a second payload copy; original backing
        # inputs are never opened for writing or moved.
        os.rename(staged, dest)
        moved.append(dict(reference=ref, destination=target, bytes=source['bytes'], sha256=digest))
    result = dict(status='partial-private-head', followerComplete=False, packages=[],
                  head=target_ref, headSha256=hashlib.sha256(encoded).hexdigest().upper(), headBytes=len(encoded), tint=mapping[tint_ref], assets=moved, referenceProof=proof,
                  dynamicShapes=len(after['shapes']), geometryPreserved=True,
                  headPhysics=physics_proof,
                  retainedSmpBindFrames=bind_frames,
                  headIdentity=identity_proof,
                  bytesRead=read_bytes, generatedBytes=len(encoded), copiedBytes=0,
                  filesRead=len(mapping)+4, elapsedMilliseconds=int((time.monotonic()-started)*1000),
                  limits=after['limits'], finalFaceGenIdentityAssigned=False)
    with (output / 'private-head-result.json').open('x', encoding='utf-8') as stream: json.dump(result, stream, indent=2)

if __name__ == '__main__': run(sys.argv[1])
