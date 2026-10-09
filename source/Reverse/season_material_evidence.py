"""Compare saved native screenshots and the completed tree GPU mip receipt.

No game attachment or image synthesis. Original evidence files stay unchanged.
"""
import argparse
import hashlib
import json
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory',type=Path)
    args=parser.parse_args();root=args.directory
    audit=json.loads((root/'audit.json').read_text())
    if not audit['minimalTreeDrawApplied'] or not audit['nativeWorkerSignaled'] or audit['retainedAllocations']:
        raise ValueError('Completed real tree application and recovery required')
    names=['original-01.jpg','original-02.jpg','applied-01.jpg','restored-01.jpg','restored-02.jpg']
    images={name:Image.open(root/name).convert('RGB') for name in names}
    if len({im.size for im in images.values()})!=1:raise ValueError('Screenshot sizes differ')
    # Excludes the UI, animated river and pointer. Tree wind/lighting and JPEG
    # differences can remain, so report repeated originals and restoration too.
    crop=(300,130,1000,580)
    data={name:np.array(im.crop(crop),dtype=np.int16) for name,im in images.items()}
    def difference(a,b):
        d=np.abs(data[a]-data[b]);return dict(meanAbsRgb=d.mean(axis=(0,1)).tolist(),pixelsWithAnyDifference=int(np.any(d,axis=2).sum()))
    gpu=[(root/name).read_bytes() for name in ('original-decoded-rgba-mips.bin','spring-rgba-mips.bin')]
    if len(gpu[0])!=699008 or len(gpu[1])!=699008 or gpu[0][3::4]!=gpu[1][3::4]:raise ValueError('GPU mip alpha differs')
    result=dict(mode='Saved original screenshots and actual GPU readback',crop=list(crop),
        originalToApplied=difference('original-01.jpg','applied-01.jpg'),
        originalRepeat=difference('original-01.jpg','original-02.jpg'),
        originalToRestored=difference('original-01.jpg','restored-01.jpg'),
        restoredRepeat=difference('restored-01.jpg','restored-02.jpg'),
        alphaBytesEqual=True,alphaPixels=174752,viewFormat=audit['treeMaterialTrial']['viewFormat'],
        screenPixelExactRestorationClaimed=False,animationAndJpegDifferencesRemain=True,
        screenshotHashes={name:hashlib.sha256((root/name).read_bytes()).hexdigest() for name in names},
        transitionCaptureExcluded='transition-063951.jpg: active lease not independently confirmed at this capture',
        fullSeasonImplementationVerified=False)
    sheet=Image.new('RGB',(crop[2]-crop[0],3*(crop[3]-crop[1]+26)),'#202020');draw=ImageDraw.Draw(sheet)
    for i,(name,label) in enumerate(zip(['original-01.jpg','applied-01.jpg','restored-01.jpg'],['Original','Minimum spring material trial (one tree submesh)','Restored after native worker signaled'])):
        y=i*(crop[3]-crop[1]+26);draw.text((8,y+6),label,fill='white');sheet.paste(images[name].crop(crop),(0,y+26))
    sheet.save(root/'comparison.png');result['comparisonSha256']=hashlib.sha256((root/'comparison.png').read_bytes()).hexdigest()
    (root/'screen-comparison.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result))

if __name__=='__main__':main()
