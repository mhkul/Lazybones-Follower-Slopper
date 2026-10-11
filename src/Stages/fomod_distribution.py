"""Build the single recipient-facing FOMOD archive from validated package maps."""
from __future__ import annotations
import hashlib
import time
import zipfile
from pathlib import Path, PurePosixPath
from xml.etree import ElementTree as ET

FOMOD_SCHEMA = 'http://qconsulting.ca/fo3/ModConfig5.0.xsd'

def _path(value: str) -> str:
    p = PurePosixPath(value.replace('\\', '/'))
    if p.is_absolute() or not p.parts or any(x in ('', '.', '..') for x in p.parts) or ':' in p.parts[0]:
        raise ValueError('Unsafe FOMOD payload path: ' + value)
    return p.as_posix()

def _xml_bytes(root):
    return ET.tostring(root, encoding='utf-8', xml_declaration=True)

def _requirements(package):
    lines = []
    masters = package.get('requiredMasters', [])
    if masters:
        lines.append('Plugin masters: ' + ', '.join(masters))
    for host in package.get('hostRequirements', []):
        lines.append(host['name'] + ' (required; runtime behavior not verified)')
    for dep in package.get('retainedDependencies', []):
        authority = dep.get('authority', {})
        name = dep.get('providerName') or authority.get('providerName') or authority.get('provider') or 'selected source provider'
        ref = dep.get('reference', '')
        activation = authority.get('activation')
        lines.append('Retained asset: ' + ref + ' from ' + str(name) + (('; activate ' + activation) if activation else ''))
    return lines

def _description(package, variant):
    if variant == 'prebuilt':
        return "Uses the captured preset's body shape without requiring OBody NG."
    elif variant == 'obody':
        return 'Uses your installed OBody NG with the included preset.'
    else:
        return 'Uses the captured body and outfits as they are.'

def _recipient_readme(package, variant, plugin, preset_name):
    from follower_names import level_summary
    if variant == 'prebuilt':
        route = 'XML-built body and outfit targets; this option is intended for a host without OBody NG.'
    elif variant == 'obody':
        route = 'Captured winning OBody body and outfit meshes with compatible morph files; the included preset is applied by compatible installed OBody NG.'
    else:
        route = 'Captured body and outfit meshes are included as-is. No preset or OBody variant is supplied.'
    lines = [
        'Generated follower package: ' + plugin.removesuffix('.esp') + '.',
        '', route,
        'The selected RaceMenu head NIF, composed tint and JSlot were used with the selected save/co-save. Their association is explicit and has no unique save/session binding; runtime behavior is not verified.',
        'Required plugin masters: ' + (', '.join(package.get('requiredMasters', [])) or 'none listed.'),
    ]
    mode=(package.get('followerSettings') or {}).get('values',{}).get('raceMode','private')
    if mode=='existing':
        lines.append('Race: Use existing captured race; its installed race plugin and skeleton provider are required; no skeleton packaged.')
    else:
        lines.append('Race: Generate new custom race; source-local abilities and supported linked effects are copied privately. Other required masters remain listed above.')
    reasons=(package.get('pluginValidation') or {}).get('requiredMasterReasons',[])
    for master,reason in sorted({(d['master'],d['reason']) for d in reasons}):
        lines.append('Required master: '+master+' — '+reason+'.')
    summary = level_summary(package.get('followerSettings'))
    if summary:
        lines.append(summary)
    notice=package.get('overlayNotice') or dict(warning='RaceMenu overlays are not fully supported yet and may be missing from this follower.', omittedOperations=[])
    lines += [notice['warning'], 'The supplied head and tint are preserved with private reference edits. Full overlay reconstruction and reapplication are deferred; exports do not guarantee every overlay is included.']
    for conflict in package.get('materialConflicts') or []:
        lines.append('Material warning: '+conflict['warning'])
    lines.extend(package.get('runtimeAssignmentWarnings') or [])
    lines.extend('Asset repair: '+warning for warning in package.get('assetRepairWarnings') or [])
    for warning in package.get('missingReferences') or []:
        lines.append('Unavailable called asset: '+warning['reference']+'. The safe original reference remains; no replacement is packaged. Appearance, audio or behavior may differ.')
    if package.get('skinGeometryNotice'):
        lines.append(package['skinGeometryNotice'])
    for operation in notice['omittedOperations']:
        lines.append('Deferred overlay operation: '+operation['scope']+' on '+operation['node']+(('; overlay registration '+operation['registration']) if operation.get('registration') else '')+'.')
    lines.extend('Required host: ' + h['name'] + ' (runtime behavior not verified).' for h in package.get('hostRequirements', []))
    for dep in package.get('retainedDependencies', []):
        authority = dep.get('authority', {})
        provider = dep.get('providerName') or 'selected source provider'
        activation = authority.get('activation')
        lines.append('Retained dependency: ' + dep.get('reference', '') + ' from ' + provider + (('; activate ' + activation) if activation else '') + '.')
    if preset_name:
        lines.append('Preserved authored preset: ' + preset_name + '.')
    if variant in ('prebuilt', 'obody'):
        lines.append('To change body routes, reinstall this same generated MO2 mod and explicitly choose Replace. Merge can leave files exclusive to the previous route, such as its preset or morph files. Replacing files does not reset morph values already stored in an existing save.')
        lines.append('Existing-save OBody switching, reset/migration behavior, physics motion, ground appearance and follower gameplay are not verified. No automatic migration is provided.')
    else:
        lines.append('This package supplies one as-is body route; no alternative body route is offered. Replacing its files does not reset morph values already stored in an existing save.')
        lines.append('Physics motion, ground appearance and follower gameplay are not verified.')
    lines += [
        'D86: TNG and analogous runtime-assigned components are omitted; their assignments and appearance are not reproduced, and alternate-provider runtime behavior is unverified.',
        'Follower-scoped RSV/SPID and OBody PDA NG configuration files are included; no runtime DLL/PEX or global configuration is bundled.',
        'Source mods and manual follower versions are not modified. See docs/Installation.txt and docs/Provenance.txt.'
    ]
    compatibility=package.get('compatibility')
    if compatibility:
        lines.append('Compatibility target NPC EditorID: '+compatibility['npcEditorID']+'.')
        lines.append('PDA target: '+compatibility['pluginFilename']+' / '+compatibility['pdaFormID']+' ('+compatibility['npcSex']+'). Rule: '+compatibility['pdaRule'])
        lines.append('RSVignore is consumed by recipient RSV/SPID. Prebuilt/as-is works without OBody/PDA; its omission rule is only active when compatible PDA/OBody is installed. OBody automatic assignment requires compatible PDA NG/OBody NG.')
        lines.append(compatibility['switching'])
    return ('\n'.join(lines) + '\n').encode('utf-8')

def build_fomod(package_root: Path, variants: dict[str, dict[str, bytes]], packages: list[dict], plugin: str, identity: str, obody: bool, preset_name: str | None, mod_name: str | None = None):
    started = time.monotonic()
    expected = {'prebuilt', 'obody'} if obody else {'as-is'}
    if set(variants) != expected or {p['variant'] for p in packages} != expected:
        raise ValueError('FOMOD branch set does not match the captured body route')
    if len({p.get('pluginValidation', {}).get('identity', plugin) for p in packages}) != 1:
        raise ValueError('Conflicting follower identities across FOMOD branches')
    ordered = ['prebuilt', 'obody'] if obody else ['as-is']
    maps = {}
    for variant in ordered:
        mapped = {}
        for raw_name, data in variants[variant].items():
            name = _path(raw_name)
            if name in mapped:
                raise ValueError('Conflicting duplicate install destination: ' + name)
            if not isinstance(data, bytes):
                raise ValueError('Non-binary branch payload: ' + name)
            mapped[name] = data
        if plugin not in mapped:
            raise ValueError('Follower ESP missing from ' + variant)
        package = next(p for p in packages if p['variant'] == variant)
        mapped['README-T08.txt'] = _recipient_readme(package, variant, plugin, preset_name)
        providers = sorted({str(row.get('providerName')) for row in package.get('assetResults', []) if row.get('providerName') and row.get('status') in ('Packaged', 'Retained dependency')})
        provenance = [
            'Generated from the selected save/co-save, matching RaceMenu export bundle, and resolved source providers in the selected installed Data view.',
            'Included source providers: ' + (', '.join(providers) if providers else 'No provider names were available in the validated build result.'),
            'This generated package does not grant permission to redistribute upstream assets. Follow each source mod’s own permissions and attribution requirements.',
            'The selected source/build receipt is retained locally with the build result; this recipient document does not expose developer run paths.'
        ]
        mapped['docs/Provenance.txt'] = ('\n'.join(provenance) + '\n').encode('utf-8')
        maps[variant] = mapped
    common = {}
    if obody:
        for name, data in maps['prebuilt'].items():
            if name in maps['obody'] and maps['obody'][name] == data:
                common[name] = data
    else:
        common = dict(maps['as-is'])
    exclusive = {variant: {n:b for n,b in maps[variant].items() if n not in common} for variant in ordered}
    # A common destination is only shared when all branches carry identical bytes.
    for name in common:
        if any(maps[v].get(name) != common[name] for v in ordered):
            raise ValueError('Conflicting common destination: ' + name)
    docs = [
        'Install this archive as one MO2 mod and keep it enabled.',
    ]
    if obody:
        docs += [
        'To change body routes, reinstall this same generated mod in MO2 and explicitly choose Replace. Merge can leave files exclusive to the previous route, including its preset or morph files. Replacing files does not reset morph values already stored in an existing save.',
        'For an OBody NG route, use the installed OBody facilities and the included assigned preset. Existing-save switching, reset and migration behavior are not verified; no automatic migration is provided.',
        ]
    else:
        docs += [
        'This installer has one as-is body route and offers no alternative body route. Replacing its files does not reset morph values already stored in an existing save.',
        ]
    docs += [
        'RaceMenu overlays are not fully supported yet and may be missing from this follower.',
        'Keep the selected plugin enabled. Required host and deliberately retained asset providers, along with plugin masters, are listed in README-T08.txt and the build result.',
        'The archive preserves source-provider/license conditions. No author, permission grant or redistribution license is asserted by this generated package.'
    ]
    if preset_name:
        docs.append('Included authored preset: ' + preset_name + '.')
    install_text = '\n'.join(docs) + '\n'
    info = ET.Element('fomod')
    ET.SubElement(info, 'Name').text = mod_name or 'Auto Follower ' + identity
    ET.SubElement(info, 'Version', {'MachineVersion':'1.0.0'}).text = 'Generated package'
    ET.SubElement(info, 'Description').text = 'Generated follower package with a single conditional body route.'
    config = ET.Element('config', {'xmlns:xsi':'http://www.w3.org/2001/XMLSchema-instance', 'xsi:noNamespaceSchemaLocation':FOMOD_SCHEMA})
    ET.SubElement(config, 'moduleName').text = mod_name or 'Auto Follower ' + identity
    required = ET.SubElement(config, 'requiredInstallFiles')
    ET.SubElement(required, 'file', {'source':'docs/Installation.txt'})
    if obody:
        step = ET.SubElement(ET.SubElement(config, 'installSteps', {'order':'Explicit'}), 'installStep', {'name':'Body route'})
        groups = ET.SubElement(step, 'optionalFileGroups', {'order':'Explicit'})
        group = ET.SubElement(groups, 'group', {'name':'Choose one body route', 'type':'SelectExactlyOne'})
        plugins_node = ET.SubElement(group, 'plugins', {'order':'Explicit'})
        for variant in ordered:
            branch = ET.SubElement(plugins_node, 'plugin', {'name':'Prebuilt' if variant=='prebuilt' else 'OBody NG'})
            ET.SubElement(branch, 'description').text = _description(next(p for p in packages if p['variant']==variant), variant)
            files = ET.SubElement(branch, 'files')
            for name in sorted(common):
                ET.SubElement(files, 'file', {'source':'payload/common/'+name, 'destination':name})
            for name in sorted(exclusive[variant]):
                ET.SubElement(files, 'file', {'source':'payload/'+variant+'/'+name, 'destination':name})
            descriptor = ET.SubElement(branch, 'typeDescriptor')
            ET.SubElement(descriptor, 'type', {'name':'Recommended' if variant=='prebuilt' else 'Optional'})
    else:
        for name in sorted(common):
            ET.SubElement(required, 'file', {'source':'payload/common/'+name, 'destination':name})
    path = package_root / ((mod_name or 'AutoFollower_' + identity) + '_fomod.zip')
    if path.exists():
        raise FileExistsError('Refusing to overwrite distribution output: ' + str(path))
    entries = {'fomod/info.xml':_xml_bytes(info), 'fomod/ModuleConfig.xml':_xml_bytes(config), 'docs/Installation.txt':install_text.encode('utf-8')}
    for name, data in common.items(): entries['payload/common/'+name] = data
    for variant in ordered:
        for name, data in exclusive[variant].items(): entries['payload/'+variant+'/'+name] = data
    referenced = []
    for node in config.iter():
        if node.tag in ('file','folder') and node.get('source'):
            referenced.append(node.get('source'))
    if any(source not in entries for source in referenced):
        raise ValueError('FOMOD references an absent payload source')
    if len(entries) != len(set(entries)):
        raise ValueError('FOMOD archive contains duplicate source entries')
    with zipfile.ZipFile(path, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name, data in sorted(entries.items()): archive.writestr(name, data)
    digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    payload_bytes=sum(map(len,common.values()))+sum(sum(map(len,exclusive[v].values())) for v in ordered)
    branch_bytes={v:sum(map(len,maps[v].values()))+len(install_text.encode('utf-8')) for v in ordered}
    return {'primary':True, 'path':str(path), 'sha256':digest, 'bytes':path.stat().st_size, 'members':len(entries), 'branchCount':len(ordered), 'payloadFiles':len(common)+sum(len(exclusive[v]) for v in ordered), 'payloadBytes':payload_bytes, 'additionalSourceFilesRead':0, 'additionalSourceBytesRead':0, 'additionalStagedCopies':0, 'branches':[{ 'variant':v, 'default':v=='prebuilt' if obody else True, 'installedMembers':len(maps[v])+1, 'installedBytes':branch_bytes[v], 'commonMembers':len(common), 'exclusiveMembers':len(exclusive[v]), 'classification':next(p['classification'] for p in packages if p['variant']==v), 'retainedDependencies':next(p['retainedDependencies'] for p in packages if p['variant']==v), 'requiredMasters':next(p['requiredMasters'] for p in packages if p['variant']==v), 'hostRequirements':next(p['hostRequirements'] for p in packages if p['variant']==v)} for v in ordered], 'elapsedMilliseconds':int((time.monotonic()-started)*1000), 'runtimeVerified':False, 'branchMaps':maps}


def build_plain(package_root, variants, packages, plugin, identity, preset_name, mod_name=None):
    started = time.monotonic()
    if len(variants)!=1 or len(packages)!=1 or packages[0]['variant'] not in variants: raise ValueError('Plain ZIP requires exactly one selected variant')
    variant, payload = next(iter(variants.items()))
    if variant not in ('prebuilt','obody','as-is'): raise ValueError('Unsupported plain ZIP variant')
    entries = {_path(n):b for n,b in payload.items()}
    if len(entries)!=len(payload) or plugin not in entries or any(not isinstance(b,bytes) for b in entries.values()): raise ValueError('Invalid plain ZIP payload')
    if any(n.startswith(('payload/','fomod/')) or n.lower().endswith('.zip') for n in entries): raise ValueError('Plain ZIP cannot contain installer payload paths or nested ZIPs')
    package=packages[0]
    entries['README-T08.txt']=_recipient_readme(package,variant,plugin,preset_name)
    entries['docs/Installation.txt']=('Install this archive as one MO2 mod and keep its plugin enabled.\n'
        'RaceMenu overlays are not fully supported yet and may be missing from this follower.\n'
        'This archive contains only the selected '+variant+' output.\n'
        'When changing body routes, reinstall the same generated MO2 mod and explicitly choose Replace. Merge can leave old exclusive preset or morph files. Replacing files does not reset morph values stored in an existing save.\n'
        'Required masters, hosts and deliberately retained providers are listed in README-T08.txt and the build result. Runtime behavior and save migration are not verified.\n').encode('utf-8')
    providers=sorted({str(r['providerName']) for r in package.get('assetResults',[]) if r.get('providerName') and r.get('status') in ('Packaged','Retained dependency')})
    entries['docs/Provenance.txt']=('Generated from the selected save/co-save, RaceMenu export bundle and resolved sources in the selected installed Data view.\n'
        'Included source providers: '+', '.join(providers)+'.\n'
        'This package grants no permission to redistribute upstream assets. Follow each source mod’s permissions and attribution requirements.\n').encode('utf-8')
    path=package_root/((mod_name or 'AutoFollower_'+identity)+'_'+variant+'.zip')
    with zipfile.ZipFile(path,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        for name,raw in sorted(entries.items()): archive.writestr(name,raw)
    return dict(primary=True,format='plain-zip',path=str(path),sha256=hashlib.sha256(path.read_bytes()).hexdigest().upper(),bytes=path.stat().st_size,members=len(entries),branchCount=1,payloadFiles=len(entries),payloadBytes=sum(map(len,entries.values())),additionalSourceFilesRead=0,additionalSourceBytesRead=0,additionalStagedCopies=0,
        branches=[dict(variant=variant,default=True,installedMembers=len(entries),installedBytes=sum(map(len,entries.values())),classification=package['classification'],retainedDependencies=package['retainedDependencies'],requiredMasters=package['requiredMasters'],hostRequirements=package['hostRequirements'])],branchMaps={variant:entries},elapsedMilliseconds=int((time.monotonic()-started)*1000),runtimeVerified=False)
