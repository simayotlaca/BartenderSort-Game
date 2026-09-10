# Bartender Sort

Bartender Sort, renkli sıvıları bardaklar arasında aktararak siparişleri tamamladığınız, Unity ile geliştirilmiş bir bulmaca oyunudur.

## Projeyi açma

1. Unity Hub üzerinden Unity 6000.0.30f1 sürümünü kurun.
2. Bu klasörü proje olarak ekleyin; Unity paketleri ilk açılışta çözümlenir.
3. Assets/LiquidSort/SortingShelfShowcase.unity sahnesini açıp Play düğmesine basın.
4. Ana menüdeki LEVEL düğmesi oyun sahnesine geçer.

Oyun sahnesi Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab_WorkingCopy.unity dosyasıdır. Dosya adlarındaki Showcase, Lab ve WorkingCopy ifadeleri geçmişten kalmıştır; bu iki dosya aktif sahnelerdir.

## Oynanış

Kaynak bardağa, ardından hedef bardağa dokunarak sıvı aktarın. Siparişin istediği renkleri ve katman sırasını tamamlayıp teslim edin. Kampanyada 40 bölüm bulunur; ilerleme, tarif koleksiyonu ve günlük görevler yerel olarak saklanır.

## Kaynak kod haritası

| Klasör / bileşen | Sorumluluk |
| --- | --- |
| Assets/LiquidSort/LevelSystem/Core | Tahta kuralları, sipariş eşleşmesi, çözücü ve akış durumları |
| BartenderLevelController | Bölüm durumu ve oynanış komutları |
| BartenderPourInteraction | Oyuncu girdisi ve dökme işlemleri |
| BartenderShelfLevelView | Raf ve bardakların sahnedeki sunumu |
| Assets/LiquidSort/Runtime | Bardak geometrisi, katman çizimi ve dökme animasyonu |
| Assets/LiquidSort/Shaders | Cam, sıvı, ışık ve arayüz efektleri |
| Assets/LiquidSort/LevelSystem/Resources/Levels | Kampanya bölüm verileri |
| Assets/LiquidSort/Prefabs | Bardak, raf, arayüz ve ses prefabları |
| Assets/Plugins/iOS | iOS titreşim desteği |
| Tools/levelkit | İsteğe bağlı Python bölüm üretme ve çözümleme araçları |

## Geliştirme notları

- Assets altındaki .meta dosyaları Unity bağlantılarının parçasıdır; varlıklarla birlikte tutulur.
- Resources klasörlerindeki dosyalar koddan da yüklendiği için yalnız sahnedeki doğrudan referanslara göre silinmemelidir.
- Yeni bardak profilleri VesselProfileBaker ve GlassInteriorFitter ile hazırlanabilir. Ayrıntılar Assets/LiquidSort/README.md içindedir.
- Paket sürümleri Packages/manifest.json ve Packages/packages-lock.json ile sabitlenmiştir.
- Menü ve oyun sahneleri ProjectSettings/EditorBuildSettings.asset içinde kayıtlıdır.

Bu depo oyunun kaynak projesini ve son oyun varlıklarını içerir. Yerel önbellekler, geçici kontrol araçları, tanıtım videosu üretimleri, promptlar ve ara görsel/ses çalışma dosyaları kapsam dışındadır.

Üçüncü taraf bileşen ve ses kaynağı bildirimleri THIRD_PARTY_NOTICES.md dosyasında yer alır.
