// Hafif dokunma haptigi. Unity'nin tek yerlesik API'si Handheld.Vibrate() ve o,
// iOS'ta yarim saniyelik tam bir buzz uretir (AudioServicesPlaySystemSound). Bir UI
// reddi icin bu cok agir; oyunlarda hissedilen kisa "tik" UIImpactFeedbackGenerator.
//
// Generator ORNEGI SAKLANIR ve her atistan sonra tekrar prepare edilir: Taptic Engine
// hazir degilken ilk impact gozle gorulur biçimde geç gelir, prepare o gecikmeyi alir.
#import <UIKit/UIKit.h>

static UIImpactFeedbackGenerator *bartenderLightGenerator = nil;

extern "C" {

void BartenderHapticsPrepare(void)
{
    if (@available(iOS 10.0, *)) {
        if (bartenderLightGenerator == nil) {
            bartenderLightGenerator = [[UIImpactFeedbackGenerator alloc]
                initWithStyle:UIImpactFeedbackStyleLight];
        }
        [bartenderLightGenerator prepare];
    }
}

void BartenderHapticsLight(void)
{
    if (@available(iOS 10.0, *)) {
        BartenderHapticsPrepare();
        [bartenderLightGenerator impactOccurred];
        // Arka arkaya dokunuslarda ikinci atisin da anlik olmasi icin yeniden hazirla.
        [bartenderLightGenerator prepare];
    }
}

}
