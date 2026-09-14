"""I check BsBoard solvability; hitting the search budget means unknown.
Pours move min(top chain, free space), any colour. Locks/hidden layers stop chains.
Chained glasses cannot pour or receive. Delivery rejects chains/locks,
removes the glass, increments Delivered, then shifts and refills slots.
"""
import re, struct, glob, os, heapq
from collections import Counter

CAP = {0: 1, 1: 2, 2: 3, 3: 4, 4: 5}
NAME = {0: 'Shot', 1: 'Kadeh', 2: 'Latte', 3: 'Tumbler', 4: 'Bira'}
SET, LAYER = 0, 1


# Asset loading.
def parse_level(path):
    t = open(path, encoding='utf-8').read()
    glasses = []
    gsec = t.split('  Glasses:')[1].split('  ColumnsPerRow:')[0]
    for blk in re.split(r'\n  - Type: ', gsec)[1:]:
        ty = int(blk.split('\n')[0])
        layers = tuple(
            (int(m.group(1)), int(m.group(2)), int(m.group(3)))
            for m in re.finditer(
                r'- Color: (\d+)\s*\n\s*Hidden: (\d+)\s*\n\s*LockUntil: (\d+)', blk)
        )
        ua = int(re.search(r'UnlockAfter: (-?\d+)', blk).group(1))
        glasses.append((ty, layers, ua))
    orders = []
    osec = t.split('  Orders:')[1].split('  OrderSlots:')[0]
    for blk in re.split(r'\n  - Kind: ', osec)[1:]:
        kind = int(blk.split('\n')[0])
        g = int(re.search(r'Glass: (\d+)', blk).group(1))
        b = bytes.fromhex(re.search(r'Contents: ([0-9a-f]*)', blk).group(1))
        cont = tuple(struct.unpack('<%di' % (len(b) // 4), b))
        orders.append((kind, g, cont))
    return {
        'index': int(re.search(r'^  Index: (\d+)', t, re.M).group(1)),
        'glasses': glasses,
        'orders': orders,
        'slots': int(re.search(r'OrderSlots: (\d+)', t).group(1)),
        'path': path,
    }


# Rule engine.
def reveal_top(ls):
    """I reveal the top hidden layer, like BsBoard.RevealTop."""
    if ls and ls[-1][1]:
        return ls[:-1] + ((ls[-1][0], 0, ls[-1][2]),)
    return ls


def top_chain(ls, delivered):
    """RtGlass.TopChainLength"""
    if not ls:
        return 0
    if ls[-1][2] > 0 and delivered < ls[-1][2]:
        return 0                       # A locked top layer cannot pour.
    col = ls[-1][0]
    n = 1
    for i in range(len(ls) - 2, -1, -1):
        c, h, lk = ls[i]
        if h or c != col:
            break
        if lk > 0 and delivered < lk:
            break
        n += 1
    return n


def has_locked(ls, delivered):
    return any(lk > 0 and delivered < lk for _, _, lk in ls)


def matches(ty, ls, order):
    """BsBoard.MatchesLayers"""
    kind, g, cont = order
    if ty != g or len(ls) != len(cont):
        return False
    if any(h for _, h, _ in ls):
        return False
    if kind == LAYER:
        return all(ls[i][0] == cont[i] for i in range(len(ls)))
    return Counter(c for c, _, _ in ls) == Counter(cont)


class State:
    """Immutable, hashable (glasses, slots, deck_index, delivered) state."""
    __slots__ = ('g', 'slots', 'di', 'd')

    def __init__(self, g, slots, di, d):
        self.g, self.slots, self.di, self.d = g, slots, di, d

    def key(self):
        """I sort identical type/content glasses like BsBoard.StateKey."""
        return (self.di, self.d, self.slots,
                tuple(sorted((ty, ua, ls) for ty, ls, ua in self.g)))


def initial(level):
    g = tuple((ty, reveal_top(ls), ua) for ty, ls, ua in level['glasses'])
    n = max(1, level['slots'])
    deck = level['orders']
    slots = tuple(deck[i] if i < len(deck) else None for i in range(n))
    return State(g, slots, min(n, len(deck)), 0)


def refill(slots, deck, di, n):
    """I shift cards left and add the next card on the right, like BsBoard.RefillSlots."""
    kept = [s for s in slots if s is not None]
    while len(kept) < n and di < len(deck):
        kept.append(deck[di]); di += 1
    while len(kept) < n:
        kept.append(None)
    return tuple(kept), di


def successors(st, level):
    """I yield (new_state, move_description), with deliveries first."""
    deck, n = level['orders'], max(1, level['slots'])
    out = []
    # Deliveries.
    for gi, (ty, ls, ua) in enumerate(st.g):
        if ua > 0 and st.d < ua:
            continue
        if has_locked(ls, st.d):
            continue
        for si, o in enumerate(st.slots):
            if o is not None and matches(ty, ls, o):
                ng = st.g[:gi] + st.g[gi + 1:]
                ns = list(st.slots); ns[si] = None
                ns, ndi = refill(tuple(ns), deck, st.di, n)
                out.append((State(ng, ns, ndi, st.d + 1),
                            f"TESLIM g{gi} -> slot{si}"))
                break
    # Pours.
    for i, (tyi, lsi, uai) in enumerate(st.g):
        if (uai > 0 and st.d < uai) or not lsi:
            continue
        ch = top_chain(lsi, st.d)
        if ch <= 0:
            continue
        col = lsi[-1][0]
        for j, (tyj, lsj, uaj) in enumerate(st.g):
            if i == j or (uaj > 0 and st.d < uaj):
                continue
            free = CAP[tyj] - len(lsj)
            if free <= 0:
                continue
            a = min(ch, free)
            ng = list(st.g)
            ng[i] = (tyi, reveal_top(lsi[:len(lsi) - a]), uai)
            ng[j] = (tyj, lsj + tuple((col, 0, 0) for _ in range(a)), uaj)
            out.append((State(tuple(ng), st.slots, st.di, st.d),
                        f"DOK g{i}->g{j} ({a} birim, renk {col})"))
    return out


def is_win(st, level):
    return st.di >= len(level['orders']) and all(s is None for s in st.slots)


# Solver.
def solve(level, node_budget=120_000):
    """I search states with more deliveries and fewer glasses first, keeping the full path. Returns (result, move_count_or_none, nodes, path_or_none): COZULDU means solved, COZULEMEZ means exhausted, BUTCE means undecided."""
    start = initial(level)
    if is_win(start, level):
        return 'COZULDU', 0, 0, []
    seen = {start.key()}
    ctr = 0
    heap = [(0, 0, start, ())]
    nodes = 0
    while heap:
        _, _, st, path = heapq.heappop(heap)
        for ns, mv in successors(st, level):
            k = ns.key()
            if k in seen:
                continue
            nodes += 1
            if nodes > node_budget:
                return 'BUTCE', None, nodes, None
            seen.add(k)
            npath = path + (mv,)
            if is_win(ns, level):
                return 'COZULDU', len(npath), nodes, list(npath)
            ctr += 1
            # Try more deliveries and fewer glasses first.
            pri = -(ns.d * 100) + len(ns.g)
            heapq.heappush(heap, (pri, ctr, ns, npath))
    return 'COZULEMEZ', None, nodes, None


def first_delivery_reachable(level, node_budget=400_000):
    """I exhaust the Delivered=0 layer to check whether any delivery is possible."""
    start = initial(level)
    seen = {start.key()}
    frontier = [start]
    nodes = 0
    while frontier:
        nxt = []
        for st in frontier:
            for ns, mv in successors(st, level):
                if ns.d > st.d:
                    return True, nodes
                k = ns.key()
                if k in seen:
                    continue
                nodes += 1
                if nodes > node_budget:
                    return None, nodes
                seen.add(k)
                nxt.append(ns)
        frontier = nxt
    return False, nodes


if __name__ == '__main__':
    import sys
    files = sorted(glob.glob(
        'Assets/LiquidSort/LevelSystem/Resources/Levels/Level_*.asset'))
    if len(sys.argv) > 1:
        want = set(int(x) for x in sys.argv[1:])
        files = [f for f in files
                 if int(re.search(r'Level_(\d+)', f).group(1)) in want]
    print(f"{'level':>6}  {'ilk teslim':<12} {'tam cozum':<12} "
          f"{'hamle':>6}  {'dugum':>9}")
    print('-' * 56)
    broken, unknown = [], []
    for f in files:
        lv = parse_level(f)
        fd, fdn = first_delivery_reachable(lv)
        fdtxt = {True: 'var', False: 'YOK', None: 'olculemedi'}[fd]
        if fd is False:
            broken.append(lv['index'])
            print(f"  L{lv['index']:02d}  {fdtxt:<12} {'-':<12} {'-':>6}  {fdn:>9,}")
            continue
        res, mv, n, _ = solve(lv)
        if res == 'COZULEMEZ':
            broken.append(lv['index'])
        elif res == 'BUTCE':
            unknown.append(lv['index'])
        print(f"  L{lv['index']:02d}  {fdtxt:<12} {res:<12} "
              f"{(str(mv) if mv is not None else '-'):>6}  {n:>9,}")
    print('-' * 56)
    print('COZULEMEZ (kanitli) :', broken if broken else 'yok')
    print('BUTCE  (karar yok)  :', unknown if unknown else 'yok')
