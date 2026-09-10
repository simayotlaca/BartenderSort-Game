# Bölüm araçları

Bu isteğe bağlı araçlar Python standart kütüphanesini kullanır. Unity build sürecinin parçası değildir.

- bssolver.py: bölüm verisini okuma, kural motoru ve çözüm araması.
- rebalance.py: bardak kompozisyonunu düzenleme.
- generate.py: bölüm üretimi ve mekanik yerleştirme.
- drive.py: üretim ve doğrulama akışını çalıştırma.
- naive.py: bölümün basit stratejilerle çözülme oranını ölçme.

Proje kökünde python3 Tools/levelkit/bssolver.py komutuyla kampanyayı; python3 Tools/levelkit/bssolver.py 29 30 komutuyla seçilen bölümleri çözümleyebilirsiniz.

Oyunun kendisi bu araçlara bağlı değildir; çalışma anındaki C# çözücü Assets/LiquidSort/LevelSystem/Core/BsSolver.cs dosyasıdır.
