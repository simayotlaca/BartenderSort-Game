# More Lives — clean asset map

This folder contains the standalone transparent sprites used by both More Lives
presentations. The Turkish main-menu card remains wired through
`BartenderMainMenuCanvas.prefab`; the English gameplay card is assembled by hand in
`LevelSystem/Resources/Ui/Lives/BartenderMoreLivesPopup.prefab`.

## New sprites

- `MoreLivesClean_Title_TR.png` — `DAHA FAZLA CAN`
- `MoreLivesClean_Text_NextLife_TR.png` — `SONRAKİ CANA KALAN SÜRE`
- `MoreLivesClean_Button_Refill_Green.png` — empty green refill button; detached alpha artifacts removed
- `MoreLivesClean_Button_Rewarded_Orange.png` — empty orange rewarded button; detached alpha artifacts removed
- `MoreLivesClean_Text_Refill_TR.png` — `DOLDUR`
- `MoreLivesClean_Card_Blue.png` — empty cyan/gold gameplay card body
- `MoreLivesClean_Close_Base_Red.png` — empty red/gold close-button base
- `MoreLivesClean_Close_X.png` — white close glyph
- `MoreLivesClean_Button_Refill_Blue.png` — empty blue/gold gameplay refill button
- `MoreLivesClean_Heart_Empty.png` — standalone empty red heart; the life count remains dynamic Unity text
- `MoreLivesClean_Icon_Clock_v2.png` — cleaned gameplay clock with a tighter, low-noise silhouette
- `MoreLivesClean_TimerFrame_Empty_v2.png` — cleaned cream/gold gameplay timer frame with simplified edge depth

The English gameplay popup uses the two `v2` timer assets. Its existing
`MoreLivesClean_Button_Refill_Blue.png` skin is intentionally unchanged.

The English title, caption, life count, countdown, `REFILL`, cost (`900`), and purchase
feedback are live Unity text in the gameplay prefab rather than baked into sprites.

The life count, countdown, cost (`900`), button labels, and feedback remain dynamic
Unity text. The gameplay popup uses only the retained clean assets listed above; deleted
legacy HUD and result-popup sprites are intentionally not dependencies.
