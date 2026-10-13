"""Required body/private-asset/package adapter for the compact selected plan.

Only owned staged files are read. Existing editors and binary/package validators
are reused; neither legacy capture maps nor historical follower payloads enter.
"""
from pathlib import Path
import copy, ctypes, hashlib, json, math, os, shutil, struct, subprocess, sys, time
import xml.etree.ElementTree as ET
sys.dont_write_bytecode = True
PROJECT = Path(__file__).resolve().parents[2]
_runtime_lease = open(PROJECT / 'runtime-use.lock', 'rb') if (PROJECT / 'runtime-use.lock').is_file() else None
for task in ('T02', 'T02A', 'T06', 'T07', 'T08'):
    sys.path.insert(0, str(PROJECT / 'prototypes' / task))
from best_effort import HostProtectionError, StageFailed, QUALITY_ERRORS, finding, guard_members, owned_path
from nif_refs import Nif, Cursor, prove_only_references_changed, texture_reference
from head_validate import head
from nif_detail import detail
from validate_geometry import data_for, defaults, f32, metrics, truth, shader_raw
import validate_geometry as geometry
from validate_package import validate_members
from fomod_distribution import build_fomod, build_plain
from validate_binary import fields
from ground_records import read_bytes
from output_policy import successor_members
from follower_names import names,require_settings_readback,level_summary
from output_compatibility import emit as emit_compatibility, safe_value
from placement_validation import validate as validate_placement
from head_physics import physics_inputs
from head_skeleton import preload as preload_head_skeleton

def norm(value, category=None):
    if category == 'textures':
        try: value = texture_reference(value)
        except ValueError as error: raise HostProtectionError('Unsafe creator texture reference') from error
    value = value.replace('\\', '/').lower()
    if value.startswith('/'): raise HostProtectionError('Rooted asset reference')
    while '//' in value: value=value.replace('//','/')
    if value.startswith('data/'): value = value[5:]
    if category and not value.startswith(category + '/'): value = category + '/' + value
    if ':' in value or any(p in ('', '.', '..') for p in value.split('/')): raise HostProtectionError('Unsafe asset path: ' + value)
    return value

def shader_signature(d, shape):
    # Compare all shader bytes and ordered texture slots. Only path spelling
    # and a validated creator prefix are canonicalized; other data stay exact.
    result=[]
    for row in shader_raw(d,shape):
        if row[0]=='BSLightingShaderProperty':
            q=Cursor(row[4]); slots=q.unpack('I')
            values=[]
            for _ in range(slots):
                value=q.string().decode('utf-8');values.append(norm(value,'textures') if value else '')
            if q.pos!=len(q.data): raise ValueError('Shader texture set trailing data')
            row=(*row[:4],tuple(values),*row[5:])
        result.append(row)
    return result

class Build:
    def __init__(self, plan_path, writer):
        self.started = time.monotonic(); self.root = Path(plan_path).resolve().parent
        self._storage_lease = open(self.root / 'storage.lock', 'rb') if (self.root / 'storage.lock').is_file() else None
        self.read_bytes = self.generated = self.copied = 0; self.read_files = set(); self.stages = []
        self.plan = self.load(Path(plan_path)); self.owner = self.load(self.root / 'owned-output.json')
        self.overlay_notice = self.plan.get('overlayNotice') or dict(warning='RaceMenu overlays are not fully supported yet and may be missing from this follower.', playerRegistration=None, omittedOperations=[], detectionExhaustive=False)
        if self.plan['schema'] != 'lean-export-input-plan/v1' or self.owner['kind'] != 'T09B lean export build': raise ValueError('Unowned/unsupported compact plan')
        self.warnings = list(self.plan.get('buildWarnings') or [])
        for gap in self.plan['gaps']: self.warn('input-quality','selected-input',gap['evidence'])
        outputs = self.plan.get('outputs')
        self.variants = outputs['variants'] if outputs else (['prebuilt', 'obody'] if self.plan['body'] else ['as-is'])
        if not self.variants or len(set(self.variants)) != len(self.variants) or any(v not in ('prebuilt','obody','as-is') for v in self.variants): raise ValueError('Invalid selected output set')
        if self.plan['body'] is None and self.variants != ['as-is']: self.warn('prebuilt-fallback','body-input','No recoverable assigned-preset build plan')
        self.writer = Path(writer).resolve()
        naming = names(self.plan,self.owner)
        self.uuid = naming['identity']; self.namespace = naming['namespace']; self.plugin = naming['plugin']; self.mod_name=naming['display']
        if self.plan['body'] is not None:
            safe_value('AutoFollower_'+self.uuid+'_'+self.plan['body']['assignedPreset'],'private preset filename/internal name')
        self.sources = {norm(s['reference']): s for s in self.plan['sources']}
        self.missing = {norm(r['reference']):r for r in self.plan.get('missingReferences') or []}
        self.choices = {r['reference']: r for r in self.plan.get('assetChoices', []) if r['layer'] == 'shared'}
        self.retained = []
        self.head = self.load(self.root / 'private-head-result.json')
        self.warnings.extend(r for r in self.head.get('buildWarnings',[]) if r not in self.warnings)
        self.moved = {a['reference']: a['destination'] for a in self.head['assets']}
        self.assets = []; self.paths = {}; self.prebuilt = {}; self.preset = None; self.ground_shapes = []
        self.diff_cache = {}; self.diff_pins = {}
        geometry.diffs = self.diffs
        geometry.pin = lambda path: self.diff_pins[str(Path(path).resolve())] if str(Path(path).resolve()) in self.diff_pins else self.pin_dataset(path)

    def pin_dataset(self, path):
        self.diffs(path); return self.diff_pins[str(Path(path).resolve())]

    def diffs(self, path):
        path = self.owned(path); key = str(path)
        if key in self.diff_cache: return self.diff_cache[key]
        raw = self.read(path); q = Cursor(raw)
        if q.take(4)!=b'\0DSO' or q.unpack('I')!=1: raise ValueError('Required OSD format unsupported')
        count = q.unpack('I')
        if count>10000: raise ValueError('Required OSD dataset bound')
        result = {}
        for _ in range(count):
            name = q.string('B').decode('utf-8'); rows = {}
            for _ in range(q.unpack('H')):
                index, x, y, z = q.unpack('Hfff')
                if index in rows or not all(math.isfinite(v) for v in (x,y,z)): raise ValueError('Duplicate OSD index/nonfinite delta')
                rows[index] = (x,y,z)
            if not name or name in result: raise ValueError('Duplicate/empty OSD dataset')
            result[name] = rows
        if q.pos!=len(raw): raise ValueError('OSD trailing data')
        self.diff_cache[key] = result; self.diff_pins[key] = dict(path=key,bytes=len(raw),sha256=hashlib.sha256(raw).hexdigest().upper())
        return result

    def check(self):
        if (self.root / 'stop').exists(): raise InterruptedError('Owned build cancelled')

    def owned(self, path):
        return owned_path(self.root, path)

    def warn(self, code, stage, detail):
        row = finding(code, stage, detail)
        if row not in self.warnings: self.warnings.append(row)
        return row

    def quality(self, code, stage, operation, fallback=None):
        self.check()
        try: return operation()
        except QUALITY_ERRORS as error:
            self.warn(code, stage, error)
            return fallback

    def assessment(self, data, variant, hosts, external, external_data):
        guard_members(data)
        # Every serialized reference is checked for traversal separately from
        # output quality. These checks cannot become warning-only checks.
        for name, raw in {**data, **external_data}.items():
            if name.lower().endswith('.nif'):
                model = self.quality('asset-quality', variant, lambda: Nif(raw))
                if model is not None:
                    for edge in model.references(): norm(edge['value'], 'textures' if edge['field']=='texture' else 'meshes')
        fallback=dict(passed=False,checksComplete=False,retainedReferences=[r['reference'] for r in external],missingReferences=list(self.missing.values()),completeReferenceClosure=False,runtimeVerified=False)
        return self.quality('package-quality', variant, lambda: validate_members(data,self.plugin,self.uuid,variant,hosts,external,external_data,require_compatibility=True,missing_references=list(self.missing.values()),best_effort=True), fallback)

    def captured_preset(self):
        body=self.plan['body']
        if body is None or self.preset is not None: return
        _, authored=self.staged(body['xmlReference'])
        originals=[p for p in ET.fromstring(authored).findall('Preset') if p.get('name')==body['assignedPreset']]
        if len(originals)!=1: raise ValueError('Authored preset identity is not unique')
        preset=copy.deepcopy(originals[0]);preset.set('name','AutoFollower_'+self.uuid+'_'+body['assignedPreset'])
        document=ET.Element('SliderPresets');document.append(preset)
        self.preset=dict(original=authored,completed=ET.tostring(document,encoding='utf-8',xml_declaration=True),runtimeName=preset.get('name'),originalValues=len(preset.findall('SetSlider')),defaults=[])

    def read(self, path):
        self.check(); path = self.owned(path); b = path.read_bytes()
        self.read_files.add(str(path)); self.read_bytes += len(b); return b

    def load(self, path): return json.loads(self.read(path).decode('utf-8-sig'),parse_int=lambda v:-0.0 if v=='-0' else int(v))

    def dump(self, path, value):
        path = self.owned(path); path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, indent=2), encoding='utf-8')

    def staged(self, reference):
        ref = norm(reference); source = self.sources[ref]
        p = self.root / ('private-head/' + self.moved[ref] if ref in self.moved else 'assets/' + source['reference'])
        b = self.read(p)
        if len(b) != source['bytes'] or hashlib.sha256(b).hexdigest().upper() != source['sha256']: raise HostProtectionError('Staged source changed: ' + ref)
        return p, b

    def run(self, command, cwd, name):
        self.check(); log = self.root / (name + '.log'); started = time.monotonic()
        self.dump(self.root / (name + '-launch.json'), dict(launchIntent=True))
        with log.open('w', encoding='utf-8') as stream:
            startup = None; lease_handles = []
            leases = [_runtime_lease, getattr(self, '_storage_lease', None)]
            if any(lease is not None for lease in leases):
                import msvcrt
                for lease in leases:
                    if lease is not None:
                        handle = msvcrt.get_osfhandle(lease.fileno()); os.set_handle_inheritable(handle, True); lease_handles.append(handle)
                startup = subprocess.STARTUPINFO(lpAttributeList={'handle_list': lease_handles})
            try:
                process = subprocess.Popen([str(v) for v in command], cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW, startupinfo=startup)
            finally:
                for handle in lease_handles: os.set_handle_inheritable(handle, False)
            try:
                created = ctypes.c_ulonglong(); exited = ctypes.c_ulonglong(); kernel = ctypes.c_ulonglong(); user = ctypes.c_ulonglong()
                get_times = ctypes.WinDLL('kernel32', use_last_error=True).GetProcessTimes
                get_times.argtypes = [ctypes.c_void_p] + [ctypes.POINTER(ctypes.c_ulonglong)] * 4
                get_times.restype = ctypes.c_int
                if not get_times(int(process._handle), ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
                    raise OSError('Owned child identity unavailable')
                self.dump(self.root / (name + '-process.json'), dict(pid=process.pid, createdFileTime=created.value, command=[str(v) for v in command], parent=os.getpid(), ownedStage=True, userMo2Owned=False))
                while process.poll() is None:
                    self.check()
                    try: process.wait(timeout=0.1)
                    except subprocess.TimeoutExpired: pass
            except BaseException:
                process.terminate(); process.wait(timeout=15); raise
        receipt = dict(stage=name, exitCode=process.returncode, milliseconds=int((time.monotonic()-started)*1000), log=str(log))
        self.stages.append(receipt); self.dump(self.root / 'package-stage-progress.json', self.stages)
        if process.returncode: raise StageFailed(name, process.returncode)
        return receipt

    def native_path(self, path):
        # Existing filesystem aliases are names for the same owned object, not
        # links or renamed user folders. NTFS/ReFS need not provide an alias.
        self.check(); path = self.owned(path); candidates = [str(path)]
        if os.name == 'nt':
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            short = kernel.GetShortPathNameW
            short.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint32]
            short.restype = ctypes.c_uint32
            buffer = ctypes.create_unicode_buffer(32768)
            source = str(path)
            if not source.startswith('\\\\?\\'):
                source = '\\\\?\\UNC\\' + source[2:] if source.startswith('\\\\') else '\\\\?\\' + source
            length = short(source, buffer, len(buffer))
            if 0 < length < len(buffer):
                value = buffer.value
                if value.startswith('\\\\?\\UNC\\'): value = '\\\\' + value[8:]
                elif value.startswith('\\\\?\\'): value = value[4:]
                try:
                    if Path(value).samefile(path): candidates.append(value)
                except OSError: pass
        candidates = [p for p in candidates if p.isascii() and len(p) < (248 if path.is_dir() else 260)]
        if not candidates:
            raise ValueError('BodySlide build work path unsupported: no short ASCII path is available for this owned attempt. Set OutputRoot in application.json beside the app to a writable short ASCII folder outside game, mod and input folders, then retry. Existing attempts are preserved.')
        return min(candidates, key=len)

    def compact_project(self, node, inputs):
        # Relocate only references in the private XML copy. Dataset names,
        # slider values, project identities and source geometry stay intact.
        folder = node.findtext('DataFolder').replace('\\', '/')
        source = norm('ShapeData/' + folder + '/' + node.findtext('SourceFile'))
        shapes = {s.get('target'): s for s in node.findall('Shape')}
        for slider in node.findall('Slider'):
            for data in slider.findall('Data'):
                value = data.text.replace('\\', '/'); filename, record = value.rsplit('/', 1)
                data_folder = folder
                if data.get('local') != 'true':
                    data_folder = shapes[data.get('target')].get('DataFolder', folder).replace('\\', '/')
                data.text = inputs[norm('ShapeData/' + data_folder + '/' + filename)] + '/' + record
        node.find('DataFolder').text = 'i'
        node.find('SourceFile').text = inputs[source]
        for shape in node.findall('Shape'):
            if shape.get('DataFolder') is not None: shape.set('DataFolder', 'i')

    def body(self):
        body = self.plan['body']
        if body is None:
            self.dump(self.root/'body-work.json', dict(route='as-is', bodySlideInvoked=False, projectInputsRequired=False, basis='Non-OBody: captured installed body/outfit geometry, private references only' if self.variants==['as-is'] else 'Assigned-preset input unavailable: captured installed body/outfit geometry, no preset bake or automatic assignment claimed'))
            return
        if 'projects' not in body: raise ValueError('Required body plan was not established')
        if 'prebuilt' not in self.variants or not body['projects']:
            _, authored = self.staged(body['xmlReference'])
            original = [p for p in ET.fromstring(authored).findall('Preset') if p.get('name') == body['assignedPreset']]
            if len(original) != 1: raise ValueError('Authored preset identity is not unique')
            preset = copy.deepcopy(original[0]); preset.set('name', 'AutoFollower_' + self.uuid + '_' + body['assignedPreset'])
            private = ET.Element('SliderPresets'); private.append(preset)
            self.preset = dict(original=authored, completed=ET.tostring(private, encoding='utf-8', xml_declaration=True), runtimeName=preset.get('name'), originalValues=len(preset.findall('SetSlider')), defaults=[])
            self.dump(self.root/'body-work.json', dict(route='captured-installed', bodySlideInvoked=False, projectInputsRequired=False, omittedEndpoints='Unchanged; explicit user choice; handled by installed OBody', authoredValuesPreserved=self.preset['originalValues']))
            return
        base = self.root / 'body-stage'; tool = base / 'tool'; project = tool / 'projects'; tool.mkdir(parents=True)
        required = {r for p in body['projects'] for r in p['inputReferences']}
        relocations = []; inputs = {}
        for index, ref in enumerate(sorted(required)):
            path, raw = self.staged(ref); relative = ref.replace('\\', '/').split('BodySlide/', 1)[1]
            compact = 'i' + str(index) + Path(relative).suffix.lower()
            dest = project / 'ShapeData/i' / compact; dest.parent.mkdir(parents=True, exist_ok=True); os.rename(path, dest)
            inputs[norm(relative)] = compact
            relocations.append(dict(reference=ref, destination=str(dest.relative_to(base)), bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper()))
        sets = []; source_defs = {}; project_roots = {}
        for row in body['projects']:
            ref = row['definitionReference']
            if ref not in source_defs: source_defs[ref] = ET.fromstring(self.staged(ref)[1])
            candidates = [n for n in source_defs[ref].findall('SliderSet') if n.get('name') == row['name']]
            if len(candidates) != 1: raise ValueError('Required project definition not unique')
            node = copy.deepcopy(candidates[0]); self.compact_project(node, inputs); project_roots[row['name']] = node
            node.find('OutputPath').text = 'meshes/p' + str(len(sets))
            node.find('OutputFile').text = 'mesh'
            sets.append(node)
        combined = ET.Element('SliderSetInfo', {'version': '1'}); combined.extend(sets)
        (project / 'SliderSets').mkdir(); ET.ElementTree(combined).write(project / 'SliderSets/T09G.osp', encoding='utf-8', xml_declaration=True)
        groups = ET.Element('SliderGroups')
        for i, node in enumerate(sets): ET.SubElement(ET.SubElement(groups, 'Group', {'name': 'T09G-' + str(i)}), 'Member', {'name': node.get('name')})
        (project / 'SliderGroups').mkdir(); ET.ElementTree(groups).write(project / 'SliderGroups/T09G.xml', encoding='utf-8', xml_declaration=True)
        _, authored = self.staged(body['xmlReference']); preset_doc = ET.fromstring(authored)
        originals = [p for p in preset_doc.findall('Preset') if p.get('name') == body['assignedPreset']]
        if len(originals) != 1: raise ValueError('Authored preset identity is not unique')
        preset = copy.deepcopy(originals[0]); preset.set('name', 'AutoFollower_' + self.uuid + '_' + body['assignedPreset'])
        values = {(s.get('name'), s.get('size')): float(s.get('value')) for s in preset.findall('SetSlider')}
        completions = {}; completion_basis = {}
        for node in sets:
            for slider in node.findall('Slider'):
                if any(truth(slider, k) for k in ('zap', 'hidden', 'clamp')): continue
                for endpoint in ('small', 'big'):
                    key = (slider.get('name'), endpoint)
                    if key in values: continue
                    val = float(slider.get(endpoint, slider.get('default', '0')))
                    if key in completions and completions[key] != val: raise ValueError('Conflicting required project fallback: ' + str(key))
                    completions[key] = val; completion_basis.setdefault(key, []).append(node.get('name'))
        for (name, endpoint), value in sorted(completions.items()): ET.SubElement(preset, 'SetSlider', dict(name=name, size=endpoint, value=str(value)))
        completed = ET.Element('SliderPresets'); completed.append(preset); completed_bytes = ET.tostring(completed, encoding='utf-8', xml_declaration=True)
        (project / 'SliderPresets').mkdir(); (project / 'SliderPresets/Sen.xml').write_bytes(authored)
        self.preset = dict(original=authored, completed=completed_bytes, runtimeName=preset.get('name'), originalValues=len(values), defaults=[dict(name=k[0], size=k[1], value=v, projects=completion_basis[k]) for k, v in sorted(completions.items())])
        _, choices_raw = self.staged('CalienteTools/BodySlide/BuildSelection.xml'); choice_tree = ET.fromstring(choices_raw)
        filtered = ET.Element('BuildSelection')
        for node in choice_tree.iter('ZapChoice'):
            if node.get('project') in project_roots: filtered.append(copy.deepcopy(node))
        garment = [x for x in filtered if x.get('project') == "Sen's Netch Leather" and x.get('zap') == 'Remove Lower Torso Armor']
        if len(garment) > 1: raise ValueError('Ambiguous garment selection')
        if "Sen's Netch Leather" in project_roots:
            if not garment: garment = [ET.SubElement(filtered, 'ZapChoice', dict(project="Sen's Netch Leather", zap='Remove Lower Torso Armor'))]
            garment[0].set('choice', 'false')
        ET.ElementTree(filtered).write(tool / 'BuildSelection.xml', encoding='utf-8', xml_declaration=True)
        for ref in body['toolResources']:
            path, _ = self.staged(ref); dest = tool / ref.replace('\\','/').split('BodySlide/',1)[1]
            dest.parent.mkdir(parents=True,exist_ok=True); os.rename(path,dest)
        tool_ref = body['toolReference']
        if tool_ref not in ('CalienteTools/BodySlide/BodySlide.exe', 'CalienteTools/BodySlide/BodySlide x64.exe'):
            raise ValueError('Selected BodySlide executable reference unsupported')
        exe, _ = self.staged(tool_ref); tool_exe = tool / Path(tool_ref).name; os.rename(exe, tool_exe)
        empty = base / 'empty-data'; empty.mkdir(); outputs = base / 'outputs'; outputs.mkdir()
        native_tool = self.native_path(tool); native_project = self.native_path(project)
        native_empty = self.native_path(empty); native_outputs = self.native_path(outputs)
        # The executable directory controls logger/resource startup. Check the
        # exact paths BodySlide will compose, rather than individual leaf aliases.
        for path in tool.rglob('*'):
            self.owned(path)
            actual = str(Path(native_tool) / path.relative_to(tool))
            if len(actual) >= (248 if path.is_dir() else 260):
                raise ValueError('BodySlide build work path unsupported: a required native resource exceeds the compatible path layout. Set OutputRoot in application.json to a writable short ASCII folder outside game, mod and input folders, then retry. Existing attempts are preserved.')
        for directory, native in ((tool, native_tool), (project, native_project), (outputs, native_outputs), (empty, native_empty)):
            probe = self.owned(directory / 'path-check.tmp')
            created = False
            try:
                with probe.open('xb') as stream:
                    created = True; stream.write(b'BodySlide owned path check')
                if not (Path(native) / probe.name).samefile(probe): raise HostProtectionError('BodySlide owned path equivalence failed')
            except OSError as error:
                raise ValueError('BodySlide build work folder is not writable. Set OutputRoot in application.json to a writable short ASCII folder outside game, mod and input folders, then retry. Existing attempts are preserved.') from error
            finally:
                if created: probe.unlink()
        self.dump(base / 'native-paths.json', dict(tool=native_tool, projects=native_project, outputs=native_outputs, emptyData=native_empty, sameOwnedObjects=True, inputRelocations=relocations))
        config = ET.Element('Config')
        for key, value in dict(TargetGame='4', WarnMissingGamePath='false', WarnBatchBuildOverride='false', BSATextureScan='false', Language='0', LogLevel='3', GameDataPath=native_empty+'\\', OutputDataPath=native_outputs+'\\', ProjectPath=native_project, GameRegKey='', GameRegVal='').items(): ET.SubElement(config, key).text = value
        ET.ElementTree(config).write(tool / 'Config.xml', encoding='utf-8', xml_declaration=True)
        ET.ElementTree(ET.Element('BodySlideConfig')).write(tool / 'BodySlide.xml', encoding='utf-8', xml_declaration=True)
        validation = []
        for i, row in enumerate(body['projects']):
            self.check(); dest = outputs / str(i); dest.mkdir()
            # The native log appends. Clear only this owned tool copy's log so
            # a prior job cannot supply a receipt for the current invocation.
            native_log = tool / 'Log_BS.txt'; native_log.unlink(missing_ok=True)
            native_dest = Path(native_outputs) / str(i)
            if len(str(native_dest / ('meshes/p'+str(i)))) >= 248 or len(str(native_dest / ('meshes/p'+str(i)+'/mesh_1.nif'))) >= 260:
                raise ValueError('BodySlide build work path unsupported: generated mesh path exceeds the native path limit. Set OutputRoot in application.json to a writable short ASCII folder, then retry.')
            try:
                self.run([Path(native_tool) / tool_exe.name, '-gbuild', 'T09G-'+str(i), '-p', body['assignedPreset'], '-t', str(native_dest)+'\\'], Path(native_tool), 'prebuilt-'+str(i))
                log=self.read(native_log).decode('utf-8',errors='replace')
                if not any(marker in log for marker in ('All group build sets processed successfully!', 'All command-line build sets processed successfully!')):
                    self.warn('prebuilt-quality','prebuilt-'+str(i),'BodySlide success receipt absent; only actually written readable outputs can be used')
                generated=sorted(dest.rglob('*.nif'))
                if len(generated)!=2: raise ValueError('No complete readable prebuilt weight pair')
                for path in generated: Nif(self.read(path))
                for endpoint,path in enumerate(generated):
                    ref=norm(row['outputTarget']);self.prebuilt[ref[:-6]+'_'+str(endpoint)+'.nif']=path
                if list(dest.rglob('*.tri')): self.warn('prebuilt-quality','prebuilt-'+str(i),'Unexpected runtime TRI was generated; captured compatible morph inputs are preserved where called')
                rows=self.quality('prebuilt-quality','geometry-'+str(i),lambda:self.validate_prebuilt(row,project_roots[row['name']],project,generated,values,filtered),[])
                validation.extend(rows)
            except (*QUALITY_ERRORS, FileNotFoundError) as error:
                self.warn('prebuilt-fallback','prebuilt-'+str(i),error)
                validation.append(dict(project=row['name'],passed=False,presetBaked=False,installedFallback=True,error=str(error)))
        self.dump(base / 'body-result.json', dict(projects=body['projects'], prebuiltPairs=len(self.prebuilt)//2, authoredValuesPreserved=len(values), defaults=self.preset['defaults'], garmentChoice=dict(zap='Remove Lower Torso Armor', choice=False, authority='D51'), validation=validation, inputRelocations=relocations, obodyGeometryGenerated=False))
        self.dump(self.root/'body-work.json', dict(route='XML-prebuilt', bodySlideInvoked=True, projectsBuilt=len(self.prebuilt)//2, selectedVariants=self.variants))

    def validate_prebuilt(self, row, node, project, generated, values, choices):
        # Independent OSD/XML simulation validates authored values and the
        # explicit intact zap choice against fresh generated geometry.
        source = project / 'ShapeData' / Path(node.findtext('DataFolder').replace('\\', '/')) / Path(node.findtext('SourceFile').replace('\\', '/'))
        src = detail(source); self.read_files.add(str(source)); self.read_bytes += source.stat().st_size * 2
        shapes = {s['name']: s for s in src['shapes']}; shape_map = {s.text: s.get('target') for s in node.findall('Shape')}
        picked = {x.get('zap'): x.get('choice') == 'true' for x in choices if x.get('project') == row['name']}
        defs = defaults(node, values, picked); rows = []
        datasets = {name: {s.get('name'): data_for(node, s, target, project) for s in node.findall('Slider')} for name, target in shape_map.items()}
        for endpoint, path in zip(('small', 'big'), generated):
            actual = detail(path); self.read_files.add(str(path)); self.read_bytes += path.stat().st_size * 2
            target_shapes = {s['name']: s for s in actual['shapes']}; expected_names = set()
            for name, target in shape_map.items():
                if name not in shapes:
                    if name in target_shapes:raise ValueError('Fresh output invented absent source shape: '+row['name']+'/'+name)
                    rows.append(dict(project=row['name'],endpoint=endpoint,shape=name,status='Definition names a shape absent from both source and generated mesh',basis='BodySlide 5.7.1 skips shape when GetVertsForShape returns false; no geometry or zap changed'))
                    continue
                s = shapes[name]; verts = [list(v) for v in s['vertices']]; uvs = [list(v) for v in s['uvs']]; zaps = set()
                for slider in node.findall('Slider'):
                    sl = slider.get('name'); evidence, diff = datasets[name][sl]
                    if evidence is None: continue
                    value = values.get((sl, endpoint), defs[sl][endpoint]); coeff = f32(value / 100)
                    if truth(slider, 'invert'): coeff = f32(1-coeff)
                    if truth(slider, 'clamp'): raise ValueError('Required clamp geometry unsupported: ' + row['name'])
                    if truth(slider, 'zap') and not truth(slider, 'uv'):
                        active = values.get((sl, 'big'), defs[sl]['big']) / 100
                        if not truth(slider, 'hidden') and sl in picked: active = float(picked[sl])
                        if active > 0: zaps.update(i for i, v in diff.items() if any(abs(c)>0 for c in v))
                    elif coeff != 0:
                        array = uvs if truth(slider,'uv') else verts
                        for i, v in diff.items():
                            if i >= s['count']: continue
                            for axis in range(2 if truth(slider,'uv') else 3): array[i][axis] = f32(array[i][axis] + f32(v[axis]*coeff))
                keep = [i for i in range(s['count']) if i not in zaps]
                if not keep:
                    if name in target_shapes: raise ValueError('Zapped shape was retained')
                    continue
                expected_names.add(name); g = target_shapes.get(name)
                if g is None: raise ValueError('Required garment/source shape missing: ' + name)
                error = metrics([verts[i] for i in keep], g['vertices'])
                if error['maximum'] >= .0002: raise ValueError('Fresh XML geometry differs: ' + row['name'] + '/' + name)
                mapping = {old: new for new, old in enumerate(keep)}; faces = [tuple(mapping[i] for i in t) for t in s['faces'] if all(i in mapping for i in t)]
                if faces != g['faces'] or s['bones'] != g['bones'] or s['transform'] != g['transform']: raise ValueError('Fresh source topology/skin binding differs')
                quantized_uvs = [tuple(struct.unpack('<e',struct.pack('<e',v))[0] for v in uvs[i]) for i in keep]
                uv_error = metrics(quantized_uvs,g['uvs'])
                if uv_error['maximum'] >= .001: raise ValueError('Fresh UV differs: ' + row['name'] + '/' + name)
                if shader_signature(src,s) != shader_signature(actual,g): raise ValueError('Fresh shader differs: ' + row['name'] + '/' + name)
                if int(s['desc'],16)>>44 != int(g['desc'],16)>>44: raise ValueError('Fresh vertex attributes differ')
                for old,new in mapping.items():
                    if s['rawVertices'][old*s['stride']+s['skinOffset']:old*s['stride']+s['skinOffset']+12] != g['rawVertices'][new*g['stride']+g['skinOffset']:new*g['stride']+g['skinOffset']+12]: raise ValueError('Fresh encoded SSE skin differs')
                expected_weights = [{mapping[i]:v for i,v in bone['weights'] if i in mapping} for bone in s['skinData']]
                if expected_weights != [dict(bone['weights']) for bone in g['skinData']] or [x['transform'] for x in s['skinData']] != [x['transform'] for x in g['skinData']]: raise ValueError('Fresh legacy skin weights/transforms differ')
                if sorted(set(part[1] for part in s['parts'])) != sorted(set(part[1] for part in g['parts'])): raise ValueError('Fresh partition binding differs')
                rows.append(dict(project=row['name'], endpoint=endpoint, shape=name, vertices=g['count'], deletedVertices=s['count']-len(keep), positionError=error, uvError=uv_error, sourceSkinChannelsPreserved=True))
            if set(target_shapes) != expected_names: raise ValueError('Unexpected fresh prebuilt shapes')
        return rows

    def isolate(self):
        private_race=self.plan.get('followerSettings',{}).get('values',{}).get('raceMode','private')!='existing'
        physics=self.quality('asset-quality','smp-inputs',lambda:physics_inputs(self.plan,self.root,lambda value,field: norm(value,'meshes' if field=='BODYTRI' else None)),[]) if private_race else []
        for source in physics:
            if hashlib.sha256(source['model'].original).hexdigest().upper()!=self.sources[source['reference']]['sha256']:raise HostProtectionError('Selected SMP source tree changed')
        roots = self.root / 'private-assets'; shared = roots / 'shared'; shared.mkdir(parents=True)
        for ref, source in self.sources.items():
            if source.get('runtimeAsset', True) and ref.startswith(('meshes/', 'textures/', 'sound/')):
                category, tail = ref.split('/', 1)
                self.paths[ref] = ref if self.choices.get(ref, {}).get('mode') == 'Retain dependency' else category+'/'+self.namespace+'/'+tail
        tint_ref = norm(self.plan['appearance']['tintReference']); self.paths[tint_ref] = self.head['tint']; self.paths['meshes/lean-export/head.nif'] = self.head['head']
        for ref, target in self.moved.items(): self.paths[ref] = target
        for ref in sorted(self.paths):
            if ref == 'meshes/lean-export/head.nif': continue
            if ref in self.prebuilt and 'obody' not in self.variants: continue
            source = self.sources[ref]; path, raw = self.staged(ref); destination = self.paths[ref]
            before = raw; changes = []; proof = None
            if ref.endswith('.nif'):
                model = Nif(raw)
                if any(ref == norm(g,'meshes') for eq in self.plan['equipment'] for g in eq['ground']):
                    names = []
                    for i, k in enumerate(model.indices):
                        if model.types[k] in ('BSTriShape','BSDynamicTriShape','BSSubIndexTriShape'):
                            names.append(model.strings[Cursor(model.blocks[i]).unpack('I')].decode('utf-8'))
                    self.ground_shapes.extend(dict(reference=ref,index=i,name=name) for i,name in enumerate(names))
                def replace(edge):
                    category = 'textures' if edge['field'] == 'texture' else 'meshes'
                    dependency = norm(edge['value'], category)
                    if dependency in self.missing: return edge['value']
                    if dependency not in self.paths:
                        self.warn('asset-quality','asset-reference','Unresolved private dependency: '+dependency);return edge['value']
                    target = self.paths[dependency]
                    return (target.removeprefix('meshes/') if edge['field'] == 'BODYTRI' else target).replace('/', '\\')
                if self.choices.get(ref, {}).get('mode') == 'Retain dependency':
                    for edge in model.references():
                        dependency = norm(edge['value'], 'textures' if edge['field']=='texture' else 'meshes')
                        if dependency not in self.missing and self.paths.get(dependency) != dependency: self.warn('asset-quality','retained-reference',ref+': retained source still calls its original dependency '+dependency)
                else:
                    changes = model.rewrite(replace); raw = model.encode(); proof = self.quality('asset-quality','asset-reference-proof',lambda:prove_only_references_changed(before,raw,changes),dict(passed=False,checksComplete=False))
            if self.choices.get(ref, {}).get('mode') == 'Retain dependency':
                row = dict(layer='shared',assetReference=ref,destination=ref,source=source,mode='Retain dependency',output=None)
                self.retained.append(dict(reference=ref,providerName=self.choices[ref]['providerName'],authority=source,sourceIdentity=self.choices[ref]['sourceIdentity'],requiredReferences=self.choices[ref]['requiredReferences'],stagedPath=str(path)))
                self.assets.append(row); continue
            dest = shared / destination; dest.parent.mkdir(parents=True, exist_ok=True)
            if changes:
                with dest.open('xb') as f: f.write(raw)
                self.generated += len(raw)
            else: os.rename(path, dest)
            self.assets.append(dict(layer='shared', assetReference=ref, destination=destination, output=dict(path=str(dest), bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper()), source=source, referenceChanges=changes, referenceProof=proof, unchangedGeometry=not changes or proof is not None and proof.get('passed',True)))
        # The finished head has already passed T09F's geometry-preserving editor.
        hpath = self.root / 'private-head' / self.head['head']; raw = self.read(hpath); dest = shared / self.head['head']; dest.parent.mkdir(parents=True, exist_ok=True); os.rename(hpath, dest)
        self.assets.append(dict(layer='shared', assetReference='meshes/lean-export/head.nif', destination=self.head['head'], output=dict(path=str(dest), bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper()), source=self.sources['meshes/lean-export/head.nif'], referenceProof=self.head['referenceProof']))
        for ref, path in self.prebuilt.items():
            raw = self.read(path); model = Nif(raw)
            changes = model.rewrite(lambda edge: edge['value'] if norm(edge['value'], 'textures' if edge['field']=='texture' else 'meshes') in self.missing else self.paths[norm(edge['value'], 'textures' if edge['field']=='texture' else 'meshes')].replace('/', '\\'))
            encoded = model.encode(); proof = self.quality('asset-quality','prebuilt-reference-proof',lambda:prove_only_references_changed(raw,encoded,changes),dict(passed=False,checksComplete=False))
            destination = 'meshes/'+self.namespace+'/'+ref.removeprefix('meshes/'); dest = roots / 'prebuilt' / destination; dest.parent.mkdir(parents=True, exist_ok=True); dest.write_bytes(encoded); self.generated += len(encoded)
            self.assets.append(dict(layer='prebuilt', assetReference=ref, destination=destination, output=dict(path=str(dest), bytes=len(encoded), sha256=hashlib.sha256(encoded).hexdigest().upper()), source=dict(kind='Fresh XML BodySlide', input=str(path)), referenceChanges=changes, referenceProof=proof))
        skeleton_source=next((s for s in self.plan['sources'] if s['consumer']=='winning selected-sex race skeleton'),None)
        bindings=[];available=set();skeleton_physics=dict(required=False,passed=False,runtimeVerified=False)
        if skeleton_source is None: self.warn('asset-quality','skeleton','Selected skeleton bytes are unavailable; original record reference preserved')
        else:
            skeleton_ref = norm(skeleton_source['reference'])
            skeleton_path = self.staged(skeleton_ref)[0] if self.choices.get(skeleton_ref,{}).get('mode')=='Retain dependency' else shared/self.paths[skeleton_ref]
            skeleton = Nif(self.read(skeleton_path)); available = set(skeleton.nodes()); bindings = []
            skeleton_physics=dict(required=False,runtimeVerified=False)
            if physics:
                head_model=Nif(self.read(shared/self.head['head']))
                if set(head_model.skin_bones())-available and self.choices.get(skeleton_ref,{}).get('mode')=='Retain dependency':
                    self.warn('asset-quality','smp-skeleton','Selected retained skeleton lacks original hair bones; installed dependency remains unchanged')
                skeleton_physics=dict(required=False,passed=False,runtimeVerified=False) if self.choices.get(skeleton_ref,{}).get('mode')=='Retain dependency' else self.quality('asset-quality','smp-skeleton',lambda:preload_head_skeleton(skeleton,head_model,physics),dict(required=False,passed=False,runtimeVerified=False))
                if skeleton_physics['required']:
                    skeleton_path.write_bytes(skeleton.encode());self.generated+=skeleton_path.stat().st_size
                    asset=next(a for a in self.assets if a['assetReference']==skeleton_ref)
                    asset['output']=dict(path=str(skeleton_path),bytes=skeleton_path.stat().st_size,sha256=hashlib.sha256(skeleton.encode()).hexdigest().upper())
                    asset['smpSkeletonPreload']=skeleton_physics;available=set(skeleton.nodes())
                    asset['referenceProofScope']='Reference edits precede SMP preload; preload input/output skeleton hashes bind the final source-hierarchy addition.'
        for asset in self.assets:
            if not asset['destination'].endswith('.nif'): continue
            path = self.staged(asset['assetReference'])[0] if asset.get('mode')=='Retain dependency' else Path(asset['output']['path'])
            model = Nif(self.read(path)); missing = set(model.skin_bones()) - available - set(model.nodes())
            if missing: self.warn('asset-quality','skeleton-binding',asset['assetReference']+' -> '+str(sorted(missing)))
            bindings.append(dict(model=asset['destination'], weightedBones=len(model.skin_bones()), missing=sorted(missing),passed=not missing))
        manifest = dict(schema='lean-private-assets/v1', missingReferences=list(self.missing.values()), inputPlan=str(self.root/'input-plan.json'), pluginIdentity=self.plugin, namespace=self.namespace, assets=self.assets, retainedDependencies=self.retained, assetChoices=self.plan.get('assetChoices',[]), skeletonBindings=bindings, smpSkeletonPreload=skeleton_physics, groundShapeNames=self.ground_shapes, variants=self.variants)
        self.dump(roots / 'asset-manifest.json', manifest); return manifest

    def package(self, manifest):
        roots = self.root / 'private-assets'; plugin_root = self.root / 'plugin'; packages = []
        record_inputs=self.load(self.root/'record-inputs.json')
        master_paths = record_inputs.get('masterPaths', {Path(x['source']).name: x['source'] for x in record_inputs['overlays']})
        self.dump(self.root/'master-paths.json', master_paths)
        fghead = f'meshes/actors/character/FaceGenData/FaceGeom/{self.plugin}/00000800.nif'; fgtint = f'textures/actors/character/FaceGenData/FaceTint/{self.plugin}/00000800.dds'
        package_root = self.root / 'packages'; package_root.mkdir()
        package_payloads = {}; package_checks = {}
        for variant in manifest['variants']:
            selected = {a['assetReference']: a for a in self.assets if a['layer']=='shared'}
            if variant=='prebuilt': selected.update({a['assetReference']:a for a in self.assets if a['layer']=='prebuilt'})
            variant_manifest = dict(manifest, assets=list(selected.values()))
            manifest_path=roots/(variant+'-manifest.json'); self.dump(manifest_path,variant_manifest)
            # One plugin identity; variant bytes differ only when asset locations differ.
            # Reuse the already-generated plugin when both maps are identical.
            locations = {k:a['destination'] for k,a in selected.items()}
            if not packages or locations != previous_locations:
                variant_plugin_root=plugin_root/variant
                try: self.run([self.writer,'generate-best-effort',manifest_path,self.root/'record-inputs.json',variant_plugin_root],PROJECT,'plugin-generation-'+variant)
                except StageFailed as error:
                    if not (variant_plugin_root/self.plugin).is_file():
                        self.warn('variant-unavailable',variant,error);continue
                    self.warn('plugin-quality',variant,error)
                validation_path=self.root/('plugin-validation-'+variant+'.json')
                self.quality('plugin-quality',variant,lambda:self.run([self.writer,'validate-best-effort',variant_plugin_root/self.plugin,self.root/'master-paths.json',validation_path,self.root/'input-plan.json',manifest_path],PROJECT,'plugin-validation-'+variant))
                plugin_bytes=self.read(variant_plugin_root/self.plugin); plugin_validation=self.load(validation_path) if validation_path.is_file() else dict(passed=False,checksComplete=False)
                if not plugin_validation.get('passed',False): self.warn('plugin-quality',variant,'Independent plugin readback failed: '+json.dumps(plugin_validation))
                plugin_validation['masters']=read_bytes(plugin_bytes)['masters']
                record_result=self.quality('plugin-quality','writer-details-'+variant,lambda:self.load(variant_plugin_root/'records.json'),dict(dependencies=[],buildWarnings=[]))
                for row in record_result.get('buildWarnings',[]):
                    self.warn('plugin-quality',variant,row.get('detail') or row.get('Detail') or 'Writer quality assessment failed')
                plugin_validation['requiredMasterReasons']=[d for d in record_result['dependencies'] if d['master']!='Skyrim.esm']
                self.quality('plugin-quality','settings-'+variant,lambda:require_settings_readback(self.plan,plugin_validation))
                plugin_validation['savedPlacement']=self.quality('plugin-quality','placement-'+variant,lambda:validate_placement(plugin_bytes,self.plan),dict(passed=False,checksComplete=False))
                if not packages:
                    self.dump(self.root/'plugin-validation.json',plugin_validation)
                    (plugin_root/self.plugin).write_bytes(plugin_bytes)
            previous_locations=locations
            data = {a['destination']:self.read(Path(a['output']['path'])) for a in selected.values() if a.get('mode')!='Retain dependency'}
            external = [r for r in self.retained if selected[r['reference']].get('mode')=='Retain dependency']
            external_data = {r['reference']:self.read(Path(r['stagedPath'])) for r in external}
            original = data.pop(self.head['head']); tint = data.pop(self.head['tint']); n = Nif(original)
            changes = n.rewrite(lambda r: fgtint.replace('/', '\\') if r['field']=='texture' and r['slot']==6 and norm(r['value'],'textures')==self.head['tint'] else r['value'])
            if len(changes)!=1: self.warn('head-quality',variant,'Final FaceGen tint binding is not unique')
            data[fghead] = n.encode(); data[fgtint] = tint; data[self.plugin] = plugin_bytes
            proof = self.quality('head-quality','facegen-reference-proof-'+variant,lambda:prove_only_references_changed(original,data[fghead],changes),dict(passed=False,checksComplete=False))
            if self.preset:
                data['docs/presets/original/Sen Abersha.xml'] = self.preset['original']
                prefix = 'docs/presets/completed/' if variant=='prebuilt' else 'CalienteTools/BodySlide/SliderPresets/'
                data[prefix+self.preset['runtimeName']+'.xml'] = self.preset['completed']
            successor_members(data, variant, self.plugin)
            compatibility=self.quality('compatibility-unavailable',variant,lambda:emit_compatibility(data,self.plugin,self.uuid,variant,self.preset['runtimeName'] if self.preset else None))
            data['README-T08.txt'] += (f'\nLean export build: selected ESS/co-save and required RaceMenu export bundle, selected installed Data sources. Export association is explicit and has no unique ESS/session binding. Preset: {self.preset["runtimeName"] if self.preset else "none"}. Runtime behavior remains unverified. {self.plan['skin']['runtimeAssignmentOmission']}\n').encode()
            for warning in self.plan.get('assetRepairWarnings') or []:
                data['README-T08.txt'] += ('\nAsset repair: '+warning+'\n').encode()
            for warning in self.plan.get('runtimeAssignmentWarnings') or []:
                data['README-T08.txt'] += ('\nRuntime assignment: '+warning+'\n').encode()
            data['README-T08.txt'] += ('\n'+self.overlay_notice['warning']+'\nThe supplied head and tint are preserved with private reference edits. Full overlay reconstruction and reapplication are deferred; exports do not guarantee every overlay is included.\n').encode()
            for conflict in self.plan.get('materialConflicts') or []:
                data['README-T08.txt'] += ('\nMaterial warning: '+conflict['warning']+'\n').encode()
            if self.plan['skin'].get('asIsFootModels'):
                data['README-T08.txt'] += b'\nBase feet and other non-body skin use captured installed geometry in every output, with private reference edits only. Only bodies and outfits affected by the applied OBody preset receive a BodySlide build. The Prebuilt output does not bake base foot morphs; recipient OBody foot behavior remains unverified.\n'
            for operation in self.overlay_notice['omittedOperations']:
                data['README-T08.txt'] += ('Deferred overlay operation: '+operation['scope']+' on '+operation['node']+'\n').encode()
            # Retain only transitive runtime members reachable from emitted
            # records/head/models. An installed TRI replaced in prebuilt is not
            # an uncalled payload in that ZIP.
            used = {k for k in data if not k.lower().endswith('.tri')}
            for record in read_bytes(plugin_bytes)['records']:
                if record['tag']=='HDPT':
                    for tag,value in fields(record):
                        if tag=='NAM1' and value.endswith(b'.tri\0'):used.add(norm(value[:-1].decode(),'meshes'))
            for name, raw in list(data.items()):
                if name.lower().endswith('.nif'):
                    for edge in Nif(raw).references():
                        if edge['field']=='BODYTRI': used.add(norm(edge['value'],'meshes'))
            data = {k:v for k,v in data.items() if k in used}
            hosts = self.plan.get('hostResources', [])
            validation = self.assessment(data,variant,hosts,external,external_data)
            if not validation.get('passed'): self.warn('package-quality',variant,json.dumps(validation))
            # Report only dependencies actually called in this variant. A retained
            # installed BODYTRI may be unused after the prebuilt override.
            external=[r for r in external if r['reference'] in validation['retainedReferences'] or self.choices.get(r['reference'],{}).get('requiredMode')=='Retain dependency']
            external_data={r['reference']:external_data[r['reference']] for r in external}
            classification='Contains unresolved original asset references' if validation['missingReferences'] else 'Mixed / retained dependencies' if external else 'Standalone assets'
            declaration='\nPackaging: '+classification+'. Required plugin masters: '+', '.join(plugin_validation['masters'])+'\n'
            for row in external:
                s=row['authority']; declaration+='Retain '+row['reference']+' from '+s['backing']+((' :: '+s['archiveMember']) if s.get('archiveMember') else '')+'; SHA256 '+s['sha256']+(('; '+s['activation']) if s.get('activation') else '')+'\n'
            race_mode=self.plan.get('followerSettings',{}).get('values',{}).get('raceMode','private')
            declaration+='Race: '+('Use existing captured race '+self.plan['savedActor']['race']+'; installed race/skeleton provider required; no skeleton packaged.' if race_mode=='existing' else 'Generate new custom race; owned links remapped. Independent required masters remain as listed.')+'\n'
            for dependency in sorted({ (d['master'],d['target'],d['reason']) for d in plugin_validation['requiredMasterReasons'] }):
                declaration+='Required '+dependency[0]+' :: '+dependency[1]+' — '+dependency[2]+'\n'
            if race_mode=='existing' and any(name.lower().endswith('.nif') and 'skeleton' in Path(name).name.lower() for name in data):raise ValueError('Existing race output contains a skeleton NIF')
            data['README-T08.txt']+=declaration.encode()
            target=self.plan['savedActor']['placement']
            data['README-T08.txt']+=('Saved destination: '+target['cell']+'; worldspace '+str(target['worldspace'])+'; position '+str(target['position'])+'; rotation radians '+str(target['rotation'])+'; in-game accessibility/persistence unverified.\n').encode()
            validation=self.assessment(data,variant,hosts,external,external_data)
            package_payloads[variant] = data
            package_checks[variant] = (hosts, external, external_data, validation)
            called_fields={edge['field'] for name,raw in {**data,**external_data}.items() if name.lower().endswith('.nif') for edge in Nif(raw).references()}
            host_requirements=[]
            if 'HDT Skinned Mesh Physics Object' in called_fields:host_requirements.append(dict(name='Compatible SMP physics host for the recipient Skyrim runtime',basis='Emitted or retained NIF calls HDT Skinned Mesh Physics Object',runtimeVerified=False))
            if 'BODYTRI' in called_fields:host_requirements.append(dict(name='Compatible RaceMenu/SKEE morph host',basis='Emitted or retained NIF calls BODYTRI',runtimeVerified=False))
            # Heel lifts are regenerated from original footwear metadata. A
            # cached co-save lift is never applied to placement or geometry.
            heel_metadata=set()
            for name,raw in {**data,**external_data}.items():
                if not name.lower().endswith('.nif'):continue
                mesh=Nif(raw)
                for block,kind in zip(mesh.blocks,mesh.indices):
                    if mesh.types[kind] in ('NiFloatExtraData','NiStringExtraData') and len(block)==8:
                        key=struct.unpack_from('<I',block)[0]
                        if key<len(mesh.strings) and mesh.strings[key] in (b'HH_OFFSET',b'SDTA'):heel_metadata.add(mesh.strings[key].decode())
            if heel_metadata:host_requirements.append(dict(name='Compatible RaceMenu/SKEE equippable-transform host; keep Heels Fix for its interaction corrections',basis='Original footwear adjustment metadata preserved: '+', '.join(sorted(heel_metadata))+'; heel lift reapplied at runtime, never baked into the follower',runtimeVerified=False))
            if variant=='obody':host_requirements.append(dict(name='Compatible installed OBody NG',basis='Captured installed body/outfits with the packaged assigned preset' if self.preset else 'Captured installed body/outfits; no recoverable preset or automatic assignment is claimed',runtimeVerified=False))
            for host in host_requirements:data['README-T08.txt']+=('Required host: '+host['name']+'; '+host['basis']+'\n').encode()
            if self.plan.get('followerSettings'):
                data['README-T08.txt']+=(level_summary(self.plan['followerSettings'])+'\n').encode()
            for resource in hosts:
                if resource.get('status')=='RequiredHostRecord':
                    data['README-T08.txt']+=('Required original gameplay effect: '+resource['reference']+'; its origin master supplies effect art and scripts; runtime behavior unverified\n').encode()
                if resource.get('status')=='Resolved':
                    source=resource['authority']
                    data['README-T08.txt']+=('Required race system asset: '+resource['reference']+' from '+source['backing']+((' :: '+source['archiveMember']) if source.get('archiveMember') else '')+'; selected race behavior preserved, runtime unverified\n').encode()
            dependencies=[{k:v for k,v in r.items() if k!='stagedPath'} for r in external]
            asset_results=self.asset_results(variant,selected,data,external,fghead,fgtint)
            payload_hash=hashlib.sha256()
            for name,raw in sorted(data.items()):payload_hash.update(name.encode('utf-8')+b'\0'+hashlib.sha256(raw).digest())
            skin_notice='Base feet and other non-body skin use captured installed geometry in every output, with private reference edits only. Only bodies and outfits affected by the applied OBody preset receive a BodySlide build. The Prebuilt output does not bake base foot morphs; recipient OBody foot behavior remains unverified.' if self.plan['skin'].get('asIsFootModels') else None
            packages.append(dict(variant=variant, buildWarnings=list(self.warnings),presetAvailable=self.preset is not None,presetBakeComplete=variant!='prebuilt' or not any(w['category'] in ('prebuilt-fallback','prebuilt-quality') for w in self.warnings), overlayNotice=self.overlay_notice, materialConflicts=self.plan.get("materialConflicts") or [], runtimeAssignmentWarnings=self.plan.get("runtimeAssignmentWarnings") or [], assetRepairWarnings=self.plan.get("assetRepairWarnings") or [], equipmentOmissions=self.plan.get("equipmentOmissions") or [], missingReferences=list(self.missing.values()), skinGeometryNotice=skin_notice, classification=classification,followerSettings=self.plan.get('followerSettings'),assetResults=asset_results, retainedDependencies=dependencies,requiredMasters=plugin_validation['masters'],hostRequirements=host_requirements,compatibility=compatibility,pluginValidation=plugin_validation,payloadMapSha256=payload_hash.hexdigest().upper(),members=len(data),uncompressedBytes=sum(map(len,data.values())), validation=validation, faceGenReferenceProof=proof))
        def applicable_warnings(variant):
            def applies(row):
                if row.get('category')=='variant-unavailable': return True
                scoped=next((v for v in self.variants if row['stage'] in (v,'settings-'+v,'placement-'+v,'writer-details-'+v,'selected-placement-'+v,'facegen-reference-proof-'+v)),None)
                return scoped is None or scoped==variant
            return [row for row in self.warnings if applies(row)]
        def finalize_quality(maps):
            # No output has been sealed yet. Assess exact selected branch maps,
            # collect later findings, then finalize every surviving recipient.
            for package in packages:
                variant=package['variant'];branch=maps[variant]
                hosts,external,external_data,prior_validation=package_checks[variant]
                payload={k:v for k,v in branch.items() if k not in ('docs/Installation.txt','docs/Provenance.txt')}
                selected_validation=self.assessment(payload,variant,hosts,external,external_data)
                self.quality('plugin-quality','selected-placement-'+variant,lambda:validate_placement(payload[self.plugin],self.plan))
                if selected_validation!=prior_validation:self.warn('package-quality',variant,'Selected branch assessment differs from the prior assessment')
                package['selectedPayloadValidation']=selected_validation
            for package in packages:package['buildWarnings']=applicable_warnings(package['variant'])
        for package in packages:package['buildWarnings']=applicable_warnings(package['variant'])
        distribution_started=time.monotonic()
        if not packages: raise ValueError('No requested variant produced a serializable core plugin; no follower ZIP can be written')
        self.delivered_variants=[p['variant'] for p in packages]
        if len(packages)==1:
            distribution = build_plain(package_root, package_payloads, packages, self.plugin, self.uuid, self.preset['runtimeName'] if self.preset else None, self.mod_name, before_write=finalize_quality)
        else:
            distribution = build_fomod(package_root, package_payloads, packages, self.plugin, self.uuid, True, self.preset['runtimeName'] if self.preset else None, self.mod_name, before_write=finalize_quality)
            distribution['format']='fomod'
        for package in packages:
            variant=package['variant']
            branch=distribution['branchMaps'][variant]
            member_manifest=package_root/(variant+'-payload-manifest.json')
            self.dump(member_manifest,dict(variant=variant,plugin=self.plugin,memberCount=len(branch),members=[dict(path=name,bytes=len(raw),sha256=hashlib.sha256(raw).hexdigest().upper()) for name,raw in sorted(branch.items())]))
            package['memberManifest']=str(member_manifest)
            payload_hash=hashlib.sha256()
            for name,raw in sorted(branch.items()):payload_hash.update(name.encode('utf-8')+b'\0'+hashlib.sha256(raw).digest())
            package['payloadMapSha256']=payload_hash.hexdigest().upper()
            package['members']=len(branch);package['uncompressedBytes']=sum(map(len,branch.values()))
        distribution['elapsedMilliseconds']=int((time.monotonic()-distribution_started)*1000)
        for package in packages:
            package['distribution'] = {k:v for k,v in distribution.items() if k not in ('branchMaps',)}
        self.dump(package_root/'packages.json', dict(packages=packages, distribution={k:v for k,v in distribution.items() if k not in ('branchMaps',)})); return packages, distribution

    def asset_results(self,variant,selected,data,external,fghead,fgtint):
        retained={r['reference'] for r in external};rows=[]
        for choice in self.plan.get('assetChoices',[]):
            ref=choice['reference'];destination=None
            if variant not in choice['variants']:
                if variant=='prebuilt' and ref in selected and ref not in self.prebuilt:
                    destination=selected[ref]['destination'];status='Captured installed fallback; requested preset bake unavailable'
                else:status='Not applicable to this variant'
            else:
                if choice['id']=='generated/original-preset':destination='docs/presets/original/Sen Abersha.xml'
                elif choice['id']=='generated/completed-preset':destination=(('docs/presets/completed/' if variant=='prebuilt' else 'CalienteTools/BodySlide/SliderPresets/')+self.preset['runtimeName']+'.xml') if self.preset else None
                elif ref=='meshes/lean-export/head.nif':destination=fghead
                elif ref==norm(self.plan['appearance']['tintReference']):destination=fgtint
                else:destination=selected.get(ref,{}).get('destination')
                status='Retained dependency' if ref in retained else 'Packaged' if destination in data else 'Unavailable in this variant'
                if choice['layer']=='prebuilt' and ref not in self.prebuilt: status='Captured installed fallback; requested preset bake unavailable'
            rows.append(dict(id=choice['id'],mode=choice['mode'],status=status,destination=destination,providerName=choice.get('providerName'),sourceIdentity=choice.get('sourceIdentity')))
        return rows

    def retain_package(self, distribution):
        # The ZIP may outlive a crash before either result publication. Keep a
        # durable ownership-bound completion receipt while the stage lease is
        # held; absence of this receipt still cannot authorize ZIP deletion.
        marker = self.root / 'storage-owner.json'
        if not marker.is_file():
            return  # Older isolated stage callers have no lifecycle to update.
        owner = json.loads(marker.read_text(encoding='utf-8'))
        path = Path(distribution['path']).resolve()
        if owner['kind'] != 'Lazybones/working-storage-v1' or Path(owner['directory']).resolve() != self.root or path.parent != self.root / 'packages' or path.suffix.lower() != '.zip':
            raise ValueError('Package lifecycle ownership disagrees')
        receipt = dict(kind='Lazybones/package-ready-v1', ownerId=owner['id'], path=str(path), sha256=distribution['sha256'], bytes=path.stat().st_size)
        pending = self.root / ('state-' + os.urandom(16).hex() + '.partial')
        with pending.open('x', encoding='utf-8') as output:
            json.dump(receipt, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(pending, self.root / 'package-ready.json')

    def build(self):
        try:
            self.quality('prebuilt-fallback','body-transform',self.body)
            self.quality('compatibility-unavailable','authored-preset',self.captured_preset)
            manifest = self.isolate(); packages, distribution = self.package(manifest)
            self.retain_package(distribution)
            failures=[dict(validationFailure=p['pluginValidation']['validationFailure']) for p in packages if p['pluginValidation'].get('validationFailure')]
            self.dump(self.root/'package-stage-result.json', dict(status='built-with-warnings' if self.warnings else 'structural-build-passed',buildWarnings=self.warnings,validationFailures=failures,requestedVariants=self.variants,deliveredVariants=self.delivered_variants,unavailableVariants=[v for v in self.variants if v not in self.delivered_variants],qualityFindingsPresent=bool(self.warnings), overlayNotice=self.overlay_notice, packages=packages, distribution={k:v for k,v in distribution.items() if k not in ('branchMaps',)}, stages=self.stages, bytesRead=self.read_bytes, filesRead=len(self.read_files), generatedBytes=self.generated, copiedBytes=self.copied, elapsedMilliseconds=int((time.monotonic()-self.started)*1000), runtimeVerified=False, noDllPexOrHelpers=True, externalToolIoInstrumented=False))
        except BaseException as e:
            self.dump(self.root/'package-stage-result.json', dict(status='cancelled' if isinstance(e,InterruptedError) else 'failed', error=type(e).__name__+': '+str(e), stages=self.stages, bytesRead=self.read_bytes, filesRead=len(self.read_files), elapsedMilliseconds=int((time.monotonic()-self.started)*1000), outputsPreserved=True)); raise

if __name__=='__main__': Build(*sys.argv[1:]).build()
