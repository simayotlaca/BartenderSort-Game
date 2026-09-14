using UnityEngine;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Liquid Sort/Audio/Loading Music Bridge")]
    public sealed class BartenderLoadingMusicBridge : MonoBehaviour
    {
        private const float MinimumFadeSeconds = 0.08f;
        private const float MaximumFadeSeconds = 0.35f;

        [Tooltip("Authored source for the loading cue. It is scene-local by design.")]
        [SerializeField] private AudioSource cueSource;
        [Tooltip("Scene loads: the longer transition cue.")]
        [SerializeField] private AudioClip longClip;
        [Tooltip("In-scene swaps: the short cue.")]
        [SerializeField] private AudioClip shortClip;

        private int playingPresentationId;
        private static int resolvedPresentationId;
        private float fadeFrom;
        private float fadeSeconds;
        private float fadeElapsed;
        private bool fading;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetResolvedPresentation() => resolvedPresentationId = 0;

        private void Awake()
        {
            if (cueSource == null) return;
            cueSource.playOnAwake = false;
            cueSource.loop = false;
            cueSource.Stop();
            cueSource.volume = 0f;
        }

        private void OnEnable()
        {
            BartenderLoadingOverlayPresenter.PresentationResolving += HandleResolving;
        }

        private void OnDisable()
        {
            BartenderLoadingOverlayPresenter.PresentationResolving -= HandleResolving;
            StopCue();
        }

        private void Update()
        {
            if (cueSource == null) return;

            int visible = BartenderLoadingOverlayPresenter.VisiblePresentationId;
            if (visible != 0 && visible != playingPresentationId
                && visible != resolvedPresentationId)
            {
                BeginCue(visible);
            }
            else if (visible == 0 && playingPresentationId != 0 && !fading)
            {
                BeginFade(MinimumFadeSeconds);
            }
            else if (playingPresentationId != 0 && !fading && !MusicAllowed)
            {
                BeginFade(MinimumFadeSeconds);
            }

            AdvanceFade();
        }

        private void BeginCue(int presentationId)
        {
            resolvedPresentationId = 0;
            AudioClip clip = BartenderLoadingOverlayPresenter.AnyVisibleIsLongForm
                ? longClip
                : shortClip;
            if (clip == null) clip = longClip != null ? longClip : shortClip;
            if (clip == null || !MusicAllowed)
            {
                // Still claim the presentation so a missing clip cannot retry every frame.
                playingPresentationId = presentationId;
                return;
            }

            fading = false;
            playingPresentationId = presentationId;
            cueSource.Stop();
            cueSource.clip = clip;
            cueSource.loop = false;
            cueSource.pitch = 1f;
            cueSource.volume = CueVolume;
            cueSource.Play();
        }

        private void HandleResolving(
            BartenderLoadingOverlayPresenter.LoadingExitKind kind, float visibleSecondsLeft)
        {
            if (playingPresentationId == 0) return;
            // A completed loading frame can remain visible while the destination prepares.
            // Keep it claimed across scene-local bridges so neither this Update nor the
            // destination's bridge can replay the cue after the fade.
            resolvedPresentationId = playingPresentationId;
            // A handoff destroys this source with its scene, so the fade must fit the announced window.
            BeginFade(Mathf.Clamp(visibleSecondsLeft, MinimumFadeSeconds, MaximumFadeSeconds));
        }

        private void BeginFade(float seconds)
        {
            if (cueSource == null || !cueSource.isPlaying)
            {
                StopCue();
                return;
            }

            fadeFrom = cueSource.volume;
            fadeSeconds = Mathf.Max(seconds, MinimumFadeSeconds);
            fadeElapsed = 0f;
            fading = true;
        }

        private void AdvanceFade()
        {
            if (!fading) return;
            fadeElapsed += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(fadeElapsed / fadeSeconds);
            cueSource.volume = Mathf.Lerp(fadeFrom, 0f, k * k * (3f - 2f * k));
            if (k < 1f) return;
            StopCue();
        }

        private void StopCue()
        {
            fading = false;
            playingPresentationId = 0;
            if (cueSource == null) return;
            cueSource.Stop();
            cueSource.volume = 0f;
        }

        private static bool MusicAllowed => BsAudio.Instance == null
                                         || (BsAudio.Instance.MusicEnabled
                                             && BsAudio.Instance.BgmVolume > 0f);

        private static float CueVolume => BsAudio.Instance == null
            ? 0.5f * BsAudio.BgmBedTrim
            : BsAudio.Instance.BgmVolume * BsAudio.BgmBedTrim;
    }
}
