"""Required included ARMO ground models and alternate-texture references.

Consumes the project's raw TES4 reader output. MODL on ARMO is an armature
FormID, not a filename; MOD2/MOD4 are the male/female inventory/ground models.
"""
import struct
from validate_binary import fields


def read_bytes(data):
    records=[];groups=[]
    def walk(start,end,parents=()):
        p=start
        while p<end:
            if p+24>end:raise ValueError('Truncated TES4 record/group')
            tag=data[p:p+4].decode('ascii');size,flags,fid=struct.unpack_from('<III',data,p+4)
            if tag=='GRUP':
                if size<24 or p+size>end:raise ValueError('Invalid group size')
                label=data[p+8:p+12];kind=struct.unpack_from('<I',data,p+12)[0]
                group=dict(type=kind,label=label.decode('ascii') if kind==0 else struct.unpack('<I',label)[0]);groups.append(group)
                walk(p+24,p+size,parents+(group,));p+=size
            else:
                if p+24+size>end:raise ValueError('Invalid record size')
                records.append(dict(tag=tag,id=fid,flags=flags,offset=p,payload=data[p+24:p+24+size],groups=parents));p+=24+size
        if p!=end:raise ValueError('Record boundary mismatch')
    walk(0,len(data))
    if not records or records[0]['tag']!='TES4':raise ValueError('Missing TES4 header')
    masters=[v.rstrip(b'\0').decode() for t,v in fields(records[0]) if t=='MAST']
    return dict(records=records,groups=groups,masters=masters)


def path(value):
    if not value or value[-1:] != b'\0' or b'\0' in value[:-1]:
        raise ValueError('Invalid terminated model path')
    return value[:-1].decode('utf-8')


def alternate_textures(value):
    count, = struct.unpack_from('<I', value)
    result = []; pos = 4
    for _ in range(count):
        size, = struct.unpack_from('<I', value, pos); pos += 4
        name = value[pos:pos+size].decode('utf-8'); pos += size
        form, index = struct.unpack_from('<II', value, pos); pos += 8
        result.append(dict(shape=name, textureSet=form, index=index))
    if pos != len(value):
        raise ValueError('Alternate texture payload has trailing bytes')
    return result


def ground_calls(mod):
    rows = []
    for rec in mod['records']:
        if rec['tag'] not in ('ARMO','AMMO','PROJ'):
            continue
        fs = fields(rec)
        for tag, value in fs:
            if tag not in (('MOD2','MOD4') if rec['tag']=='ARMO' else ('MODL',)):
                continue
            model = path(value)
            if not model:
                continue
            alt_tag = ('MO2S' if tag == 'MOD2' else 'MO4S') if rec['tag']=='ARMO' else 'MODS'
            alts = [a for t,v in fs if t == alt_tag for a in alternate_textures(v)]
            rows.append(dict(recordId=rec['id'], recordTag=rec['tag'], field=tag,
                reference='meshes/'+model.replace('\\','/').removeprefix('meshes/'),
                alternateTextures=alts, required=True))
    return rows
