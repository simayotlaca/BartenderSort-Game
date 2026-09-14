#!/usr/bin/env python3
"""Alfa gomulugu temizleyici.

Arka plani sonradan sokulen PNG'lerde alfa kanali hic tam opak
olmaz: govde 249-253 arasinda dalgalanir (icerigin hayaleti alfaya gomulur),
siluetin disinda alfasi 1-7 olan renkli benekler kalir ve saydam bolgenin RGB'si
siyahtir (kucultunce kenara kir bulasir).

Yapilan is:
  1. Ana siluet disindaki benekleri siler.
  2. (opaque modunda) govdeyi tam 255'e sabitler; kenar yumusakligi korunur.
  3. Saydam piksellerin RGB'sini en yakin GERCEK opak pikselden doldurur.

DIKKAT — modlar:
  opaque   Govdenin tamamen opak olmasi gerekiyorsa (buton, kart, rozet).
  preserve Govdede gercekten yari saydam bolge varsa (bardak, kupa, cam, golge):
           sadece benek temizligi + renk tasirma yapar, alfayi sertlestirmez.
  auto     (varsayilan) Govdede genis yari saydam bolge olcerse preserve'e duser
           ve bunu bildirir.

Kullanim:
  python3 alpha_deembed.py girdi.png cikti.png [--mode auto|opaque|preserve]
                           [--core 128] [--floor 4] [--keep-ring 6]
                           [--fill-holes] [--report]
"""

import argparse
import sys

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

DONOR_MIN_ALPHA = 8          # renk tasirmada kaynak sayilacak en dusuk alfa
SOFT_BODY_LIMIT = 0.02       # govdenin %2'sinden fazlasi yari saydamsa "yumusak"


def largest_component(mask):
    lbl, n = ndi.label(mask)
    if n <= 1:
        return mask
    sizes = ndi.sum(mask, lbl, range(1, n + 1))
    return lbl == (int(np.argmax(sizes)) + 1)


def softness(a, core, thr=247):
    """Govde gercekten yari saydam mi? Iki olcut dondurur (oran, oran)."""
    if not core.any():
        return 0.0, 0.0
    d = ndi.distance_transform_edt(core)
    inner = core & (d > 2)
    semi = float((inner & (a < thr)).sum()) / max(int(inner.sum()), 1)
    holes = ndi.binary_fill_holes(a > 0) & (a <= 0)
    return semi, float(holes.sum()) / max(int(core.sum()), 1)


def deembed(rgba, core_thr=128, floor=4.0, keep_ring=6, mode="auto",
            fill_holes=False, log=print):
    rgb = rgba[..., :3].astype(np.float32)
    a = rgba[..., 3].astype(np.float32)

    core = largest_component(a >= core_thr)
    if fill_holes:
        filled = ndi.binary_fill_holes(core)
        extra = int((filled & ~core).sum())
        if extra > 0.005 * core.sum():
            log(f"  uyari: ic bosluk {extra:,} px — govde gercekten delikli olabilir, "
                f"doldurma atlandi")
        else:
            core = filled

    semi, holes = softness(a, core)
    if mode == "auto":
        mode = "preserve" if (semi > SOFT_BODY_LIMIT or holes > SOFT_BODY_LIMIT) else "opaque"
        if mode == "preserve":
            log(f"  govde ici yari saydam %{semi*100:.1f}, kapali bosluk %{holes*100:.1f} "
                f"— cam/golge iceren varlik gibi duruyor, preserve moduna dusuldu "
                f"(alfa sertlestirilmiyor)")

    # 1) siluetten kopuk benekleri sil. opaque modunda kenar bandinin disinda
    #    kalan her sey gider; preserve modunda (cam, golge) sadece govdeden
    #    tamamen kopuk parcalar temizlenir — soluk hale dokunulmaz.
    if mode == "opaque":
        valid = ndi.binary_dilation(core, iterations=keep_ring)
        a = np.where(valid, a, 0.0)

    # 2) govde tam opak; kenar rampasina dokunulmuyor (rampayi yeniden olceklemek
    #    bazi kenar piksellerini esigin ustune itip araci idempotent olmaktan
    #    cikariyordu — ve rampa degerleri zaten gercek kaplama, gomulu artik degil)
    interior = ndi.binary_fill_holes(core)
    a = np.where(~interior & (a <= floor), 0.0, a)
    if mode == "opaque":
        a = np.where(core, 255.0, a)

    a = np.rint(a)

    # 3) benek artigi kalmasin: son maskede de tek parca
    keep = largest_component(a > 0)
    a = np.where(keep, a, 0.0)

    # rampa icinde tekil bosluk kalirsa bildir; doldurmuyoruz — doldurma araci
    # idempotent olmaktan cikariyor ve bu pikseller zaten alfa 1-4 bandinda.
    gaps = ndi.binary_fill_holes(a > 0) & (a <= 0)

    # 4) saydam piksellere en yakin gercek opak rengi tasi
    donors = keep & (a >= DONOR_MIN_ALPHA)
    holes = a <= 0
    if donors.any() and holes.any():
        _, idx = ndi.distance_transform_edt(~donors, return_indices=True)
        rgb = np.where(holes[..., None], rgb[idx[0], idx[1]], rgb)

    out = np.empty_like(rgba)
    out[..., :3] = np.clip(rgb, 0, 255).astype(np.uint8)
    out[..., 3] = np.clip(a, 0, 255).astype(np.uint8)
    return out


def stats(rgba, label):
    a = rgba[..., 3].astype(np.float32)
    core = a >= 128
    inner = ndi.binary_erosion(core, iterations=3)
    body = f"{a[inner].min():.0f}-{a[inner].max():.0f}" if inner.any() else "-"
    _, n = ndi.label(a > 0)
    faint = int(((a > 0) & (a <= 7)).sum())
    pin = int((ndi.binary_fill_holes(a > 0) & (a <= 0)).sum())
    return (f"{label}: govde alfa {body}, tam opak {(a >= 255).sum():,}, "
            f"silik (1-7) {faint:,}, parca {n}, rampa ici bosluk {pin}")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("src")
    p.add_argument("dst")
    p.add_argument("--mode", choices=["auto", "opaque", "preserve"], default="auto")
    p.add_argument("--core", type=int, default=128)
    p.add_argument("--floor", type=float, default=4.0)
    p.add_argument("--keep-ring", type=int, default=6)
    p.add_argument("--fill-holes", action="store_true")
    p.add_argument("--report", action="store_true")
    args = p.parse_args()

    src = np.array(Image.open(args.src).convert("RGBA"))
    out = deembed(src, args.core, args.floor, args.keep_ring, args.mode, args.fill_holes)
    Image.fromarray(out).save(args.dst)

    if args.report:
        print(stats(src, "once "))
        print(stats(out, "sonra"))
    print(f"yazildi: {args.dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
