# Glass Pour Math Demo (Unity 6)

Bu proje, farklı bardak geometrilerinde hacmi koruyan sıvı katmanlarını ve bardaktan
bardağa dökme animasyonunu içerir.

## Üretim bardak referansı

- `Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab_WorkingCopy.unity`

Bu aktif oyun sahnesindeki beş nihai bardak ön yüzü ve Royal profilleri, görünür
geometri, sıvı kapasitesi, maske ve dökme pozları için üretim referansıdır.

## Aktif iki-sahne akışı

- Ana menü: `Assets/LiquidSort/SortingShelfShowcase.unity`
- Oyun: `Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab_WorkingCopy.unity`

Ana menü yalnız menü sunumunu taşır; LEVEL düğmesi oyun sahnesini açar.

Raf oyununda bir bardağa, ardından hedef bardağa dokunmak dökme işlemini başlatır.
Sipariş arayüzü ayrı bir tüketicidir; teslim butonu/gesture'ı yalnız
`BartenderPourInteraction.RequestDelivery` çağırmalıdır. Controller'ın
`TryDeliver` metodu domain/test katmanı içindir; onu doğrudan çağırmak etkileşim
doğrulamalarını ve görünüm eşlemesini atlar. Inspector'daki
`Resume Saved Progress` açıksa tamamlanmış bir kampanya kaydı Play modunda boş
`CampaignComplete` görünümü üretebilir; sabit bir test seviyesi için bu seçeneği kapatıp
`Starting Level Number` değerini ayarlayın.

## Korunan çekirdek

- `VesselProfile`: bardak iç poligonu, görünür taban, kapasite ve dökme pozları
- `LiquidBottle`: sıvı bantlarının çizimi ve profil verisi
- `BottleShell`: bardak çerçevesi, gölge ve görsel tema
- `PourAnimator` + `PourStream`: taşıma, eğilme, akış ve geri dönüş animasyonu
- `BartenderLevelController` + `BartenderShelfLevelView` + `BartenderPourInteraction`:
  oynanabilir rafın domain, sunum ve input zinciri
- `VesselProfileBaker`: yeni veya güncellenen bardak profilini üretme

Aktif profillerin `front`, `traceSource`, materyal veya bake tablolarını eski deneme
asset'leriyle değiştirmeyin. Yeni bir bardak eklerken ayrı bir `VesselProfile` oluşturup
profili baker ile yeniden üretin.

## Unity sürümü

Proje `6000.0.30f1` ile oluşturulmuştur. `Library`, `Temp`, `Logs` ve `UserSettings`
yeniden üretilebilir yerel klasörlerdir ve Git'e dahil edilmez.

