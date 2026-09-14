#!/usr/bin/env python3
"""Daily-reward hint toast: a music-box skip answered by a small character boop.

The chosen take from the five reviewed on 10 September 2026. Built from the
project's cached CC0 VSCO samples; nothing is taken from the MagicSort reference
library. The palette targets a swelling onset, a spectral centre near 1.4 kHz and
a top band that stays in the middle of the image.
"""
from fractions import Fraction
from pathlib import Path
import math

import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfilt, fftconvolve, resample_poly

ROOT = Path(__file__).resolve().parents[2]
SAMPLES = ROOT / "AudioProduction/BartenderSortV2/samples"
OUTPUT = ROOT / "Assets/Resources/Audio/SFX_DailyRewardHint.wav"
SR = 48_000
DURATION = 1.25
N = round(SR * DURATION)
def noise(count, seed):
    """Per-layer seeds: the render stays identical whatever order layers are built in."""
    return np.random.default_rng(seed).normal(size=count)

HARP = {62: "Strings/Harp/KSHarp_D4_mf.wav", 69: "Strings/Harp/KSHarp_A4_mf.wav",
        72: "Strings/Harp/KSHarp_C5_mf.wav", 76: "Strings/Harp/KSHarp_E5_mf.wav",
        79: "Strings/Harp/KSHarp_G5_mf.wav", 83: "Strings/Harp/KSHarp_B5_mf.wav",
        86: "Strings/Harp/KSHarp_D6_mf.wav"}
FLUTE = {69: "Woodwinds/Flute/stac/LDFlute_stac_A4_v3_rr1.wav",
         72: "Woodwinds/Flute/stac/LDFlute_stac_C5_v3_rr2.wav",
         76: "Woodwinds/Flute/stac/LDFlute_stac_E5_v3_rr1.wav"}
GLOCK = {84: "Percussion/Glock/glock_medium_C6.wav"}


def pitched(bank, midi, length):
    """Nearest cached note, resampled to the wanted pitch, trimmed and levelled."""
    root = min(bank, key=lambda k: abs(k - midi))
    audio, rate = sf.read(SAMPLES / bank[root], always_2d=True)
    if rate != SR:
        audio = resample_poly(audio, SR, rate, axis=0)
    steps = midi - root
    if steps:
        ratio = Fraction(2 ** (-steps / 12)).limit_denominator(400)
        audio = resample_poly(audio, ratio.numerator, ratio.denominator, axis=0)
    count = round(length * SR)
    if len(audio) < count:
        audio = np.pad(audio, ((0, count - len(audio)), (0, 0)))
    # Peak-normalised so the gains below read as a mix balance, not as sample levels.
    return audio[:count] / (np.max(np.abs(audio[:count])) + 1e-9)


def shape(signal, attack, release, curve=2.0):
    """Swell the onset and land the tail on silence."""
    count = len(signal)
    attack_count = max(1, min(count, round(attack * SR)))
    release_count = max(1, min(count - attack_count, round(release * SR)))
    envelope = np.ones(count)
    envelope[:attack_count] = np.linspace(0, 1, attack_count) ** curve
    envelope[count - release_count:] = np.cos(np.linspace(0, math.pi / 2, release_count)) ** 2
    return signal * envelope[:, None]


def add(bus, signal, start, gain, pan=0.0):
    offset = round(start * SR)
    count = min(len(signal), len(bus) - offset)
    angle = (pan + 1) * math.pi / 4
    bus[offset:offset + count] += signal[:count] * gain * np.array(
        [math.cos(angle), math.sin(angle)]) * math.sqrt(2)


def swell(length, level):
    """The bloom the toast rides in on. The reference game's own hint bubble takes
    about 220 ms to reach its peak; without this the cue lands like a click."""
    time = np.arange(round(length * SR)) / SR
    rise = (time / time[-1]) ** 2.2
    breath = sosfilt(butter(4, [420, 1900], "bandpass", fs=SR, output="sos"),
                     noise(len(time), 4101))
    tone = np.sin(2 * math.pi * np.cumsum(150 + 130 * rise) / SR)
    mono = (0.55 * breath / (np.max(np.abs(breath)) + 1e-9) + 0.45 * tone) * rise * level
    return np.repeat(mono[:, None], 2, axis=1)


def bubble(base_hz, drop, length, attack):
    """A round, low pop: the toast arriving, not a click."""
    time = np.arange(round(length * SR)) / SR
    phase = 2 * math.pi * np.cumsum(base_hz + drop * np.exp(-time / 0.030)) / SR
    body = (np.sin(phase) + 0.10 * np.sin(2.01 * phase))
    body *= (1 - np.exp(-time / attack)) * np.exp(-time / 0.075)
    puff = sosfilt(butter(2, [300, 1400], "bandpass", fs=SR, output="sos"),
                   noise(len(time), 4102)) * np.exp(-time / 0.020) * 0.16
    return shape(np.repeat((body + puff)[:, None], 2, axis=1), 0.004, 0.05)


def boop(start_hz, end_hz, length, wobble=0.022, rate=5.8):
    """A small friendly voice: a glide upward with a light wobble, rounded off
    above 2 kHz so it stays a character sound rather than a beep."""
    time = np.arange(round(length * SR)) / SR
    glide = start_hz + (end_hz - start_hz) * (time / time[-1]) ** 0.65
    glide *= 1 + wobble * np.sin(2 * math.pi * rate * time)
    phase = 2 * math.pi * np.cumsum(glide) / SR
    tone = np.sin(phase) + 0.22 * np.sin(2 * phase) + 0.06 * np.sin(3 * phase)
    tone *= (1 - np.exp(-time / 0.020)) * np.exp(-time / (length * 0.55))
    mono = sosfilt(butter(2, 2200, "lowpass", fs=SR, output="sos"), tone)
    return shape(np.repeat(mono[:, None], 2, axis=1), 0.012, 0.045)


def glock(midi, length, soften=4200):
    """The single cached C6, pitched down into music-box register and de-iced."""
    note = sosfilt(butter(4, soften, "lowpass", fs=SR, output="sos"),
                   pitched(GLOCK, midi, length), axis=0)
    return shape(note, 0.008, min(0.20, length * 0.5), curve=1.3)


def harp(midi, length, attack=0.010):
    return shape(pitched(HARP, midi, length), attack, min(0.18, length * 0.5), curve=1.4)


def flute(midi, length):
    return shape(pitched(FLUTE, midi, length), 0.075, min(0.22, length * 0.5), curve=1.6)


def air(length, band, level, seed=4103):
    """A quiet glow under the figure; band-limited and mono, so it does not drag
    the spectral centre up into the reference's 'alien' region."""
    time = np.arange(round(length * SR)) / SR
    glow = sosfilt(butter(4, band, "bandpass", fs=SR, output="sos"),
                   noise(len(time), seed))
    glow *= (1 - np.exp(-time / 0.10)) * np.exp(-time / 0.24)
    glow *= level / (np.max(np.abs(glow)) + 1e-9)
    return np.repeat(glow[:, None], 2, axis=1)


def reverb(bus, tail=0.55, width=0.45):
    """Short room. Its side channel is low-passed, so height stays in the middle."""
    time = np.arange(round(tail * SR)) / SR
    decay = np.exp(-time / 0.16) * (time > 0.011)
    mid = sosfilt(butter(4, 4200, "lowpass", fs=SR, output="sos"),
                  noise(len(time), 4104)) * decay
    side = sosfilt(butter(2, 1800, "lowpass", fs=SR, output="sos"),
                   noise(len(time), 4105)) * decay * width
    mid[0] += 0.6
    wet_mid = fftconvolve(bus.mean(axis=1), mid)[:len(bus)]
    wet_side = fftconvolve(bus[:, 0] - bus[:, 1], side)[:len(bus)]
    wet = np.stack([wet_mid + wet_side, wet_mid - wet_side], axis=1)
    return wet / (np.max(np.abs(wet)) + 1e-9)


def narrow(bus, target_db=-9.0):
    """The VSCO room is wider than this game's palette, and a side channel as loud
    as the middle collapses on a phone speaker. Trim the sides to a fixed ratio."""
    mid = bus.mean(axis=1)
    side = (bus[:, 0] - bus[:, 1]) / 2
    ratio = np.sqrt((side ** 2).mean()) / (np.sqrt((mid ** 2).mean()) + 1e-12)
    side *= min(1.0, 10 ** (target_db / 20) / (ratio + 1e-12))
    return np.stack([mid + side, mid - side], axis=1)


def compose():
    bus = np.zeros((N, 2))
    add(bus, swell(0.070, 0.16), 0.0, 0.55)
    add(bus, bubble(240, 230, 0.21, 0.010), 0.050, 0.38, -0.05)
    for start, midi, gain, pan in ((0.100, 74, 0.40, -0.08), (0.180, 81, 0.38, 0.06)):
        add(bus, glock(midi, DURATION - start), start, gain, pan)
        # A harp doubling under each mallet gives the toy its wooden body.
        add(bus, harp(midi, DURATION - start), start, gain * 0.40, pan)
    add(bus, boop(494, 660, 0.320), 0.270, 0.50)
    add(bus, glock(83, DURATION - 0.285), 0.285, 0.26, -0.04)
    add(bus, flute(81, 0.50), 0.32, 0.12, 0.05)
    add(bus, air(0.45, [2800, 6200], 0.020), 0.30, 0.5)
    return bus


def main():
    dry = compose()
    bus = narrow(dry + reverb(dry) * 0.18 * np.max(np.abs(dry)))
    bus = sosfilt(butter(1, 55, "highpass", fs=SR, output="sos"), bus, axis=0)
    # Shaping last keeps the filter's own ringing from re-opening the tail.
    bus = shape(bus, 0.0015, 0.09)
    bus *= 10 ** (-4.5 / 20) / np.max(np.abs(bus))
    sf.write(OUTPUT, bus, SR, subtype="PCM_16")

    rendered, rate = sf.read(OUTPUT, always_2d=True)
    mono = rendered.mean(axis=1)
    spectrum = np.abs(np.fft.rfft(mono * np.hanning(len(mono))))
    freqs = np.fft.rfftfreq(len(mono), 1 / rate)
    assert rendered.shape == (N, 2) and np.isfinite(rendered).all()
    assert np.max(np.abs(rendered)) < 0.62
    assert np.max(np.abs(rendered[[0, -1]])) == 0
    print(f"{OUTPUT.name}: {len(rendered) / rate:.2f}s  "
          f"centre {(spectrum * freqs).sum() / spectrum.sum():.0f} Hz  "
          f"peak {20 * np.log10(np.max(np.abs(rendered))):.1f} dBFS")


if __name__ == "__main__":
    main()
