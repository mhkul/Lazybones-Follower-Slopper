"""Reject missing included model/TXST/NIF assets from actual Data ZIP bytes.

Host race-system fields require a separate exact evidence ledger; they never
waive a missing ground model. Explicitly unresolved inherited host fields limit
complete-reference acceptance even when included equipment closure passes.
"""
from pathlib import Path
import argparse,hashlib,json,re,sys,zipfile
sys.dont_write_bytecode=True
P=Path(__file__).resolve().parents[2];sys.path.insert(0,str(P/'prototypes/T07'))
from nif_refs import Nif
from validate_binary import fields
from ground_records import read_bytes,ground_calls
from ground_materials import effective_overrides
from package_content import audit


def normalize(value):
    value=value.replace('\\','/').lower()
    if value.startswith('/') or ':' in value or any(q in ('','..','.') for q in value.split('/')):
        raise ValueError('Unsafe asset reference '+value)
    return value


def validate_members(data,plugin,uuid,variant,host_resources=(),retained=(),retained_data=None,*,require_compatibility=False,missing_references=()):
    content=audit(list(data),plugin,uuid,variant)
    # D115 scoped INIs are checked against actual generated record/XML bytes.
    # Historical packages without these files retain their historical audit path.
    compatibility=None
    if require_compatibility or any(n.endswith('_RSV_DISTR.ini') or n.startswith('OBodyNG_PDA_') for n in data):
        sys.path.append(str(P/'prototypes/T09'))
        from output_compatibility import validate as validate_compatibility
        compatibility=validate_compatibility(data,plugin,uuid,variant)
    members={normalize(k):k for k in data}
    if len(members)!=len(data):raise ValueError('Case-colliding members')
    external={};external_bytes={normalize(k):v for k,v in (retained_data or {}).items()}
    for row in retained:
        ref=normalize(row['reference']);s=row.get('authority',{})
        if ref in external or ref in members:raise ValueError('Conflicting shared retained asset: '+ref)
        if normalize(s.get('reference',''))!=ref or not s.get('backing') or not s.get('winnerBasis') or s.get('kind') not in ('MO2 read-only final loose backing','Selected Data read-only final loose backing','Physical/deployed final loose backing','Active BSA exact member'):
            raise ValueError('Retained provider evidence missing: '+ref)
        if s['kind']=='Active BSA exact member' and (normalize(s.get('archiveMember',''))!=ref or not s.get('activation')):raise ValueError('Retained archive activation/member missing: '+ref)
        identity=hashlib.sha256('\n'.join((str(Path(s['backing']).absolute()).lower(),(s.get('archiveMember') or '').lower(),s['sha256'],str(s['bytes']))).encode()).hexdigest().upper()
        if row.get('sourceIdentity')!=identity:raise ValueError('Retained source identity changed: '+ref+'; refresh the selection')
        raw=external_bytes.get(ref)
        if raw is None:raise ValueError('Missing retained provider bytes: '+ref+'; restore selected provider or refresh the selection')
        if len(raw)!=s['bytes'] or hashlib.sha256(raw).hexdigest().upper()!=s['sha256']:raise ValueError('Retained source identity changed: '+ref+'; refresh the selection')
        external[ref]=row
    if set(external_bytes)!=set(external):raise ValueError('Unselected retained provider bytes')
    missing={}
    for row in missing_references:
        ref=normalize(row['reference'])
        if ref in members or ref in external or ref in missing or not row.get('consumers') or not row.get('warning') or row.get('status')!='Unavailable in selected loose/active-archive view; original safe reference preserved':raise ValueError('Invalid missing-reference disposition '+ref)
        missing[ref]=row
    mod=read_bytes(data[plugin]);ledger=[];host={r['reference']:r for r in host_resources}
    # Primary model-bearing headparts must identify shapes in the preserved
    # FaceGen export. A copied custom HDPT may need its proved export identity.
    by_id={r['id']:r for r in mod['records']}
    face=next(raw for name,raw in data.items() if 'facegeom/' in normalize(name))
    face_model=Nif(face);shape_names=[]
    for index,kind in enumerate(face_model.indices):
        if face_model.types[kind] in ('BSTriShape','BSDynamicTriShape','BSSubIndexTriShape','NiTriShape','NiTriStrips'):
            shape_names.append(face_model.strings[int.from_bytes(face_model.blocks[index][:4],'little')].decode('utf-8'))
    for record in mod['records']:
        if record['tag']!='NPC_':continue
        for tag,raw in fields(record):
            if tag!='PNAM':continue
            part=by_id[int.from_bytes(raw,'little')];pf=list(fields(part));assert part['tag']=='HDPT'
            if any(t=='MODL' and value.lower().endswith(b'.nif\0') for t,value in pf):
                editor=next(value.rstrip(b'\0').decode() for t,value in pf if t=='EDID')
                if shape_names.count(editor)!=1:raise ValueError('Primary HDPT/export shape identity mismatch: '+editor)
    prefix=f'meshes/autofollower/{uuid}/'
    # The generated follower's stable local identity consumes these actual exports.
    for face in (f'meshes/actors/character/FaceGenData/FaceGeom/{plugin}/00000800.nif',
                 f'textures/actors/character/FaceGenData/FaceTint/{plugin}/00000800.dds'):
        if normalize(face) not in members:raise ValueError('Missing required FaceGen export: '+face)
    def require(origin,value,category,private=False):
        ref=normalize(value)
        if not ref.startswith(('meshes/','textures/','sound/')):ref=category+'/'+ref
        if ref in missing:
            ledger.append(dict(origin=origin,reference=ref,status='Missing; original reference preserved',evidence=missing[ref]));return
        if ref in external:
            ledger.append(dict(origin=origin,reference=ref,status='Retained dependency',provider=external[ref]['authority']))
        else:
            if private and not ref.startswith(prefix):raise ValueError('Ground model not isolated or explicitly retained: '+origin+' -> '+ref)
            if ref not in members:raise ValueError('Missing required asset: '+origin+' -> '+ref)
            ledger.append(dict(origin=origin,reference=ref,member=members[ref],status='Packaged'))
    calls=ground_calls(mod)
    for call in calls:
        require(f"ARMO {call['recordId']:08X}.{call['field']}",call['reference'],'meshes',True)
        for alt in call['alternateTextures']:
            if not any(r['tag']=='TXST' and r['id']==alt['textureSet'] for r in mod['records']):
                raise ValueError('Ground alternate TXST absent from plugin')
    material_bindings=[];unavailable_materials=[]
    for ref in sorted({c['reference'].replace('\\','/').lower() for c in calls}):
        if ref in missing:
            unavailable_materials.append(dict(reference=ref,status='Material/shape comparison unavailable because the called mesh is missing',evidence=missing[ref]));continue
        _,proof=effective_overrides(ref,external_bytes[ref] if ref in external else data[members[ref]],calls,mod)
        if any(p['shapeNameCorrection'] for p in proof):raise ValueError('Ground alternate texture shape does not match packaged NIF: '+ref)
        material_bindings.extend(proof)
    # Signature meanings are interpreted by record type; ARMO MODL is a FormID.
    host_fields=[]
    for rec in mod['records']:
        for tag,value in fields(rec):
            origin=f"{rec['tag']} {rec['id']:08X}.{tag}"
            if rec['tag']=='TXST' and re.fullmatch(r'TX\d\d',tag):
                require(origin,value.rstrip(b'\0').decode(),'textures')
            elif rec['tag']=='SNDR' and tag=='ANAM':
                sound=value.rstrip(b'\0').decode().replace('\\','/')
                if sound.lower().startswith('data/'):sound=sound[5:]
                require(origin,sound,'sound')
            elif rec['tag'] in ('ARMA','HDPT','WEAP','STAT') and tag in ('MODL','MOD2','MOD3','MOD4','MOD5'):
                if value.endswith(b'.nif\0'):
                    model=value[:-1].decode();require(origin,model,'meshes')
                    dnam=next((raw for key,raw in fields(rec) if key=='DNAM'),b'')
                    sex_index=3 if tag in ('MOD3','MOD5') else 2
                    if rec['tag']=='ARMA' and model.lower().endswith(('_0.nif','_1.nif')) and len(dnam)>sex_index and dnam[sex_index]&2:
                        require(origin+' implicit weight endpoint',model[:-6]+('_1.nif' if model.lower().endswith('_0.nif') else '_0.nif'),'meshes')
            elif rec['tag']=='HDPT' and tag=='NAM1' and value.endswith(b'.tri\0'):
                require(origin,value[:-1].decode(),'meshes')
            elif rec['tag']=='RACE' and tag=='MODL':
                ref='meshes/'+normalize(value.rstrip(b'\0').decode()).removeprefix('meshes/')
                if ref in members or ref in external:
                    require(origin,ref,'meshes')
                else:
                    evidence=host.get(ref)
                    if evidence is None:raise ValueError('Unclassified race-system field '+ref)
                    if not evidence.get('authority') and not evidence.get('inheritedVanillaField'):
                        raise ValueError('Host classification lacks provider or vanilla-record evidence '+ref)
                    host_fields.append(dict(origin=origin,reference=ref,evidence=evidence))
    for name,raw in sorted({**data,**external_bytes}.items()):
        if not name.lower().endswith('.nif'):continue
        for edge in Nif(raw).references():
            category='textures' if edge['field']=='texture' else 'meshes'
            ref=normalize(edge['value']);ref=ref if ref.startswith(('meshes/','textures/')) else category+'/'+ref
            if normalize(name) in external and ref not in external and ref not in missing:raise ValueError('Retained NIF has unselected external reference: '+name+' -> '+ref+'; retain this dependency or package the NIF')
            require(f"NIF {name} block {edge['block']}.{edge['field']}",edge['value'],category)
    return dict(passed=True,groundClosurePassed=True,groundCalls=calls,uniqueGroundModels=len({normalize(c['reference']) for c in calls}),
        contentAudit=content,referenceLedger=ledger,groundMaterialBindings=material_bindings,unavailableGroundMaterialChecks=unavailable_materials,hostSystemFields=host_fields,
        retainedReferences=sorted({r['reference'] for r in ledger if r['status']=='Retained dependency'}),
        missingReferences=[r for r in ledger if r['status']=='Missing; original reference preserved'],
        completeReferenceClosure=not any(r['status']=='Missing; original reference preserved' for r in ledger) and all(r['evidence']['status']=='Resolved' for r in host_fields),runtimeVerified=False,**({'compatibility':compatibility} if compatibility is not None else {}))


def validate_zip(path,variant,hosts=(),retained=(),retained_data=None):
    with zipfile.ZipFile(path) as z:
        if len(z.namelist())!=len(set(z.namelist())):raise ValueError('Duplicate ZIP member')
        data={n:z.read(n) for n in z.namelist()}
    plugins=[n for n in data if n.endswith('.esp')]
    if len(plugins)!=1:raise ValueError('Expected one follower ESPFE')
    plugin=plugins[0];legacy=re.fullmatch(r'AutoFollower_([0-9a-f]{32})\.esp',plugin,re.I)
    uuid=legacy.group(1).lower() if legacy else plugin.removesuffix('.esp').lower()
    return validate_members(data,plugin,uuid,variant,hosts,retained,retained_data)


if __name__=='__main__':
    a=argparse.ArgumentParser();a.add_argument('zip');a.add_argument('variant');a.add_argument('--hosts');a.add_argument('--output');args=a.parse_args()
    hosts=json.loads(Path(args.hosts).read_text(encoding='utf-8-sig')) if args.hosts else []
    try:
        result=validate_zip(args.zip,args.variant,hosts)
    except ValueError as ex:
        result=dict(passed=False,error=str(ex))
        if args.output:Path(args.output).write_text(json.dumps(result,indent=2))
        print(json.dumps(result));sys.exit(1)
    if args.output:Path(args.output).write_text(json.dumps(result,indent=2))
    print(json.dumps(dict(passed=True,groundCalls=len(result['groundCalls']),uniqueGroundModels=result['uniqueGroundModels'],completeReferenceClosure=result['completeReferenceClosure'])))
