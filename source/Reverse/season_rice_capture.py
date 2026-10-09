"""Inspect opt-in RCAP v1 vegetation pass readback without game access.

Usage: python season_rice_capture.py weather-capture.bin.rice
Writes raw before/after PNGs and measured differences beside the capture.
Framebuffer evidence is separate from the final presented game screen.
"""
import json
from pathlib import Path
import struct
import sys

import numpy as np
from PIL import Image


def analyze(path):
    path = Path(path)
    data = path.read_bytes()
    magic, version, count, error = struct.unpack_from('<4I', data)
    if (magic, version) != (0x52434150, 1) or not 1 <= count <= 2 or error:
        raise ValueError('Incomplete/invalid RCAP v1 capture')
    at, frames, differences, cameras, dates = 16, [], [], [], []
    for frame in range(count):
        width, height, color_format, depth_format, year, month, day, reserved = struct.unpack_from('<8I', data, at)
        at += 32
        if not 1 <= width <= 2048 or not 1 <= height <= 2048 or reserved:
            raise ValueError('Invalid frame shape')
        weights = struct.unpack_from('<4f', data, at)
        seconds = struct.unpack_from('<f', data, at+44)[0]
        min_depth, inverse_span, direction, _ = struct.unpack_from('<2fIf', data, at+64)
        at += 80
        matrix = data[at:at+144]
        values = np.frombuffer(matrix, dtype='<f4')
        if len(values) != 36 or not np.all(np.isfinite(values)) or values[32] != 1 or direction not in (1, 2) or inverse_span <= 0:
            raise ValueError('Invalid camera/depth contract')
        cameras.append(matrix)
        dates.append((year, month, day))
        at += 144
        if depth_format not in (19, 39, 44):
            raise ValueError('Unsupported depth format')
        at += width*height*(8 if depth_format == 19 else 4)
        images = []
        for stage in ('before', 'after'):
            pixels = np.frombuffer(data, dtype=np.uint8, count=width*height*4, offset=at).reshape(height, width, 4)
            at += width*height*4
            if color_format in (87, 91):
                pixels = pixels[:, :, [2, 1, 0, 3]]
            elif color_format not in (28, 29):
                raise ValueError('Unsupported color format')
            Image.fromarray(pixels).save(path.with_name(f'rice-{frame}-{stage}.png'))
            images.append(pixels.astype(np.int16))
        difference = images[1]-images[0]
        changed = np.any(difference[:, :, :3] != 0, axis=2)
        ys, xs = np.nonzero(changed)
        differences.append(difference)
        frames.append(dict(frame=frame, date=list(dates[-1]), seconds=seconds,
                           size=[width, height], colorFormat=color_format,
                           depthFormat=depth_format, seasonWeights=list(weights),
                           summerWeight=weights[1], autumnWeight=weights[2],
                           depthRange=[min_depth, inverse_span, direction],
                           rgbChangedPixels=int(changed.sum()),
                           maximumChannelChange=int(np.abs(difference[:, :, :3]).max()),
                           alphaChangedPixels=int(np.count_nonzero(difference[:, :, 3])),
                           changedBounds=[int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())] if len(xs) else None))
    if at != len(data):
        raise ValueError('Capture payload length differs')
    comparable = count == 2 and cameras[0] == cameras[1] and dates[0] == dates[1] and differences[0].shape == differences[1].shape
    result = dict(frames=frames, actualFramebufferReadback=True, gameAccess=False,
                  sameCameraAndDate=comparable, finalPresentVerified=False,
                  ricePassDifferenceChangedPixels=int(np.count_nonzero(np.any(differences[0][:, :, :3] != differences[1][:, :, :3], axis=2))) if comparable else None)
    path.with_name('rice-pixel-evidence.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))
    return result


if __name__ == '__main__':
    analyze(sys.argv[1])
