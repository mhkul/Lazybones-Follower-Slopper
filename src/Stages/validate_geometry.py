"""Independent source/XML/OSD simulation and generated SSE NIF/PIRT validation."""
from pathlib import Path
import functools,hashlib,json,math,struct,sys,xml.etree.ElementTree as ET
from inventory import P,pin,verify
from prepare_build import NAMES
from nif_detail import Reader,detail

def f32(v):return struct.unpack('<f',struct.pack('<f',v))[0]
def truth(e,key):return e.get(key)=='true'
def native_path(s):return Path(s.replace('\\','/'))

@functools.lru_cache(None)
def diffs(path):
 r=Reader(Path(path).read_bytes());assert r.read(4)==b'\0DSO';version,count=r.get('II');assert version==1 and count<10000;result={}
 for _ in range(count):
  name=r.string('B');number=r.get('H');values={}
  for _ in range(number):
   i,x,y,z=r.get('Hfff');assert i not in values and all(math.isfinite(v) for v in (x,y,z));values[i]=(x,y,z)
  assert name and name not in result;result[name]=values
 assert r.i==len(r.b);return result

def tri(path):
 r=Reader(Path(path).read_bytes());assert r.read(4)==b'PIRT';result={}
 for kind in ('position','uv'):
  if r.i==len(r.b):break
  shape_count=r.get('H');assert shape_count<=1024;seen=set()
  for _ in range(shape_count):
   shape=r.string('B');assert shape and shape not in seen;seen.add(shape);number=r.get('H');assert number<=4096
   for _ in range(number):
    name=r.string('B');mult,n=r.get('fH');assert name and math.isfinite(mult) and mult>=0;key=(shape,kind,name);assert key not in result;values={}
    for _ in range(n):
     row=r.get('Hhhh' if kind=='position' else 'Hhh');assert row[0] not in values;values[row[0]]=tuple(f32(v*mult) for v in row[1:])
    result[key]=(mult,values)
 assert r.i==len(r.b);return result

def data_for(project,slider,target,root):
 rows=[d for d in slider.findall('Data') if d.get('target')==target]
 assert len(rows)<=1,'Multiple target datasets require explicit handling'
 if not rows:return None,{}
 d=rows[0];file,record=d.text.replace('\\','/').rsplit('/',1);folders=[project.findtext('DataFolder')]
 if not truth(d,'local'):
  shape=next(s for s in project.findall('Shape') if s.get('target')==target)
  folders=[shape.get('DataFolder') or project.findtext('DataFolder')]
 candidates=[root/'ShapeData'/native_path(folder)/native_path(file) for folder in folders]
 paths=[p for p in candidates if p.is_file()];assert len(paths)==1,(target,file,candidates)
 raw=diffs(str(paths[0]))[record]
 # DiffData.cpp LoadData clamps components below nifly::EPSILON (0.0001).
 effective={i:tuple(0.0 if abs(v)<.0001 else v for v in row) for i,row in raw.items()}
 return dict(file=pin(paths[0]),record=record,dataName=d.get('name'),rawDiffEntries=len(raw),epsilonClampedComponents=sum(0<abs(v)<.0001 for row in raw.values() for v in row)),effective

def defaults(project,values,choices):
 sliders=project.findall('Slider');defs={s.get('name'):{e:float(s.get(e,s.get('default','0'))) for e in ('small','big')} for s in sliders}
 for s in sliders:
  name=s.get('name')
  if truth(s,'zap') and not truth(s,'uv'):
   v=values.get((name,'big'),defs[name]['big'])/100
   if not truth(s,'hidden') and name in choices:v=float(choices[name])
   if v!=defs[name]['big']/100:
    for toggle in filter(None,s.get('zaptoggles','').split(';')):
     assert toggle in defs
     for e in ('small','big'):defs[toggle][e]=100-defs[toggle][e]
 return defs

def metrics(expected,actual):
 assert len(expected)==len(actual);d=[math.dist(a,b) for a,b in zip(expected,actual)]
 return dict(vertices=len(d),maximum=max(d,default=0),rms=math.sqrt(sum(v*v for v in d)/len(d)) if d else 0)

def shader_raw(d,s):
 result=[]
 for i in (s['shader'],s['alpha']):
  if i<0:continue
  typ,raw=d['blocks'][i];r=Reader(raw);shader_type=r.get('I') if typ=='BSLightingShaderProperty' else None
  key=r.get('I');name=d['strings'][key] if key!=0xffffffff else '';assert r.get('I')==0 and r.get('i')==-1,'Property extras/controller need explicit handling'
  if typ=='BSLightingShaderProperty':
   settings=r.read(24);texture=r.get('i');assert 0<=texture<len(d['blocks']) and d['blocks'][texture][0]=='BSShaderTextureSet'
   result.append((typ,shader_type,name,settings,d['blocks'][texture][1],r.read(len(raw)-r.i)))
  else:result.append((typ,name,r.read(len(raw)-r.i)))
 return result

def validate(build,output):
 out=P/'runs/T06'/output;out.mkdir(exist_ok=False);folder=P/'runs/T06'/build;info=json.loads((folder/'build-inputs.json').read_text());root=Path(info['stage'])/'projects'
 for row in info['provenance']:
  for key in ('source','retained','toolOriginal','toolCopy'):
   if key in row:verify(row[key])
 for key in ('source','retained'):verify(info['authorZapSelection'][key])
 projects={n.get('name'):n for n in ET.parse(root/'SliderSets/T06.osp').getroot()}
 presets={mode:{(s.get('name'),s.get('size')):float(s.get('value')) for s in ET.parse(root/'SliderPresets'/file).findall('.//SetSlider')} for mode,file in (('prebuilt','Sen Abersha.xml'),('obody','T06 Zeroed.xml'))}
 assert len(presets['prebuilt'])==239
 reports=[];range_rows=[];trireports=[];source_pins=[]
 for index,name in enumerate(NAMES):
  p=projects[name];source=root/'ShapeData'/native_path(p.findtext('DataFolder'))/native_path(p.findtext('SourceFile'));src=detail(source);source_pins.append(pin(source));source_shapes={s['name']:s for s in src['shapes']};shape_map={s.text:s.get('target') for s in p.findall('Shape')};choices={r['zap']:r['choice']=='true' for r in info['authorZapSelection']['choices'] if r['project']==name}
  bymode={};maps={};datasets={}
  for shapename,target in shape_map.items():
   datasets[shapename]={s.get('name'):data_for(p,s,target,root) for s in p.findall('Slider')}
  for mode in ('prebuilt','obody'):
   defs=defaults(p,presets[mode],choices);modefolder=Path(info['outputs'])/(mode+'-'+str(index));files=sorted(modefolder.rglob('*.nif'));assert len(files)==2
   bymode[mode]=[];maps[mode]={}
   for endpoint,filename in zip(('small','big'),files):
    assert filename.stem.endswith('_'+str(int(endpoint=='big')));generated=detail(filename);target_shapes={s['name']:s for s in generated['shapes']};expected_names=set();shape_reports=[]
    for shapename,target in shape_map.items():
     s=source_shapes[shapename];verts=[list(v) for v in s['vertices']];uvs=[list(v) for v in s['uvs']];zaps=set();effects=[]
     for slider in p.findall('Slider'):
      sl=slider.get('name');ref,ds=datasets[shapename][sl]
      if ref is None:continue
      value=presets[mode].get((sl,endpoint),defs[sl][endpoint]);coeff=f32(value/100)
      if truth(slider,'invert'):coeff=f32(1-coeff)
      if truth(slider,'clamp'):raise ValueError('Clamp must be independently implemented')
      inrange={i:v for i,v in ds.items() if i<s['count']};outside={i:v for i,v in ds.items() if i>=s['count']}
      if outside and mode=='prebuilt' and endpoint=='small':range_rows.append(dict(project=name,shape=shapename,slider=sl,sourceVertices=s['count'],data=ref,inRange=len(inrange),outOfRange=len(outside),maxIndex=max(ds),coefficients={e:presets[mode].get((sl,e),defs[sl][e])/100 for e in ('small','big')},semantics='Raw OSD retained; BodySlide 5.7.1 bounds skips absent vertices; generated positions/TRIs checked against bounded current project',kind='zap' if truth(slider,'zap') else 'uv' if truth(slider,'uv') else 'position'))
      if truth(slider,'zap') and not truth(slider,'uv'):
       active=presets[mode].get((sl,'big'),defs[sl]['big'])/100
       if not truth(slider,'hidden') and sl in choices:active=float(choices[sl])
       if active>0:zaps.update(i for i,v in ds.items() if any(abs(c)>0 for c in v))
      elif coeff!=0:
       array=uvs if truth(slider,'uv') else verts
       for i,v in inrange.items():
        for axis in range(2 if truth(slider,'uv') else 3):array[i][axis]=f32(array[i][axis]+f32(v[axis]*coeff))
       effects.append(dict(slider=sl,value=value,origin='XML' if (sl,endpoint) in presets[mode] else 'project-default',affectedVertices=sum(any(v) for v in inrange.values()),kind='uv' if truth(slider,'uv') else 'position'))
     valid_zaps={i for i in zaps if i<s['count']};keep=[i for i in range(s['count']) if i not in valid_zaps];maps[mode][shapename]=keep
     if not keep:
      assert shapename not in target_shapes;continue
     expected_names.add(shapename);g=target_shapes[shapename];positions=metrics([verts[i] for i in keep],g['vertices']);assert positions['maximum']<.0002,(name,mode,endpoint,shapename,positions)
     quantized_uvs=[tuple(struct.unpack('<e',struct.pack('<e',v))[0] for v in uvs[i]) for i in keep];uv=metrics(quantized_uvs,g['uvs']);assert uv['maximum']<.001,(name,mode,shapename,uv)
     mapping={old:new for new,old in enumerate(keep)};faces=[tuple(mapping[i] for i in t) for t in s['faces'] if not any(i in valid_zaps for i in t)];assert faces==g['faces'],(name,shapename,'faces')
     assert s['bones']==g['bones'] and s['transform']==g['transform']
     assert shader_raw(src,s)==shader_raw(generated,g),'Shader/alpha changed'
     # Normal/tangent regeneration is expected; UV and skin channels are checked separately.
     assert int(s['desc'],16)>>44==int(g['desc'],16)>>44
     for old,new in mapping.items():
      sa=s['rawVertices'][old*s['stride']+s['skinOffset']:old*s['stride']+s['skinOffset']+12];ga=g['rawVertices'][new*g['stride']+g['skinOffset']:new*g['stride']+g['skinOffset']+12];assert sa==ga,'Encoded SSE skin changed'
     expected_weights=[{mapping[i]:v for i,v in bone['weights'] if i in mapping} for bone in s['skinData']];actual_weights=[dict(bone['weights']) for bone in g['skinData']];assert expected_weights==actual_weights,'Legacy skin weights changed'
     assert [x['transform'] for x in s['skinData']]==[x['transform'] for x in g['skinData']]
     assert sorted(set(part[1] for part in s['parts']))==sorted(set(part[1] for part in g['parts']))
     shape_reports.append(dict(shape=shapename,sourceVertices=s['count'],vertices=g['count'],triangles=g['triangles'],deletedVertices=len(valid_zaps),outOfRangeZapIndices=len(zaps-valid_zaps),positionError=positions,uvError=uv,bones=len(g['bones']),partitions=g['parts'],legacyNonUnitSkinTotals=g['legacyNonUnitSkinTotals'],legacySkinTotalRange=g['legacySkinTotalRange'],sourceSkinChannelsPreserved=True,effects=effects))
    assert set(target_shapes)==expected_names
    # Per-shape properties/texture sets are resolved above; deleted shapes remove
    # their unused property blocks and renumber references in the generated NIF.
    bodytris=[e['value'] for e in generated['extras'] if e['name']=='BODYTRI'];assert len(bodytris)==(mode=='obody')
    if mode=='obody':assert len(list(modefolder.rglob('*.tri')))==1
    else:assert not list(modefolder.rglob('*.tri'))
    entry=dict(project=name,index=index,mode=mode,endpoint=endpoint,file=pin(filename),bodyTri=bodytris,shapes=shape_reports);reports.append(entry);bymode[mode].append(generated)
  assert maps['prebuilt']==maps['obody'],'Preset alters baseline topology'
  for a,b in zip(bymode['prebuilt'],bymode['obody']):
   for sa,sb in zip(a['shapes'],b['shapes']):assert sa['name']==sb['name'] and sa['faceHash']==sb['faceHash']
  trfile=next((Path(info['outputs'])/('obody-'+str(index))).rglob('*.tri'));morphs=tri(trfile);seen=set();rows=[]
  for shapename,keep in maps['obody'].items():
   if not keep:continue
   mapping={old:new for new,old in enumerate(keep)}
   for slider in p.findall('Slider'):
    if truth(slider,'zap') or truth(slider,'clamp'):continue
    sl=slider.get('name');_,ds=datasets[shapename][sl];kind='uv' if truth(slider,'uv') else 'position';axes=2 if kind=='uv' else 3
    expected={mapping[i]:v[:axes] for i,v in ds.items() if i in mapping and any(abs(c)>0 for c in v[:axes])};key=(shapename,kind,sl)
    if not expected:assert key not in morphs;continue
    assert key in morphs,(name,key,'missing morph');seen.add(key);mult,actual=morphs[key];assert set(expected)==set(actual),(name,key,'morph indices')
    err=max((max(abs(a-b) for a,b in zip(expected[i],actual[i])) for i in expected),default=0);assert err<=mult*1.02+1e-6,(name,key,err,mult)
    assert max(actual)<len(keep);rows.append(dict(shape=shapename,name=sl,kind=kind,vertices=len(actual),maximumIndex=max(actual),maximumError=err,quantizationStep=mult))
  assert seen==set(morphs),(name,'unexpected morphs',set(morphs)-seen)
  trireports.append(dict(project=name,file=pin(trfile),morphs=rows,compatibleWithBothEndpoints=True))
 assert len(range_rows)==29,len(range_rows)
 result=dict(build=build,inputs=pin(folder/'build-inputs.json'),xmlValues=239,files=reports,tris=trireports,rangeDiagnostics=range_rows,rangeResolution='All raw OSD records preserved. Independently checked all six current-project topologies, both endpoints and variants, XML effects and bounded TRI deltas. Old March custom output is not current August project authority.',bodySources=source_pins,structuralOnly=True)
 (out/'geometry-validation.json').write_text(json.dumps(result,indent=2));print(json.dumps(dict(output=str(out),nifs=len(reports),tris=len(trireports),morphs=sum(len(t['morphs']) for t in trireports),resolvedRangeDiagnostics=len(range_rows))))

if __name__=='__main__':validate(*sys.argv[1:])
