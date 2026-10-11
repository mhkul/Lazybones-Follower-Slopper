"""Package only the local import closure of the two active build stages."""
from pathlib import Path
import ast, hashlib, json, shutil, sys
root=Path(__file__).resolve().parents[1]
destination=Path(sys.argv[1]).resolve()/'tools/stages'
destination.mkdir(parents=True,exist_ok=False)
# Matches the stage's import search order. No task inputs or historical output.
folders=[root/'prototypes'/p for p in ('T09','T08','T07','T06','T02A','T02')]
if (root/'Stages').is_dir():folders=[root/'Stages']
queue=[folders[0]/'lean_build_stage.py',folders[0]/'lean_head_stage.py'];seen={}
while queue:
 path=queue.pop(0)
 if path.name in seen:
  if seen[path.name]!=path:raise ValueError('Ambiguous stage module '+path.name)
  continue
 seen[path.name]=path
 tree=ast.parse(path.read_text(encoding='utf-8-sig'))
 for node in ast.walk(tree):
  names=[x.name.split('.')[0] for x in node.names] if isinstance(node,ast.Import) else [node.module.split('.')[0]] if isinstance(node,ast.ImportFrom) and node.module else []
  for name in names:
   candidate=next((folder/(name+'.py') for folder in folders if (folder/(name+'.py')).is_file()),None)
   if candidate:queue.append(candidate)
rows=[]
for name,path in sorted(seen.items()):
 raw=path.read_bytes();(destination/name).write_bytes(raw);rows.append(dict(name=name,sha256=hashlib.sha256(raw).hexdigest().upper()))
(destination/'stage-files.json').write_text(json.dumps(rows,indent=2))
print(len(rows),'application stage modules packaged')
