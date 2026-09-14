#!/usr/bin/env python3
"""I remove grey checkerboards with NumPy and Pillow.
I keep the largest 4-connected colour/dark region and refill smooth holes.
Chroma contrast sets edge alpha, using foreground colour from 2 px inward.
I reject implausible grey backgrounds, limit the halo to 1 px,
and bleed colour into transparent pixels for clean filtered edges.

Usage: unmatte_chroma.py SOURCE.png OUT.png
Debug: DEBUG_PTS="x,y;x,y" unmatte_chroma.py SOURCE.png OUT.png
"""
import os, sys
import numpy as np
from PIL import Image

CHROMA_FG = 18      # max-min above this -> foreground
DARK_FG   = 95      # grey below this  -> foreground (dark outlines)
BG_CHROMA = 8       # confident checker: chroma <= this and grey inside BG_RANGE
BG_RANGE  = (105, 222)
HOLE_STD  = 12      # holes with grey std below this are art (highlight/shadow), not checker
HALO_MIN  = 0.12
FLOOR_IN  = 0.25    # pixels that passed the chroma test never drop below this


def label(mask):
    """4-connected components (labels int32 starting at 1, count) using min-label propagation."""
    H, W = mask.shape
    idx = np.arange(H * W, dtype=np.int64).reshape(H, W)
    lab = np.where(mask, idx, -1).ravel()
    h = mask[:, 1:] & mask[:, :-1]; v = mask[1:, :] & mask[:-1, :]
    A = np.concatenate([idx[:, 1:][h], idx[1:, :][v]]); B = np.concatenate([idx[:, :-1][h], idx[:-1, :][v]])
    while True:
        la, lb = lab[A], lab[B]; mn = np.minimum(la, lb); changed = False
        if (la != mn).any(): np.minimum.at(lab, A, mn); changed = True
        if (lb != mn).any(): np.minimum.at(lab, B, mn); changed = True
        valid = lab >= 0; nxt = lab.copy(); nxt[valid] = lab[lab[valid]]
        if (nxt != lab).any(): lab = nxt; changed = True
        if not changed: break
    out = np.zeros(H * W, np.int32); valid = lab >= 0
    u, inv = np.unique(lab[valid], return_inverse=True); out[valid] = inv + 1
    return out.reshape(H, W), len(u)


def dil(m, r):
    out = m.copy()
    for _ in range(r):
        p = np.pad(out, 1)
        out = (p[1:-1, 1:-1] | p[:-2, 1:-1] | p[2:, 1:-1] | p[1:-1, :-2] | p[1:-1, 2:]
               | p[:-2, :-2] | p[:-2, 2:] | p[2:, :-2] | p[2:, 2:])
    return out


def ero(m, r):
    return ~dil(~m, r)


def propagate(vals, known, iters):
    """Fill unknown pixels with the mean of their known 8-neighbours, repeated `iters` times."""
    H, W, _ = vals.shape
    v = vals.copy(); k = known.copy()
    for _ in range(iters):
        pv = np.pad(v, ((1, 1), (1, 1), (0, 0))); pk = np.pad(k, 1).astype(np.float32)
        acc = np.zeros_like(v); cnt = np.zeros((H, W), np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0: continue
                wk = pk[1 + dy:H + 1 + dy, 1 + dx:W + 1 + dx]
                acc += pv[1 + dy:H + 1 + dy, 1 + dx:W + 1 + dx] * wk[..., None]; cnt += wk
        new = (~k) & (cnt > 0)
        v[new] = acc[new] / cnt[new][:, None]; k = k | new
    return v, k


def unmatte(I):
    H, W, _ = I.shape
    a16 = I.astype(np.int16); c = a16.max(-1) - a16.min(-1); g = I.mean(-1)

    # 1. hard mask: colour or dark outline, largest component only
    fg_hard = (c > CHROMA_FG) | (g < DARK_FG)
    bg_hard = (c <= BG_CHROMA) & (g >= BG_RANGE[0]) & (g <= BG_RANGE[1])
    lab, n = label(fg_hard); s = np.bincount(lab.ravel(), minlength=n + 1)[1:]
    M = lab == (np.argmax(s) + 1)

    # 2. fill smooth holes (highlights/shadows); checker holes have grey std ~30 and stay open
    lab2, n2 = label(~M)
    cnt = np.bincount(lab2.ravel(), minlength=n2 + 1)
    sumg = np.bincount(lab2.ravel(), weights=g.ravel(), minlength=n2 + 1)
    sumg2 = np.bincount(lab2.ravel(), weights=(g * g).ravel(), minlength=n2 + 1)
    mean = sumg / np.maximum(cnt, 1); std = np.sqrt(np.maximum(sumg2 / np.maximum(cnt, 1) - mean ** 2, 0))
    fill = (std < HOLE_STD) | (cnt < 6); fill[0] = False; fill[np.argmax(cnt[1:]) + 1] = False
    M = M | fill[lab2]
    print('mask px', int(M.sum()), ' holes filled', int(fill.sum()))

    # 3. confident interior / checker, and the band where alpha is estimated
    Fin = ero(M, 2); Bconf = bg_hard & ~dil(M, 2)
    band = (dil(M, 3) & ~Fin) | (M & dil(~M, 5) & (c < 30))   # edge band + weakly coloured px next to a gap
    B, kb = propagate(np.where(Bconf[..., None], I, 0), Bconf, 8)
    Fsrc = Fin & ~band
    F, kf = propagate(np.where(Fsrc[..., None], I, 0), Fsrc, 10)

    # 4a. luminance projection (fallback where F is neutral)
    d = F - B; num = ((I - B) * d).sum(-1); den = (d * d).sum(-1)
    alpha = np.where(M, 1.0, 0.0).astype(np.float32)
    ok = band & kb & kf & (den > 30 * 30)
    alpha[ok] = np.clip(num[ok] / den[ok], 0, 1)

    # 4b. chroma ratio (checker-phase independent), with a faint colour-cast correction
    Ip = I - I.mean(-1, keepdims=True); Fp = F - F.mean(-1, keepdims=True)
    Bp = np.where(kb[..., None], B - B.mean(-1, keepdims=True), 0)
    Fpb = Fp - Bp; Ipb = Ip - Bp
    Im = np.sqrt((Ipb * Ipb).sum(-1)); Fm = np.sqrt((Fpb * Fpb).sum(-1))
    okc = band & kf & (Fm > 25)
    ac = np.clip(Im / np.maximum(Fm, 1e-3), 0, 1)
    a1 = np.minimum(ac, 0.95)[..., None]
    Bimp = (I - a1 * F) / (1 - a1); Bl = Bimp.mean(-1); Bc = np.sqrt(((Bimp - Bl[..., None]) ** 2).sum(-1))
    plaus = ((Bl >= 95) & (Bl <= 235) & (Bc <= 30)) | (ac >= 0.85)
    okc = okc & plaus
    alpha[okc] = ac[okc]
    rej = band & kf & (Fm > 25) & ~plaus
    alpha[rej] = np.where(M[rej], 1.0, 0.0)
    # a rejected mask pixel that is much lighter than the interior colour but not a true white highlight
    # is a blend with something bright (e.g. a neutral glint blob next to the art): keep the chroma alpha
    Il = I.mean(-1); Fl = F.mean(-1)
    blend = rej & M & dil(~M, 1) & (Il < 238) & (Il - Fl > 30)
    alpha[blend] = np.maximum(ac[blend], FLOOR_IN)
    okc = okc | blend; rej = rej & ~blend
    print('polish: light rejected blends re-accepted', int(blend.sum()))
    if os.environ.get('DUMP_MASK'): np.save(os.environ['DUMP_MASK'], blend)
    ok = (ok & ~rej) | okc

    # 5. halo / floor / single component
    outside = ~M
    alpha[outside & ~dil(M, 1)] = 0
    alpha[outside & (alpha < HALO_MIN)] = 0
    alpha[M & (alpha < FLOOR_IN)] = FLOOR_IN
    # 5b. polish: low-coverage pixels must touch a solid pixel, otherwise they are floating flecks
    strong = alpha >= 0.6
    alpha[(alpha < 0.5) & ~dil(strong, 1)] = 0
    alpha[outside & (alpha < 0.15)] = 0
    # 5c. enclosed gaps: the checker inside the art picks up a colour cast from the liquid, which reads as
    #     faint tinted glass on the dark cells only (dotted line). Around enclosed transparent regions,
    #     coverage below one half is dropped; the outer silhouette keeps its full anti-aliasing.
    labT, nT = label(alpha == 0); sT = np.bincount(labT.ravel(), minlength=nT + 1)[1:]
    enclosed = (alpha == 0) & (labT != (np.argmax(sT) + 1))
    near_gap = dil(enclosed, 2)
    alpha[near_gap & (alpha < 0.5)] = 0
    print('polish: enclosed-gap px', int(enclosed.sum()), ' hardened px', int((near_gap & (alpha == 0) & M).sum()))
    labA, nA = label(alpha > 0); sA = np.bincount(labA.ravel(), minlength=nA + 1)[1:]
    alpha[labA != (np.argmax(sA) + 1)] = 0

    # 6. colour: own chroma scaled back, luminance from the interior; blend to I as alpha -> 1
    al = np.maximum(alpha, 0.05)[..., None]
    scale = np.minimum(1.0 / al[..., 0], 1.2 * Fm / np.maximum(Im, 1e-3))[..., None]
    est = np.clip(F.mean(-1, keepdims=True) + Bp + Ipb * scale, 0, 255)
    decon = np.clip(B + (I - B) / al, 0, 255)
    est = np.where(okc[..., None], est, np.where(ok[..., None], np.where((alpha >= 0.5)[..., None], decon, F), I))
    w = np.clip((alpha - 0.8) / 0.2, 0, 1)[..., None]; w = w * w * (3 - 2 * w)
    col = np.where(ok[..., None], w * I + (1 - w) * est, I)

    # 6b. defringe: solid boundary pixels that stayed grey (checker-tinted) lean towards the interior colour
    op = alpha >= 1
    ring = op & ~ero(op, 1)
    cc = col.max(-1) - col.min(-1); cl = col.mean(-1)
    grey = ring & kf & (cc < 22) & (cl > 110) & (cl < 215)
    col[grey] = 0.4 * col[grey] + 0.6 * F[grey]
    print('polish: defringed boundary px', int(grey.sum()))

    # 7. bleed colour into the transparent area, rest = mean art colour
    vis = alpha > 0
    bl, kbl = propagate(np.where(vis[..., None], col, 0), vis, 12)
    col = np.where(kbl[..., None], bl, col[M].mean(0))

    for pt in os.environ.get('DEBUG_PTS', '').split(';'):
        if not pt: continue
        x, y = map(int, pt.split(','))
        print('DBG (%d,%d) I=%s F=%s B=%s kb=%d kf=%d M=%d band=%d ok=%d okc=%d Fm=%.1f Im=%.1f alpha=%.2f col=%s' % (
            x, y, I[y, x].astype(int), F[y, x].astype(int), B[y, x].astype(int), kb[y, x], kf[y, x], M[y, x],
            band[y, x], ok[y, x], okc[y, x], Fm[y, x], Im[y, x], alpha[y, x], col[y, x].astype(int)))
    print('QA: bright source px (g>225) inside mask with alpha<0.5:', int((M & (g > 225) & (alpha < 0.5)).sum()),
          ' rejected-implausible px:', int(rej.sum()))
    out = np.dstack([np.clip(col, 0, 255), np.clip(alpha * 255, 0, 255)]).astype(np.uint8)
    A = out[..., 3]; _, nA = label(A > 0)
    print('alpha components', nA, ' semi px', int(((A > 0) & (A < 255)).sum()), ' opaque px', int((A == 255).sum()))
    return out


def main():
    if len(sys.argv) != 3:
        print(__doc__); sys.exit(2)
    I = np.array(Image.open(sys.argv[1]).convert('RGB')).astype(np.float32)
    Image.fromarray(unmatte(I)).save(sys.argv[2])


if __name__ == '__main__':
    main()
