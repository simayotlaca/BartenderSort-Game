using System;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>Shared, persistent settings authority for menu and in-game presenters.</summary>
    public static class BartenderSettingsStore
    {
        private enum RuntimeChannel
        {
            Music,
            Sound,
            Vibration,
            Notifications,
        }

        private const string VibrationPreferenceKey =
            "LiquidSort.Bartender.Settings.Vibration";
        private const string NotificationsPreferenceKey =
            "LiquidSort.Bartender.Settings.Notifications";
        private const float DefaultMusicVolume = 0.5f;
        private const float DefaultSoundVolume = 1f;
        private const float MinimumAudibleVolume = 0.001f;

        private static bool loaded;
        private static bool mutationInProgress;
        private static bool musicOn = true;
        private static bool soundOn = true;
        private static bool vibrationOn = true;
        private static bool notificationsOn = true;
        private static float musicVolume = DefaultMusicVolume;
        private static float soundVolume = DefaultSoundVolume;

        public static bool MusicOn { get { EnsureLoaded(); return musicOn; } }
        public static bool SoundOn { get { EnsureLoaded(); return soundOn; } }
        public static bool VibrationOn { get { EnsureLoaded(); return vibrationOn; } }
        public static bool NotificationsOn
        {
            get { EnsureLoaded(); return notificationsOn; }
        }
        public static float MusicVolume { get { EnsureLoaded(); return musicVolume; } }
        public static float SoundVolume { get { EnsureLoaded(); return soundVolume; } }
        public static float EffectiveMusicVolume => MusicOn ? MusicVolume : 0f;
        public static float EffectiveSoundVolume => SoundOn ? SoundVolume : 0f;

        public static event Action SettingsChanged;

        public static bool ToggleMusic() => SetMusicOn(!MusicOn);
        public static bool ToggleSound() => SetSoundOn(!SoundOn);
        public static bool ToggleVibration() => SetVibrationOn(!VibrationOn);

        /// <summary>
        /// Zero mutes without forgetting the last audible volume. Moving above zero enables the channel and
        /// saves the new level together.
        /// </summary>
        public static float SetMusicVolume(float volume)
        {
            EnsureLoaded();
            return CommitVolume(BsAudio.MusicVolumePreferenceKey,
                BsAudio.MusicPreferenceKey, volume, ref musicVolume, ref musicOn,
                RuntimeChannel.Music);
        }

        public static float SetSoundVolume(float volume)
        {
            EnsureLoaded();
            return CommitVolume(BsAudio.SoundVolumePreferenceKey,
                BsAudio.SoundPreferenceKey, volume, ref soundVolume, ref soundOn,
                RuntimeChannel.Sound);
        }

        public static bool SetMusicOn(bool enabled)
        {
            EnsureLoaded();
            Commit(BsAudio.MusicPreferenceKey, enabled, ref musicOn,
                RuntimeChannel.Music);
            return musicOn;
        }

        public static bool SetSoundOn(bool enabled)
        {
            EnsureLoaded();
            Commit(BsAudio.SoundPreferenceKey, enabled, ref soundOn,
                RuntimeChannel.Sound);
            return soundOn;
        }

        public static bool SetVibrationOn(bool enabled)
        {
            EnsureLoaded();
            Commit(VibrationPreferenceKey, enabled, ref vibrationOn,
                RuntimeChannel.Vibration);
            return vibrationOn;
        }

        /// <summary>
        /// Saves notification preference only. Permission prompts and scheduling belong to the notification
        /// service.
        /// </summary>
        public static bool SetNotificationsOn(bool enabled)
        {
            EnsureLoaded();
            Commit(NotificationsPreferenceKey, enabled, ref notificationsOn,
                RuntimeChannel.Notifications);
            return notificationsOn;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            loaded = false;
            mutationInProgress = false;
            musicOn = true;
            soundOn = true;
            vibrationOn = true;
            notificationsOn = true;
            musicVolume = DefaultMusicVolume;
            soundVolume = DefaultSoundVolume;
            SettingsChanged = null;
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            musicOn = PlayerPrefs.GetInt(BsAudio.MusicPreferenceKey, 1) != 0;
            soundOn = PlayerPrefs.GetInt(BsAudio.SoundPreferenceKey, 1) != 0;
            vibrationOn = PlayerPrefs.GetInt(VibrationPreferenceKey, 1) != 0;
            notificationsOn = PlayerPrefs.GetInt(
                NotificationsPreferenceKey, 1) != 0;
            musicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(
                BsAudio.MusicVolumePreferenceKey, DefaultMusicVolume));
            soundVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(
                BsAudio.SoundVolumePreferenceKey, DefaultSoundVolume));
            if (musicVolume <= MinimumAudibleVolume)
                musicVolume = DefaultMusicVolume;
            if (soundVolume <= MinimumAudibleVolume)
                soundVolume = DefaultSoundVolume;
            loaded = true;
        }

        private static float CommitVolume(string volumeKey, string enabledKey,
                                          float requestedVolume,
                                          ref float currentVolume,
                                          ref bool currentEnabled,
                                          RuntimeChannel channel)
        {
            float clamped = Mathf.Clamp01(requestedVolume);
            bool enabled = clamped > MinimumAudibleVolume;
            bool volumeChanged = enabled && !Mathf.Approximately(currentVolume, clamped);
            bool enabledChanged = currentEnabled != enabled;
            if (mutationInProgress || (!volumeChanged && !enabledChanged))
                return currentEnabled ? currentVolume : 0f;

            float previousVolume = currentVolume;
            bool previousEnabled = currentEnabled;
            mutationInProgress = true;
            try
            {
                if (volumeChanged)
                {
                    PlayerPrefs.SetFloat(volumeKey, clamped);
                    currentVolume = clamped;
                }
                if (enabledChanged)
                {
                    PlayerPrefs.SetInt(enabledKey, enabled ? 1 : 0);
                    currentEnabled = enabled;
                }
                ApplyRuntimeVolume(channel, currentVolume, currentEnabled);
                InvokeSafely(SettingsChanged);
                return currentEnabled ? currentVolume : 0f;
            }
            catch (Exception exception)
            {
                currentVolume = previousVolume;
                currentEnabled = previousEnabled;
                try
                {
                    PlayerPrefs.SetFloat(volumeKey, previousVolume);
                    PlayerPrefs.SetInt(enabledKey, previousEnabled ? 1 : 0);
                }
                catch { /* Preserve the original persistence error. */ }
                Debug.LogException(exception);
                return previousEnabled ? previousVolume : 0f;
            }
            finally
            {
                mutationInProgress = false;
            }
        }

        private static bool Commit(string key, bool enabled, ref bool current,
                                   RuntimeChannel channel)
        {
            if (mutationInProgress || current == enabled) return false;
            bool previous = current;
            mutationInProgress = true;
            try
            {
                PlayerPrefs.SetInt(key, enabled ? 1 : 0);
                PlayerPrefs.Save();
                current = enabled;
                ApplyRuntime(channel, enabled);
                InvokeSafely(SettingsChanged);
                return true;
            }
            catch (Exception exception)
            {
                try { PlayerPrefs.SetInt(key, previous ? 1 : 0); }
                catch { /* Preserve the original persistence error. */ }
                Debug.LogException(exception);
                return false;
            }
            finally
            {
                mutationInProgress = false;
            }
        }

        private static void ApplyRuntime(RuntimeChannel channel, bool enabled)
        {
            if (channel == RuntimeChannel.Vibration
                || channel == RuntimeChannel.Notifications)
                return;
            try
            {
                BsAudio audio = BsAudio.Instance;
                if (channel == RuntimeChannel.Music) audio?.SetMusicEnabled(enabled);
                else if (channel == RuntimeChannel.Sound) audio?.SetSfxEnabled(enabled);
            }
            catch (Exception exception)
            {
                // The preference is saved already. Keep it even if audio needs the next scene to recover.
                Debug.LogException(exception);
            }
        }

        private static void ApplyRuntimeVolume(RuntimeChannel channel, float volume,
                                               bool enabled)
        {
            try
            {
                BsAudio audio = BsAudio.Instance;
                if (channel == RuntimeChannel.Music)
                {
                    audio?.SetMusicVolume(volume);
                    audio?.SetMusicEnabled(enabled);
                }
                else if (channel == RuntimeChannel.Sound)
                {
                    audio?.SetSfxVolume(volume);
                    audio?.SetSfxEnabled(enabled);
                }
                audio?.SchedulePreferenceSave();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static void InvokeSafely(Action handlers)
        {
            if (handlers == null) return;
            Delegate[] subscribers = handlers.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try { ((Action)subscribers[i]).Invoke(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }
    }
}
