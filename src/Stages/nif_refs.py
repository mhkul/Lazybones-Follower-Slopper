"""Bounded SSE NIF reference editor. Opaque non-reference blocks stay byte-identical.

Supports 20.2.0.7 / user 12 / BS 83 or 100. BS83 geometry stays opaque/as-is.
Header/reference framing follows niftools nif.xml. No geometry resave.
Path edits are limited to BSShaderTextureSet and path-valued NiStringExtraData.
"""
import struct

def texture_reference(raw):
    """Recover only an unambiguous embedded drive-absolute DDS suffix."""
    value=raw.replace('\\','/')
    if len(value)>=3 and value[0].isascii() and value[0].isalpha() and value[1:3]==':/':
        parts=value[3:].split('/')
        if len(value)>4096 or any(ord(c)<32 or ord(c)==127 for c in value) or any(c in value[3:] for c in ':*?"<>|') or any(not p or p in ('.','..') or p.endswith((' ','.')) for p in parts):
            raise ValueError('Malformed creator texture path')
        roots=[i for i,p in enumerate(parts) if p.lower()=='textures']
        if len(roots)!=1 or roots[0]>=len(parts)-1 or not value.lower().endswith('.dds'):
            raise ValueError('Creator texture path has no unique complete textures/*.dds suffix')
        return '/'.join(parts[roots[0]:])
    return raw


class Cursor:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def take(self, count):
        if count < 0 or self.pos + count > len(self.data):
            raise ValueError('Truncated NIF')
        result = self.data[self.pos:self.pos + count]
        self.pos += count
        return result

    def unpack(self, fmt):
        values = struct.unpack('<' + fmt, self.take(struct.calcsize('<' + fmt)))
        return values[0] if len(values) == 1 else values

    def string(self, fmt='I'):
        return self.take(self.unpack(fmt))


def sized(raw):
    return struct.pack('<I', len(raw)) + raw


class Nif:
    def __init__(self, data):
        if len(data) > 128 * 1024 * 1024:
            raise ValueError('NIF exceeds bound')
        r = Cursor(data)
        r.take(data.index(b'\n') + 1)
        version, endian, user, count, bs = r.unpack('IBIII')
        if (version, endian, user) != (0x14020007, 1, 12) or bs not in (83, 100) or count > 100000:
            raise ValueError('Unsupported NIF header')
        for _ in range(3):
            r.string('B')
        self.type_header = data[:r.pos]
        self.block_count_offset = data.index(b'\n') + 1 + 9
        self.types = [r.string().decode('ascii') for _ in range(r.unpack('H'))]
        self.bs_version = bs
        if bs == 83 and any(t in ('BSTriShape', 'BSDynamicTriShape', 'BSSubIndexTriShape') for t in self.types):
            raise ValueError('SSE shape in BS83 mesh is unsupported')
        self.indices = [r.unpack('H') for _ in range(count)]
        if any(i >= len(self.types) for i in self.indices):
            raise ValueError('Invalid block type index')
        self.before_sizes = data[:r.pos]
        sizes = [r.unpack('I') for _ in range(count)]
        string_count, _ = r.unpack('II')
        if string_count > 100000:
            raise ValueError('String bound')
        self.strings = [r.string() for _ in range(string_count)]
        start = r.pos
        for _ in range(r.unpack('I')):
            r.unpack('I')
        self.groups = data[start:r.pos]
        self.blocks = [r.take(n) for n in sizes]
        self.footer = data[r.pos:]
        foot = Cursor(self.footer)
        roots = [foot.unpack('i') for _ in range(foot.unpack('I'))]
        if foot.pos != len(self.footer) or any(i < 0 or i >= count for i in roots):
            raise ValueError('Invalid footer roots')
        self.original = data

    def append_block(self, kind, raw):
        if self.bs_version != 100 or len(self.blocks) >= 100000:
            raise ValueError('Unsupported appended NIF block')
        if kind not in self.types: self.types.append(kind)
        index = len(self.blocks)
        self.indices.append(self.types.index(kind)); self.blocks.append(raw)
        header = bytearray(self.type_header)
        struct.pack_into('<I', header, self.block_count_offset, len(self.blocks))
        self.before_sizes = (bytes(header) + struct.pack('<H', len(self.types))
            + b''.join(sized(t.encode('ascii')) for t in self.types)
            + b''.join(struct.pack('<H', i) for i in self.indices))
        return index

    def encode(self):
        return (self.before_sizes + b''.join(struct.pack('<I', len(b)) for b in self.blocks)
                + struct.pack('<II', len(self.strings), max(map(len, self.strings), default=0))
                + b''.join(sized(s) for s in self.strings) + self.groups
                + b''.join(self.blocks) + self.footer)

    def references(self):
        result = []
        for index, (kind_index, raw) in enumerate(zip(self.indices, self.blocks)):
            kind = self.types[kind_index]
            r = Cursor(raw)
            if kind == 'BSShaderTextureSet':
                for slot in range(r.unpack('I')):
                    value = r.string().decode('utf-8')
                    if value:
                        result.append(dict(block=index, field='texture', slot=slot, value=value))
                if r.pos != len(raw):
                    raise ValueError('Texture block trailing data')
            elif kind == 'NiStringExtraData':
                name, value = r.unpack('II')
                if r.pos != len(raw) or max(name, value) >= len(self.strings):
                    raise ValueError('Invalid string extra data')
                name = self.strings[name].decode('utf-8')
                value = self.strings[value].decode('utf-8')
                if name in ('BODYTRI', 'HDT Skinned Mesh Physics Object'):
                    result.append(dict(block=index, field=name, value=value))
                elif value.lower().endswith(('.nif', '.tri', '.dds', '.xml', '.bgsm', '.bgem')):
                    raise ValueError('Unsupported path-valued extra data: ' + name)
        return result

    def rewrite(self, resolve):
        changes = []
        for ref in self.references():
            replacement = resolve(ref)
            if replacement == ref['value']:
                continue
            index = ref['block']
            if ref['field'] == 'texture':
                r = Cursor(self.blocks[index])
                paths = [r.string() for _ in range(r.unpack('I'))]
                paths[ref['slot']] = replacement.encode('utf-8')
                self.blocks[index] = struct.pack('<I', len(paths)) + b''.join(sized(p) for p in paths)
            else:
                # Allocate an independent string: shared string indices must not
                # accidentally rename node/shape names or unrelated extra data.
                name, _ = struct.unpack('<II', self.blocks[index])
                self.blocks[index] = struct.pack('<II', name, len(self.strings))
                self.strings.append(replacement.encode('utf-8'))
            changes.append(dict(**ref, replacement=replacement))
        return changes

    def nodes(self):
        result = []
        for i, raw in zip(self.indices, self.blocks):
            if self.types[i] in ('NiNode', 'BSFadeNode'):
                key = struct.unpack_from('<I', raw)[0]
                if key == 0xffffffff:  # Valid unnamed node, e.g. generated head root.
                    continue
                if key >= len(self.strings):
                    raise ValueError('Invalid node name index')
                result.append(self.strings[key].decode('utf-8'))
        return result

    def skin_bones(self):
        result = set()
        for i, raw in zip(self.indices, self.blocks):
            if self.types[i] not in ('NiSkinInstance', 'BSDismemberSkinInstance'):
                continue
            r = Cursor(raw)
            r.take(12)
            for _ in range(r.unpack('I')):
                block = r.unpack('i')
                if block < 0 or block >= len(self.blocks) or self.types[self.indices[block]] not in ('NiNode', 'BSFadeNode'):
                    raise ValueError('Invalid skin bone link')
                name = struct.unpack_from('<I', self.blocks[block])[0]
                result.add(self.strings[name].decode('utf-8'))
        return sorted(result)


def prove_only_references_changed(before, after, changes):
    a, b = Nif(before), Nif(after)
    if a.before_sizes != b.before_sizes or a.groups != b.groups or a.footer != b.footer:
        raise ValueError('Non-reference header/footer changed')
    edited = {c['block'] for c in changes}
    if b.strings[:len(a.strings)] != a.strings:
        raise ValueError('Original string table changed')
    for i, (x, y) in enumerate(zip(a.blocks, b.blocks)):
        if i not in edited and x != y:
            raise ValueError('Non-reference block changed: ' + str(i))
    expected = [{**r, 'value': next((c['replacement'] for c in changes
                 if c['block'] == r['block'] and c.get('slot') == r.get('slot')), r['value'])}
                for r in a.references()]
    if expected != b.references():
        raise ValueError('Reference readback mismatch')
    # Restore the precise allowed reference block bytes and appended strings.
    for i in edited:
        b.blocks[i] = a.blocks[i]
    b.strings = a.strings
    if b.encode() != a.encode():
        raise ValueError('Reference reversal mismatch')
    return dict(blockCount=len(a.blocks), unchangedOpaqueBlocks=len(a.blocks)-len(edited),
                originalStringsPreserved=len(a.strings), editedReferenceBlocks=sorted(edited),
                geometrySkinPartitionsShadersControllersPreserved=True)
