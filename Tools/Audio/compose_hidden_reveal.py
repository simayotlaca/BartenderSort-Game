#!/usr/bin/env python3
"""Synthesize the hidden-liquid reveal: a soft bubble and three rising glass tones."""

from pathlib import Path
import math

import numpy as np
import soundfile as sf


PROJECT = Path(__file__).resolve().parents[2]
OUTPUT = PROJECT / "Assets/Resources/Audio/SFX_HiddenReveal.wav"
SR = 48_000
DURATION = 0.52


def smooth_edges(signal, attack=0.003, release=0.05):
    attack_count = min(len(signal), round(attack * SR))
    release_count = min(len(signal), round(release * SR))
    fade_in = np.sin(np.linspace(0, math.pi / 2, attack_count)) ** 2
    fade_out = np.cos(np.linspace(0, math.pi / 2, release_count)) ** 2
    if signal.ndim == 2:
        fade_in = fade_in[:, None]
        fade_out = fade_out[:, None]
    signal[:attack_count] *= fade_in
    signal[-release_count:] *= fade_out
    return signal


def bubble(duration, base):
    time = np.arange(round(duration * SR)) / SR
    frequency = base + 520 * np.exp(-time / 0.018)
    phase = 2 * math.pi * np.cumsum(frequency) / SR
    envelope = (1 - np.exp(-time / 0.0025)) * np.exp(-time / 0.028)
    signal = (np.sin(phase) + 0.12 * np.sin(2.03 * phase)) * envelope
    return smooth_edges(signal, release=0.025)


def glass_tone(frequency, duration):
    time = np.arange(round(duration * SR)) / SR
    signal = np.zeros_like(time)
    # Fast-decaying upper partials keep the onset sparkling and the tail gentle.
    for ratio, gain, decay in ((1, 1, 0.105), (2.01, 0.23, 0.065), (3.94, 0.055, 0.035)):
        signal += gain * np.sin(2 * math.pi * frequency * ratio * time) * np.exp(-time / decay)
    return smooth_edges(signal, attack=0.004, release=0.065)


def add(bus, signal, start, gain, pan):
    offset = round(start * SR)
    count = min(len(signal), len(bus) - offset)
    angle = (pan + 1) * math.pi / 4
    bus[offset:offset + count] += signal[:count, None] * gain * np.array([math.cos(angle), math.sin(angle)])


def main():
    bus = np.zeros((round(DURATION * SR), 2), dtype=np.float64)
    add(bus, bubble(0.13, 290), 0, 0.9, -0.06)
    add(bus, bubble(0.10, 510), 0.047, 0.25, 0.07)
    for start, frequency, gain, pan in ((0.045, 880, 0.37, -0.12),
                                       (0.12, 1108.73, 0.33, 0.0),
                                       (0.20, 1318.51, 0.37, 0.12)):
        add(bus, glass_tone(frequency, DURATION - start), start, gain, pan)

    smooth_edges(bus, attack=0.002, release=0.06)
    bus *= 10 ** (-6 / 20) / np.max(np.abs(bus))
    sf.write(OUTPUT, bus, SR, subtype="PCM_16")

    rendered, rate = sf.read(OUTPUT, always_2d=True)
    assert rendered.shape == (round(DURATION * SR), 2)
    assert np.isfinite(rendered).all()
    assert np.max(np.abs(rendered)) < 0.51
    assert np.max(np.abs(rendered[[0, -1]])) == 0
    print(f"{OUTPUT.name}: {len(rendered) / rate:.2f}s, {rate}Hz stereo PCM16, "
          f"peak {20 * np.log10(np.max(np.abs(rendered))):.2f} dBFS, "
          f"RMS {20 * np.log10(np.sqrt(np.mean(rendered ** 2))):.2f} dBFS")


if __name__ == "__main__":
    main()
