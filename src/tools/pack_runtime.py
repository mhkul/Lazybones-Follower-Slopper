"""Pack the official private Python, current stages and self-contained writer.

Developer Python is used only for source builds. The delivered GUI contains
this ZIP and resolves its own runtime regardless of user interpreter settings.
"""
from pathlib import Path
import hashlib, json, sys, zipfile

payload, python_zip, writer, output = map(Path, sys.argv[1:])
expected = '91D828C2DA3A029B41699E918674A0CB379C02CF20DAB9C501306885F837402A'
if hashlib.sha256(python_zip.read_bytes()).hexdigest().upper() != expected:
    raise ValueError('Require the pinned official Python 3.13.9 amd64 embeddable ZIP')
# Native modules used by the production stdlib/stage closure. No general Python
# environment is exposed. ctypes supports verified Windows path aliases and
# owned-process identities/leases. OpenSSL/SQLite/bzip2/xz extensions are unused.
keep={'python.exe','python313.dll','python3.dll','vcruntime140.dll','vcruntime140_1.dll','LICENSE.txt','python313.zip','python313._pth','pyexpat.pyd','_elementtree.pyd','select.pyd','unicodedata.pyd','_ctypes.pyd','libffi-8.dll'}
python = payload / 'tools/python'; python.mkdir(parents=True)
with zipfile.ZipFile(python_zip) as archive:
    for member in archive.infolist():
        if '/' in member.filename or '\\' in member.filename or member.filename.startswith('.'):
            raise ValueError('Unexpected embeddable member')
        if member.filename in keep:
            (python / member.filename).write_bytes(archive.read(member))
(python / 'python313._pth').write_text('python313.zip\n.\n../stages\n', encoding='ascii')
import shutil
shutil.copytree(writer, payload / 'writer', ignore=shutil.ignore_patterns('*.pdb'))
rows = [dict(path=p.relative_to(payload).as_posix(), sha256=hashlib.sha256(p.read_bytes()).hexdigest().upper(), bytes=p.stat().st_size)
        for p in sorted(payload.rglob('*')) if p.is_file()]
(payload / 'runtime-files.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for p in sorted(payload.rglob('*')):
        if p.is_file():
            info=zipfile.ZipInfo(p.relative_to(payload).as_posix(), (2026,10,9,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED
            archive.writestr(info,p.read_bytes())
print(len(rows), 'private runtime files embedded;', output.stat().st_size, 'ZIP bytes')

