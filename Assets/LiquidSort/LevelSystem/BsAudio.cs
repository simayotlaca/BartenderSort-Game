using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Which background track to play.</summary>
    public enum BsBgm
    {
        /// <summary>Gameplay: the 24-bar BGM_Bar_Loop.</summary>
        Gameplay,
        /// <summary>Menu: a 48-bar A/B loop with a fuller second pass.</summary>
        Menu,
        /// <summary>Timed levels: the 32-bar BGM_Bar_Rush_Loop. Append new tracks after this.</summary>
        GameplayRush,
    }

    /// <summary>Typed IDs for clips in Resources/Audio.</summary>
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
        // I keep unused enum values because prefabs store their numbers. Removing one would shift later
        // sounds.
        ButtonBack,
        TabSwitch,
        SliderTick,
        ToggleOn,
        ToggleOff,
        LevelNodePop,
        MapAdvance,
        // Appended to preserve every serialized value above.
        PourFlow1,
        PourFlow2,
        PourFlow3,
        PourFlow4,
        PourFlow5,
        // Final-seconds ticking. Add new enum values only at the end.
        TimerTick,
        TimerTock,
        // A new order lands in the tray.
        OrderBell,
        // First Shift bubble and hippo voice.
        TutorialPop,
        HippoBlip,
        StepComplete,
        // A glass unlocks.
        LockOpen,
        // Old coin clips were replaced by reward and counter sounds. Keep their enum values for saved
        // prefab references.
        CoinSingle,
        CoinTick,
        CoinCascade,
        // Unused coin-flight sound. Keep its enum value so serialized IDs stay fixed.
        CoinFlight,
        // Full 0.70-second balance-counting sequence.
        CoinCounterRise,
        // Menu Play sound, separate from ButtonClick.
        PlayButton,
        // Legacy coin accent. Keep its serialized ID; the card has a separate reveal cue.
        WinCoinReward,
        // Extra-glass arrival and time-boost flight sounds.
        GlassArrive,
        TimeBoost,
        // Reserved old win sound. Removing it would shift prefab sound IDs.
        RetiredWinAccent,
        // Popup open and close.
        PopupOpen,
        PopupClose,
        // Life loss, timed refill and paid refill. Append new values only.
        LifeLost,
        LifeRegained,
        LifeRefill,
        // Dedicated Undo and Shuffle sounds. Append new values only.
        BoosterUndo,
        BoosterShuffle,
        // A covered liquid layer becomes visible after a pour.
        HiddenReveal,
        // Brand signature on app launch, independent of winning a level.
        StartupLogo,
        // Result card entrance after the Cheers toast, separate from coin collection.
        WinCardReveal,
        // The Daily Orders hint bubble opening on the menu.
        DailyRewardHint,
        // A timed order runs out. Append new values only.
        OrderExpired,
        // Coins leave the balance: booster fees, paid continue, paid refill.
        CoinSpend,
        // The board and HUD flying in at the start of every level. Append new values only.
        LevelIntro,
    }

    /// <summary>
    /// Loads named clips from Resources/Audio and stays silent if one is missing. Pour sounds have
    /// generation-checked ownership so old cleanup cannot stop a new round.
    /// </summary>
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

        private static readonly Dictionary<BsSfx, string> ClipNames =
            new Dictionary<BsSfx, string>
            {
                { BsSfx.GlassPickup, "SFX_GlassPickup" },
                { BsSfx.GlassSet, "SFX_GlassSet" },
                { BsSfx.Check, "SFX_Check" },
                { BsSfx.DeliverSlide, "SFX_DeliverSlide" },
                { BsSfx.Invalid, "SFX_Invalid" },
                // I use a unique win-track name so Resources.Load cannot confuse its WAV and OGG versions.
                { BsSfx.Win, "SFX_WinScreen" },
                { BsSfx.Fail, "SFX_Fail" },
                { BsSfx.ButtonClick, "SFX_ButtonClick" },
                // Back and tab sounds use their existing reserved IDs.
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
            };

        // Sorted by pour amount, starting at one unit. This lets missing clips fall back to the nearest
        // amount.
        private static readonly BsSfx[] PourFlowWeights =
        {
            BsSfx.PourFlow1, BsSfx.PourFlow2, BsSfx.PourFlow3,
            BsSfx.PourFlow4, BsSfx.PourFlow5,
        };

        public static BsAudio Instance { get; private set; }

        [Header("Levels")]
        [Range(0f, 1f)] public float SfxVolume = 1f;
        [Range(0f, 1f)] public float BgmVolume = 0.5f;

        public bool SfxEnabled { get; private set; } = true;
        public bool MusicEnabled { get; private set; } = true;

        private readonly Dictionary<BsSfx, AudioClip> clips =
            new Dictionary<BsSfx, AudioClip>();
        [Header("Authored Audio Sources")]
        [Tooltip("Looping music source authored under this object.")]
        [SerializeField] private AudioSource bgmSource;
        [Tooltip("Finite pour-flow source authored under this object.")]
        [SerializeField] private AudioSource pourFlowSource;
        [Tooltip("Win/fail result source authored under this object.")]
        [SerializeField] private AudioSource resultSource;
        [Tooltip("Authored one-shot sources. When every source is busy, the next "
                 + "authored source is reused; no AudioSource is created at runtime.")]
        [SerializeField] private List<AudioSource> oneShotPool =
            new List<AudioSource>();
        private int nextOneShotSourceIndex;
        private AudioClip bgmClip;
        private AudioClip menuBgmClip;
        private AudioClip rushBgmClip;
        // I keep the selected track here so StartBgm can restore the correct music after a result cue.
        private BsBgm currentBgm = BsBgm.Gameplay;
        private Coroutine preferenceSaveRoutine;
        private float resultVolumeMultiplier = 1f;
        private bool bgmRequested;
        private bool bgmPaused;
        private bool resultHoldsBgm;
        // Pausing gameplay can lower the bed; result and loading screens suspend playback entirely.
        private float pauseMix = 1f;
        private Coroutine pauseTransition;

        // The coin cue sits under whatever the purchase itself plays, so it stays a layer.
        private const float CoinSpendVolume = 0.65f;

        // The toast music and the card entrance have independently balanced cues.
        private const float WinResultLayerGain = 0.80f;
        private const float WinCardRevealLayerGain = 0.80f;

        // Fixed music trim keeps effects clear. It is separate from the player's saved MusicVolume
        // preference.
        public const float BgmBedTrim = 0.5f;   // -6 dB
        // Lower music during pause so menu sounds stand out.
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
            LoadClips();
            ValidateAuthoredSources();
            // Start only when a presenter requests a ready menu or board, never via playOnAwake.
            if (bgmSource != null)
            {
                bgmSource.playOnAwake = false;
                bgmSource.Stop();
            }
        }

        private void LateUpdate()
        {
            if (bgmSource == null || !bgmSource.isPlaying) return;
            AudioClip clip = bgmSource.clip;
            if (clip == null) return;
            if (!ReferenceEquals(clip, lastBgmClip))
            {
                // I cache the clip name because Object.name creates a string on each read.
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
            // A balance read before the player file is readable is a placeholder, not a purchase.
            lastKnownCoins = BartenderProgressService.IsAvailable
                ? BartenderProgressService.Coins
                : int.MinValue;
        }

        private void OnDisable()
        {
            BartenderProgressService.CoinsChanged -= HandleCoinsChanged;
        }

        private void HandleCoinsChanged(int coins)
        {
            int previous = lastKnownCoins;
            lastKnownCoins = coins;
            // Rewards raise the balance and stay silent; the first event only sets the baseline.
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

        /// <summary>Saves the loop position when the scene closes.</summary>
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

        /// <summary>
        /// Keeps background music silent across overlapping covers and scene loads. The last owner
        /// releases its hold only when its destination is ready; sound effects remain independent.
        /// </summary>
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
                // Removing by identity also makes old leases harmless after static state is reset.
                if (!BgmSuspensions.Remove(this)) return;
                Instance?.RefreshBgmPlayback();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) FlushScheduledPreferenceSave();
        }

        private void OnApplicationQuit() => FlushScheduledPreferenceSave();

        /// <summary>Plays a one-shot, or stays silent if its clip is missing.</summary>
        public void Play(BsSfx sfx, float volume = 1f, float pitch = 1f,
                         float pan = 0f)
        {
            if (!CanPlaySfx || !clips.TryGetValue(sfx, out AudioClip clip)) return;

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

        /// <summary>Time left on this sound's active voices, including their playback pitch.</summary>
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

        /// <summary>
        /// Plays the complete pour clip once, including its tail; pause, mute or completion never restart
        /// it. Missing clips or amounts outside 1–5 return false.
        /// </summary>
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

        /// <summary>
        /// Missing pour clips try the nearest amount, lighter first, within the V5 set. Returns false if all
        /// five are missing.
        /// </summary>
        private bool TryResolvePourFlow(int amount, out BsSfx sfx)
        {
            sfx = default;
            if (amount < 1 || amount > PourFlowWeights.Length) return false;

            int index = amount - 1;
            for (int distance = 0; distance < PourFlowWeights.Length; distance++)
            {
                int lighter = index - distance;
                if (lighter >= 0 && clips.ContainsKey(PourFlowWeights[lighter]))
                {
                    sfx = PourFlowWeights[lighter];
                    return true;
                }

                int heavier = index + distance;
                if (heavier < PourFlowWeights.Length
                    && clips.ContainsKey(PourFlowWeights[heavier]))
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

        /// <summary>Invalidates old pour ownership when the level changes.</summary>
        public void InvalidatePourFlow()
        {
            pourFlowSuspended = false;
            pourFlowGeneration = pourFlowGeneration == int.MaxValue
                ? 1
                : pourFlowGeneration + 1;
            pourFlowActive = false;
            StopPourFlowPlayback();
        }

        /// <summary>Requests the selected track; active covers and results keep it paused.</summary>
        public void StartBgm()
        {
            bgmRequested = true;
            RefreshBgmPlayback();
        }

        /// <summary>The ready menu or board selects its own resumable track.</summary>
        public void StartBgm(BsBgm track)
        {
            currentBgm = track;
            StartBgm();
        }

        /// <summary>Falls back to the bar bed whenever a track's own clip is missing.</summary>
        private AudioClip SelectBgmClip()
        {
            switch (currentBgm)
            {
                case BsBgm.Menu:
                    return menuBgmClip != null ? menuBgmClip : bgmClip;
                case BsBgm.GameplayRush:
                    return rushBgmClip != null ? rushBgmClip : bgmClip;
                default:
                    return bgmClip;
            }
        }

        /// <summary>Stops the playback request without losing the current position.</summary>
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
            if (bgmSource == null) return;
            if (!bgmRequested || !CanPlayMusic || resultHoldsBgm || BgmSuspensions.Count > 0)
            {
                PauseBgmPlayback();
                return;
            }

            AudioClip wanted = SelectBgmClip();
            if (wanted == null) return;

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
                // A new scene may already have the desired clip assigned in its prefab.
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

        /// <summary>Suspends the bed for the entire result screen, including silent/missing cues.</summary>
        public void PlayResult(BsSfx sfx)
        {
            if (sfx != BsSfx.Win && sfx != BsSfx.Fail)
            {
                Play(sfx);
                return;
            }

            resultHoldsBgm = true;
            RefreshBgmPlayback();
            StopResultCues();
            if (!CanPlaySfx || !clips.TryGetValue(sfx, out AudioClip clip)) return;

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

        /// <summary>Plays the card music while continuing the result screen's background hold.</summary>
        public void PlayWinCardAccent()
        {
            resultHoldsBgm = true;
            RefreshBgmPlayback();
            Play(BsSfx.WinCardReveal, WinCardRevealLayerGain);
        }

        /// <summary>Only a ready menu/board may end the result hold; loading holds still apply.</summary>
        public void RestoreBgmAfterResult()
        {
            StopResultCues();
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

        public void SetSfxEnabled(bool enabled)
        {
            if (SfxEnabled == enabled) return;
            SfxEnabled = enabled;
            if (!CanPlaySfx)
            {
                // Let short one-shots finish their tails. Pour and result sounds follow the new setting
                // immediately.
                StopPourFlowPlayback();
                StopResultCues();
                return;
            }
        }

        public void SetMusicEnabled(bool enabled)
        {
            if (MusicEnabled == enabled) return;
            MusicEnabled = enabled;
            RefreshBgmPlayback();
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
        }

        /// <summary>Slider drags persist once after they settle instead of every frame.</summary>
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

        private void LoadClips()
        {
            int missing = 0;
            foreach (KeyValuePair<BsSfx, string> pair in ClipNames)
            {
                AudioClip clip = Resources.Load<AudioClip>(ResourceDirectory + pair.Value);
                if (clip != null) clips[pair.Key] = clip;
                else missing++;
            }

            bgmClip = Resources.Load<AudioClip>(ResourceDirectory + BgmName);
            if (bgmClip == null) missing++;
            menuBgmClip = Resources.Load<AudioClip>(ResourceDirectory + MenuBgmName);
            if (menuBgmClip == null) missing++;
            rushBgmClip = Resources.Load<AudioClip>(ResourceDirectory + RushBgmName);
            if (rushBgmClip == null) missing++;

            if (missing > 0)
                Debug.Log($"[BartenderAudio] Ses hattı hazır — "
                          + $"{clips.Count + (bgmClip != null ? 1 : 0)}/"
                          + $"{ClipNames.Count + 1} klip yüklü; eksikler sessiz geçecek.",
                    this);
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

        /// <summary>
        /// Uses a free authored source or reuses one in round-robin order. This keeps playback predictable
        /// without allocations.
        /// </summary>
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

        /// <summary>
        /// Lowers music on pause and restores it on resume. If disabled, apply the value directly without a
        /// coroutine.
        /// </summary>
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
