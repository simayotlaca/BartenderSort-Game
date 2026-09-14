# Ready badge

The production badge is a clean vector drawing: two circles with restrained
vertical gradients and one ivory check with round caps and joins. It has no
paint texture, grain, bevel noise, or baked shadow.

Run from the repository root:

```sh
swift Tools/ArtBake/ReadyBadge/render.swift
```

The exporter writes the editable SVG next to this file and the 1024 px RGBA PNG
to `Assets/LiquidSort/RoyalGlassLab/Art/CheckBadge_Royal_CleanBlue_v4.png`.
Both are generated from the same geometry and palette in the Swift source.
CoreGraphics draws at four times the output size before high-quality export.

Unity uses uncompressed sRGB, transparent edges, mipmaps, and trilinear filtering
for the small world-space badge. Its pixels-per-unit value preserves the existing
badge transform and full sprite bounds. The subtle highlight remains a separate
prefab child so the existing completion animation also owns its visibility.
