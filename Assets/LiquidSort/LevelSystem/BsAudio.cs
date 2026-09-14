using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort.Levels
{
    public enum BsBgm
    {
        Gameplay,
        Menu,
        GameplayRush,
    }

    public enum BsSfx
    {
        PourLoop,
        PourStart,
        PourEnd,
        GlassPickup,
        GlassSet,
        Check,
        DeliverSlide,
        Invalid,
        Win,
        Fail,
        ButtonClick,
        ButtonBack,
        TabSwitch,
        SliderTick,
        ToggleOn,
        ToggleOff,
        LevelNodePop,
        MapAdvance,
        PourFlow1,
        PourFlow2,
        PourFlow3,
        PourFlow4,
        PourFlow5,
        TimerTick,
        TimerTock,
        // A new order lands in the tray.
        OrderBell,
        TutorialPop,
        HippoBlip,
        StepComplete,
        LockOpen,
        CoinSingle,
        CoinTick,
        CoinCascade,
        CoinFlight,
        CoinCounterRise,
        PlayButton,
        WinCoinReward,
        GlassArrive,
        TimeBoost,
        RetiredWinAccent,
        PopupOpen,
        PopupClose,
        LifeLost,
        LifeRegained,
        LifeRefill,
        BoosterUndo,
        BoosterShuffle,
        HiddenReveal,
        StartupLogo,
        WinCardReveal,
        DailyRewardHint,
        // A timed order runs out. Append new values only.
        OrderExpired,
        CoinSpend,
        LevelIntro,
        // A purchase the player cannot afford. Append new values only.
        NotEnoughCoins,
        // A daily order task reaches its goal: the in-game toast and the popup card. Append new values only.
        DailyTaskComplete,
    }

    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class BsAudio : MonoBehaviour
    {
        public const string MusicPreferenceKey = "LiquidSort.Bartender.Settings.Music";
        public const string SoundPreferenceKey = "LiquidSort.Bartender.Settings.Sound";
        public const string MusicVolumePreferenceKey =
            "LiquidSort.Bartender.Settings.MusicVolume";
        public const string SoundVolumePreferenceKey =
            "LiquidSort.Bartender.Settings.SoundVolume";

        private const string ResourceDirectory = "Audio/";
        private const string BgmName = "BGM_Bar_Loop";
        private const string MenuBgmName = "BGM_Menu_Loop";
        private const string RushBgmName = "BGM_Bar_Rush_Loop";
        private const string FailWaitMusicName = "BGM_Fail_Wait_Loop";

        private static readonly Dictionary<BsSfx, string> ClipNames =
            new Dictionary<BsSfx, string>
            {
                { BsSfx.GlassPickup, "SFX_GlassPickup" },
                { BsSfx.GlassSet, "SFX_GlassSet" },
                { BsSfx.Check, "SFX_Check" },
                { BsSfx.DeliverSlide, "SFX_DeliverSlide" },
                { BsSfx.Invalid, "SFX_Invalid" },
                { BsSfx.Win, "SFX_WinScreen" },
                { BsSfx.Fail, "SFX_Fail" },
                { BsSfx.ButtonClick, "SFX_ButtonClick" },
                { BsSfx.ButtonBack, "SFX_ButtonBack" },
                { BsSfx.TabSwitch, "SFX_TabSwitch" },
                { BsSfx.SliderTick, "SFX_SliderTick" },
                { BsSfx.ToggleOn, "SFX_ToggleOn" },
                { BsSfx.ToggleOff, "SFX_ToggleOff" },
                { BsSfx.PourFlow1, "SFX_Pour_Flow_1" },
                { BsSfx.PourFlow2, "SFX_Pour_Flow_2" },
                { BsSfx.PourFlow3, "SFX_Pour_Flow_3" },
                { BsSfx.PourFlow4, "SFX_Pour_Flow_4" },
                { BsSfx.PourFlow5, "SFX_Pour_Flow_5" },
                { BsSfx.TimerTick, "SFX_TimerTick" },
                { BsSfx.TimerTock, "SFX_TimerTock" },
                { BsSfx.OrderBell, "SFX_OrderBell" },
                { BsSfx.TutorialPop, "SFX_TutorialPop" },
                { BsSfx.HippoBlip, "SFX_HippoBlip" },
                { BsSfx.StepComplete, "SFX_StepComplete" },
                { BsSfx.LockOpen, "SFX_LockOpen" },
                { BsSfx.CoinTick, "SFX_CoinTick" },
                { BsSfx.CoinCounterRise, "SFX_CoinCounterRise" },
                { BsSfx.PlayButton, "SFX_PlayButton" },
                { BsSfx.GlassArrive, "SFX_GlassArrive" },
                { BsSfx.TimeBoost, "SFX_TimeBoost" },
                { BsSfx.PopupOpen, "SFX_PopupOpen" },
                { BsSfx.PopupClose, "SFX_PopupClose" },
                { BsSfx.WinCoinReward, "SFX_WinCoinReward" },
                { BsSfx.LifeLost, "SFX_LifeLost" },
                { BsSfx.LifeRegained, "SFX_LifeRegained" },
                { BsSfx.LifeRefill, "SFX_LifeRefill" },
                { BsSfx.BoosterUndo, "SFX_BoosterUndo" },
                { BsSfx.BoosterShuffle, "SFX_BoosterShuffle" },
                { BsSfx.HiddenReveal, "SFX_HiddenReveal" },
                { BsSfx.StartupLogo, "SFX_StartupLogo" },
                { BsSfx.WinCardReveal, "SFX_WinCardReveal" },
                { BsSfx.DailyRewardHint, "SFX_DailyRewardHint" },
                { BsSfx.OrderExpired, "SFX_OrderExpired" },
                { BsSfx.CoinSpend, "SFX_CoinSpend" },
                { BsSfx.LevelIntro, "SFX_LevelIntro" },
                { BsSfx.NotEnoughCoins, "SFX_NotEnoughCoins" },
                { BsSfx.DailyTaskComplete, "SFX_DailyTaskComplete" },
            };

        private static readonly BsSfx[] PourFlowWeights =
        {
            BsSfx.PourFlow1, BsSfx.PourFlow2, BsSfx.PourFlow3,
            BsSfx.PourFlow4, BsSfx.PourFlow5,
        };

        private static readonly BsSfx[] FirstClips =
        {
            BsSfx.StartupLogo, BsSfx.ButtonClick, BsSfx.PlayButton, BsSfx.LevelIntro,
            BsSfx.GlassPickup, BsSfx.GlassSet, BsSfx.PourFlow1, BsSfx.PourFlow2,
            BsSfx.PourFlow3, BsSfx.PourFlow4, BsSfx.PourFlow5,
        };

        public static BsAudio Instance { get; private set; }

        [Header("Levels")]
        [Range(0f, 1f)] public float SfxVolume = 1f;
        [Range(0f, 1f)] public float BgmVolume = 0.5f;

        public bool SfxEnabled { get; private set; } = true;
        public bool MusicEnabled { get; private set; } = true;

        private readonly Dictionary<BsSfx, AudioClip> clips =
            new Dictionary<BsSfx, AudioClip>();
        private Coroutine clipPreloadRoutine;
        private bool clipPreloadFinished;
        private readonly Dictionary<BsBgm, AudioClip> bgmClips =
            new Dictionary<BsBgm, AudioClip>();
        private readonly Dictionary<BsBgm, ResourceRequest> bgmLoads =
            new Dictionary<BsBgm, ResourceRequest>();
        [Header("Authored Audio Sources")]
        [Tooltip("Looping music source authored under this object.")]
        [SerializeField] private AudioSource bgmSource;
        [Tooltip("Finite pour-flow source authored under this object.")]
        [SerializeField] private AudioSource pourFlowSource;
        [Tooltip("Win/fail result source authored under this object.")]
        [SerializeField] private AudioSource resultSource;
        [Tooltip("Looping fail-card music source authored under this object. It follows the music settings.")]
        [SerializeField] private AudioSource resultMusicSource;
        [Tooltip("Authored one-shot sources. When every source is busy, the next "
                 + "authored source is reused; no AudioSource is created at runtime.")]
        [SerializeField] private List<AudioSource> oneShotPool =
            new List<AudioSource>();
        private int nextOneShotSourceIndex;
        private BsBgm currentBgm = BsBgm.Gameplay;
        private Coroutine preferenceSaveRoutine;
        private float resultVolumeMultiplier = 1f;
        private bool bgmRequested;
        private bool bgmPaused;
        private bool resultHoldsBgm;
        private float pauseMix = 1f;
        private Coroutine pauseTransition;

        // The fail card's waiting loop is 100 BPM, so its beats land on the 50 BPM heart pulse. It enters
        // after the fail cue and the life-loss cue (1.45 s), one beat before bar 1, so the loop's own last
        // beat is the pickup.
        private const float FailWaitMusicEntrySeconds = 1.98f;
        private const float FailWaitMusicPickupSeconds = 0.6f;
        private const float FailWaitMusicFadeInSeconds = 1.2f;
        private const float FailWaitMusicResumeFadeSeconds = 0.6f;
        private const float FailWaitMusicLateEntrySeconds = 0.1f;
        private const float FailWaitMusicStopFadeSeconds = 0.15f;
        private const float FailWaitMusicHandoffFadeSeconds = 0.08f;
        private const float FailWaitMusicSuspendFadeSeconds = 0.2f;
        private AudioClip failWaitMusicClip;
        private ResourceRequest failWaitMusicLoad;
        private bool failWaitMusicLoaded;
        private bool failWaitMusicRequested;
        private bool failWaitMusicPending;
        private bool failWaitMusicEnterOnPickup;
        private float failWaitMusicEnterAt;
        private float failWaitMusicEntryFade;
        private float failWaitMusicCardEntryAt = float.NegativeInfinity;
        private bool failWaitMusicFading;
        private bool failWaitMusicStopping;
        private float failWaitMusicFadeFrom;
        private float failWaitMusicFadeTo;
        private float failWaitMusicFadeSeconds;
        private float failWaitMusicFadeElapsed;
        private float failWaitMusicMix;

        private const float CoinSpendVolume = 0.65f;

        private const float WinResultLayerGain = 0.80f;
        private const float WinCardRevealLayerGain = 0.80f;

        public const float BgmBedTrim = 0.5f;   // -6 dB
        private const float PauseDuckMix = 0.25f;
        private const float PauseDuckSeconds = 0.18f;
        private int pourFlowGeneration = 1;
        private bool pourFlowActive;
        private bool pourFlowSuspended;
        private bool pourFlowPausedByService;
        private bool pourFlowPendingStart;
        private BsSfx requestedPourFlow;
        private float requestedPourFlowVolume = 1f;
        private float requestedPourFlowPan;

        // Keep separate cursors: a gameplay visit must not replace the menu's saved position.
        private static readonly Dictionary<string, int> BgmPositions =
            new Dictionary<string, int>();
        private static readonly HashSet<BackgroundMusicSuspension> BgmSuspensions =
            new HashSet<BackgroundMusicSuspension>();
        // Cache the position while playing because the child source may be destroyed before OnDestroy can
        // read it.
        private int lastBgmSamples = -1;
        private string lastBgmClipName;
        private AudioClip lastBgmClip;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Instance = null;
            BgmPositions.Clear();
            BgmSuspensions.Clear();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            LoadPreferences();
            ValidateAuthoredSources();
            // Start only when a presenter requests a ready menu or board, never via playOnAwake.
            if (bgmSource != null)
            {
                bgmSource.playOnAwake = false;
                bgmSource.Stop();
            }
        }

        private void Update()
        {
            TickFailWaitMusic();
            if (bgmLoads.Count == 0) return;
            // Poll the three possible requests without callbacks or per-frame allocations. A scene
            // change cannot let an old completion start music on the new scene's audio service.
            bool completed = CompleteBgmLoad(BsBgm.Gameplay);
            completed |= CompleteBgmLoad(BsBgm.Menu);
            completed |= CompleteBgmLoad(BsBgm.GameplayRush);
            if (completed) RefreshBgmPlayback();
        }

        private void LateUpdate()
        {
            if (bgmSource == null || !bgmSource.isPlaying) return;
            AudioClip clip = bgmSource.clip;
            if (clip == null) return;
            if (!ReferenceEquals(clip, lastBgmClip))
            {
                lastBgmClip = clip;
                lastBgmClipName = clip.name;
            }

            lastBgmSamples = bgmSource.timeSamples;
        }

        // Coins leave the balance through several routes, and the campaign path settles inside the
        // progress service rather than at a presenter. One balance watcher covers every route, and the
        // singleton guard keeps a duplicate instance from doubling the cue.
        private int lastKnownCoins = int.MinValue;

        private void OnEnable()
        {
            if (Instance != this) return;
            BartenderProgressService.CoinsChanged += HandleCoinsChanged;
            lastKnownCoins = BartenderProgressService.IsAvailable
                ? BartenderProgressService.Coins
                : int.MinValue;
            BeginClipPreload();
        }

        private void OnDisable()
        {
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
            StopClipPreload();
        }

        private void HandleCoinsChanged(int coins)
        {
            int previous = lastKnownCoins;
            lastKnownCoins = coins;
            if (previous == int.MinValue || coins >= previous
                || !BartenderProgressService.IsAvailable) return;
            Play(BsSfx.CoinSpend, CoinSpendVolume);
        }

        private void OnDestroy()
        {
            FlushScheduledPreferenceSave();
            if (Instance == this) CarryBgmPosition();
            if (Instance == this) Instance = null;
        }

        private void CarryBgmPosition()
        {
            if (bgmSource != null && bgmSource.clip != null
                && (bgmSource.isPlaying || bgmPaused))
            {
                lastBgmSamples = bgmSource.timeSamples;
                lastBgmClip = bgmSource.clip;
                lastBgmClipName = lastBgmClip.name;
                BgmPositions[lastBgmClipName] = lastBgmSamples;
                return;
            }

            // Use the latest cached position if the source is gone. An unused duplicate stays at -1 and
            // cannot overwrite it.
            if (lastBgmSamples < 0 || lastBgmClipName == null) return;
            BgmPositions[lastBgmClipName] = lastBgmSamples;
        }

        public static IDisposable SuspendBackgroundMusic()
        {
            var suspension = new BackgroundMusicSuspension();
            BgmSuspensions.Add(suspension);
            Instance?.RefreshBgmPlayback();
            return suspension;
        }

        private sealed class BackgroundMusicSuspension : IDisposable
        {
            public void Dispose()
            {
                if (!BgmSuspensions.Remove(this)) return;
                Instance?.RefreshBgmPlayback();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) FlushScheduledPreferenceSave();
        }

        private void OnApplicationQuit() => FlushScheduledPreferenceSave();

        public void Play(BsSfx sfx, float volume = 1f, float pitch = 1f,
                         float pan = 0f)
        {
            if (!CanPlaySfx || !TryGetClip(sfx, out AudioClip clip)) return;

            AudioSource source = AcquireOneShotSource();
            if (source == null) return;
            source.clip = clip;
            source.volume = Mathf.Clamp01(volume) * SfxVolume;
            source.pitch = pitch;
            source.panStereo = Mathf.Clamp(pan, -1f, 1f);
            source.Play();
        }

        public static void UI(BsSfx sfx, float pitch = 1f) =>
            Instance?.Play(sfx, 1f, pitch);

        public static void NotEnoughCoins() =>
            Instance?.Play(BsSfx.NotEnoughCoins, 0.75f);

        internal float RemainingPlaybackSeconds(BsSfx sfx)
        {
            if (!CanPlaySfx || oneShotPool == null
                || !clips.TryGetValue(sfx, out AudioClip clip)) return 0f;

            float remaining = 0f;
            foreach (AudioSource source in oneShotPool)
            {
                if (source == null || !source.isPlaying || source.clip != clip) continue;
                float seconds = Mathf.Max(0f, clip.length - source.time)
                    / Mathf.Max(0.01f, Mathf.Abs(source.pitch));
                remaining = Mathf.Max(remaining, seconds);
            }
            return remaining;
        }

        public bool TryAcquirePourFlow(int amount, float volume, float pan,
                                       out PourFlowLease lease)
        {
            lease = null;
            if (!TryResolvePourFlow(amount, out BsSfx sfx)) return false;

            pourFlowGeneration = pourFlowGeneration == int.MaxValue
                ? 1
                : pourFlowGeneration + 1;
            StopPourFlowPlayback();
            pourFlowActive = true;
            requestedPourFlow = sfx;
            requestedPourFlowVolume = Mathf.Clamp01(volume);
            requestedPourFlowPan = Mathf.Clamp(pan, -1f, 1f);

            if (pourFlowSuspended)
                pourFlowPendingStart = CanPlaySfx;
            else if (CanPlaySfx)
                StartPourFlow();

            lease = new PourFlowLease(this, pourFlowGeneration);
            return true;
        }

        private bool TryResolvePourFlow(int amount, out BsSfx sfx)
        {
            sfx = default;
            if (amount < 1 || amount > PourFlowWeights.Length) return false;

            int index = amount - 1;
            for (int distance = 0; distance < PourFlowWeights.Length; distance++)
            {
                int lighter = index - distance;
                if (lighter >= 0 && TryGetClip(PourFlowWeights[lighter], out _))
                {
                    sfx = PourFlowWeights[lighter];
                    return true;
                }

                int heavier = index + distance;
                if (heavier < PourFlowWeights.Length
                    && TryGetClip(PourFlowWeights[heavier], out _))
                {
                    sfx = PourFlowWeights[heavier];
                    return true;
                }
            }

            return false;
        }

        public void PausePourFlow()
        {
            if (pourFlowSuspended) return;
            pourFlowSuspended = true;
            pourFlowPausedByService = pourFlowSource != null
                                      && pourFlowSource.isPlaying;
            if (pourFlowPausedByService) pourFlowSource.Pause();
        }

        public void ResumePourFlow()
        {
            if (!pourFlowSuspended) return;
            pourFlowSuspended = false;
            if (!CanPlaySfx)
            {
                pourFlowPendingStart = false;
                pourFlowPausedByService = false;
                return;
            }

            if (pourFlowPendingStart)
            {
                pourFlowPendingStart = false;
                if (pourFlowActive && CanPlaySfx) StartPourFlow();
            }
            else if (pourFlowPausedByService)
            {
                pourFlowPausedByService = false;
                if (pourFlowActive && CanPlaySfx && pourFlowSource != null)
                {
                    pourFlowSource.volume = requestedPourFlowVolume * SfxVolume;
                    pourFlowSource.pitch = 1f;
                    pourFlowSource.panStereo = requestedPourFlowPan;
                    pourFlowSource.UnPause();
                }
            }
        }

        public void InvalidatePourFlow()
        {
            pourFlowSuspended = false;
            pourFlowGeneration = pourFlowGeneration == int.MaxValue
                ? 1
                : pourFlowGeneration + 1;
            pourFlowActive = false;
            StopPourFlowPlayback();
        }

        public void StartBgm()
        {
            bgmRequested = true;
            RefreshBgmPlayback();
        }

        public void StartBgm(BsBgm track)
        {
            currentBgm = track == BsBgm.Menu || track == BsBgm.GameplayRush
                ? track : BsBgm.Gameplay;
            if (currentBgm != BsBgm.Menu) PrepareFailWaitMusic();
            StartBgm();
        }

        private AudioClip SelectBgmClip()
        {
            AudioClip clip = RequestBgmClip(currentBgm, out bool finished);
            // A pending track is not missing: never start the fallback while it is still loading.
            if (clip != null || !finished || currentBgm == BsBgm.Gameplay) return clip;
            return RequestBgmClip(BsBgm.Gameplay, out _);
        }

        public void StopBgm()
        {
            bgmRequested = false;
            PauseBgmPlayback();
        }

        private void PauseBgmPlayback()
        {
            CarryBgmPosition();
            if (bgmSource == null || !bgmSource.isPlaying) return;
            bgmSource.Pause();
            bgmPaused = true;
        }

        private void RefreshBgmPlayback()
        {
            if (BgmSuspensions.Count > 0) StopFailWaitMusic(FailWaitMusicSuspendFadeSeconds);
            if (bgmSource == null) return;
            if (!bgmRequested || !CanPlayMusic || resultHoldsBgm || BgmSuspensions.Count > 0)
            {
                PauseBgmPlayback();
                return;
            }

            AudioClip wanted = SelectBgmClip();
            if (wanted == null)
            {
                PauseBgmPlayback();
                return;
            }

            if (bgmSource.clip != wanted)
            {
                CarryBgmPosition();
                bgmSource.Stop();
                bgmPaused = false;
                bgmSource.clip = wanted;
                bgmSource.timeSamples = BgmPositions.TryGetValue(wanted.name, out int position)
                    ? Mathf.Clamp(position, 0, Mathf.Max(0, wanted.samples - 1))
                    : 0;
            }
            else if (!bgmSource.isPlaying && !bgmPaused)
            {
                if (BgmPositions.TryGetValue(wanted.name, out int position))
                    bgmSource.timeSamples = Mathf.Clamp(position, 0, Mathf.Max(0, wanted.samples - 1));
            }

            bgmSource.loop = true;
            ApplyBgmMix();
            if (bgmSource.isPlaying) return;
            if (bgmPaused) bgmSource.UnPause();
            else bgmSource.Play();
            bgmPaused = false;
        }

        public void PlayResult(BsSfx sfx)
        {
            if (sfx != BsSfx.Win && sfx != BsSfx.Fail)
            {
                Play(sfx);
                return;
            }

            if (sfx == BsSfx.Win) StopFailWaitMusic(FailWaitMusicHandoffFadeSeconds);
            resultHoldsBgm = true;
            RefreshBgmPlayback();
            StopResultCues();
            if (sfx == BsSfx.Fail)
            {
                failWaitMusicCardEntryAt = Time.unscaledTime + FailWaitMusicEntrySeconds;
                BeginFailWaitMusic(FailWaitMusicEntrySeconds, FailWaitMusicFadeInSeconds, true);
            }
            if (!CanPlaySfx || !TryGetClip(sfx, out AudioClip clip)) return;

            resultVolumeMultiplier = sfx == BsSfx.Win ? WinResultLayerGain : 1f;
            if (resultSource == null)
            {
                Play(sfx, resultVolumeMultiplier);
                return;
            }

            resultSource.clip = clip;
            resultSource.volume = SfxVolume * resultVolumeMultiplier;
            resultSource.pitch = 1f;
            resultSource.Play();
        }

        public void PlayWinCardAccent()
        {
            resultHoldsBgm = true;
            RefreshBgmPlayback();
            Play(BsSfx.WinCardReveal, WinCardRevealLayerGain);
        }

        public void RestoreBgmAfterResult()
        {
            StopResultCues();
            StopFailWaitMusic(FailWaitMusicHandoffFadeSeconds);
            resultHoldsBgm = false;
            StartBgm();
        }

        private void StopResultCues()
        {
            if (resultSource != null) resultSource.Stop();
            if (oneShotPool == null) return;
            clips.TryGetValue(BsSfx.Win, out AudioClip win);
            clips.TryGetValue(BsSfx.Fail, out AudioClip fail);
            clips.TryGetValue(BsSfx.WinCardReveal, out AudioClip card);
            foreach (AudioSource source in oneShotPool)
            {
                if (source == null || source.clip == null) continue;
                if (source.clip == win || source.clip == fail || source.clip == card)
                    source.Stop();
            }
        }

        public void ResumeFailWaitMusic()
        {
            bool running = failWaitMusicRequested && !failWaitMusicStopping
                && (failWaitMusicPending
                    || (resultMusicSource != null && resultMusicSource.isPlaying));
            if (running) return;
            if (!CanPlayMusic)
            {
                failWaitMusicRequested = true;
                return;
            }
            if (failWaitMusicStopping
                && resultMusicSource != null && resultMusicSource.isPlaying)
            {
                failWaitMusicRequested = true;
                failWaitMusicStopping = false;
                StartFailWaitMusicFade(1f, FailWaitMusicResumeFadeSeconds);
                return;
            }
            float untilEntry = failWaitMusicCardEntryAt - Time.unscaledTime;
            if (untilEntry > 0f)
                BeginFailWaitMusic(untilEntry, FailWaitMusicFadeInSeconds, true);
            else
                BeginFailWaitMusic(0f, FailWaitMusicResumeFadeSeconds, false);
        }

        public void StopFailWaitMusic(float fadeSeconds = FailWaitMusicStopFadeSeconds,
                                      bool shortenRunningFade = true)
        {
            failWaitMusicRequested = false;
            FadeOutFailWaitMusic(fadeSeconds, shortenRunningFade);
        }

        private void BeginFailWaitMusic(float delaySeconds, float fadeSeconds, bool enterOnPickup)
        {
            failWaitMusicRequested = true;
            failWaitMusicFading = false;
            failWaitMusicStopping = false;
            failWaitMusicMix = 0f;
            if (resultMusicSource != null) resultMusicSource.Stop();
            ApplyFailWaitMusicVolume();
            failWaitMusicPending = true;
            failWaitMusicEnterOnPickup = enterOnPickup;
            failWaitMusicEnterAt = Time.unscaledTime + Mathf.Max(0f, delaySeconds);
            failWaitMusicEntryFade = fadeSeconds;
            PrepareFailWaitMusic();
        }

        private void TickFailWaitMusic()
        {
            if (failWaitMusicPending && CompleteFailWaitMusicLoad()
                && Time.unscaledTime >= failWaitMusicEnterAt)
                EnterFailWaitMusic();

            if (!failWaitMusicFading) return;
            failWaitMusicFadeElapsed += Time.unscaledDeltaTime;
            float progress = failWaitMusicFadeSeconds > 0f
                ? Mathf.Clamp01(failWaitMusicFadeElapsed / failWaitMusicFadeSeconds)
                : 1f;
            float eased = failWaitMusicStopping
                ? Mathf.SmoothStep(0f, 1f, progress)
                : 1f - (1f - progress) * (1f - progress);
            failWaitMusicMix = Mathf.Lerp(failWaitMusicFadeFrom, failWaitMusicFadeTo, eased);
            ApplyFailWaitMusicVolume();
            if (progress < 1f) return;
            failWaitMusicFading = false;
            if (failWaitMusicStopping) SilenceFailWaitMusic();
        }

        private void EnterFailWaitMusic()
        {
            failWaitMusicPending = false;
            AudioClip clip = failWaitMusicClip;
            if (clip == null || resultMusicSource == null || !CanPlayMusic
                || BgmSuspensions.Count > 0) return;

            float sinceEntry = Time.unscaledTime - failWaitMusicCardEntryAt;
            bool anchored = !float.IsInfinity(failWaitMusicCardEntryAt) && sinceEntry >= 0f;
            bool firstEntry = failWaitMusicEnterOnPickup && anchored
                && sinceEntry < FailWaitMusicLateEntrySeconds;
            int pickupSamples = Mathf.RoundToInt(clip.frequency * FailWaitMusicPickupSeconds);
            long position = anchored && clip.samples > pickupSamples
                ? clip.samples - pickupSamples + (long)Math.Round(sinceEntry * clip.frequency)
                : 0L;
            resultMusicSource.clip = clip;
            resultMusicSource.loop = true;
            resultMusicSource.pitch = 1f;
            resultMusicSource.timeSamples = (int)(position % clip.samples);
            failWaitMusicMix = 0f;
            ApplyFailWaitMusicVolume();
            resultMusicSource.Play();
            StartFailWaitMusicFade(1f, firstEntry
                ? failWaitMusicEntryFade
                : Mathf.Min(failWaitMusicEntryFade, FailWaitMusicResumeFadeSeconds));
        }

        private void FadeOutFailWaitMusic(float fadeSeconds, bool shortenRunningFade = true)
        {
            failWaitMusicPending = false;
            if (resultMusicSource == null || !resultMusicSource.isPlaying || failWaitMusicMix <= 0f)
            {
                SilenceFailWaitMusic();
                return;
            }
            float remaining = failWaitMusicFadeSeconds - failWaitMusicFadeElapsed;
            if (failWaitMusicStopping && failWaitMusicFading
                && (!shortenRunningFade || remaining <= fadeSeconds)) return;
            failWaitMusicStopping = true;
            StartFailWaitMusicFade(0f, fadeSeconds);
        }

        private void StartFailWaitMusicFade(float target, float seconds)
        {
            failWaitMusicFadeFrom = failWaitMusicMix;
            failWaitMusicFadeTo = target;
            failWaitMusicFadeSeconds = Mathf.Max(0f, seconds);
            failWaitMusicFadeElapsed = 0f;
            failWaitMusicFading = true;
        }

        private void SilenceFailWaitMusic()
        {
            failWaitMusicFading = false;
            failWaitMusicStopping = false;
            failWaitMusicMix = 0f;
            if (resultMusicSource == null) return;
            resultMusicSource.Stop();
            ApplyFailWaitMusicVolume();
        }

        private void ApplyFailWaitMusicVolume()
        {
            if (resultMusicSource != null)
                resultMusicSource.volume = BgmVolume * BgmBedTrim * failWaitMusicMix;
        }

        private void PrepareFailWaitMusic()
        {
            if (failWaitMusicLoaded || failWaitMusicLoad != null) return;
            failWaitMusicLoad = Resources.LoadAsync<AudioClip>(ResourceDirectory + FailWaitMusicName);
        }

        private bool CompleteFailWaitMusicLoad()
        {
            if (failWaitMusicLoaded) return true;
            PrepareFailWaitMusic();
            if (!failWaitMusicLoad.isDone) return false;
            AudioClip clip = failWaitMusicLoad.asset as AudioClip;
            if (clip != null)
            {
                // Same rule as the beds: never Play before the sample data is ready.
                if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                if (clip.loadState == AudioDataLoadState.Loading) return false;
                if (clip.loadState != AudioDataLoadState.Loaded) clip = null;
            }
            failWaitMusicClip = clip;
            failWaitMusicLoaded = true;
            failWaitMusicLoad = null;
            return true;
        }

        public void SetSfxEnabled(bool enabled)
        {
            if (SfxEnabled == enabled) return;
            SfxEnabled = enabled;
            if (!CanPlaySfx)
            {
                StopPourFlowPlayback();
                StopResultCues();
                StopClipPreload();
                return;
            }
            BeginClipPreload();
        }

        public void SetMusicEnabled(bool enabled)
        {
            if (MusicEnabled == enabled) return;
            MusicEnabled = enabled;
            RefreshBgmPlayback();
            if (!CanPlayMusic) FadeOutFailWaitMusic(FailWaitMusicHandoffFadeSeconds);
            else if (failWaitMusicRequested) ResumeFailWaitMusic();
        }

        public void SetSfxVolume(float volume)
        {
            SfxVolume = Mathf.Clamp01(volume);
            if (pourFlowSource != null)
                pourFlowSource.volume = requestedPourFlowVolume * SfxVolume;
            if (resultSource != null && resultSource.isPlaying)
                resultSource.volume = SfxVolume * resultVolumeMultiplier;
        }

        public void SetMusicVolume(float volume)
        {
            BgmVolume = Mathf.Clamp01(volume);
            ApplyBgmMix();
            ApplyFailWaitMusicVolume();
        }

        public void SchedulePreferenceSave()
        {
            if (preferenceSaveRoutine != null)
                StopCoroutine(preferenceSaveRoutine);
            preferenceSaveRoutine = StartCoroutine(SavePreferencesAfterDelay());
        }

        private bool CanPlaySfx => SfxEnabled;
        private bool CanPlayMusic => MusicEnabled;

        private void LoadPreferences()
        {
            SfxEnabled = BartenderSettingsStore.SoundOn;
            MusicEnabled = BartenderSettingsStore.MusicOn;
            SfxVolume = BartenderSettingsStore.SoundVolume;
            BgmVolume = BartenderSettingsStore.MusicVolume;
        }

        private bool TryGetClip(BsSfx sfx, out AudioClip clip)
        {
            if (clips.TryGetValue(sfx, out clip)) return clip != null;
            if (!ClipNames.TryGetValue(sfx, out string name)) return false;
            // A first tap or startup cue must play now, not become a stale queued sound after loading.
            // Normally the preloader has already cached it; this fallback loads only the requested SFX.
            clip = Resources.Load<AudioClip>(ResourceDirectory + name);
            clips[sfx] = clip; // Cache missing files too, so they are not retried on every tap.
            return clip != null;
        }

        private void BeginClipPreload()
        {
            if (!isActiveAndEnabled || !CanPlaySfx || clipPreloadFinished
                || clipPreloadRoutine != null) return;
            clipPreloadRoutine = StartCoroutine(PreloadClips());
        }

        private void StopClipPreload()
        {
            if (clipPreloadRoutine == null) return;
            StopCoroutine(clipPreloadRoutine);
            clipPreloadRoutine = null;
        }

        private IEnumerator PreloadClips()
        {
            yield return null;
            foreach (BsSfx sfx in FirstClips)
                yield return PreloadClip(sfx);
            foreach (BsSfx sfx in ClipNames.Keys)
                yield return PreloadClip(sfx);
            clipPreloadFinished = true;
            clipPreloadRoutine = null;
        }

        private IEnumerator PreloadClip(BsSfx sfx)
        {
            if (!clips.TryGetValue(sfx, out AudioClip clip))
            {
                ResourceRequest request = Resources.LoadAsync<AudioClip>(
                    ResourceDirectory + ClipNames[sfx]);
                yield return request;
                clip = request.asset as AudioClip;
                clips[sfx] = clip;
            }

            if (clip != null && clip.loadState == AudioDataLoadState.Unloaded)
                clip.LoadAudioData();
            yield return null;
        }

        private AudioClip RequestBgmClip(BsBgm track, out bool finished)
        {
            if (bgmClips.TryGetValue(track, out AudioClip clip))
            {
                finished = true;
                return clip;
            }
            finished = false;
            if (!bgmLoads.ContainsKey(track))
            {
                string name = track == BsBgm.Menu ? MenuBgmName
                    : track == BsBgm.GameplayRush ? RushBgmName : BgmName;
                bgmLoads[track] = Resources.LoadAsync<AudioClip>(ResourceDirectory + name);
            }
            return null;
        }

        private bool CompleteBgmLoad(BsBgm track)
        {
            if (!bgmLoads.TryGetValue(track, out ResourceRequest request) || !request.isDone)
                return false;
            AudioClip clip = request.asset as AudioClip;
            if (clip != null)
            {
                // Background audio data loading can outlive the resource request. Wait for it before
                // Play so Unity cannot defer an obsolete play request past a mute or result screen.
                if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                if (clip.loadState == AudioDataLoadState.Loading) return false;
                if (clip.loadState != AudioDataLoadState.Loaded) clip = null;
            }
            bgmClips[track] = clip;
            bgmLoads.Remove(track);
            return true;
        }

        private void ValidateAuthoredSources()
        {
            bool hasOneShotSource = false;
            if (oneShotPool != null)
            {
                for (int i = 0; i < oneShotPool.Count; i++)
                {
                    if (oneShotPool[i] == null) continue;
                    hasOneShotSource = true;
                    break;
                }
            }

            if (bgmSource != null && pourFlowSource != null && resultSource != null
                && hasOneShotSource)
                return;

            Debug.LogError(
                "[BartenderAudio] BGM, Pour Flow and Result AudioSources must "
                + "be authored and assigned in the Inspector, and the authored "
                + "one-shot pool must contain at least one AudioSource.", this);
        }

        private AudioSource AcquireOneShotSource()
        {
            int count = oneShotPool != null ? oneShotPool.Count : 0;
            if (count == 0) return null;

            int start = Mathf.Abs(nextOneShotSourceIndex % count);
            AudioSource authoredFallback = null;
            int authoredFallbackIndex = -1;
            for (int offset = 0; offset < count; offset++)
            {
                int index = (start + offset) % count;
                AudioSource candidate = oneShotPool[index];
                if (candidate == null) continue;
                if (authoredFallback == null)
                {
                    authoredFallback = candidate;
                    authoredFallbackIndex = index;
                }

                if (candidate.isPlaying) continue;
                nextOneShotSourceIndex = (index + 1) % count;
                return candidate;
            }

            if (authoredFallback == null) return null;
            authoredFallback.Stop();
            nextOneShotSourceIndex = (authoredFallbackIndex + 1) % count;
            return authoredFallback;
        }

        private void StartPourFlow()
        {
            if (!pourFlowActive || pourFlowSource == null || !CanPlaySfx
                || !clips.TryGetValue(requestedPourFlow, out AudioClip clip))
                return;

            pourFlowSource.Stop();
            pourFlowSource.loop = false;
            pourFlowSource.clip = clip;
            pourFlowSource.volume = requestedPourFlowVolume * SfxVolume;
            pourFlowSource.pitch = 1f;
            pourFlowSource.panStereo = requestedPourFlowPan;
            pourFlowPendingStart = false;
            pourFlowPausedByService = false;
            pourFlowSource.Play();
        }

        private void StopPourFlowPlayback()
        {
            pourFlowPendingStart = false;
            pourFlowPausedByService = false;
            if (pourFlowSource == null) return;
            pourFlowSource.Stop();
            pourFlowSource.loop = false;
            pourFlowSource.pitch = 1f;
            pourFlowSource.panStereo = 0f;
        }

        private IEnumerator SavePreferencesAfterDelay()
        {
            yield return new WaitForSecondsRealtime(0.25f);
            preferenceSaveRoutine = null;
            try { PlayerPrefs.Save(); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private void FlushScheduledPreferenceSave()
        {
            if (preferenceSaveRoutine == null) return;
            StopCoroutine(preferenceSaveRoutine);
            preferenceSaveRoutine = null;
            try { PlayerPrefs.Save(); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        private void ApplyBgmMix()
        {
            if (bgmSource != null)
                bgmSource.volume = BgmVolume * pauseMix * BgmBedTrim;
        }

        public void SetBgmPaused(bool paused)
        {
            float target = paused ? PauseDuckMix : 1f;
            if (pauseTransition != null)
            {
                StopCoroutine(pauseTransition);
                pauseTransition = null;
            }
            if (!isActiveAndEnabled)
            {
                pauseMix = target;
                ApplyBgmMix();
                return;
            }
            if (Mathf.Approximately(pauseMix, target))
            {
                ApplyBgmMix();
                return;
            }
            pauseTransition = StartCoroutine(FadePauseMix(target));
        }

        private IEnumerator FadePauseMix(float target)
        {
            float start = pauseMix;
            float elapsed = 0f;
            // Use unscaled time because pause can stop the game clock.
            while (elapsed < PauseDuckSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                pauseMix = Mathf.Lerp(start, target,
                    Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01(elapsed / PauseDuckSeconds)));
                ApplyBgmMix();
                yield return null;
            }
            pauseMix = target;
            ApplyBgmMix();
            pauseTransition = null;
        }

        private void ReleasePourFlow(int generation)
        {
            if (generation != pourFlowGeneration || !pourFlowActive) return;
            pourFlowActive = false;
            StopPourFlowPlayback();
        }

        public sealed class PourFlowLease : IDisposable
        {
            private BsAudio owner;
            private readonly int generation;

            internal PourFlowLease(BsAudio owner, int generation)
            {
                this.owner = owner;
                this.generation = generation;
            }

            public void Dispose()
            {
                BsAudio current = owner;
                if (current == null) return;
                owner = null;
                current.ReleasePourFlow(generation);
            }
        }
    }
}
