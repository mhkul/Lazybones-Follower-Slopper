"""D115/D157: final-plugin/FormID PDA rules; no runtime/global-state writes."""
from pathlib import PurePosixPath
import xml.etree.ElementTree as ET
import struct
from ground_records import read_bytes
from validate_binary import fields

SWITCH_LIMIT=('When changing Prebuilt/as-is to OBody, a previously applied PDA blacklist may persist in OBody_presetDistributionConfig.json. '
    'Close the game and use the installed host\'s supported configuration workflow to remove only this generated plugin/sex exclusion and any legacy blacklist entry for this follower before applying the OBody rule. '
    'Back up that host configuration first. This builder does not edit it or emit an unverified cleanup rule. '
    'Replacing files does not reset saved morphs; use installed OBody reset/application facilities and verify the result. Automatic migration is not provided.')

def safe_value(value,label):
    if not isinstance(value,str) or not value or value!=value.strip() or any(ord(c)<32 or c in '|,;#=[]<>:"/\\?*' for c in value):
        raise ValueError('Unrepresentable '+label+' in compatibility INI; delimiters, comments and line breaks are unsupported')
    return value

def npc_identity(raw,plugin):
    plugin=safe_value(plugin,'plugin filename')
    if PurePosixPath(plugin).name!=plugin or not plugin.lower().endswith('.esp'):
        raise ValueError('PDA assignment requires a generated ESPFE filename')
    parsed=read_bytes(raw)
    if not parsed['records'][0]['flags']&0x200:
        raise ValueError('PDA FExxx identity requires an ESL-flagged plugin')
    npcs=[r for r in parsed['records'] if r['tag']=='NPC_']
    if len(npcs)!=1:raise ValueError('Generated follower NPC identity is not unique')
    npc=npcs[0];local=npc['id']&0xffffff
    if npc['id']>>24!=len(parsed['masters']) or not 0<local<=0xfff or npc['flags']&0x20:
        raise ValueError('Generated NPC is not a live owned representable ESL identity')
    values=fields(npc)
    edid_values=[v for k,v in values if k=='EDID']
    if len(edid_values)!=1 or not edid_values[0].endswith(b'\0') or b'\0' in edid_values[0][:-1]:
        raise ValueError('Generated NPC EditorID absent, malformed or ambiguous')
    edid=edid_values[0][:-1].decode('utf-8')
    acbs=[v for k,v in values if k=='ACBS']
    if len(acbs)!=1 or len(acbs[0])!=24:raise ValueError('Generated NPC sex flags absent or ambiguous')
    female=bool(struct.unpack_from('<I',acbs[0])[0]&1)
    return dict(npcEditorID=safe_value(edid,'NPC EditorID'),pluginFilename=plugin,
        npcLocalFormID=f'{local:06X}',pdaFormID=f'FExxx{local:03X}',esl=True,
        npcSex='female' if female else 'male',exclusionCategory='NpcsPluginFemale' if female else 'NpcsPluginMale')

def packaged_preset(data):
    xmls=[v for k,v in data.items() if k.startswith('CalienteTools/BodySlide/SliderPresets/') and k.endswith('.xml')]
    if len(xmls)!=1:raise ValueError('OBody compatibility requires one actual packaged preset XML')
    presets=ET.fromstring(xmls[0]).findall('Preset')
    if len(presets)!=1:raise ValueError('Packaged XML preset internal name is ambiguous')
    return safe_value(presets[0].get('name'),'packaged preset internal name')

def pda_rule(npc,variant,preset=None):
    if variant=='obody':return 'npcFormID = '+npc['pluginFilename']+'|'+npc['pdaFormID']+','+preset+'|\n'
    if variant in ('prebuilt','as-is'):return 'blacklisted = '+npc['exclusionCategory']+'|'+npc['pluginFilename']+'|\n'
    raise ValueError('Unsupported compatibility variant')

def filenames(identity):
    identity=safe_value(identity,'stable output identity')
    if PurePosixPath(identity).name!=identity or any(c in identity for c in ':\\/*?<>"'):raise ValueError('Unsafe config filename identity')
    return 'AutoFollower_'+identity+'_RSV_DISTR.ini','OBodyNG_PDA_AutoFollower_'+identity+'.ini'

def emit(data,plugin,identity,variant,preset):
    npc=npc_identity(data[plugin],plugin);edid=npc['npcEditorID'];rsv,pda=filenames(identity)
    name=packaged_preset(data) if variant=='obody' else None
    if variant=='obody' and preset!=name:raise ValueError('Requested preset differs from actual packaged XML')
    rule=pda_rule(npc,variant,name)
    data[rsv]=('Keyword = RSVignore|'+edid+'\n').encode('utf-8')
    data[pda]=rule.encode('utf-8')
    return validate(data,plugin,identity,variant)

def validate(data,plugin,identity,variant):
    npc=npc_identity(data[plugin],plugin);edid=npc['npcEditorID'];rsv,pda=filenames(identity)
    if data.get(rsv)!=('Keyword = RSVignore|'+edid+'\n').encode('utf-8'):raise ValueError('Missing or incorrectly scoped RSVignore rule')
    preset=packaged_preset(data) if variant=='obody' else None
    expected=pda_rule(npc,variant,preset)
    if data.get(pda)!=expected.encode('utf-8'):raise ValueError('PDA rule is absent, mixed, stale, or targets the wrong NPC/preset')
    return dict(**npc,presetInternalName=preset,pdaRule=expected.rstrip('\n'),files=[rsv,pda],runtimeVerified=False,
        requirements=[dict(name='RSV and SPID',condition='Recipient uses RSV; keyword prevents its head/skin distribution'),dict(name='Compatible OBody NG and PDA NG',condition='Required for automatic preset assignment' if variant=='obody' else 'Only needed if recipient uses OBody and wants the omission rule applied')],switching=SWITCH_LIMIT)
