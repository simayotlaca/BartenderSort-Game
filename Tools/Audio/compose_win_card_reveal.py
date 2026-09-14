#!/usr/bin/env python3
"""Result-card reveal after the Cheers toast: a mallet run up the pentatonic, a
held top note and a small character lift.

Chosen on 10 September 2026 over the orchestral jingle that shipped before it; the
previous take is kept at AudioProduction/WinCardReveal/. Voiced to match the daily
reward hint, so the two cues sound like the same toy.
"""
import importlib.util
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfilt

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("hint", HERE / "compose_daily_reward_hint.py")
hint = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hint)

SR = hint.SR
DURATION = 2.30
OUTPUT = HERE.parents[1] / "Assets/Resources/Audio/SFX_WinCardReveal.wav"


def bed(bus, midis, start, gain, spread=0.055):
    """A quiet harp chord rolled by hand, holding the tune up from underneath."""
    for index, midi in enumerate(midis):
        note = hint.harp(midi, DURATION - start - index * spread, attack=0.012)
        hint.add(bus, note, start + index * spread, gain, -0.10 + 0.10 * index)


def compose():
    bus = np.zeros((round(DURATION * SR), 2))
    hint.add(bus, hint.swell(0.065, 0.14), 0.0, 0.50)
    hint.add(bus, hint.bubble(250, 220, 0.20, 0.010), 0.045, 0.30, -0.05)
    bed(bus, (62, 69, 74), 0.055, 0.16)
    for start, midi, gain, pan in ((0.095, 74, 0.38, -0.09), (0.190, 78, 0.34, -0.02),
                                   (0.280, 81, 0.38, 0.05), (0.400, 83, 0.36, -0.04),
                                   (0.520, 86, 0.46, 0.02)):
        hint.add(bus, hint.glock(midi, DURATION - start), start, gain, pan)
        hint.add(bus, hint.harp(midi, DURATION - start), start, gain * 0.38, pan)
    bed(bus, (74, 78, 81), 0.540, 0.20, spread=0.040)
    hint.add(bus, hint.boop(523, 698, 0.420, wobble=0.020, rate=5.2), 0.760, 0.40)
    hint.add(bus, hint.flute(81, 0.85), 0.560, 0.13, 0.05)
    hint.add(bus, hint.air(0.80, [2600, 6000], 0.020, seed=5101), 0.52, 0.5)
    return bus


def main():
    dry = compose()
    bus = hint.narrow(dry + hint.reverb(dry, tail=0.75) * 0.22 * np.max(np.abs(dry)))
    bus = sosfilt(butter(1, 55, "highpass", fs=SR, output="sos"), bus, axis=0)
    # Shaping last keeps the filter's own ringing from re-opening the tail.
    bus = hint.shape(bus, 0.0015, 0.14)
    bus *= 10 ** (-4.0 / 20) / np.max(np.abs(bus))
    sf.write(OUTPUT, bus, SR, subtype="PCM_16")

    rendered, rate = sf.read(OUTPUT, always_2d=True)
    mono = rendered.mean(axis=1)
    spectrum = np.abs(np.fft.rfft(mono * np.hanning(len(mono))))
    freqs = np.fft.rfftfreq(len(mono), 1 / rate)
    assert rendered.shape == (round(DURATION * SR), 2) and np.isfinite(rendered).all()
    assert np.max(np.abs(rendered)) < 0.68
    assert np.max(np.abs(rendered[[0, -1]])) == 0
    print(f"{OUTPUT.name}: {len(rendered) / rate:.2f}s  centre "
          f"{(spectrum * freqs).sum() / spectrum.sum():.0f} Hz  "
          f"peak {20 * np.log10(np.max(np.abs(rendered))):.1f} dBFS")


if __name__ == "__main__":
    main()
