"""Independent raw ESP/container readback for the resolved saved-placement contract."""
import math,struct
from ground_records import read_bytes
from validate_binary import fields

def validate(raw,plan):
    p=plan['placement'];t=plan['savedActor']['placement']
    if p['schema']!='lean-saved-placement/v1' or p['transform']!=t:raise ValueError('Placement plan/actor disagree')
    mod=read_bytes(raw);masters=mod['masters']
    def canonical(fid):
        slot=fid>>24
        if slot>=len(masters):raise ValueError('Location is not an originating-master override')
        return masters[slot]+'|'+format(fid&0xffffff,'06X')
    cells=[r for r in mod['records'] if r['tag']=='CELL'];worlds=[r for r in mod['records'] if r['tag']=='WRLD'];actors=[r for r in mod['records'] if r['tag']=='ACHR']
    if len(cells)!=1 or len(actors)!=1 or len(worlds)!=(0 if p['interior'] else 1):raise ValueError('Unexpected location/actor containers')
    cell=cells[0];actor=actors[0]
    if canonical(cell['id'])!=p['containerCell'] or cell['flags']!=(0x4000 if p['interior'] else 0x4400):raise ValueError('Partial persistent CELL identity/flags differ')
    for record,proof in [(cell,p['containerOrigin'])]+([(worlds[0],p['worldOrigin'])] if worlds else []):
        fs=fields(record);expected=[] if proof['editorId'] is None else [('EDID',proof['editorId'].encode()+b'\0')]
        if fs!=expected:raise ValueError('Unexpected host override fields in '+record['tag'])
    if not actor['flags']&0x400:raise ValueError('Placed actor is not persistent')
    if len([g for g in actor['groups'] if g['type']==8 and g['label']==cell['id']])!=1:raise ValueError('Actor persistent subgroup differs')
    if len([g for g in actor['groups'] if g['type']==6 and g['label']==cell['id']])!=1:raise ValueError('Actor cell-children group differs')
    if p['interior']:
        expected=[(0,'CELL'),(2,p['interiorBlock']),(3,p['interiorSubBlock'])]
    else:
        world=worlds[0]
        if canonical(world['id'])!=p['worldspace'] or world['flags']!=0x4000:raise ValueError('WRLD origin differs')
        expected=[(0,'WRLD'),(1,world['id'])]
        x,y=[math.floor(v/4096) for v in t['position'][:2]]
        if (x,y)!=(p['gridX'],p['gridY']) or (x//32,y//32,x//8,y//8)!=(p['blockX'],p['blockY'],p['subBlockX'],p['subBlockY']):raise ValueError('Physical exterior grid differs')
        # Persistent actors intentionally live in the top CELL, outside grid groups.
        if any(g['type'] in (4,5) for g in actor['groups']):raise ValueError('Persistent actor was emitted in physical exterior subcell')
    if [(g['type'],g['label']) for g in cell['groups']]!=expected:raise ValueError('Origin placement group labels differ')
    if not set(p['masters'])<=set(masters):raise ValueError('Required location master absent')
    data=[v for k,v in fields(actor) if k=='DATA']
    if data!=[struct.pack('<6f',*t['position'],*t['rotation'])]:raise ValueError('Saved full transform bits differ')
    return dict(passed=True,cell=t['cell'],worldspace=t['worldspace'],containerCell=p['containerCell'],position=t['position'],rotation=t['rotation'],groups=expected,locationMasters=p['masters'],minimalFields=True,runtimeAcceptance=False)
