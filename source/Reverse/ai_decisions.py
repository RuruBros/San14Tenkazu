# Read-only reproduction of the diplomacy/domestic map in notes/ai.md.
# Uses ai.py's parser, names and numeric-role tables; this is an inventory, not
# an editable-value allowlist. The editor's interpretation and write rules live
# in Core/Ai and are checked by AiTreeTests.
#   python ai_decisions.py list
#   python ai_decisions.py tree goodwill
#   python ai_decisions.py numbers [diplomacy|domestic|personnel|scheme|key]
import hashlib
import os
import struct
import sys
from datetime import datetime, timezone

import ai

# key, group, tree, root, expected StaticLink hash (None for another node kind), label
BLOCKS = [
    ("friend", "diplomacy", 0, 77, 0xBE2B2510, "우호 대상 세력"),
    ("goodwill", "diplomacy", 1, 3341, 0xAEDD56EC, "친선"),
    ("alliance", "diplomacy", 1, 3398, 0xBF186B14, "동맹"),
    ("annul", "diplomacy", 1, 1265, 0xB5B1B07B, "협정 파기"),
    ("exchange", "diplomacy", 1, 1150, 0x5ED0B89C, "교환"),
    ("prisoner", "diplomacy", 1, 878, 0x66A370DC, "포로 반환"),
    ("request", "diplomacy", 1, 3009, 0x1E67A315, "공격 요청"),
    ("recruit", "domestic", 1, 2460, 0x6194E96E, "징병 담당"),
    ("training", "domestic", 1, 2512, 0x85AA8E6C, "훈련 담당"),
    ("development", "domestic", 1, 2564, 0x1BE5DB28, "내정 담당과 개발 분야"),
    ("merchant", "domestic", 1, 1061, 0x5EBBD40E, "상인"),
    ("trade", "domestic", 1, 4163, 0x0137B3B0, "교역"),
    ("inspect", "domestic", 1, 3057, 0x7CA8F884, "시찰"),
    ("scenery", "domestic", 1, 2974, 0xF7D66DE5, "명소 투자"),
    ("hire", "personnel", 1, 716, 0x1D41DD52, "고용"),
    ("move", "personnel", 1, 753, 0x4F434AAE, "장수 이동"),
    ("employ", "personnel", 1, 765, 0xB64C60B8, "등용"),
    ("search", "personnel", 1, 829, 0xE79AC205, "탐색 (같은 유형에서 두 번 실행)"),
    ("reward", "personnel", 1, 933, 0xD0A50A02, "포상"),
    ("rankup", "personnel", 1, 957, 0x835FFEA5, "공훈 승급"),
    ("credit", "personnel", 1, 997, 0x29A4BF9C, "공훈 수여"),
    ("item", "personnel", 1, 1037, 0xBBF03330, "명품 하사"),
    ("scheme-base", "scheme", 1, 39, None, "계략 공통 기준"),
    ("poison", "scheme", 1, 3490, 0xC4BD9D36, "매복의 독"),
    ("estrange", "scheme", 1, 3553, 0xF3A85FD4, "이간"),
    ("destroy", "scheme", 1, 3616, 0x60732FC0, "파괴"),
    ("placation", "scheme", 1, 3677, 0x964C9E7C, "선무"),
    ("rumor", "scheme", 1, 3749, 0x5208EA1D, "유언"),
    ("unattended", "scheme", 1, 3772, 0x29A79F67, "빈집 노리기"),
    ("dual", "scheme", 1, 3822, 0x918C8FAC, "이호경식"),
    ("spy-away", "scheme", 1, 2881, 0x92BF725A, "첩자 파견"),
]


# These three loaders contain a param-count test, so ai.py's straight-copy detector deliberately returns None.
# They still keep each loaded param at node + 0x38 + 4*k. Optional params must obey the loader's actual branch.
COUNT_LOADERS = {0xBB940C54: 0x92AE0, 0x874694D6: 0x92B10, 0x184B7500: 0x92BC0}


def loaded_count(node):
    count = len(node.params)
    if node.cls == 0xBB940C54:
        return 7 if count == 7 else 5
    if node.cls == 0x874694D6:
        return 5 if count == 5 else 2
    if node.cls == 0x184B7500:
        return min(count, 6)
    raise ValueError("not a count-dependent loader")


def show_loaders():
    from image import Image
    image, classes = Image(), ai.classes()
    for hash_, expected in COUNT_LOADERS.items():
        cls = classes[hash_]
        if cls["load"] != expected:
            raise SystemExit("조사한 로더 RVA와 다릅니다")
        end = next(i.address + i.size for i in image.insns(expected, expected + 0x200) if i.mnemonic == "ret")
        print(cls["name"], hex(expected), "\n" + image.text(expected, end))


def compare_loaders(trees):
    from game import Game
    game, classes = Game(), ai.classes()
    print(datetime.now(timezone.utc).isoformat(), "PID", game.pid)
    same = different = fields = 0
    for t, (nodes, (_, pointers)) in enumerate(zip(trees, ai.live_trees(game))):
        if len(nodes) != len(pointers):
            raise SystemExit("트리 노드 수가 다릅니다")
        for node, address in zip(nodes, pointers):
            if node.cls not in COUNT_LOADERS:
                continue
            if struct.unpack("<I", game.read(address + 8, 4))[0] != node.cls:
                raise SystemExit("실게임 노드 클래스가 다릅니다")
            count = min(len(node.params), loaded_count(node))
            got = list(struct.unpack(f"<{count}i", game.read(address + 0x38, 4 * count)))
            ok = got == node.params[:count]
            same += ok
            different += not ok
            fields += count
            print(f"{t}:{node.i} {classes[node.cls]['name']} node {address:#x}: {got}; file {node.params[:count]} {'same' if ok else 'DIFFERENT'}")
    print(f"{same} same, {different} different, {fields} fields compared; no writes")
    if different:
        raise SystemExit(1)


def descendants(node):
    yield node
    for child in node.children:
        yield from descendants(child)


def main():
    args = sys.argv[1:] or ["list"]
    mode, selected = args[0], args[1] if len(args) > 1 else None
    if mode not in ("list", "tree", "numbers", "loaders", "live"):
        raise SystemExit("명령: list | tree <key> | numbers [group|key] | loaders | live")
    blocks = [b for b in BLOCKS if selected is None or selected in b[:2]]
    if not blocks or (mode == "tree" and (selected is None or len(blocks) != 1)):
        raise SystemExit("list에서 판단 key를 고르세요")
    path = os.path.join(ai.game_dir(), ai.FILE)
    trees, names = ai.read_file(path), ai.Names()
    print("bt_exce.bin SHA-256", hashlib.sha256(open(path, "rb").read()).hexdigest())
    if mode == "loaders":
        show_loaders()
        return
    if mode == "live":
        compare_loaders(trees)
        return
    numbers = list(ai.tunables(names, trees)) if mode == "numbers" else []
    for key, group, t, index, block_hash, label in blocks:
        node = trees[t][index]
        if block_hash is not None and (node.cls != ai.STATIC_LINK or len(node.params) != 1 or node.params[0] & 0xFFFFFFFF != block_hash):
            raise SystemExit(f"트리 {t} #{index} 블록이 조사한 파일과 다릅니다")
        members = {n.i for n in descendants(node)}
        copies = [n.i for n in trees[t] if n.cls == ai.STATIC_LINK and n.params == node.params] if block_hash is not None else []
        print(f"\n{key} [{group}] {label}: tree {t} #{index}, {len(members)} nodes; 동일 이름 블록 {copies}")
        if mode == "tree":
            ai.cmd_path(t, index)
            ai.cmd_tree(t, index)
        if mode == "numbers":
            for nt, n, slot, raw, text in numbers:
                if nt == t and n.i in members:
                    term, arg = (slot[1], slot[2]) if slot[0] == "term" else (-1, slot[1])
                    print(f"  {t}:{n.i}:{term}:{arg} = {raw}  {text}")


if __name__ == "__main__":
    main()
