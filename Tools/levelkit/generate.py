"""I generate levels by reverse scrambling, targeting free units and requiring mixed glass types. I reject every candidate whose solvability the solver cannot prove."""
import random, time
from collections import Counter

CAP = {0: 1, 1: 2, 2: 3, 3: 4, 4: 5}
NAME = {0: 'Shot', 1: 'Kadeh', 2: 'Latte', 3: 'Tumbler', 4: 'Bira'}


def build_solved(order_specs, spare_specs):
    """I create one full glass per order plus spares so colour totals already match."""
    glasses = []
    for kind, gtype, contents in order_specs:
        glasses.append([gtype, [(c, 0, 0) for c in contents], 0])
    for t, fill in spare_specs:
        glasses.append([t, list(fill), 0])
    return glasses


def legal_moves(glasses):
    """I move 1..chain top units to the target during scrambling."""
    out = []
    for i, (ti, li, _) in enumerate(glasses):
        if not li:
            continue
        col = li[-1][0]
        chain = 1
        for k in range(len(li) - 2, -1, -1):
            if li[k][0] == col:
                chain += 1
            else:
                break
        for j, (tj, lj, _) in enumerate(glasses):
            if i == j:
                continue
            free = CAP[tj] - len(lj)
            if free <= 0:
                continue
            out.append((i, j, min(chain, free)))
    return out


def scramble(glasses, steps, rng):
    g = [[t, list(l), ua] for t, l, ua in glasses]
    for _ in range(steps):
        mv = legal_moves(g)
        if not mv:
            break
        i, j, amt = rng.choice(mv)
        col = g[i][1][-1][0]
        del g[i][1][len(g[i][1]) - amt:]
        g[j][1].extend([(col, 0, 0)] * amt)
    return [(t, tuple(l), ua) for t, l, ua in g]


def apply_locks(glasses, orders, rng, lock_count, chain_count, hidden_count):
    """I add locks, chains and hidden colours with thresholds spread across the level."""
    n = len(orders)
    g = [[t, list(l), ua] for t, l, ua in glasses]
    # I chain only empty or lightly filled glasses and spread unlock thresholds across the level.
    cand = [i for i, (t, l, ua) in enumerate(g) if len(l) <= 2]
    rng.shuffle(cand)
    for k, i in enumerate(cand[:chain_count]):
        g[i][2] = max(1, min(n - 1, round(n * (0.2 + 0.25 * k))))
    # Locked layer.
    slots = [(i, k) for i, (t, l, ua) in enumerate(g) for k in range(len(l)) if g[i][2] == 0]
    rng.shuffle(slots)
    for k, (i, j) in enumerate(slots[:lock_count]):
        c, h, _ = g[i][1][j]
        g[i][1][j] = (c, h, max(1, min(n - 1, round(n * (0.25 + 0.18 * (k % 3))))))
    # I hide only lower layers because the top layer is revealed anyway.
    hid = [(i, k) for i, (t, l, ua) in enumerate(g) for k in range(len(l) - 1)]
    rng.shuffle(hid)
    for i, j in hid[:hidden_count]:
        c, _, lk = g[i][1][j]
        g[i][1][j] = (c, 1, lk)
    return [(t, tuple(l), ua) for t, l, ua in g]


def evaluate(glasses, orders, slots, solver, verifier):
    lv = {'index': 0, 'glasses': list(glasses), 'orders': orders, 'slots': slots, 'path': ''}
    errs = [e for e in verifier(lv, list(glasses)) if not e.startswith('uyari')]
    if errs:
        return None, 'invariant: ' + errs[0]
    ok, info = solver(lv)
    if not ok:
        return None, info
    return lv, info
