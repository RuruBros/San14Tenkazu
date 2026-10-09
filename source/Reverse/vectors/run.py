# Writes every formula's vectors from one snapshot of the running game: world.json.gz, then each module's cases
# (the game's own functions answered by the oracle), with the coverage of the functions each module tracks.
#   python vectors/run.py
#   python vectors/run.py [module...]        from the running game; writes .cache/frozen.pkl (every page read)
#   python vectors/run.py --frozen [module...]  from .cache/frozen.pkl instead, so new modules join the same snapshot
#   --moving   run although the game is advancing (the snapshot is then inconsistent; for experiments only)
#   --out=<directory> --dump=<file> stage a complete replacement separately, then validate before publishing.
# The game must hold still (paused, or demo play stopped): Context refuses a moving game.
import importlib, os, sys, time
from context import Context, OUT

sys.stdout.reconfigure(line_buffering=True, encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

FROZEN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".cache", "frozen.pkl")
MODULES = ["abilities", "rng", "battlepower", "facility", "link", "battlepower_fx", "attack", "siege", "defense", "damage", "wounded", "tactic", "reach", "capture", "experience", "stepend", "bombard", "injury", "recruit", "exchange", "siegedmg", "engagement", "capacity", "tactic_targets", "tactic_support", "tactic_boost", "tactic_ailment", "tactic_damage", "tactic_morale", "diplomacy", "tactic_fire", "tactic_siege", "tactic_apply_damage", "unit_tactics", "tactic_dispatch", "tactic_link", "tactic_link_graph", "bombard_challenge", "tactic_flow", "engagement_tactic", "engagement_routing", "destruction_queue", "engagement_challenge", "engagement_sequence", "engagement_tactic_sequence", "engagement_siege_sequence", "destruction_consumer"]

args = [a for a in sys.argv[1:] if not a.startswith("--")]
output = os.path.abspath(next((a.split('=', 1)[1] for a in sys.argv[1:] if a.startswith('--out=')), OUT))
FROZEN = os.path.abspath(next((a.split('=', 1)[1] for a in sys.argv[1:] if a.startswith('--dump=')), FROZEN))
frozen = "--frozen" in sys.argv
ctx = Context(FROZEN if frozen else None, moving="--moving" in sys.argv)
ctx.output = output
mods = [importlib.import_module(m) for m in (args or MODULES)]
# A few modules only: the game (or the frozen dump) must still be as the saved snapshot has it, or their cases would
# not match world.json.gz. A new table or flag may join; everything the saved snapshot has must read the same, and
# then the snapshot is saved again with the additions.
import gzip, json
from snapshot_guard import differences
saved_path = os.path.join(output, "world.json.gz")
same = False

if args and os.path.exists(saved_path):
    saved = json.load(gzip.open(saved_path, "rt"))
    same = not differences(saved, ctx.snap)
    if not same:
        raise SystemExit("게임이 저장된 스냅샷(world.json.gz)과 다릅니다: 모듈을 지정하지 말고 전체를 다시 실행하세요.")
for m in mods:
    t = time.time()
    ctx.o.track(ctx.im, m.COVER)
    before = len(ctx.failures)
    print(f"{m.__name__}: {m.generate(ctx)} ({time.time() - t:.1f} s)" +
          (f", {len(ctx.failures) - before} cases the oracle could not run (.cache/failures.json)" if len(ctx.failures) > before else ""))
    for r, (hit, total, missed) in ctx.o.coverage().items():
        print(f"  coverage {r:#x}: {hit}/{total}" + (f" missed {[hex(x) for x in missed[:12]]}{'...' if len(missed) > 12 else ''}" if missed else ""))
    ctx.o.untrack()
    ctx.o.unstub()
if ctx.failures:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '.cache', 'failures.json'), 'w') as f:
        json.dump(ctx.failures, f)
    raise SystemExit(f'오라클 실패 {len(ctx.failures)}건: 스냅샷과 동결본을 게시하지 않습니다. .cache/failures.json을 확인하세요.')
if not frozen:
    if '--moving' not in sys.argv:
        # All pages must belong to the initial stopped game, including pages lazily read late in a long run.
        end = Context()
        changed = differences(ctx.snap, end.snap)
        if changed:
            raise SystemExit(f'생성 중 게임 스냅샷이 바뀌었습니다. 검증본을 게시하지 마세요: {changed}')
        print('snapshot: every initial table, vector, flag, list and setting remained unchanged')
    ctx.save("world.json.gz", ctx.snap)
    if args and same and os.path.exists(FROZEN):
        # a few modules on the same game: their pages join the dump instead of replacing it
        import pickle
        old = pickle.load(open(FROZEN, "rb"))
        n = ctx.o.save_pages(FROZEN)
        new = pickle.load(open(FROZEN, "rb"))
        old["pages"].update(new["pages"])
        new["pages"] = old["pages"]
        pickle.dump(new, open(FROZEN, "wb"))
        print(f"frozen: {n} pages added, {len(new['pages'])} in the dump")
    else:
        print(f"frozen: {ctx.o.save_pages(FROZEN)} pages")
elif same or not os.path.exists(saved_path):
    ctx.save("world.json.gz", ctx.snap)   # the same game, with whatever tables or flags joined since
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".cache", "failures.json"), "w") as f:
    json.dump(ctx.failures, f)
