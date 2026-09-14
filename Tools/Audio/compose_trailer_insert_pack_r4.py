#!/usr/bin/env python3
"""Render a small, original editorial SFX pack for the Bartender Sort r4 trailer.

The effects intentionally live above the trailer's dense low/mid music bed:
short, bright, and with -3 dBFS asset peaks.  They are separate editorial
layers, not replacements for the gameplay SFX already in Assets/Resources.
"""

from pathlib import Path
import math

import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfilt


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "output/BartenderSortPromoPresentation/AudioInsertPack_r4"
SR = 48_000


def env(n, attack=0.006, release=0.08):
    """Cosine-edged envelope that always finishes at digital silence."""
    a = max(1, min(n, round(attack * SR)))
    r = max(1, min(n - a, round(release * SR)))
    e = np.ones(n)
    e[:a] = np.sin(np.linspace(0, math.pi / 2, a)) ** 2
    e[-r:] = np.cos(np.linspace(0, math.pi / 2, r)) ** 2
    return e


def band_noise(seconds, low, high, seed, decay=0.08):
    n = round(seconds * SR)
    x = np.random.default_rng(seed).normal(size=n)
    x = sosfilt(butter(4, [low, high], btype="bandpass", fs=SR, output="sos"), x)
    x /= np.max(np.abs(x)) + 1e-12
    return x * np.exp(-np.arange(n) / SR / decay)


def chirp(seconds, start_hz, end_hz, decay=0.20, harmonic=0.16):
    n = round(seconds * SR)
    t = np.arange(n) / SR
    freq = start_hz * (end_hz / start_hz) ** (t / max(t[-1], 1 / SR))
    phase = 2 * math.pi * np.cumsum(freq) / SR
    x = np.sin(phase) + harmonic * np.sin(2.01 * phase)
    return x * np.exp(-t / decay)


def glass_tone(seconds, hz, decay=0.16):
    n = round(seconds * SR)
    t = np.arange(n) / SR
    x = (np.sin(2 * math.pi * hz * t)
         + 0.26 * np.sin(2 * math.pi * 2.01 * hz * t)
         + 0.07 * np.sin(2 * math.pi * 3.96 * hz * t))
    return x * np.exp(-t / decay)


def stereo_add(bus, mono, start, gain=1.0, pan=0.0):
    offset = round(start * SR)
    count = min(len(mono), len(bus) - offset)
    if count <= 0:
        return
    angle = (max(-1.0, min(1.0, pan)) + 1) * math.pi / 4
    gains = np.array([math.cos(angle), math.sin(angle)]) * gain
    bus[offset:offset + count] += mono[:count, None] * gains


def finish(bus):
    # Keep every asset safe to lay over a master that is already near full scale.
    bus *= env(len(bus), 0.002, min(0.09, len(bus) / SR * 0.35))[:, None]
    bus = sosfilt(butter(1, 55, btype="highpass", fs=SR, output="sos"), bus, axis=0)
    bus *= 10 ** (-3 / 20) / (np.max(np.abs(bus)) + 1e-12)
    bus[0] = 0
    bus[-1] = 0
    return bus


def chapter_card_turn():
    duration = 0.82
    b = np.zeros((round(duration * SR), 2))
    # Card/paper motion remains deliberately light; the harmonic lift says "new chapter".
    stereo_add(b, band_noise(0.33, 900, 6800, 1001, 0.105), 0.00, 0.16, -0.35)
    stereo_add(b, band_noise(0.24, 1600, 8800, 1002, 0.075)[::-1], 0.16, 0.07, 0.30)
    stereo_add(b, chirp(0.13, 410, 255, 0.045), 0.015, 0.22, -0.12)
    stereo_add(b, glass_tone(0.64, 1046.5, 0.18), 0.19, 0.25, -0.10)
    stereo_add(b, glass_tone(0.53, 1318.5, 0.13), 0.30, 0.18, 0.12)
    return finish(b)


def timed_orders_pulse():
    duration = 2.80
    b = np.zeros((round(duration * SR), 2))
    # Four restrained clock pulses: tension, without adding a second music loop.
    for i, start in enumerate((0.04, 0.70, 1.36, 2.02)):
        hz = 1660 + i * 105
        stereo_add(b, chirp(0.075, hz * 1.13, hz, 0.032), start, 0.15, -0.09 + i * 0.06)
        stereo_add(b, glass_tone(0.17, hz, 0.048), start + 0.010, 0.075, -0.08 + i * 0.06)
    # A near-inaudible airy bed connects the four ticks but carries no low-mid weight.
    airy = band_noise(duration, 2800, 7200, 1003, 1.4) * np.linspace(0.30, 0.06, len(b))
    stereo_add(b, airy, 0, 0.018, 0.0)
    return finish(b)


def timer_last_call():
    duration = 1.58
    b = np.zeros((round(duration * SR), 2))
    for i, start in enumerate((0.02, 0.23, 0.43, 0.62, 0.80)):
        hz = 1180 + i * 145
        stereo_add(b, chirp(0.075, hz * 1.22, hz, 0.026), start, 0.19, -0.09 + 0.04 * i)
    stereo_add(b, chirp(0.56, 1060, 2220, 0.23, 0.10), 0.91, 0.20, 0.06)
    stereo_add(b, glass_tone(0.63, 1760, 0.18), 1.02, 0.19, -0.08)
    stereo_add(b, glass_tone(0.51, 2349.3, 0.13), 1.10, 0.14, 0.10)
    return finish(b)


def lock_reveal_spark():
    duration = 0.64
    b = np.zeros((round(duration * SR), 2))
    # Tiny mechanical release first, then glass/light reveal—appropriate for locks and ? layers.
    click = band_noise(0.075, 1300, 7500, 1004, 0.018)
    stereo_add(b, click, 0.00, 0.24, -0.02)
    stereo_add(b, chirp(0.11, 360, 255, 0.038), 0.006, 0.18, -0.06)
    for start, hz, pan in ((0.09, 1174.7, -0.12), (0.17, 1568.0, 0.02), (0.25, 2093.0, 0.12)):
        stereo_add(b, glass_tone(duration - start, hz, 0.12), start, 0.22, pan)
    return finish(b)


def final_serve():
    duration = 1.10
    b = np.zeros((round(duration * SR), 2))
    # A serving sweep, then a three-note payoff. It is intentionally brighter than a normal win.
    stereo_add(b, band_noise(0.34, 1200, 8200, 1005, 0.13), 0.00, 0.15, -0.28)
    stereo_add(b, chirp(0.22, 580, 930, 0.095), 0.07, 0.15, 0.10)
    for start, hz, gain, pan in ((0.24, 1046.5, 0.23, -0.10),
                                 (0.34, 1318.5, 0.21, 0.00),
                                 (0.45, 1760.0, 0.24, 0.10)):
        stereo_add(b, glass_tone(duration - start, hz, 0.20), start, gain, pan)
    return finish(b)


def logo_signature():
    duration = 1.52
    b = np.zeros((round(duration * SR), 2))
    # A contained logo identity: soft table hit, fizzed lift, then a clean glass chord.
    thump = chirp(0.19, 128, 78, 0.09, 0.08)
    stereo_add(b, thump, 0.00, 0.22, 0.0)
    fizz = band_noise(0.55, 2300, 9200, 1006, 0.24)
    fizz *= np.sin(np.linspace(0, math.pi, len(fizz)))
    stereo_add(b, fizz, 0.10, 0.08, 0.0)
    for start, hz, gain, pan in ((0.22, 783.99, 0.22, -0.12),
                                 (0.34, 1174.66, 0.20, 0.00),
                                 (0.46, 1567.98, 0.24, 0.12)):
        stereo_add(b, glass_tone(duration - start, hz, 0.30), start, gain, pan)
    stereo_add(b, chirp(0.44, 1240, 2100, 0.19, 0.07), 0.48, 0.13, 0.0)
    return finish(b)


ASSETS = {
    "BS_Chapter_CardTurn_01.wav": chapter_card_turn,
    "BS_TimedOrders_Pulse_01.wav": timed_orders_pulse,
    "BS_Timer_LastCall_01.wav": timer_last_call,
    "BS_LockReveal_Spark_01.wav": lock_reveal_spark,
    "BS_FinalServe_01.wav": final_serve,
    "BS_Logo_Signature_01.wav": logo_signature,
}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, make in ASSETS.items():
        audio = make()
        destination = OUT / name
        sf.write(destination, audio, SR, subtype="PCM_24")
        rendered, rate = sf.read(destination, always_2d=True)
        assert rate == SR and rendered.shape == audio.shape and np.isfinite(rendered).all()
        assert np.max(np.abs(rendered)) <= 10 ** (-2.9 / 20)
        assert np.max(np.abs(rendered[[0, -1]])) == 0
        print(f"{name}: {len(rendered)/rate:.2f}s, 48kHz stereo PCM24, "
              f"peak {20*np.log10(np.max(np.abs(rendered))):.1f} dBFS")


if __name__ == "__main__":
    main()
