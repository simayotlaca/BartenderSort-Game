"""I rebalance spare glass capacity without changing liquid, orders or locks. I remove empty spares, shrink spares only when layers fit, or add empty glasses, then check colour balance, type supply, capacity and solvability."""
import re, io, glob, os, sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bssolver import (CAP, NAME, parse_level, first_delivery_reachable, solve)

LEVEL_DIR = 'Assets/LiquidSort/LevelSystem/Resources/Levels'


# Serialization.
def glass_block(ty, layers, unlock_after):
    """I write one glass in the asset's Unity YAML format."""
    out = [f"  - Type: {ty}"]
    if layers:
        out.append("    Layers:")
        for col, hid, lock in layers:
            out.append(f"    - Color: {col}")
            out.append(f"      Hidden: {hid}")
            out.append(f"      LockUntil: {lock}")
    else:
        out.append("    Layers: []")
    out.append(f"    UnlockAfter: {unlock_after}")
    return "\n".join(out)


def write_glasses(path, glasses):
    """I replace only the asset's Glasses section and keep everything else."""
    s = io.open(path, encoding='utf-8').read()
    head, rest = s.split('  Glasses:\n', 1)
    _old, tail = rest.split('  ColumnsPerRow:', 1)
    body = "\n".join(glass_block(t, l, u) for t, l, u in glasses)
    io.open(path, 'w', encoding='utf-8').write(
        head + '  Glasses:\n' + body + '\n  ColumnsPerRow:' + tail)


# Plan generation.
def spare_indices(glasses, orders):
    """Indices of glasses beyond order demand, which can be removed or shrunk."""
    sup = Counter(t for t, _, _ in glasses)
    dem = Counter(g for _, g, _ in orders)
    quota = {t: sup[t] - dem.get(t, 0) for t in sup}
    used = Counter()
    out = []
    # I try empty, larger glasses first to save the most space.
    order = sorted(range(len(glasses)),
                   key=lambda i: (len(glasses[i][1]) > 0, -CAP[glasses[i][0]]))
    for i in order:
        t, l, ua = glasses[i]
        if used[t] >= quota.get(t, 0):
            continue
        if ua > 0:
            continue          # I leave chained glasses alone because they are part of the level design.
        used[t] += 1
        out.append(i)
    return out


def smallest_fit(n_layers):
    return min([t for t in CAP if CAP[t] >= n_layers], key=lambda t: CAP[t])


def plan_for(lv, target_free):
    """I find a short action plan for the target free space. Returns (new_glasses, actions, reached_free_space)."""
    glasses = [list(g) for g in lv['glasses']]
    cur_free = sum(CAP[t] for t, _, _ in glasses) - sum(len(l) for _, l, _ in glasses)
    ops = []

    if target_free < cur_free:
        need = cur_free - target_free
        for i in spare_indices([tuple(g) for g in glasses], lv['orders']):
            if need <= 0:
                break
            t, l, ua = glasses[i]
            if not l:
                # Remove the whole glass.
                if CAP[t] <= need:
                    ops.append((i, 'KALDIR', t, None)); need -= CAP[t]
                    glasses[i] = None
                else:
                    small = smallest_fit(0)
                    # If removal goes too far, shrink it instead.
                    for cand in sorted(CAP, key=lambda x: CAP[x]):
                        if CAP[t] - CAP[cand] <= need and CAP[cand] >= 0:
                            small = cand
                    if CAP[t] > CAP[small]:
                        ops.append((i, 'KUCULT', t, small))
                        need -= CAP[t] - CAP[small]
                        glasses[i][0] = small
            else:
                small = smallest_fit(len(l))
                # Use the smallest type that does not overshoot the target.
                best = t
                for cand in sorted(CAP, key=lambda x: CAP[x]):
                    if CAP[cand] >= len(l) and CAP[t] - CAP[cand] <= need:
                        best = cand; break
                if CAP[t] > CAP[best]:
                    ops.append((i, 'KUCULT', t, best))
                    need -= CAP[t] - CAP[best]
                    glasses[i][0] = best
        glasses = [g for g in glasses if g is not None]

    elif target_free > cur_free:
        need = target_free - cur_free
        # Add empty glasses, largest first.
        while need > 0:
            cand = max([t for t in CAP if CAP[t] <= need], default=None)
            if cand is None:
                cand = min(CAP, key=lambda x: CAP[x])   # A one-unit Shot.
            glasses.append([cand, (), 0])
            ops.append((len(glasses) - 1, 'EKLE', cand, None))
            need -= CAP[cand]

    out = [(g[0], tuple(g[1]), g[2]) for g in glasses]
    free = sum(CAP[t] for t, _, _ in out) - sum(len(l) for _, l, _ in out)
    return out, ops, free


# Validation.
def verify(lv, glasses):
    """I check that all invariants still hold after the change."""
    errs = []
    orders = lv['orders']
    board = Counter(c for _, l, _ in glasses for c, _, _ in l)
    need = Counter(x for _, _, cont in orders for x in cont)
    for c in set(board) | set(need):
        if board[c] != need[c]:
            errs.append(f"korunum bozuldu renk{c}: {board[c]} vs {need[c]}")
    sup = Counter(t for t, _, _ in glasses)
    dem = Counter(g for _, g, _ in orders)
    for t, d in dem.items():
        if sup[t] < d:
            errs.append(f"{NAME[t]} arzi {sup[t]} < talep {d}")
    for i, (t, l, ua) in enumerate(glasses):
        if len(l) > CAP[t]:
            errs.append(f"bardak#{i+1} kapasite asimi ({len(l)}>{CAP[t]})")
    if not any(len(l) == 0 for _, l, _ in glasses):
        errs.append("uyari: hic bos bardak yok")
    return errs


def solvable(lv_like):
    fd, _ = first_delivery_reachable(lv_like)
    if fd is not True:
        return False, 'ilk teslim yok'
    r, mv, n, _ = solve(lv_like, node_budget=400_000)
    return (r == 'COZULDU'), (f"{mv} hamle" if mv else r)
