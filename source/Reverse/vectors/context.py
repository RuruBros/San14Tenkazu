# What every vector module shares in one run: the game, the oracle, one snapshot (world.json.gz) that all the
# modules' cases refer to, the snapshot's records by table, and pokes.
import os, random, struct, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import world
from game import Game
from image import Image
from oracle import Oracle
from pokes import Poker

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Tests", "Vectors")

OFFICERS, FORCES, CORPS, ITEMS, RANKS, OFFICES, DOCTRINE = 0x73A50, 0xDF30, 0xE0D0, 0x77B18, 0x78490, 0x784F8, 0x85F20
UNITS, TILES, TERRAINS, FORMATIONS, POLICIES, POLICYFX, CREDITS = 0x7E838, 0xE270, 0x73860, 0x77428, 0x78D50, 0x80880, 0x85DF0


class Context:
    def __init__(self, frozen=None, moving=False):
        from game import FrozenGame
        self.g = FrozenGame(frozen) if frozen else Game()
        if not frozen and not moving: self.require_still()
        self.o = Oracle(self.g)
        self.im = Image()
        self.poker = Poker(self.g, self.o)
        self.raw = self.g.base - 0x290
        self.snap = world.snapshot(self.o)
        self.rec = {int(k, 16): [bytes.fromhex(h) for h in t["records"]] for k, t in self.snap["tables"].items()}
        self.owned = [item[0][1] for item in self.snap["lists"]["0x110"]["items"]]
        self.byid = self.snap["byid"]
        self.failures = []
        self.frozen = frozen is not None
        self.output = OUT

    def require_still(self, seconds=3.0):
        # The oracle copies pages one at a time over the whole run, so a game that is still advancing (demo play,
        # an unpaused turn) gives an inconsistent snapshot: lists whose nodes were copied at different moments,
        # officers and units half-changed. Refuse unless the officers, units and world state hold still.
        import hashlib, time
        g = self.g
        raw = g.base - 0x290
        ws = g.q(raw + world.WORLDSTATE_RAW)

        def state():
            h = hashlib.md5()
            for table, n in ((OFFICERS, 1200), (UNITS, 600)):
                for i in range(1, n):
                    p = g.q(raw + table + i * 8)
                    if p: h.update(g.read(p, 0x160) or b"")
            h.update(g.read(ws, 0x800) or b"")
            return h.hexdigest()

        a = state(); time.sleep(seconds); b = state()
        if a != b:
            raise SystemExit("게임이 진행 중입니다 (무장/부대/월드 상태가 바뀌고 있음). 일시정지하거나 데모 플레이를 멈춘 뒤 다시 실행하세요"
                             " (--moving 으로 강제 실행 가능, 결과는 신뢰할 수 없음).")

    def addr(self, table, i):
        return world.table_ptr(self.o, self.g, table, i)

    def u16(self, table, i, off):
        return struct.unpack_from("<H", self.rec[table][i], off)[0]

    def officer_force(self, i):
        # the force number of officer i (array index), through his corps
        corps = self.rec[OFFICERS][i][0xE0]
        c = self.rec[CORPS][corps if corps <= 0x33 else 0]
        f = c[0x10]
        return f if f <= 0x33 else 0

    def officer_by_id(self, oid):
        return self.byid[oid] if oid < len(self.byid) else 0

    def run(self, pokes, call):
        # the call's result with the pokes applied to the oracle's copies, then the copies put back; None when the
        # oracle cannot run it (kept in self.failures with the pokes, written by run.py)
        from oracle import OracleError
        undo = self.poker.apply(pokes)
        try:
            return call()
        except OracleError as e:
            self.failures.append({"error": str(e), "pokes": pokes})
            return None
        finally:
            self.poker.undo(undo)

    def save(self, name, obj):
        os.makedirs(self.output, exist_ok=True)
        world.save(obj, os.path.join(self.output, name))
