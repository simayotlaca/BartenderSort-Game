# levelkit — level analiz ve üretim araçları

`Assets/` DIŞINDADIR: Unity bu klasörü görmez, build'e girmez.

`BsBoard`'un kurallarının bağımsız bir Python kopyası. Amaç Unity'yi açmadan
level verisini ölçmek, doğrulamak ve üretmek. Editördeki `Tools > LiquidSort >
Validate Campaign` ile aynı kuralları uygular; ikisi birbirini çapraz doğrular.

## Dosyalar

| dosya | ne yapar |
|---|---|
| `bssolver.py`  | Kural motoru + çözücü. `parse_level`, `solve`, `first_delivery_reachable` |
| `rebalance.py` | Bardak kompozisyonu düzenleyici. Sıvıya/siparişe dokunmadan boş alanı değiştirir |
| `generate.py`  | Üreteç çekirdeği: geriye-karıştırma, kilit/zincir/gizli yerleştirme |
| `drive.py`     | Üreteç sürücüsü: hedef slack + çeşitlilik + doğrulama döngüsü |
| `naive.py`     | "Düşünmeden geçme oranı" — onboarding eğrisinin ölçüldüğü metrik |

## Kullanım

Proje kökünden çalıştır:

```bash
python3 Tools/levelkit/bssolver.py            # 30+ levelin tamamını çöz ve raporla
python3 Tools/levelkit/bssolver.py 29 30      # yalnız belirtilen levelleri
```

## Neden bu araçlar var

`ValidatedSolvable` bayrağı runtime'da hiç okunmuyordu ve Level_029 çözülemez
halde yayına çıkmıştı. Sebep üreteç hatası değildi: `BsBoard.MatchedSlot` bir
noktada "kilitli katmanlı bardak teslim edilemez" diye sıkılaştırıldı (doğru bir
karar) ama 30 level yeni kurala göre yeniden doğrulanmadı.

DERS: kural değişirse level verisi yeniden doğrulanmalı. Bu araçlar onun içindir.

## Ölçülen invariantlar

- korunum: renk bazında board toplamı == sipariş talebi
- bardak tipi arzı >= talebi (teslimde bardak sahneden çıkar)
- her kilit/zincir eşiği < toplam sipariş sayısı (yoksa asla açılmaz)
- başlangıçta hazır sipariş yok
- çözülebilirlik (kanonik durum anahtarlı arama)
