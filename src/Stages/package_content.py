"""Positive follower delivery allowlist, independent of the input asset inventory."""
from pathlib import PurePosixPath
from collections import Counter

def audit(members,plugin,uuid,variant,*,historical_pda=False):
 roles=[];seen=set()
 for member in members:
  path=PurePosixPath(member);parts=path.parts;lower=member.lower();suffix=path.suffix.lower()
  if not parts or path.is_absolute() or '..' in parts or '\\' in member or ':' in member or lower in seen:raise ValueError('Unsafe/duplicate ZIP member '+member)
  seen.add(lower)
  if member==plugin:role='ESPFE'
  elif historical_pda and member==f'OBodyNG_PDA_AutoFollower_{uuid}.ini':role='historical unverified PDA; excluded from successor parity'
  elif member==f'OBodyNG_PDA_AutoFollower_{uuid}.ini':role='D115 scoped PDA compatibility config'
  elif member==f'AutoFollower_{uuid}_RSV_DISTR.ini':role='D115 scoped RSV/SPID compatibility config'
  elif member=='README-T08.txt':role='test installation/limits documentation'
  elif parts[0]=='meshes' and (member.startswith(f'meshes/autofollower/{uuid}/') or member==f'meshes/actors/character/FaceGenData/FaceGeom/{plugin}/00000800.nif') and suffix in ('.nif','.tri','.xml'):role='mesh/morph/private physics config'
  elif parts[0]=='textures' and (member.startswith(f'textures/autofollower/{uuid}/') or member==f'textures/actors/character/FaceGenData/FaceTint/{plugin}/00000800.dds') and suffix=='.dds':role='texture'
  elif parts[0]=='sound' and member.startswith(f'sound/autofollower/{uuid}/') and suffix in ('.wav','.xwm'):role='private weapon sound'
  elif member=='docs/presets/original/Sen Abersha.xml':role='original preset documentation'
  elif variant=='prebuilt' and member.startswith(f'docs/presets/completed/AutoFollower_{uuid}_') and path.parent.as_posix()=='docs/presets/completed' and suffix=='.xml':role='completed preset documentation'
  elif variant=='obody' and member.startswith(f'CalienteTools/BodySlide/SliderPresets/AutoFollower_{uuid}_') and path.parent.as_posix()=='CalienteTools/BodySlide/SliderPresets' and suffix=='.xml':role='installed BodySlide preset configuration'
  else:raise ValueError('Not an approved follower payload '+member)
  if suffix in ('.dll','.pex','.psc','.exe','.py','.json','.jslot','.ps1','.bat','.cmd'):raise ValueError('Developer/runtime artifact '+member)
  roles.append(role)
 if plugin.lower() not in seen:raise ValueError('Plugin absent')
 return dict(passed=True,policy='D49/D58/D115 positive path/type allowlist',members=len(members),roles=dict(Counter(roles)),noDllPexRuntimeOrDeveloperHelpers=True)
