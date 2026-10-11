"""Independent TES4/GRUP/subrecord reader: no Mutagen or writer data structures."""
from pathlib import Path
import hashlib,json,struct,sys,zlib
P=Path(__file__).resolve().parents[2]
def pin(p):
 p=Path(p);return dict(path=str(p.resolve()),bytes=p.stat().st_size,sha256=hashlib.file_digest(p.open('rb'),'sha256').hexdigest().upper())
def fields(rec):
 b=rec['payload']
 if rec['flags']&0x40000:
  n=struct.unpack_from('<I',b)[0];b=zlib.decompress(b[4:]);assert len(b)==n
 result=[];p=0;extended=None
 while p<len(b):
  assert p+6<=len(b);tag=b[p:p+4].decode('ascii');n=struct.unpack_from('<H',b,p+4)[0];p+=6
  if tag=='XXXX':assert n==4;extended=struct.unpack_from('<I',b,p)[0];p+=n;continue
  if extended is not None:n=extended;extended=None
  assert p+n<=len(b);result.append((tag,b[p:p+n]));p+=n
 assert extended is None
 return result
def read(path):
 data=Path(path).read_bytes();records=[];groups=[]
 def walk(start,end,parents=()):
  p=start
  while p<end:
   assert p+24<=end;tag=data[p:p+4].decode('ascii');n,flags,fid=struct.unpack_from('<III',data,p+4)
   if tag=='GRUP':
    assert n>=24 and p+n<=end
    label=data[p+8:p+12];kind=struct.unpack_from('<I',data,p+12)[0];entry=dict(type=kind,label=label.decode('ascii') if kind==0 else struct.unpack('<I',label)[0]);groups.append(entry);walk(p+24,p+n,parents+(entry,));p+=n
   else:
    assert p+24+n<=end;records.append(dict(tag=tag,id=fid,flags=flags,offset=p,payload=data[p+24:p+24+n],groups=parents));p+=24+n
  assert p==end
 walk(0,len(data));assert records[0]['tag']=='TES4'
 masters=[v.rstrip(b'\0').decode() for t,v in fields(records[0]) if t=='MAST']
 return dict(records=records,groups=groups,masters=masters)
def one(r,t):
 vals=[v for tag,v in fields(r) if tag==t];assert len(vals)==1,(r['tag'],hex(r['id']),t,len(vals));return vals[0]
def uint(b):assert len(b)==4;return struct.unpack('<I',b)[0]

def validate(pluginpath,output):
 mod=read(pluginpath);masters=mod['masters'];plugin=Path(pluginpath).name
 assert masters==['Skyrim.esm','ccbgssse041-netchleather.esl'],masters
 assert mod['records'][0]['flags']&0x200
 version,count,nextid=struct.unpack('<fII',one(mod['records'][0],'HEDR'))
 assert version==struct.unpack('<f',struct.pack('<f',1.7))[0]
 # HEDR count includes both records (excluding TES4) and groups in Bethesda plugins.
 assert count==len(mod['records'])-1+len(mod['groups']),(count,len(mod['records']),len(mod['groups']))
 def canonical(raw):
  if raw==0:return None
  index=raw>>24;assert index<=len(masters),hex(raw)
  return (masters+[plugin])[index],raw&0xFFFFFF
 records={canonical(r['id']):r for r in mod['records'][1:]};assert len(records)==len(mod['records'])-1
 local=lambda i:(plugin,i)
 localids=sorted(i for name,i in records if name==plugin)
 registry=json.loads((P/'prototypes/T08/identity-registry.json').read_text())
 assert localids==sorted(registry['localIds']) and all(0x800<=i<=0xFFF for i in localids)
 cell=records[('Skyrim.esm',0x16DF2)];assert cell['flags']==0x4000
 assert [t for t,v in fields(cell)]==['EDID'];assert len(fields(cell))==1
 originpath=Path(__file__).resolve().parent / 'historical-input-required' / 'Skyrim.esm';origin=read(originpath)
 original=next(r for r in origin['records'] if r['tag']=='CELL' and r['id']==0x16DF2)
 assert one(cell,'EDID')==one(original,'EDID')
 assert cell['groups']==original['groups'],(cell['groups'],original['groups'])
 npc=records[local(0x800)];race=records[local(0x802)];skin=records[local(0x803)];outfit=records[local(0x804)];actor=records[local(0x801)]
 for t,i in [('RNAM',0x802),('WNAM',0x803),('DOFT',0x804)]:assert canonical(uint(one(npc,t)))==local(i)
 assert not any(t=='VMAD' for t,v in fields(npc))
 assert canonical(uint(one(skin,'RNAM')))==local(0x802)
 assert canonical(uint(one(actor,'NAME')))==local(0x800)
 assert actor['flags']&0x400
 assert actor['groups'][:3]==cell['groups']
 assert [(g['type'],canonical(g['label'])) for g in actor['groups'][3:]]==[(6,('Skyrim.esm',0x16DF2)),(8,('Skyrim.esm',0x16DF2))]
 transform=struct.unpack('<6f',one(actor,'DATA'));expected=struct.unpack('<6f',struct.pack('<6f',-3897.552734375,-1116.3406982421875,6656,.2665759325027466,0,4.975196361541748))
 assert transform==expected and struct.unpack('<f',one(actor,'XSCL'))[0]==struct.unpack('<f',struct.pack('<f',.95))[0]
 # Validate serialized ARMO armatures / ARMA RNAM+MODL, not just their FormID existence.
 eqbytes=one(outfit,'INAM');assert len(eqbytes)==24;equipment=[canonical(v[0]) for v in struct.iter_unpack('<I',eqbytes)]
 rows=[]
 for key in [local(0x803)]+equipment:
  ar=records[key];assert ar['tag']=='ARMO';armatures=[canonical(uint(v)) for t,v in fields(ar) if t=='MODL'];assert armatures
  flags=uint(next(v[:4] for t,v in fields(ar) if t in ('BODT','BOD2')))
  for arm in armatures:
   aa=records[arm];assert aa['tag']=='ARMA'
   assert canonical(uint(one(aa,'RNAM')))==('Skyrim.esm',0x13742)
   additional=[canonical(uint(v)) for t,v in fields(aa) if t=='MODL'];assert additional==[local(0x802)]
   model=one(aa,'MOD3').rstrip(b'\0').decode();assert model.startswith('autofollower\\')
   aflags=uint(next(v[:4] for t,v in fields(aa) if t in ('BODT','BOD2')))
   overlap=flags&aflags;assert overlap or (key==local(0x803) and arm==local(0x82E) and model.endswith('tng\\f_blank.nif'))
   rows.append(dict(armor=key,addon=arm,primaryRace=('Skyrim.esm',0x13742),additionalRaces=additional,sharedSlots=overlap,model=model))
 assert len(equipment)==6
 # Check every record/group's master index against its actual on-disk master table.
 assert all(canonical(r['id']) for r in mod['records'][1:])
 for g in mod['groups']:
  if g['type'] in (1,6,7,8,9,10):assert canonical(g['label'])==('Skyrim.esm',0x16DF2)
 # Confirm identity/FaceGen/follower fields stayed stable byte-for-byte after decoding FormIDs.
 old=read(P/'runs/T08/plugin-08'/plugin);oldindex={((old['masters']+[plugin])[r['id']>>24],r['id']&0xFFFFFF):r for r in old['records'][1:]}
 assert set(oldindex)==set(records)
 before=oldindex[local(0x800)]
 # Normalize exact scalar FormLink subrecords in this NPC; all other payloads (including factions) stayed byte-identical.
 formfields={'RNAM','VTCK','CNAM','ZNAM','WNAM','DOFT','HCLF','FTST','PNAM','PKID'}
 def normalized(r,table):return [(t,((table+[plugin])[uint(v)>>24],uint(v)&0xFFFFFF) if t in formfields else v.hex()) for t,v in fields(r)]
 assert normalized(before,old['masters'])==normalized(npc,masters),'NPC appearance/follower fields changed'
 result=dict(passed=True,reader='Independent Python raw TES4/GRUP/subrecords (no Mutagen)',plugin=pin(pluginpath),actualMasters=masters,records=len(records),groups=len(mod['groups']),headerCount=count,masterIndexEncodingValid=True,cellOrigin=pin(originpath),cellRecordOffset=original['offset'],cellContainer=dict(flags=hex(cell['flags']),subrecords=['EDID'],groups=cell['groups']),persistentGroups=actor['groups'],transform=transform,armorApplicability=rows,stableLocalIds=localids,npcFieldsEqualHistorical=True,additionalXEditFailure='No additional master-index/group encoding defect demonstrated in independent readback; exact user xEdit error unavailable. No xEdit/game launch claimed.',runtimeVerified=False)
 Path(output).write_text(json.dumps(result,indent=2),encoding='utf8');print(json.dumps(dict(passed=True,records=len(records),groups=len(mod['groups']),armorBindings=len(rows),noLux=True)))

if __name__ == '__main__':
    raise SystemExit('Historical diagnostic CLI is disabled. Use the application build stages.')
