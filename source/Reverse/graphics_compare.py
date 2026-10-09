"""Compare captured game frames with Pillow/numpy from the bundled Python.

The verified ROI excludes overlapping desktop windows. The comparison contains
unmodified crops, with labels outside the crops.
"""
import argparse
import json
import pathlib

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('folder')
    parser.add_argument('--frames',nargs=3,metavar=('BEFORE','CHANGED','RESTORED'))
    parser.add_argument('--roi',nargs=4,type=int)
    args=parser.parse_args()
    folder=pathlib.Path(args.folder)
    roi=tuple(args.roi or (350,280,580,400))
    names=('before','changed','after') if args.frames else ('before','changed','changed2','after','after2')
    files=dict(zip(names,args.frames)) if args.frames else {name:name+'.png' for name in names}
    crops={name:Image.open(folder/files[name]).convert('RGB').crop(roi) for name in names}
    data={name:np.asarray(crop,dtype=np.float64) for name,crop in crops.items()}
    def delta(a,b):
        d=data[b]-data[a]
        return dict(mean_signed_rgb=d.mean((0,1)).tolist(),mean_absolute_rgb=np.abs(d).mean((0,1)).tolist(),
                    pixels_over_2=int(np.any(np.abs(d)>2,axis=2).sum()),pixels=d.shape[0]*d.shape[1])
    result=dict(roi=list(roi),files=files,changed=delta('before','changed'),restored=delta('before','after'))
    if not args.frames:
        result.update(changed_repeat=delta('changed','changed2'),restored_repeat=delta('after','after2'))
    (folder/'comparison.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',18)
    width,height=crops['before'].size
    canvas=Image.new('RGB',(width*3+40,height+58),(245,245,245))
    draw=ImageDraw.Draw(canvas)
    for i,(name,label) in enumerate((('before','Before'),('changed','Changed'),('after','Restored'))):
        x=10+i*(width+10)
        canvas.paste(crops[name],(x,40))
        draw.text((x,10),label,font=font,fill=(25,25,25))
    canvas.save(folder/'comparison.png')
    print(json.dumps(result,indent=2))


if __name__=='__main__': main()
