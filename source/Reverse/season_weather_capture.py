"""Read the opt-in real weather framebuffer/depth capture. No game access.

Usage: python season_weather_capture.py weather-capture.bin scene-upload.bin
Requires numpy. Output distinguishes actual GPU pixels from final screen capture.
"""
import json
import math
import pathlib
import struct
import sys
import numpy as np


def smooth(a, b, value):
    t = np.clip((value-a)/(b-a), 0, 1)
    return t*t*(3-2*t)


def hash32(value):
    value &= 0xffffffff
    value ^= value >> 16
    value = (value*2246822519) & 0xffffffff
    value ^= value >> 13
    value = (value*3266489917) & 0xffffffff
    return (value ^ (value >> 16)) & 0xffffffff


def analyze(capture_path, scene_path):
    data = pathlib.Path(capture_path).read_bytes()
    magic, version, count, error = struct.unpack_from('<4I', data)
    if (magic, version) != (0x57434150, 2) or not 1 <= count <= 2 or error:
        raise ValueError(f'Invalid/incomplete capture header: {magic:x}/{version}/{count}/{error:x}')
    scene = pathlib.Path(scene_path).read_bytes()
    header = struct.unpack_from('<16I', scene)
    climate = np.frombuffer(scene, dtype='<f4', count=256*256*12*2, offset=header[5]).reshape(12, 256, 256, 2)
    at = 16
    rows, snow_masks = [], []
    for frame in range(count):
        width, height, color_format, depth_format, year, month, day, reserved = struct.unpack_from('<8I', data, at)
        at += 32
        if not 1 <= width <= 2048 or not 1 <= height <= 2048 or reserved:
            raise ValueError('Invalid frame shape')
        weights = np.array(struct.unpack_from('<4f', data, at))
        extent = np.array(struct.unpack_from('<4f', data, at+16))
        months = struct.unpack_from('<2I', data, at+32)
        fraction, seconds = struct.unpack_from('<2f', data, at+40)
        viewport = np.array(struct.unpack_from('<4f', data, at+48))
        min_depth, inverse_span, direction, _ = struct.unpack_from('<2fIf', data, at+64)
        at += 80
        matrices = np.array(struct.unpack_from('<36f', data, at)).reshape(9, 4)
        at += 144
        if direction not in (1, 2) or inverse_span <= 0 or matrices[8, 0] != 1:
            raise ValueError('Invalid camera/depth contract')
        if depth_format == 44:  # R24G8_TYPELESS
            depth = (np.frombuffer(data, '<u4', width*height, at) & 0xffffff).astype(np.float64)/0xffffff
            at += width*height*4
        elif depth_format == 39:  # R32_TYPELESS
            depth = np.frombuffer(data, '<f4', width*height, at).astype(np.float64)
            at += width*height*4
        elif depth_format == 19:  # R32G8X24_TYPELESS
            depth = np.frombuffer(data, '<f4', width*height*2, at)[::2].astype(np.float64)
            at += width*height*8
        else:
            raise ValueError(f'Unsupported depth {depth_format}')
        depth = depth.reshape(height, width)
        images = []
        for _ in range(3):
            pixels = np.frombuffer(data, np.uint8, width*height*4, at).reshape(height, width, 4)
            at += width*height*4
            if color_format in (87, 91): pixels = pixels[:, :, [2, 1, 0, 3]]
            elif color_format not in (28, 29): raise ValueError(f'Unsupported color {color_format}')
            images.append(pixels.astype(np.int16))
        def delta(a, b):
            difference = b-a
            mask = np.any(difference[:, :, :3] != 0, axis=2)
            return {'rgbChangedPixels': int(mask.sum()), 'maximumChannelChange': int(np.abs(difference[:, :, :3]).max()),
                    'alphaChangedPixels': int(np.count_nonzero(difference[:, :, 3]))}, mask
        haze, _ = delta(images[0], images[1])
        snow, actual_snow = delta(images[1], images[2])
        snow_masks.append(actual_snow)
        middle = viewport[:2]+.5/viewport[2:]
        z = (depth[int(middle[1]), int(middle[0])]-min_depth)*inverse_span
        homogeneous = np.array([0., 0., z, 1.]) @ matrices[4:8]
        center = homogeneous[:3]/homogeneous[3]
        def snowfall(world):
            uv = np.clip((world[[0, 2]]-extent[:2])*extent[2:], 0, 1)*256-.5
            ix, iy = np.floor(uv).astype(int)
            fx, fy = uv-[ix, iy]
            def sample(month_index):
                def tex(x, y): return climate[month_index, min(max(y, 0), 255), min(max(x, 0), 255)]
                return (tex(ix, iy)*(1-fx)*(1-fy)+tex(ix+1, iy)*fx*(1-fy)
                        +tex(ix, iy+1)*(1-fx)*fy+tex(ix+1, iy+1)*fx*fy)
            temperature, rain = sample(months[0])*(1-fraction)+sample(months[1])*fraction
            return weights[3]*(1-smooth(0, 8, temperature))*np.clip(rain/40, 0, 1)*.35
        visible = np.zeros((height, width), dtype=bool)
        occluded = np.zeros_like(visible)
        candidates = 0
        for instance in range(4096):
            cx, cz = np.floor(center[[0, 2]]/1600).astype(int)+[instance % 64-32, instance//64-32]
            seed = hash32(int(cx) ^ hash32(int(cz)))
            jx, jy = (seed & 65535)/65535, (seed >> 16)/65535
            world = np.array([(cx+jx)*1600+math.sin(seconds*.6+jx*6.283)*250,
                              center[1]+800+((jy-seconds*.09) % 1)*7000, (cz+jy)*1600])
            intensity = snowfall(world)
            clip = np.append(world, 1) @ matrices[:4]
            if intensity <= 0 or clip[3] <= 1e-6 or not 0 <= clip[2]/clip[3] <= 1: continue
            projected = (clip[:2]/clip[3]*[.5, -.5]+.5)/viewport[2:]+viewport[:2]
            radius = 1+jx*1.5
            x0, y0 = np.maximum(np.floor(projected-radius).astype(int), 0)
            x1, y1 = np.minimum(np.ceil(projected+radius).astype(int), [width-1, height-1])
            if x1 < x0 or y1 < y0: continue
            yy, xx = np.mgrid[y0:y1+1, x0:x1+1]
            distance = np.square((xx+.5-projected[0])/radius)+np.square((yy+.5-projected[1])/radius)
            alpha = (1-smooth(.25, 1, distance))*intensity*.85
            particle_depth = clip[2]/clip[3]/inverse_span+min_depth
            covered = particle_depth <= depth[yy, xx] if direction == 1 else particle_depth >= depth[yy, xx]
            visible[yy, xx] |= (alpha > 0) & covered
            occluded[yy, xx] |= (alpha > 0) & ~covered
            candidates += 1
        # CPU sin/float/rasterization boundary differences may reach a pixel.
        padded = np.pad(visible, 1)
        tolerant_visible = np.zeros_like(visible)
        for dy in range(3):
            for dx in range(3): tolerant_visible |= padded[dy:dy+height, dx:dx+width]
        unique_occluded = occluded & ~tolerant_visible
        rows.append({'frame': frame, 'date': [year, month, day], 'seconds': seconds,
                     'size': [width, height], 'format': color_format, 'depthFormat': depth_format,
                     'depthRange': [min_depth, inverse_span, direction], 'centerWorld': center.tolist(),
                     'haze': haze, 'snow': snow, 'snowProjectedCandidates': candidates,
                     'predictedVisibleSnowPixels': int(visible.sum()), 'predictedOccludedSnowPixels': int(unique_occluded.sum()),
                     'actualSnowOutsidePredictedOnePixelTolerance': int((actual_snow & ~tolerant_visible).sum()),
                     'occludedPixelsChangedBySnow': int((actual_snow & unique_occluded).sum())})
    if at != len(data): raise ValueError('Capture payload length differs')
    result = {'frames': rows, 'actualFramebufferReadback': True, 'finalPresentVerified': False,
              'screenCaptureRequired': True, 'gameAccess': False,
              'snowChangedPixelPositionsBetweenFrames': int(np.count_nonzero(snow_masks[0] != snow_masks[1])) if len(rows) == 2 else None}
    output = pathlib.Path(capture_path).with_suffix('.json')
    output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result))


if __name__ == '__main__': analyze(sys.argv[1], sys.argv[2])
