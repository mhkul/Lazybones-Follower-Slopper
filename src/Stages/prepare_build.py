"""Prepare a self-contained offline BodySlide build; never write source mods/game."""
from pathlib import Path
import copy,json,shutil,sys,xml.etree.ElementTree as ET
from inventory import P,pin,verify
sys.path.insert(0,str(P/'prototypes/T02'))
from nif_probe import nif

BODY='CBBE 3BBB Body Amazing'
NAMES=[BODY,"Sen's Netch Leather",'CBBE 3BA Creation Club - Feet - Netch Leather Boots','CBBE 3BA Creation Club - Hands - Netch Leather Gauntlets','CBBE 3BA Creation Club - 1st Person - Boiled Netch Leather Armour','CBBE 3BA Creation Club - 1st Person - Netch Leather Gauntlets']
def main():
 out=P/'runs/T06'/sys.argv[1];out.mkdir(exist_ok=False)
 b=json.loads((P/'runs/T05B/applied-build-inputs-03/applied-build-inputs.json').read_text())
 old=json.loads((P/'runs/T05/job-4569bb8568a643d78b39d8c1577570a2/body-build-inputs/body-build-inputs.json').read_text())
 refs={s['virtualPath'].lower():s for s in b['sources']};sources=[]
 for s in refs.values():
  verify(s['source']);verify(s['retained']);sources.append((s['virtualPath'],s['retained']['path'],s['source'],s['provider']))
 # Body sources are selected-environment retained sources, never manual-follower assets.
 body=next(p for p in old['projects'] if p['name']==BODY)
 body_virtuals={s['virtualPath'] for s in old['sources'] if s['path']==body['osp'] or s['path']==body['sourceNif'] or 'SE 3BBB Amazing/' in s['virtualPath']}
 for s in old['sources']:
  if s['virtualPath'] not in body_virtuals:continue
  retained=pin(s['path']);original=pin(s['source'])
  if original['sha256']!=s['sha256'] or retained['sha256']!=s['sha256']:raise ValueError('Body source pin changed')
  sources.append((s['virtualPath'],s['path'],original,s['provider']))
 stage=out/'tool';stage.mkdir();project=stage/'projects';retention=out/'retained';retention.mkdir()
 provenance=[]
 for virtual,path,original,provider in sources:
  q=project/virtual.removeprefix('CalienteTools/BodySlide/');q.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,q)
  provenance.append(dict(virtualPath=virtual,source=original,retained=pin(q),provider=provider))
 # Preserve seven reviewed definitions separately, select five 3BA outfit candidates.
 sets=[]
 for osp in (project/'SliderSets').glob('*.osp'):
  raw=osp.read_bytes();shutil.copy2(osp,retention/osp.name)
  for row in provenance:
   if row.get('retained',{}).get('path')==str(osp):row['retained']=pin(retention/osp.name)
  for node in ET.fromstring(raw).findall('SliderSet'):
   if node.get('name') in NAMES:sets.append(copy.deepcopy(node))
  osp.unlink()  # owned build workspace only; original retained untouched
 if sorted(n.get('name') for n in sets)!=sorted(NAMES):raise ValueError('Missing/ambiguous selected projects')
 root=ET.Element('SliderSetInfo',{'version':'1'});relocations=[]
 for node in sets:
  if '1st Person - Boiled' in node.get('name'):
   before=node.findtext('OutputPath');node.find('OutputPath').text='meshes\\indoril netch leather'
   relocations.append(dict(project=node.get('name'),originalDirectory=before,actualTargetDirectory=node.findtext('OutputPath'),basis='Reviewed winning first-person addon/model target; filename unchanged'))
  root.append(node)
 ET.ElementTree(root).write(project/'SliderSets/T06.osp',encoding='utf-8',xml_declaration=True)
 group_root=ET.Element('SliderGroups');g=ET.SubElement(group_root,'Group',{'name':'T06'})
 for name in NAMES:ET.SubElement(g,'Member',{'name':name})
 for index,name in enumerate(NAMES):
  one=ET.SubElement(group_root,'Group',{'name':'T06-'+str(index)});ET.SubElement(one,'Member',{'name':name})
 (project/'SliderGroups').mkdir();ET.ElementTree(group_root).write(project/'SliderGroups/T06.xml',encoding='utf-8',xml_declaration=True)
 presets=project/'SliderPresets';presets.mkdir()
 verify(b['actualXml']['retained']);shutil.copy2(b['actualXml']['retained']['path'],presets/'Sen Abersha.xml')
 # Genuine zero baseline: all nonhidden, nonzap, nonclamp project morph sliders at 0;
 # preserve hidden seam/conversion defaults and all project zap defaults.
 zeros=ET.Element('SliderPresets');zero=ET.SubElement(zeros,'Preset',{'name':'T06 Zeroed','set':BODY})
 names=sorted({s.get('name') for n in sets for s in n.findall('Slider') if not any(s.get(k)=='true' for k in ('hidden','zap','clamp'))})
 for name in names:
  for endpoint in ('small','big'):ET.SubElement(zero,'SetSlider',{'name':name,'size':endpoint,'value':'0'})
 ET.ElementTree(zeros).write(presets/'T06 Zeroed.xml',encoding='utf-8',xml_declaration=True)
 install=Path(__file__).resolve().parent / 'historical-input-required' / 'BodySlide'
 # Preserve the effective author's saved zap choices for the actual called topology.
 selection=json.loads((P/'tests/fixtures/t04-sen-request.json').read_text())
 lines=(Path(selection['mods']).parent/'profiles'/selection['profile']/'modlist.txt').read_text(encoding='utf-8-sig').splitlines()
 roots=[Path(selection['overwrite'])]+[Path(selection['mods'])/s[1:] for s in lines if s.startswith('+')]+[Path(selection['game'])/'Data']
 candidates=[r/'CalienteTools/BodySlide/BuildSelection.xml' for r in roots if (r/'CalienteTools/BodySlide/BuildSelection.xml').is_file()]
 if not candidates:raise ValueError('Selected-environment BuildSelection unavailable')
 author=pin(candidates[0]);shutil.copy2(candidates[0],retention/'BuildSelection.xml')
 saved=ET.parse(candidates[0]);filtered=ET.Element('BuildSelection');zap_choices=[]
 for item in saved.getroot().iter('ZapChoice'):
  if item.get('project') in NAMES:filtered.append(copy.deepcopy(item));zap_choices.append(dict(item.attrib))
 ET.ElementTree(filtered).write(stage/'BuildSelection.xml',encoding='utf-8',xml_declaration=True)
 for name in ('BodySlide x64.exe',):
  shutil.copy2(install/name,stage/name);provenance.append(dict(toolOriginal=pin(install/name),toolCopy=pin(stage/name)))
 shutil.copytree(install/'res',stage/'res')
 # No merged game data is needed: exact complete project/source copies, no archive scan.
 data=stage/'empty-data';data.mkdir();outputs=out/'outputs';outputs.mkdir()
 cfg=ET.Element('Config')
 for key,value in dict(TargetGame='4',WarnMissingGamePath='false',WarnBatchBuildOverride='false',BSATextureScan='false',Language='0',LogLevel='3',GameDataPath=str(data)+'\\',OutputDataPath=str(outputs)+'\\',ProjectPath=str(project),GameRegKey='',GameRegVal='').items():ET.SubElement(cfg,key).text=value
 ET.ElementTree(cfg).write(stage/'Config.xml',encoding='utf-8',xml_declaration=True)
 # Avoid inherited user UI slider changes or zap choices.
 ET.ElementTree(ET.Element('BodySlideConfig')).write(stage/'BodySlide.xml',encoding='utf-8',xml_declaration=True)
 immutable=[project/'SliderSets/T06.osp',project/'SliderGroups/T06.xml',presets/'Sen Abersha.xml',presets/'T06 Zeroed.xml',stage/'Config.xml',stage/'BuildSelection.xml']
 result=dict(provenance=provenance,preparedInputs=[pin(p) for p in immutable],projects=NAMES,relocations=relocations,zeroedMorphNames=names,zeroedPolicy='Project geometry plus hidden structural/default/zap settings; exposed morph values zero; no reverse morphing',authorZapSelection=dict(source=author,retained=pin(retention/'BuildSelection.xml'),choices=zap_choices),presetClarification='October 8 user confirmed SenAbersha for all six targets; omitted weight endpoint values use project defaults',stage=str(stage),outputs=str(outputs),assignedXml=b['actualXml'],bodySource=body,preparedNotBuilt=True)
 (out/'build-inputs.json').write_text(json.dumps(result,indent=2));print(json.dumps(dict(output=str(out),projects=len(sets),sources=len(sources),zeroMorphNames=len(names))))
if __name__ == '__main__':
    raise SystemExit('Historical diagnostic CLI is disabled. Use the application build stages.')
