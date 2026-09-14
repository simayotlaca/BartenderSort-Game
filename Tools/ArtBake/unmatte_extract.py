import sys
import numpy as np
from PIL import Image
from scipy import ndimage as ndi

SRC = sys.argv[1]
OUT = sys.argv[2]
DBG = sys.argv[3]

im = np.array(Image.open(SRC).convert('RGB')).astype(np.float32)
H, W, _ = im.shape
gray = im.mean(2)
chroma = im.max(2) - im.min(2)

# ---- 1. hard candidates: anything coloured or dark can't be checker
CHR = float(sys.argv[4]) if len(sys.argv) > 4 else 12.0
_strip = np.zeros((H, W), bool); _strip[:40, :] = True; _strip[-40:, :] = True; _strip[:, :40] = True; _strip[:, -40:] = True
_g = gray[_strip & (chroma <= CHR)]
DARK = min(150.0, float(np.percentile(_g, 2)) - 25.0) if _g.size > 1000 else 150.0
print('dark threshold', round(DARK, 1))
solid0 = (chroma > CHR) | (gray < DARK)

# ---- 2. classify neutral components: outside / see-through hole / white highlight
four = np.array([[0, 1, 0], [1, 1, 1], [0, 1, 0]])
lab, n = ndi.label(~solid0, structure=four)
idx = np.arange(1, n + 1)
area = ndi.sum(np.ones_like(gray), lab, idx)
gstd = ndi.standard_deviation(gray, lab, idx)
gmean = ndi.mean(gray, lab, idx)
hf = np.abs(gray - ndi.gaussian_filter(gray, 2.0))
hfm = ndi.mean(hf, lab, idx)
border = set(np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))) - {0}

transparent = np.zeros(n + 1, bool)
rows = []
for i in range(1, n + 1):
    a, s, h, m = area[i - 1], gstd[i - 1], hfm[i - 1], gmean[i - 1]
    if i in border:
        t = True; why = 'border'
    elif a >= 40 and s > 18:
        t = True; why = 'textured'
    else:
        t = False; why = 'flat/small'
    transparent[i] = t
    if a >= 40:
        rows.append((int(a), round(float(s), 1), round(float(h), 1), round(float(m), 1), why, i in border))
rows.sort(reverse=True)
print('neutral components (area>=40): area, gray_std, hf, gray_mean, verdict, border')
for r in rows[:40]:
    print('  ', r)

M = ~transparent[lab]  # solid incl. filled highlights

# ---- 3. keep only the mug (drop dust / specks)
labS, nS = ndi.label(M, structure=np.ones((3, 3)))
sizes = ndi.sum(M, labS, np.arange(1, nS + 1))
main = int(np.argmax(sizes)) + 1
dropped = [(int(s)) for j, s in enumerate(sizes) if j + 1 != main]
print('solid components:', nS, 'main area', int(sizes[main - 1]), 'dropped islands (areas):', sorted(dropped, reverse=True)[:15])
M = labS == main

# ---- 4. soft alpha in a band around the silhouette
core = ndi.binary_erosion(M, iterations=2)
band = ndi.binary_dilation(M, iterations=2) & ~core
_, (iy, ix) = ndi.distance_transform_edt(~core, return_indices=True)
Fref = im[iy, ix]
chromaF = Fref.max(2) - Fref.min(2)
a_chroma = np.clip((chroma - 5.0) / np.maximum(chromaF - 5.0, 1.0), 0, 1)
w = np.clip((chromaF - 15.0) / 30.0, 0, 1)
Msoft = ndi.gaussian_filter(M.astype(np.float32), 1.0)
alpha = np.where(core, 1.0, np.where(band, w * a_chroma + (1 - w) * Msoft, 0.0)).astype(np.float32)
# light smoothing of the band only, to kill chroma noise
alpha_s = ndi.gaussian_filter(alpha, 0.6)
alpha = np.where(band, alpha_s, alpha)
alpha = np.clip(alpha, 0, 1)

# ---- 5. colour: nearest solid colour bleeds outward (no light fringe on bilinear sampling)
t = np.clip((alpha - 0.6) / 0.35, 0, 1)
t = t * t * (3 - 2 * t)
color = np.where(core[..., None], im, (1 - t[..., None]) * Fref + t[..., None] * im)

rgba = np.concatenate([np.clip(color, 0, 255), alpha[..., None] * 255], axis=2).round().astype(np.uint8)
Image.fromarray(rgba, 'RGBA').save(OUT)

# ---- stats
a8 = rgba[..., 3]
print('alpha hist (0,1-31,32-223,224-254,255):', int((a8 == 0).sum()), int(((a8 > 0) & (a8 < 32)).sum()),
      int(((a8 >= 32) & (a8 < 224)).sum()), int(((a8 >= 224) & (a8 < 255)).sum()), int((a8 == 255).sum()))
ys, xs = np.nonzero(a8)
print('content bbox x[%d..%d] y[%d..%d] of %dx%d' % (xs.min(), xs.max(), ys.min(), ys.max(), W, H))
print('corner alphas', a8[0, 0], a8[0, -1], a8[-1, 0], a8[-1, -1])

# ---- debug views
dbg = im.copy()
holes = transparent[lab] & (lab > 0) & ~np.isin(lab, list(border))
filled = (~transparent[lab]) & (lab > 0)
dbg[holes] = dbg[holes] * 0.4 + np.array([0, 255, 255]) * 0.6
dbg[filled] = dbg[filled] * 0.4 + np.array([255, 255, 0]) * 0.6
Image.fromarray(dbg.astype(np.uint8)).resize((W // 3, H // 3), Image.LANCZOS).save(DBG + '/dbg_components.png')

out = Image.fromarray(rgba, 'RGBA')
for name, bgc in [('dark', (28, 30, 48, 255)), ('white', (255, 255, 255, 255))]:
    bg = Image.new('RGBA', out.size, bgc)
    bg.alpha_composite(out)
    bg.resize((W // 3, H // 3), Image.LANCZOS).save(DBG + '/result_on_%s.png' % name)
