"""Quality findings are independent of delivery and filesystem protection."""
import struct
import xml.etree.ElementTree as ET
from pathlib import Path


class HostProtectionError(Exception):
    """Never handled as an output-quality finding."""


class StageFailed(ValueError):
    def __init__(self, stage, exit_code):
        self.stage = stage
        self.exit_code = exit_code
        super().__init__('Stage failed: ' + stage + '; exitCode=' + str(exit_code))


QUALITY_ERRORS = (ValueError, struct.error, ET.ParseError, FileNotFoundError,
                  KeyError, IndexError, StopIteration, UnicodeDecodeError)


MESSAGES = {
    'input-quality': 'Some selected inputs are incomplete or disagree. Available source content is preserved; appearance or behavior may differ.',
    'prebuilt-fallback': 'The requested preset bake was not completed for all targets. Available captured installed meshes are used for those targets; their shape may differ and OBody may be needed.',
    'prebuilt-native-exit': 'BodySlide exited abnormally after writing independently verified preset meshes. Those meshes are used; the process failure remains recorded and in-game appearance is unverified.',
    'prebuilt-quality': 'Written BodySlide meshes failed an independent quality check. They are preserved with this failed assessment; their shape or coverage may be wrong.',
    'head-quality': 'A head quality or appearance check failed. Available exported content is preserved; face, expressions or physics may differ.',
    'asset-quality': 'An asset or skeleton quality check failed. Available installed content and safe references are preserved; appearance or physics may differ.',
    'plugin-quality': 'The follower plugin failed one or more record/reference checks. Original serialized links are preserved; missing dependencies or invalid links may prevent Skyrim from loading it.',
    'package-quality': 'A package quality check failed. The available follower is delivered with the failed assessment; assets or behavior may be incomplete.',
    'variant-unavailable': 'A requested variant could not be serialized. Other completed requested variants are delivered; the unavailable variant is not included.',
    'compatibility-unavailable': 'Some requested compatibility configuration could not be generated. No missing preset or successful assignment is invented.',
}


def finding(code, stage, detail):
    return dict(schema='Lazybones/output-quality-finding-v1', category=code,
                stage=stage, passed=False, warning=MESSAGES[code], detail=str(detail))


def guard_members(data):
    seen = set()
    for name in data:
        value = name.replace('\\', '/')
        if value.startswith('/') or ':' in value or any(p in ('', '.', '..') for p in value.split('/')) or value.lower() in seen:
            raise HostProtectionError('Unsafe/colliding package member')
        if Path(value).suffix.lower() in ('.dll', '.exe', '.pex', '.psc', '.py', '.json', '.jslot', '.ps1', '.bat', '.cmd'):
            raise HostProtectionError('Private/developer/runtime content is outside follower delivery scope')
        seen.add(value.lower())


def owned_path(root, value):
    # Inspect original path components before resolve() can hide a junction.
    raw = Path(value).absolute()
    current = raw
    while current != root and current != current.parent:
        if current.is_symlink() or current.is_junction():
            raise HostProtectionError('Owned path is a link')
        current = current.parent
    resolved = raw.resolve()
    if not resolved.is_relative_to(root):
        raise HostProtectionError('Stage path escapes owned output')
    return resolved
