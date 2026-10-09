# Reuse frozen game-state inputs when an unexecuted branch lacks immutable code pages.
# Read the live executable section only, verify it against the analysis image and every captured code page,
# then add missing full .text pages at the frozen module's base. Heap, TLS, .data and .rdata are never merged.
import hashlib
import pickle
import sys
from pathlib import Path


def add_verified_text(dump, image, live_text):
    start, size = image.sections['.text']
    if live_text is None or live_text != image.img[start:start + size]:
        raise RuntimeError('The live executable section differs from the analysis image')
    captured = [(page, value) for page, value in dump['pages'].items()
                if dump['exe'] + start <= page and page + 4096 <= dump['exe'] + start + size]
    if not captured:
        raise RuntimeError('No original executable pages to compare')
    if any(value != image.img[page - dump['exe']:page - dump['exe'] + 4096] for page, value in captured):
        raise RuntimeError('Original executable pages differ from the analysis image')
    pages = dict(dump['pages'])
    added = []
    for rva in range((start + 4095) & ~4095, start + size - 4095, 4096):
        address = dump['exe'] + rva
        if address not in pages:
            pages[address] = live_text[rva - start:rva - start + 4096]
            added.append(hex(rva))
    evidence = dict(original_text_pages=len(captured), original_text_mismatches=0,
        verified_live_text_sha256=hashlib.sha256(live_text).hexdigest(),
        analysis_image_sha256=hashlib.sha256(image.img).hexdigest(), added_text_rvas=added,
        mutable_pages_added=0)
    return dict(dump, pages=pages, immutable_code_evidence=evidence)


def prepare(source, target):
    from game import Game
    from image import Image
    source, target = Path(source), Path(target)
    original = source.read_bytes()
    image, live = Image(), Game()
    start, size = image.sections['.text']
    code = live.read(live.exe + start, size)
    prepared = add_verified_text(pickle.loads(original), image, code)
    if live.read(live.exe + start, size) != code:
        raise RuntimeError('Executable code changed during the read-only check')
    if source.read_bytes() != original:
        raise RuntimeError('Certified source dump changed during the code check')
    prepared['immutable_code_evidence']['original_sha256'] = hashlib.sha256(original).hexdigest()
    # Exclusive creation also prevents overwriting an input after a concurrent preparation.
    with target.open('xb') as f:
        pickle.dump(prepared, f, protocol=pickle.HIGHEST_PROTOCOL)
    metadata = dict(prepared['immutable_code_evidence'])
    metadata['added_text_pages'] = len(metadata.pop('added_text_rvas'))
    metadata['prepared_sha256'] = hashlib.sha256(target.read_bytes()).hexdigest()
    print(metadata)


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit('Usage: python frozen_code.py <certified-input.pkl> <new-code-complete-input.pkl>')
    prepare(sys.argv[1], sys.argv[2])
