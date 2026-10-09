"""Read-only gate ownership evidence, compared with CGateData's native owner getter.

Run with the game's clock stopped: .venv/Scripts/python.exe gate_owners.py --out .cache/gate-owners.json
The native getter runs only on the oracle's copied pages; the live game is never written.
"""
import argparse
import hashlib
import json
import pathlib
import struct

from bases import bases, BUILDINGS, CORPS, COUNTIES, FORCES, TILES, u16
from game import Game, BASE_ADJUST
from image import Image
from oracle import Oracle


def observe():
    game = Game()
    state = game.q(game.base + 0x86F58)
    clock = game.read(state + 0x34, 5)
    year, month, day, hour = struct.unpack('<HBBB', clock)
    if not (100 < year < 400 and 1 <= month <= 12 and 1 <= day <= 31 and 0 <= hour <= 22):
        raise RuntimeError('Game clock is not a valid scenario date')
    image = Image()
    start, end = image.func(0x22CD80)
    code = game.read(game.exe + start, end - start)
    if code != image.img[start:end]:
        raise RuntimeError('Live gate owner getter differs from the preserved executable image')
    oracle = Oracle(game)

    def force_info(force):
        if force == 0:
            return dict(id=0, ruler=0, name='무소속')
        record = game.q(game.base + FORCES + force * 8)
        ruler = u16(game.read(record, 0x12), 0x10)
        # The world's ID table differs from the officer slot table for registered officers.
        officer = game.q(game.base - BASE_ADJUST + 0x158 + ruler * 8)
        name = game.utf16(officer + 0x12, 7) + game.utf16(officer + 0x20, 7)
        return dict(id=force, ruler=ruler, name=name)

    gates = []
    for base_id, address in sorted(bases(game.read, game.q, game.base).items()):
        if not 52 <= base_id <= 61:
            continue
        record = game.read(address, 0xC0)
        getter = game.q(game.q(address) + 0xB0)
        if getter != game.exe + 0x22CD80:
            raise RuntimeError(f'Unexpected gate owner getter for base {base_id}')
        corps = u16(record, 0x30)
        corps_record = game.read(game.q(game.base + CORPS + corps * 8), 0x18)
        building = game.read(game.q(game.base + BUILDINGS + base_id * 8), 0x20)
        tile_id = u16(building, 0x10)
        tile = game.read(game.q(game.base + TILES + tile_id * 8), 0x20)
        county_id = u16(tile, 0x12)
        county = game.read(game.q(game.base + COUNTIES + county_id * 8), 0x98)
        seat_id = u16(county, 0x3A)
        seat = game.read(game.q(game.base + TILES + seat_id * 8), 0x20)
        native = oracle.call(0x22CD80, address)
        valid_county = county_id != 0 and 1 <= county[0x34] <= 51
        calculated = seat[0x14] if valid_county else 0
        gates.append(dict(id=base_id, name=game.utf16(address + 0x12, 4), corps=corps,
                          corps_force=force_info(corps_record[0x10]),
                          building_force=force_info(building[0x13]), tile=tile_id,
                          tile_force=force_info(tile[0x14]), county=county_id,
                          county_name=game.utf16(game.q(game.base + COUNTIES + county_id * 8) + 0x10, 5),
                          county_home_city=county[0x34], county_current_city=county[0x35],
                          seat=seat_id, seat_force=force_info(seat[0x14]), native_force=native,
                          calculated_force=calculated, matched=native == calculated,
                          gate_bytes=record.hex(), building_bytes=building.hex(),
                          county_bytes=county.hex(), tile_bytes=tile.hex(), seat_bytes=seat.hex()))
    clock_after = game.read(state + 0x34, 5)
    return dict(pid=game.pid, date=[year, month, day, hour], clock_before=clock.hex(), clock_after=clock_after.hex(),
                clock_unchanged=clock == clock_after,
                getter_rva=hex(start), getter_sha256=hashlib.sha256(code).hexdigest(),
                getter_disassembly=image.text(start, end), gates=gates)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    result = observe()
    output = pathlib.Path(args.out)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    for gate in result['gates']:
        print(f"{gate['name']}: corps={gate['corps_force']['name']} "
              f"building={gate['building_force']['name']} tile={gate['tile_force']['name']} "
              f"seat={gate['seat_force']['name']} native={gate['native_force']} matched={gate['matched']}")
    if not result['clock_unchanged'] or not all(gate['matched'] for gate in result['gates']):
        raise SystemExit('Ownership check failed; see the saved evidence')
