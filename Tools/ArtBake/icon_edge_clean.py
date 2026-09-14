#!/usr/bin/env python3
"""Ikon kenari temizleyici: tirtikli kontur + koyu kalinti halesi.

Arka plani sokulmus ikonlarda iki kusur birlikte gelir:

  * **Tirtikli kenar.** Alfa neredeyse ikili (0 ya da 255); gecis tek piksel.
    Ustelik tirtik yalniz siluette degil, cizimin kendi koyu kontur cizgilerinde
    de var — bunlar tamamen opak bolgede oldugu icin alfayi duzeltmek yetmiyor.
  * **Koyu gomululuk.** Siluetin disinda alfasi dusuk, rengi koyu bir hale kalir
    (sokulen zeminin artigi). Kucultunce kirli bir golge gibi okunur.

Ikisi de ayni islemle cozuluyor: siluet esikten yeniden cikariliyor, isaretli
uzaklik alani (SDF) hafifce yumusatilip alfaya cevriliyor. Boylece kenar hem
duzguna yakin bir egri oluyor hem de dar ve kontrollu bir yumusak gecise sahip
oluyor. Hale, esigin altinda kaldigi icin kesiliyor; yeni gecis bandinin rengi
de en yakin gercek opak pikselden aliniyor.

Govdenin gercekten yari saydam ic bolgeleri (cam govdesi, bardagin bos ust
kismi) korunur: alfa yeniden yazimi yalnizca tuval kenarina baglanan gercek
dis bolgenin bandinda yapilir.

Kullanim:
  python3 icon_edge_clean.py girdi.png cikti.png [--threshold 128] [--sigma 0.9]
                             [--edge-width 1.4] [--min-area 100] [--report]
"""

import argparse
import os
import sys

import numpy as np
from PIL import Image
from scipy import ndimage as ndi


def despeckle(a, min_area):
    """Alani min_area altindaki kopuk parcalari siler."""
    lbl, n = ndi.label(a > 0)
    if n <= 1:
        return a, 0
    sizes = np.bincount(lbl.ravel())
    small = np.isin(lbl, np.nonzero(sizes < min_area)[0]) & (lbl > 0)
    return np.where(small, 0.0, a), int(small.sum())


def outside_region(mask):
    """Gercek dis bolge: esigin altinda olup tuval kenarina baglanan alan.

    Bardagin icindeki bosluk da esigin altinda ama disariya bagli degil —
    orasi cizimin kendi yari saydamligi, dokunulmamali.
    """
    lbl, n = ndi.label(~mask)
    if n == 0:
        return ~mask
    border = np.unique(np.concatenate([lbl[0], lbl[-1], lbl[:, 0], lbl[:, -1]]))
    border = border[border > 0]
    return np.isin(lbl, border)


def rebuild_edge(a, threshold, sigma, width, band):
    """Dis silueti esikten yeniden cikarip yumusatilmis SDF ile alfaya cevirir.

    Yeniden yazma yalnizca dis konturun bandinda yapilir; cizimin ic yari
    saydam bolgeleri (cam govdesi, bardagin bos ust kismi) oldugu gibi kalir.
    """
    mask = a >= threshold
    if not mask.any():
        return a, mask, np.zeros_like(mask)
    inside = ndi.distance_transform_edt(mask)
    outside = ndi.distance_transform_edt(~mask)
    sdf = np.where(mask, inside - 0.5, 0.5 - outside)
    if sigma > 0:
        sdf = ndi.gaussian_filter(sdf, sigma)
    new = np.clip(sdf / width + 0.5, 0.0, 1.0) * 255.0

    out = outside_region(mask)
    editable = ndi.binary_dilation(out, iterations=int(np.ceil(band)) + 1)
    return np.where(editable, new, a), mask, out


def line_kernel(angle, length, k=9):
    """Verilen yonde ince bir cizgi cekirdegi (bilinear ornekli)."""
    ker = np.zeros((k, k), np.float32)
    c = (k - 1) / 2.0
    for t in np.linspace(-length / 2, length / 2, int(length * 6) + 1):
        x, y = c + t * np.cos(angle), c + t * np.sin(angle)
        x0, y0 = int(np.floor(x)), int(np.floor(y))
        fx, fy = x - x0, y - y0
        for dy in (0, 1):
            for dx in (0, 1):
                xx, yy = x0 + dx, y0 + dy
                if 0 <= xx < k and 0 <= yy < k:
                    ker[yy, xx] += (fx if dx else 1 - fx) * (fy if dy else 1 - fy)
    return ker / ker.sum()


def directional_aa(rgb, a, length, thr, nbins=8):
    """Konturu, kendi yonunde bulandirarak merdiven basamaklarini siler.

    Yatay/dikey olmayan sig acili cizgilerde izotropik bulaniklik yetmiyor:
    basamak boyu 3-5 px oluyor. Yapi tensorunden kontur yonu cikarilip
    bulaniklik sadece o yonde uygulaniyor; boylece cizginin keskinligi
    (kontura dik profil) korunuyor.
    """
    if length <= 0:
        return rgb, a
    pm = rgb * (a[..., None] / 255.0)
    edge, gx, gy = edge_mask(pm, thr)
    jxx = ndi.gaussian_filter(gx * gx, 1.2)
    jyy = ndi.gaussian_filter(gy * gy, 1.2)
    jxy = ndi.gaussian_filter(gx * gy, 1.2)
    tangent = 0.5 * np.arctan2(2 * jxy, jxx - jyy) + np.pi / 2
    bins = np.mod(np.round(tangent / (np.pi / nbins)).astype(int), nbins)

    stack = np.concatenate([pm, a[..., None]], axis=-1)
    out = stack.copy()
    for b in range(nbins):
        sel = edge & (bins == b)
        if not sel.any():
            continue
        ker = line_kernel(b * np.pi / nbins, length)
        for ch in range(4):
            out[..., ch] = np.where(sel, ndi.convolve(stack[..., ch], ker, mode="nearest"),
                                    out[..., ch])
    a2 = np.clip(out[..., 3], 0, 255)
    safe = a2 > 0.5
    rgb2 = np.where(safe[..., None], out[..., :3] / np.maximum(a2[..., None] / 255.0, 1e-6), rgb)
    return np.clip(rgb2, 0, 255), a2


def edge_mask(pm, thr):
    """Kontur maskesi: gradyan sirtlari.

    Duz esiklemek yetmiyor — bu cizimlerde yumusak golge gradyanlari da esigi
    asiyor ve butun resim "kenar" sayilip bulaniklasiyordu. Sadece gradyanin
    yerel tepe yaptigi (yani gercek kontur olan) pikseller seciliyor.
    """
    lum = pm.mean(axis=2)
    gx = ndi.sobel(lum, axis=1) / 4.0
    gy = ndi.sobel(lum, axis=0) / 4.0
    grad = np.hypot(gx, gy)
    ridge = grad >= ndi.maximum_filter(grad, size=3) - 1e-3
    return ndi.binary_dilation(ridge & (grad > thr)), gx, gy


def antialias(rgb, a, sigma, thr):
    """Konturdaki merdiven basamaklarini yumusatir (premultiplied uzayda)."""
    if sigma <= 0:
        return rgb, a
    pm = rgb * (a[..., None] / 255.0)
    edge, _, _ = edge_mask(pm, thr)
    pm_s = np.stack([ndi.gaussian_filter(pm[..., i], sigma) for i in range(3)], axis=-1)
    a_s = ndi.gaussian_filter(a, sigma)
    pm = np.where(edge[..., None], pm_s, pm)
    a2 = np.where(edge, a_s, a)
    safe = a2 > 0.5
    rgb2 = np.where(safe[..., None], pm / np.maximum(a2[..., None] / 255.0, 1e-6), rgb)
    return np.clip(rgb2, 0, 255), a2


def bleed(rgb, donors, targets):
    """targets piksellerinin rengini en yakin donor pikselinden alir."""
    if not donors.any() or not targets.any():
        return rgb
    _, idx = ndi.distance_transform_edt(~donors, return_indices=True)
    return np.where(targets[..., None], rgb[idx[0], idx[1]], rgb)


def clean(rgba, threshold=128, sigma=0.5, width=1.0, min_area=100, band=3.0,
          aa_sigma=0.45, aa_length=3.0, aa_threshold=60.0, log=print):
    rgb = rgba[..., :3].astype(np.float32)
    a = rgba[..., 3].astype(np.float32)

    a, removed = despeckle(a, min_area)
    if removed:
        log(f"  {removed:,} px kopuk benek silindi")

    old_solid = a >= threshold
    a, mask, out_region = rebuild_edge(a, threshold, sigma, width, band)
    a = np.rint(np.clip(a, 0, 255))

    # yeni gecis bandinin ve eski halenin rengini gercek cizimden al:
    # esigin altinda kalan her yerin rengi en yakin opak pikselden gelir
    donors = old_solid & (rgba[..., 3] >= 250)
    if not donors.any():
        donors = old_solid
    # renk sadece gercek dis bolgede tazelenir; ic yari saydam bolgelerin
    # (cam govdesi) rengine dokunulmaz
    rgb = bleed(rgb, donors, out_region)

    # cizim ici konturlarin merdivenini yumusat: once hafif izotropik, sonra
    # kontur yonunde — sig acili cizgilerdeki uzun basamaklar boyle siliniyor
    rgb, a = antialias(rgb, a, aa_sigma, aa_threshold)
    rgb, a = directional_aa(rgb, a, aa_length, aa_threshold)
    a = np.rint(np.clip(a, 0, 255))

    # mip guvenligi: tamamen saydam piksellerin rengi en yakin gorunur pikselden
    rgb = bleed(rgb, a >= 8, a <= 0)

    out = np.empty_like(rgba)
    out[..., :3] = np.clip(rgb, 0, 255).astype(np.uint8)
    out[..., 3] = a.astype(np.uint8)
    return out


def stats(rgba, label):
    a = rgba[..., 3].astype(np.float32)
    core = a >= 128
    ring = ndi.binary_dilation(core) & ~core
    soft = int(((a > 8) & (a < 247)).sum())
    width = soft / max(int(ring.sum()), 1)
    _, n = ndi.label(a > 0)
    return (f"{label}: yumusak gecis {width:.2f} px, ara piksel {soft:,}, "
            f"parca {n}, kaplama {a.sum()/255:,.0f} px")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("src")
    p.add_argument("dst")
    p.add_argument("--threshold", type=float, default=128.0)
    p.add_argument("--sigma", type=float, default=0.5)
    p.add_argument("--edge-width", type=float, default=1.0)
    p.add_argument("--aa-sigma", type=float, default=0.45)
    p.add_argument("--aa-length", type=float, default=3.0)
    p.add_argument("--aa-threshold", type=float, default=60.0)
    p.add_argument("--min-area", type=int, default=100)
    p.add_argument("--report", action="store_true")
    args = p.parse_args()

    src = np.array(Image.open(args.src).convert("RGBA"))
    out = clean(src, args.threshold, args.sigma, args.edge_width, args.min_area,
                aa_sigma=args.aa_sigma, aa_length=args.aa_length,
                aa_threshold=args.aa_threshold)
    os.makedirs(os.path.dirname(os.path.abspath(args.dst)), exist_ok=True)
    Image.fromarray(out).save(args.dst)
    if args.report:
        print(stats(src, "once "))
        print(stats(out, "sonra"))
    print(f"yazildi: {args.dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
