# Daily Orders — temiz yerel UI asset üretimi

Çalışma zamanı sprite’ları SDF şekilleriyle ve gerçek alfa kanalıyla üretilir.
Altın paleti `ShopCard_Blue.png` ve `ShopCard_Purple.png` çerçevelerinden alınır;
disk, kâğıt, pano, tik ve rozet vektör benzeri yerel şekillerdir. Kaynak bitmap kesilmez.

- `DailyOrders_SideButton_Empty_Approved.png`: 1222×1287. Mavi disk, üç boş kutulu pano ve plaka temas gölgesi.
- `DailyOrders_LiveCheck_Approved.png`: 256×256. Görev durumuna göre ayrı çizilen yeşil tik.
- `DailyOrders_TimerPlate.png`: 829×289. Bağımsız, gölgesiz mor plaka ve altın çerçeve.
- `DailyOrders_CounterBadge.png`: 384×384. Yeşil rozet; sayı presenter tarafından eklenir.
- `DailyOrders_SideButton.png`: 1222×1287. Eski, ilk satırda sabit tik içeren yedek pano.

Plaka yüzü ana sprite’a gömülmez. `BartenderDailyOrdersPresenter` plakayı ayrıca
`DynamicTimer` çocuğu olarak çizdiği için bu, çift kenar ve çift yüz karışmasını önler.
Onaylı panoda üç kutu boştur. Tikler, kalan görev sayısı ve sayaç çalışma zamanında
ayrı UI katmanlarıdır. Eski pano yalnız onaylı iki resource sprite birlikte yoksa
yedek olarak kullanılır.

## Yeniden üretim

Proje kökünden, Python 3 + NumPy + Pillow ile:

```sh
python3 Tools/ArtBake/DailyOrdersFlatRim/render_approved.py output/DailyOrdersProductUpgrade/Approved/Reproduction
```

Bu komut kullanıcı tarafından onaylanan `art-draft` tasarımını yerel çizim
kaynağından yeniden üretir; `Assets/` veya kaynak geometri dosyasını değiştirmez.
Çıktı klasörüne iki `_Approved.png`, aynı kalan `DailyOrders_TimerPlate_UNCHANGED.png`,
plaka geometrisi `geom.json` ve tik yerleşimleri `live-check-geometry.json` yazılır.
Tikin UI boyutu 34×34, Z dönüşü +7°'dir. Geometri dosyası her kutunun piksel merkezini,
Unity anchor'ını ve 244×257 boyutlu köke göre konumunu içerir.

Eski pano, mevcut plaka ve rozet için önceki komutlar aynı çıktıları üretir:

```sh
python3 Tools/ArtBake/DailyOrdersFlatRim/render_c.py Tools/ArtBake/DailyOrdersFlatRim Assets/LiquidSort/LevelSystem/Resources/Ui/DailyOrders/DailyOrders_SideButton.png Assets/LiquidSort/LevelSystem/Resources/Ui/DailyOrders/DailyOrders_TimerPlate.png
python3 Tools/ArtBake/DailyOrdersFlatRim/render_counter.py Assets/LiquidSort/LevelSystem/Resources/Ui/DailyOrders/DailyOrders_CounterBadge.png
```

`render_c.py` üçüncü çıktı yolunu almazsa plakayı ara klasörde `timer_plate.png`
olarak yazar. Plaka kutusu `[188, 956, 1017, 1245]` ve `geom.json` içindeki
anchor’lar korunur. `render_counter.py` isteğe bağlı ikinci parametre olarak
daire çapını alır (varsayılan 320 px, her yanda 32 px saydam pay).
`render_c.py --empty-clipboard` sabit tiki kaldırır; seçenek verilmezse eski görünüm
korunur. `--geometry-output PATH` kaynak `geom.json` yerine belirtilen dosyaya yazar.
`render_approved.py` bu seçenekleri kullanır; taslak generator'daki kaynak metin
kopyalama/değiştirme işlemine ihtiyaç duymaz.

Onaylanan iki görsel, değişmeyen plaka ve eski varsayılan pano/plaka çıktıları
Python 3.9.6, NumPy 2.0.2 ve Pillow 11.3.0 ile yeniden üretildi: beş karşılaştırmanın
tamamında PNG baytları ve RGBA pikselleri aynı, farklı piksel sayısı 0.
Tik geometrisi de taslakla aynıdır. SHA-256 ve boyut kayıtları
`output/DailyOrdersProductUpgrade/Approved/Reproduction/reproduction-verification.json`
dosyasındadır.

## Kenar ve gölge kalitesi

`draw_clipboard.py` panoyu 2× çözünürlükte çizer. Döndürme ve küçültme, önceden
alfa ile çarpılmış kayan noktalı RGB ve alfa düzlemlerine uygulanır; PNG’ye yalnızca
son adımda düz RGBA olarak çevrilir. Önceden çarpılmış renkleri Pillow’ın RGBA
filtresine vermek ikinci kez alfa çarpımına ve klips çevresinde kirli saçaklara yol
açıyordu. Satır, kâğıt ve klips gölgeleri de küçük ekranda okunurluk için hafifletildi.
Rozet 2× SDF örneklemeden küçültülür. Saydam kenar rengi yalnızca alfa değeri sıfır
olan piksellere uzatılır; görünür piksellerin renkleri veya alfa maskesi değiştirilmez.

Bağımsız plaka doğrudan kendi `pcol` ve `alpha_p` dizilerinden üretilir. Birleşik
ana görselin RGB’siyle plaka alfa maskesini eşlemek, saydam köşelerde mavi disk ve
altın halka parçalarını bırakıyordu. Çıktı şimdi yalnızca plaka renklerini içerir;
plaka sınırındaki tüm kanvas kenarları tamamen saydamdır.

## Tarihsel geometri araçları

`fit_geom.py`, `fit2.py` ve `render.py` önceki bitmap uydurma akışıdır. Güncel
katmanlar ve ayrı tik için yeniden çalıştırılmaları gerekmez; `render_approved.py`,
`render_c.py`, `draw_clipboard.py` ve `render_counter.py` yeterlidir. `geom.json`
plaka yerleşiminin sabit kaynağıdır.
