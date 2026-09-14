# LiquidSort runtime

The runtime renders and animates layered liquid inside pre-baked vessel profiles.

## Main components

| Component | Responsibility |
| --- | --- |
| `VesselProfile` | Baked interior geometry, capacity, fill tables and pour pose |
| `LiquidBottle` | Liquid state and shader data |
| `BottleShell` | Authored glass front, contour, shadow and theme |
| `PourAnimator` | Carry, tilt, transfer, settle and return sequence |
| `PourStream` | Procedural stream mesh |
| `BartenderLevelController` | Authoritative campaign board and commands |
| `BartenderShelfLevelView` | Royal pool, shelf layout and board presentation |
| `BartenderPourInteraction` | Camera input and domain-first pour animation bridge |

The production glass reference scene is
`RoyalGlassLab/RoyalGlassLab_WorkingCopy.unity`. Its visible glass shells are the
five final `Art/PremiumBakes/*PremiumFront_Baked4x_*.png` sprites rendered by
`RoyalGlassSprite_Baked.mat`. Royal profiles remain authoritative for the dynamic
liquid geometry, capacity, masks and pour pose; the liquid system is not flattened
into the baked front. 

## Active two-scene flow

- Main menu: `SortingShelfShowcase.unity`
- Gameplay: `RoyalGlassLab/RoyalGlassLab_WorkingCopy.unity`

The main menu contains presentation only and loads the gameplay scene through its LEVEL
button.

## Adding or changing a vessel

1. Create or update a `VesselProfile` asset.
2. Assign the visible `front` sprite and, when necessary, a separate `traceSource`.
3. Bake the profile with `Tools > LiquidSort > Bake Selected Vessel Profiles`.
4. Validate the profile in an isolated test scene before using it in gameplay.

Do not replace a final profile with an old generated `_v2`, staged or comparison image.
The baked profile owns the geometry used by fill rendering and animation.
