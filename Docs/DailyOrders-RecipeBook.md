# Günlük görevler ve Tarif Defteri — prefab bağlantıları

Bu değişiklik sahne veya prefab üretmez. Mevcut günlük kart prefabı çalışmaya devam eder.
Ana menü, iki presenter bileşenini `Awake` içinde bağlar. Hazırlanan özel görünümler
menü hiyerarşisine eklendiğinde bulunur; presenter'ın `Authored View` alanına açıkça
bağlamak da mümkündür. Aynı menüde her görünümden yalnızca bir tane kullanın.

## Günlük görev paneli

Menü altındaki bir nesneye `BartenderDailyOrdersView` ekleyin. Alanları bağlayın:

| Alan | Bağlanacak nesne |
| --- | --- |
| Side Button | Ana menüdeki günlük görev düğmesi |
| Popup Root | Başlangıçta kapalı, panel ve karartmayı içeren nesne |
| Close Button | Panelin kapatma düğmesi |
| Backdrop Button | İsteğe bağlı dış karartma düğmesi; panelin kardeşi olmalı |
| Action Button / Action Text | OYNA veya 500 jetonu al düğmesi ve TMP yazısı |
| Countdown Text | Paneldeki günlük geri sayım |
| Side Timer Text | Yan ikonun mor sayaç alanındaki TMP yazısı |
| Completed Text | Paneldeki `2 / 3 COMPLETE` yazısı |
| Completion Bonus Text | İsteğe bağlı bonus/toplam TMP yazısı; miktarlar koddan güncellenir |
| Feedback Text | Ödül alındı veya kayıt hatası yazısı |
| Badge | `BartenderDailyOrdersBadgeView` bileşeni |
| Cards | Sırayla: sipariş teslimi, bölüm kazanma, servis edilen ölçü |

Her kartta `DailyOrderCardView` bulunmalı; Background, Icon, Title Text, Track, Fill,
Progress Text ve Reward Text bağlanmalı. Panel bağlantısı mevcut ikonları ve yerleşimi
korur. Butonların OnClick listesine ayrıca kod bağlamayın; presenter dinleyicileri ekler.
Popup Root, ana menü kökünden ayrı olmalı; panel açılması Home Page'i kapatmamalı.

Panelin geri sayımı başlığın altında mor-altın, yuvarlak bir kutudadır. Mevcut
`DailyOrders_TimerPlate` sprite'ı 9-slice olarak kullanılır. Küçük saat ikonu ve
`Resets in` başlığı birlikte ortalanır; `HH:MM:SS` ayrı alt satırda kutuya ortalıdır.
Rakamlar sabit genişlikte hücrelerle
hizalanır. Lilita One SDF fontu kartlarla ortaktır; yalnız bu panelin metinlerinde
`DailyOrders_PopupText_SDF` materyali ince kontur ve yumuşak alt gölge sağlar.
Saat kutusu ve yazılar tıklama almaz. Görev sayacı kutunun altında ayrı durur.

### Yeşil rozet

`BartenderDailyOrdersBadgeView` için:

- **Badge Root:** Yeşil-altın daire ve içindeki yazı/tik nesnelerinin kökü.
- **Remaining Text:** Dairenin ortasında ayrı TMP yazısı. Rakam görsele gömülmez.
- **Ready Check:** Başlangıçta kapalı ayrı tik nesnesi.
- **Ready Glow:** İsteğe bağlı parlama katmanının CanvasGroup'u. İkonun ana CanvasGroup'u kullanılmaz.

Gönderilen yeşil daire değiştirilmeden `Resources/Ui/DailyOrders/DailyOrders_CounterBadge.png`
olarak eklendi. Varsayılan menü bu sprite'ı kullanır.

Rozet kalan görevleri gösterir: **3 → 2 → 1 → tik**. Bir görevin `3/5` olması rozeti
azaltmaz; ancak `5/5` olduğunda bir görev düşer. Üçü bitince tik görünür ve ikon hafif
parlar. Ödül alınınca rozet gizlenir; günlük yenilenmede yeniden 3 olur. Mor sayaç,
ödül alındıktan sonra da bir sonraki yenilenmeye kalan süreyi gösterir.

### İlk tanıtım

Yan simge **Level 3 açıldığında**, yani ilk iki normal bölüm tamamlandıktan sonra
görünür. İlk görünüş menünün level/jeton sunumunu bekler. İlerleme Level 1'den
itibaren birikmeye devam eder. Hippo'nun Lilita One yazılı kısa İngilizce balonu
`DailyOrdersIntroduction.prefab` içindedir. Simge ile balon arasında üç küçük
krem-altın baloncuk bulunur; mevcut açılış animasyonunu ve kapanışı paylaşırlar. Arka plan yarı saydam lacivert ile
karartılır; yalnız günlük görev simgesi tıklanabilir kalır. "Daily Orders unlocked!
Tap the board to open." mesajı gösterilir; alınabilir ödül varsa "Your daily
reward is ready! Tap the board to collect." kullanılır. Süre, dış alana dokunma
ve Escape tanıtımı kapatmaz. Simgeye basılıp panel açılınca mevcut kart ilerleme ve
tamamlanma animasyonları çalışır; tanıtım o anda tamamlanmış olarak kaydedilir.
Kayıt mevcut `BartenderTutorialProgress` içinde `daily_orders`, sürüm 2 anahtarıdır.
Sürüm 1 yalnız balonun gösterildiğini kaydettiğinden yeni zorunlu adımı tamamlamaz.
Kesilen tanıtım menüye dönüldüğünde yeniden sunulur. Ödülü zaten alınmış, kapalı bir
günlük görev düğmesi zorunlu tanıtımı başlatmaz.

### Kart açılış animasyonu

Sayaç metni ve doluluk çubuğu son gösterilen ilerlemeden güncel kayda doğru ilerler.
Kartlar 0, 0.10 ve 0.20 saniye gecikmeyle başlar. Her kartın `Progress Duration`
alanı varsayılan 0.75 saniyedir; animasyon duraklatılmış oyun saatinden etkilenmez.
Saniyelik sayaç yenilemesi animasyonu yeniden başlatmaz. Erken kapatılan panelde
görülen sayılar hatırlanır. İlerleme değişmediyse tekrar açılışta sayaçlar sabit kalır.

Bu görsel geçmiş, üretim/Editor profiline göre ayrı PlayerPrefs anahtarında tutulur.
Gerçek görev ilerlemesini veya jetonları değiştiremez; bulunamazsa animasyon sıfırdan
başlar. Gün değişince önceki günün görsel ilerlemesi kullanılmaz.

## Tarif Defteri

Kitap düğmesi artık `COMING SOON` yerine koleksiyonu açar. Özel prefab bağlanana kadar
kod, aynı Page Container altında alt menüyü koruyan altı kartlık sayfalı görünüm açar.

Özel tasarım için `BartenderRecipeBookView` ekleyin:

- **Page Root:** Home Page ve Shop Page ile aynı Page Container altında ayrı sayfa.
- **Collection Count / Page Number:** Açılan tarif sayısı ve sayfa numarası TMP yazıları.
- **Empty Text:** İlk teslimatı teşvik eden isteğe bağlı yazı.
- **Previous / Next Button:** Sayfa düğmeleri; Close Button isteğe bağlıdır.
- **Cards:** Tasarımdaki kart yuvaları. Yuvaların sayısı bir sayfadaki tarif sayısını belirler.

Her kartta `RecipeBookCardView` üzerindeki Title Text, Recipe Text ve Status Text
bağlanmalı. Lock Icon ve Color Swatches isteğe bağlıdır. Katmanlı tarifte renkler
dipten yukarı sıralanır. Kilitli tariflerin içerikleri `???` olarak gösterilir.

**Replay Button** isteğe bağlıdır; bağlanmazsa kartın tamamı düğme olur. Tarifin ilk
bulunduğu bölüm daha önce tamamlandıysa karta dokunarak tekrar oynanabilir. Böylece
eski kayıtlarda koleksiyona işlenmemiş tarifler de gerçek teslimatla açılır. Henüz
tamamlanmamış bölümlerin kartları tekrar oynama açmaz. OnClick bağlantısını kod yapar.
Devam eden bölüm veya sonuç kaydı varsa tekrar oynama bu kaydı değiştirmez; önce
mevcut turun devam ettirilmesini ister.

## Oyun ve kayıt davranışı

Tamamlanan görev kartlarında yeşil tik yoktur; mor `Completed` etiketi ve kısa
tamamlanma parlaması kullanılır. Ana menüdeki günlük görev yan rozetinin tik davranışı
korunur.

`CLAIM` başarılı olduğunda panel otomatik kapanmaz. Kayıt ve 500 jeton aynı işlemde
saklanır; HUD eski bakiyeyi göstermeye devam eder. Kullanıcı paneli kapatınca mevcut
ana menü altın uçuşu düğmenin konumundan sayaca gider; görünen bakiye altınlar geldikçe
artar. Bu günlük ödül sunumu level kutlamasını tekrar çalıştırmaz. Kesilen uçuşun
makbuzu sonraki menü girişinde tekrar gösterilebilir; para yeniden verilmez.

- Üç hedef: **5 sipariş**, **2 kazanılan normal bölüm**, **10 servis edilen ölçü**.
- Bardaklar arası dökme ilerletmez. Başarılı teslimat, bir sipariş ve o bardağın
  ölçüsü kadar servis ilerlemesi verir; aynı işlem Tarif Defteri'ndeki tarifi açar.
- Teslimat ve koleksiyon değişikliği aktif oyun kaydıyla aynı atomik işlemde saklanır.
  Kayıt başarısızsa değişiklik yayınlanmaz. Son sipariş de bölüm sonuç kaydına bağlıdır.
- Aynı kayıt tekrarının veya aynı denemedeki siparişi geri alıp tekrar teslim etmenin
  ek ilerlemesi yoktur. Başka bir normal bölüm denemesindeki gerçek teslimat sayılır.
- First Shift ve bağımsız deneme bölümleri bu günlük/koleksiyon kaydını ilerletmez.
- Üç görev bitince **100 + 100 + 100 + 200 = 500 jeton**, tek seferde elle alınır.
  Görev başına ayrı para ödemesi yapılmaz. Ödül alınınca düğme `COMPLETED` olur;
  panelin çarpısı/dış alanı ile kapanıştan sonra ana menü kullanılabilir.
- Miktarlar `BartenderDailyOrdersTuning` üzerinden yönetilir; görev kartları, bonus,
  ödül düğmesi, başarı mesajı ve gerçek ödeme aynı değerleri kullanır. Önceden alınmış
  günlük ödül miktar artışıyla yeniden açılmaz; tamamlanmış fakat alınmamış ödül 500 verir.
- Ödül dokunuşu ekranda gösterilen günle eşleşmelidir. Dokunma sırasında gün değişirse
  kart yenilenir ve işlem durur; eski ödül dokunuşu OYNA işlemine dönüşmez.
- İlk günlük sınır **00:00 UTC**'ye hizalanır. Uygulama açıkken günlük süre monoton
  geçen zamandan hesaplanır. Telefonun yerel saat dilimi gün sınırını değiştirmez.
  Arka plandan dönüşte ve yeniden açılışta kaydedilmiş oyun zamanı/cihaz zamanı çifti
  kullanılır. Saat geri alınırsa sayaç günlerce donmaz; mevcut görevler ve alınmış ödül
  korunarak kalan süre işlemeye devam eder. Elle saat değiştirildikten sonra yenilenme
  yeni cihaz saatinin gece yarısıyla aynı ana denk gelmek zorunda değildir.
- Kampanya tamamlanınca ana menüde **REPLAY** kullanılabilir; eski bölümler kazanıldıkça
  sıradaki tekrar bölümüne geçilir. Tekrarlar günlük teslimat/ölçü/kazanma görevlerini ve
  koleksiyonu ilerletir. Normal bölümün can ve kazanma ödülü kuralları geçerlidir.
  Ana kampanyanın açılmış bölüm sayısı gerilemez. Kampanya sürerken kitaptan seçilen
  eski bölüm bitirildiğinde ana menü sıradaki yeni bölüme döner.
- Koleksiyon günlük sıfırlamadan etkilenmez. SET tarifleri renk çokluklarıyla,
  LAYER tarifleri kesin sıralarıyla ve bardak tipleriyle ayırt edilir.
- Katalog oyunla aynı kampanya kaynağını kullanır. İncelenen 40 bölümde 236 sipariş,
  157 farklı tarif var. Eski katalogdan açılmış bir tarif kayıt içinde korunur.

Kayıt sürümü 6 oldu; sürüm 4 ve 5 otomatik okunur. Sürüm 4'teki dökme sayacı, teslimat ölçüsü
olarak taşınmaz: yalnız bu üçüncü sayaç sıfırdan başlar. Teslimat/kazanma ilerlemesi,
jetonlar, bölüm kaydı ve alınmış ödüller korunur. Eski kayıtlarda tarif geçmişi
bulunmadığından geçmişteki kokteyller tahminle açılmaz; yeni teslimatlar kaydedilir.
Sürüm 5'in görevleri ve koleksiyonu aynen taşınır. Eski kayıtta cihaz saati nedeniyle
gelecekte takılmış gün varsa tarihi düzeltilir; sayaçları ve alınmış ödülü silinmez.
Saat takibi yereldir: uygulama kapalıyken cihaz tarihinin ileri alınmasını gerçek
çevrimdışı süreden ayıran bir sunucu doğrulaması bulunmaz.

## Kontrol edilecek akışlar

1. Sadece dökmek görevi ilerletmemeli; 2 ölçülü teslimat `+1 sipariş / +2 ölçü` vermeli.
2. Panel açılışında yeni ilerleme animasyonla görünmeli; erken kapatıp yeniden açınca
   görülen sayıdan devam etmeli; duraklatmada da animasyon işlemeli.
3. Aynı siparişin undo/tekrar teslimi ve kaydın tekrar işlenmesi ikinci kredi vermemeli.
4. Üçüncü görev tamamlanınca rozet tik olmalı; ödül tek kez 500 vermeli. Çift dokunma
   ikinci ödül vermemeli veya yanlışlıkla bölüme geçmemeli.
5. Yeniden açılış, UTC gün değişimi ve eski kayıt göçünde görev/ödül/koleksiyon korunmalı.
6. Kitap, Home ve Shop geçişlerinde tek sayfa görünmeli; kilitli içerikler gizli kalmalı.
7. Son kampanya bölümü tamamlandıktan sonra REPLAY ile iki normal bölüm kazanılabilmeli;
   günlük kazanma hedefi tamamlanmalı ve kampanya ilerlemesi azalmamalı.
8. Kitaptan eski bölüm açıp eksik tarifi teslim etmek koleksiyona eklemeli. Yeniden
   açılış aynı aktif turu sürdürmeli; mevcut tur varken başka tarif seçimi reddedilmeli.
9. Ödül hazırken günlük sınırda dokunmak yeni bölüm açmamalı veya yeni günün ödülünü
   vermemeli. Yeni kart gösterilmeli; yeniden yapılan bilinçli dokunuş kullanılmalı.
10. Cihaz saatini ileri/geri alıp uygulamaya dönün; sayaç 24 saatten uzun süreye
    takılmamalı. Yeniden açınca da sayaç akmalı ve alınmış ödül tekrar alınamamalı.
