"""Render saved GPU boundary payloads. Never opens or writes the game."""
import argparse
import hashlib
import json
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw


def sha(data):return hashlib.sha256(data).hexdigest()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder',type=Path)
    args=parser.parse_args()
    audit=json.loads((args.folder/'audit.json').read_text())
    b=audit['boundaryReadback']
    if not audit['independentCodeAndProtectionReadback'] or not b['completed']:
        raise ValueError('Completed GPU readback and independent recovery required')
    raw=(args.folder/'gpu-boundary-rgba.bin').read_bytes()
    if sha(raw)!=b['sha256'] or len(raw)!=b['bytes']:raise ValueError('Saved GPU payload changed')
    images=[]
    names=('scene-after-composite','before-first-ui-draw','after-first-ui-draw')
    for i,desc in enumerate(b['image']):
        data=raw[desc['offset']:desc['offset']+desc['bytes']]
        if sha(data)!=desc['sha256']:raise ValueError('Image GPU payload changed')
        if desc['format'] not in (28,29,87,91):raise ValueError('Unsupported saved backbuffer format')
        mode='BGRA' if desc['format'] in (87,91) else 'RGBA'
        im=Image.frombytes('RGBA',(desc['width'],desc['height']),data,'raw',mode).convert('RGB')
        im.save(args.folder/(names[i]+'.png'));images.append(im)
    delta=ImageChops.difference(images[1],images[2]);bounds=delta.getbbox()
    delta.save(args.folder/'first-ui-difference.png')
    summary=dict(gameAccess=False,rawGpuReadback=True,
        sceneAndBeforeUiByteIdentical=b['image'][0]['sha256']==b['image'][1]['sha256'],
        firstUiDrawChangedBackbuffer=bounds is not None,changedPixelBounds=list(bounds) if bounds else None,
        sceneUiSemanticClassification='Requires inspection of the saved raw images',
        sceneUiOrderVerified=False,completeFrame=False,productionReady=False)
    (args.folder/'image-comparison.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    canvas=Image.new('RGB',(1280,760),(24,24,24));draw=ImageDraw.Draw(canvas)
    for n,(im,label) in enumerate(zip((images[0],images[1],images[2],delta),
            ('GPU scene after composite','GPU before first indexed UI candidate','GPU after first indexed UI candidate','Difference: first UI candidate only'))):
        x=(n%2)*640;y=(n//2)*380
        draw.text((x+8,y+8),label,fill='white');canvas.paste(im.resize((640,360)),(x,y+20))
    canvas.save(args.folder/'boundary-comparison.png')
    print(json.dumps(summary))


if __name__=='__main__':main()
