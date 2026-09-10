# Cream garnish

The handled mug uses `Assets/LiquidSort/RoyalGlassLab/Art/Garnishes/CreamSwirl_MugRoyal_Clean_v1.png`, connected through `MugRoyal.asset` and `Latte Glass.prefab` (a legacy filename).

The sprite is RGBA, 1213 × 697, with transparent background and antialiased edges. The original 1536 × 1024 canvas was trimmed without stretching, rotating or rescaling the artwork. Interior RGB pixels were preserved; cleanup affected the narrow antialiased boundary and excess transparent padding.

## Placement

Width share: 0.90. Liquid chord share: 0.90. Horizontal offset: 0. Surface lift share: 0.40. These settings preserve the artwork aspect ratio and centre it on the live liquid surface. At rest and without rim clamping, the lower 10% of the cream lies below the surface centre. The existing rim constraint and order-ready visibility behavior still apply.

`MugFoamCap.mat` disables foam breathing/bubble effects, submerged tint and submerged transparency so the artwork remains visually consistent.

## Import settings

Single Sprite, tight mesh, centred pivot, 256 PPU, alpha transparency, mipmaps off, bilinear filtering, clamp, uncompressed, max size 2048.

The garnish presenter applies the placement settings during gameplay. A 2.2-second idle cycle adds a gentle vertical bob (0.012 of interior width) and 1.2-degree roll; transfer reservations fade this motion out.
