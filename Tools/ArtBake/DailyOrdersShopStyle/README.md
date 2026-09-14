# Today's Orders popup — mağaza (Cocktail Coins) çizim dili

`render_popup.py [çıktı.png]` → varsayılan çıktı `ArtPreviews/DailyOrders/DailyOrders_Popup_ShopStyle_v1.png`
(1080x1920, karartılmış bar arka planı üstünde popup). Oyuna bağlı DEĞİL; sadece önizleme.

## Ne nereden geliyor
| Öğe | Kaynak |
|---|---|
| Panel | `Ui/Shop/ShopPanel_Navy.png` 900x1236'ya küçültüldü, altına yumuşak gölge eklendi |
| Başlık | Lilita One, ShopTitle tarifi: turuncu-altın dikey gradyan + koyu turuncu kontur + beyaz ışıma (ölçülen düşüş: kenarda tam, ~30 px'te söner) |
| Zamanlayıcı plakası | `Ui/DailyOrders/DailyOrders_TimerPlate.png` (zaten düz altın çerçeve reçetesi) |
| Saat ikonu | SDF ile vektör (`clock_icon`), kontursuz ikon dili |
| Görev kartları | `ShopCard_Blue/Purple/Red` (mağaza kademeleri gibi), bardak ikonları `DailyOrders_Icon_*` |
| İlerleme çubuğu | `make_frame`: 4 px altın + 2 px koyu çizgi, yüzü kartın koyu tonu; dolgu ShopPrice yeşili |
| Ödül | `ShopAmount_250.png`'den kırpılan coin + "+30" ShopAmount yazı tarifi (krem dolgu, kahverengi kontur ve gölge) |
| PLAY | `make_frame` ile ShopPrice yeşili büyük pill (14 px altın + 4 px koyu, 26 px gölge, hafif iç çizgi) |
| Kapat | `Ui/Lives/MoreLivesClean_Close_Base_Red.png` + `_Close_X.png` |

Altın bant rengi, DailyOrdersFlatRim'deki gibi ShopCard_Blue (yan profil) ve ShopCard_Purple (üst açık altın)
dosyalarından okunur; `draw_clipboard.Canvas` yeniden kullanılır. Çalışma süresi ~1 s.

## Yazı tarifleri (`text_img`)
- `title_text`: gradyan (255,216,64)→(246,172,22), kontur (178,64,0) %4.5, ışıma beyaz, sigma %15.
- `amount_text`: dolgu (255,246,214), kontur (95,39,7) %5.5, gölge (60,24,5) %8 aşağı.
- `pill_text`: beyaz, yüzün koyu tonunda sert gölge %9 aşağı.
- `plain_text`: düz renk (altın/krem) + lacivert gölge; "0 / 3 COMPLETE", bonus satırı, sayaçlar.

## Kart prefab'ı: yazılar ve ilerleme çubuğu
İlerleme çubuğu altın çerçevesiz, kalın lacivert oluk ve parlak yeşil dolgudan oluşur.

    python3 bake_card_parts.py    # sprite'lar + 3 TMP materyali (deterministik GUID, tekrar çalıştırmak güvenli)
    python3 apply_card_prefab.py  # DailyOrderCard.prefab'ı bağlar; metin birebir eşleşmezse hiçbir şey yazmaz

Üretilenler:
- `Ui/DailyOrders/DailyOrders_ProgressTrack.png` 796x64 (2x; Sliced, kenar 32/30, Image çarpanı 2) ve
  `DailyOrders_ProgressFill.png` 784x52 (2x; Filled-yatay, dolgu rect'i olukta 3 birim içeride: sizeDelta -6,-6).
- `RoyalGlassLab/Materials/LilitaOne_CardTitle_SDF.mat` ve `LilitaOne_CardCounter_SDF.mat`: **düz, gölgesiz**: lacivert (7,39,88), başlık 36 / sayaç 29 punto. Underlay değerleri dosyada duruyor ama anahtar kapalı;
  inspector'dan Underlay açılırsa ~%8-9 aşağı lacivert gölge gelir.
  `LilitaOne_CardAmount_SDF.mat` (krem + kahverengi kontur %5 dışa, yüz incelmesin diye FaceDilate=OutlineWidth=0.15, + kahverengi gölge %7.5). Piksel hesabı: 1 alfa birimi = 32 atlas px (ölçüldü), kontur px = (dilate+outline)·ratioA·16.
  Oranlar TMP'nin kendi formülüyle hesaplanıp yazıldı (mevcut S24/S32 ön ayarlarıyla birebir doğrulandı):
  ratioA=(G-1)/(G·max(1,dilate+outline+soft)), ratioC=max(0,G-1-dilate·(G-1))/(G·max(1,|offset|+udilate+usoft)), G=16.
- Prefab'da renkler m_fontColor'da (başlık+sayaç lacivert, ödül krem), Bold kapalı (fontStyle 0, weight 400 — Lilita'nın bold'u yok).
Önizleme: `ArtPreviews/DailyOrders/DailyOrderCard_ShopText_preview.png` (Python taklidi; gerçek TMP çıktısı editörde görülür).

## Krem kart önizlemesi
Hazır temiz görsel: `ArtPreviews/DailyOrders/DailyOrders_Card_CreamGold_Clean_v1.png` 2063x520.
Dama arka planı kaldırılmış, saydam piksellere kenar rengi yayılmış ve 4 px payla kırpılmıştır.
Genel dama temizleme aracı: `Tools/ArtBake/unmatte_checker.py <src> <out> --opaque-only`.
- Köşe yarıçapı ~105 px → **9-slice kenar 120 px**, 198 birimlik kart için **Image çarpanı 2.626** (520/198).
- Görünen kart boyutu: `Card_CreamOrange_HD` 1056x624'ün altında 62 px boş gölge payı var ve meta'daki
  160 px kenar 198 birimlik yuvaya sığmayıp Unity'de kırpılıyor → görünen kart yuvadan ~40 birim kısa, bant kalın.
- Oyuna koymak = HD dosyayı (yedekleyip) bu PNG ile değiştirmek (GUID sabit kalır, presenter yolu değişmez), meta
  spriteBorder 120, prefab Background Image çarpanı 2.626. `render_game_popup.py <kart> <kenar> <çarpan> <çıktı>`
  gerçek popup yerleşimiyle önizler (uGUI Sliced kırpma kuralı dahil).
- Track/Fill çapaları (0,0)-(1,1) olmalı. Dikeyde sabit çapa, yükseklik 0 / -6 olduğunda çubuğu gizler.

## Tamamlanma ve ödül akışı
Biten görevde tik, hafif renk ve tek seferlik parlama gösterilir. Altta "N COINS TOTAL" ve
"COMPLETE ALL 3 ORDERS" yazar. Ödül hazırken "CLAIM N COINS" düğmesi nabız animasyonu oynatır.
Ödül alınınca "+N COINS ADDED!" ve "TODAY'S ORDERS COMPLETE - NEW ORDERS IN hh:mm:ss" gösterilir.
Panel yaklaşık 1.6 s sonra kapanır. Yan buton kilitli ve soluktur; sayaç okunur kalır.
- `DailyOrderCardView.cs`: `IsComplete`, `ApplyCompletion` (tik rozeti = CounterBadge + iki beyaz çizgi, kart tint
  (0.82,1,0.86)), `Celebrate` (rozet OutBack pop + beyaz flash + BsSfx.StepComplete) yalnız reveal tween hedefe
  vardığında; rozet/flash prefab'da yoksa runtime'da kurulur (opsiyonel SerializeField alanları var).
- `BartenderDailyOrdersPresenter.cs`: `UpdateRewardBlock`, `SetActionPulse`, `CelebrateClaim` (+DOVirtual gecikmeli
  `CloseAnimated`), `PlayOpenTransition`/`CloseAnimated` (CanvasGroup alfa + panel ölçek), `UpdateSideLock`/`TintSide`.
  PLAY yolu anında kapanır (oyun başlar). Tüm tween'ler unscaled + SetLink; DOTweenModuleUI yok → DOFade/DOColor
  kullanılmadı, DOTween.To ile yazıldı.
