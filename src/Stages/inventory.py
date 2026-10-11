from pathlib import Path
import hashlib, json, sys, xml.etree.ElementTree as ET
P=Path(__file__).resolve().parents[2]
O=P/'runs/T06/inventory-01'
def pin(p):
 p=Path(p);return dict(path=str(p),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest().upper())
def verify(e):
 p=pin(e['path'])
 if any(p[k]!=e[k] for k in ('bytes','sha256')):raise ValueError('Pin changed: '+e['path'])
 return p
def main():
 O.mkdir(exist_ok=False)
 files=['final-handoff-03/extraction-map.json','dependencies-03/capture-inputs.json','applied-build-inputs-03/applied-build-inputs.json','mapping-checks-03/mapping-checks.json','mesh-application-04/mesh-application.json']
 packets={f:json.loads((P/'runs/T05B'/f).read_text()) for f in files}
 pins=[pin(P/'runs/T05B'/f) for f in files];checked={}
 def visit(node):
  if isinstance(node,dict):
   if all(k in node for k in ('path','sha256','bytes')) and Path(node['path']).is_file():
    if node['path'] not in checked:checked[node['path']]=verify(node)
   for v in node.values():visit(v)
  elif isinstance(node,list):
   for v in node:visit(v)
 for packet in packets.values():visit(packet)
 b=packets[files[2]];mapping=packets[files[3]];app=packets[files[4]]
 summary=dict(packetPins=pins,verifiedPins=list(checked.values()),projects=[dict(name=p['name'],source=p['sourceNif'],output=p['outputPath']+'/'+p['outputFile'],shapes=p['shapes']) for p in b['projects']],
   ranges=[dict(project=p['name'],rows=[r for r in p['mappings'] if r['status']=='AvailableWithOutOfRangeIndices']) for p in mapping['projects']],
   application=[dict(mesh=m['mesh'],application=m['application']) for m in app['meshes']],xmlValues=len(b['actualXml']['values']))
 (O/'inventory.json').write_text(json.dumps(summary,indent=2))
 print(json.dumps(dict(verifiedPins=len(checked),xmlValues=summary['xmlValues'],projects=summary['projects'],ranges=[dict(project=p['project'],count=len(p['rows'])) for p in summary['ranges']],application=summary['application']),indent=2))
if __name__=='__main__':main()
