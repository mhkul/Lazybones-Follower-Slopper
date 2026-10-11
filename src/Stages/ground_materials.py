"""Prove effective emitted ARMO alternate textures for existing NIF slots.

Only applies an override when every call to the model binds the same exact
shape/index and texture slot. Does not infer winners for unused default files.
"""
import struct
from pathlib import Path
from validate_binary import fields
from nif_refs import Nif,Cursor


def effective_overrides(reference,nif_path,calls,mod):
    calls=[c for c in calls if c['reference'].lower()==reference.lower()]
    n=Nif(nif_path if isinstance(nif_path,bytes) else Path(nif_path).read_bytes());shapes=[]
    for i,k in enumerate(n.indices):
        kind=n.types[k]
        if kind not in ('BSTriShape','BSDynamicTriShape','BSSubIndexTriShape'):continue
        r=Cursor(n.blocks[i]);name=n.strings[r.unpack('I')].decode()
        for _ in range(r.unpack('I')):r.unpack('i')
        r.unpack('iI13fi4f');skin,shader,alpha=r.unpack('iii')
        if n.types[n.indices[shader]]!='BSLightingShaderProperty':raise ValueError('Unsupported ground shader')
        s=Cursor(n.blocks[shader]);s.unpack('II')
        for _ in range(s.unpack('I')):s.unpack('i')
        s.unpack('iII4f');texture=s.unpack('i')
        if n.types[n.indices[texture]]!='BSShaderTextureSet':raise ValueError('Ground texture link invalid')
        shapes.append(dict(index=len(shapes),name=name,block=i,shader=shader,texture=texture))
    sets={r['id']:r for r in mod['records'] if r['tag']=='TXST'}
    maps=[];proof=[]
    for call in calls:
        mapping={}
        for alt in call['alternateTextures']:
            matches=[s for s in shapes if s['name']==alt['shape']]
            if matches:
                shape=matches[0]
                if len(matches)!=1 or shape['index']!=alt['index']:raise ValueError('Alternate texture shape/index mismatch')
            else:
                # Preserve the captured texture/index intent while repairing a
                # stale shape name against the definitive selected source model.
                # The successor plugin must emit this actual name, never leave
                # a fallback mapping that the engine could interpret differently.
                if not 0<=alt['index']<len(shapes):raise ValueError('Alternate texture index outside selected model')
                shape=shapes[alt['index']]
            record=sets.get(alt['textureSet'])
            if record is None:raise ValueError('Ground alternate TXST not in emitted private plugin')
            # Diffuse and normal signatures map directly to texture slots 0/1.
            # Other slots are still covered by emitted TXST/package validation.
            for tag,value in fields(record):
                if tag not in ('TX00','TX01'):continue
                key=(shape['texture'],int(tag[-1]));path='textures/'+value.rstrip(b'\0').decode().replace('\\','/').removeprefix('textures/')
                if key in mapping and mapping[key]!=path:raise ValueError('Shared shader receives incompatible overrides')
                mapping[key]=path
                proof.append(dict(recordId=call['recordId'],field=call['field'],shape=shape,originalShapeName=alt['shape'],shapeNameCorrection=alt['shape']!=shape['name'],textureSet=record['id'],signature=tag,slot=key[1],effectivePrivateReference=path))
        maps.append(mapping)
    common={}
    for edge in n.references():
        key=(edge['block'],edge.get('slot'))
        if edge['field']=='texture' and maps and all(key in m for m in maps) and len({m[key] for m in maps})==1:
            common[key]=maps[0][key]
    return common,proof
