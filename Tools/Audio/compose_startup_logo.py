#!/usr/bin/env python3
"""Render Bartender Sort's 3.30s startup signature from recorded CC0 instruments.

Requires numpy, scipy and soundfile. Does not modify the victory cue.
"""
from pathlib import Path
from fractions import Fraction
import hashlib
import json
import math

import numpy as np
import soundfile as sf
from scipy.signal import butter, fftconvolve, resample_poly, sosfilt

ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / 'AudioProduction/BartenderSortV2/samples'
OUT = ROOT / 'AudioPreviews/BartenderStartup'
SR = 48000
DURATION = 3.30
N = round(SR * DURATION)

HARP = {
    62: 'Strings/Harp/KSHarp_D4_mf.wav',
    69: 'Strings/Harp/KSHarp_A4_mf.wav',
    72: 'Strings/Harp/KSHarp_C5_mf.wav',
    76: 'Strings/Harp/KSHarp_E5_mf.wav',
    79: 'Strings/Harp/KSHarp_G5_mf.wav',
    83: 'Strings/Harp/KSHarp_B5_mf.wav',
    86: 'Strings/Harp/KSHarp_D6_mf.wav',
}
GLOCK = {84: 'Percussion/Glock/glock_medium_C6.wav'}
BASS = {26: 'Strings/Solo Contrabass/Pizz/BKCtbss_Pizz_D1_v1_rr2.wav'}
sources = {}
notes = []


def read_source(path):
    path = Path(path)
    if path not in sources:
        x, sr = sf.read(path, dtype='float64', always_2d=True)
        x = x[:, :2]
        if x.shape[1] == 1:
            x = np.repeat(x, 2, axis=1)
        if sr != SR:
            divisor = math.gcd(sr, SR)
            x = resample_poly(x, SR // divisor, sr // divisor, axis=0)
        env = np.sqrt(np.convolve(np.mean(x*x, axis=1), np.ones(120)/120, 'same'))
        onset = np.flatnonzero(env > env.max() * .04)
        if len(onset):
            x = x[max(0, onset[0] - round(.003 * SR)):]
        sources[path] = x
    return sources[path]


def add(bus, signal, time):
    start = round(time * SR)
    count = min(len(signal), len(bus)-start)
    if count > 0:
        bus[start:start+count] += signal[:count]


def note(bus, instrument, time, pitch, duration, gain, pan=0):
    bank = {'harp': HARP, 'glock': GLOCK, 'bass': BASS}[instrument]
    key = min(bank, key=lambda k: abs(k-pitch))
    ratio = Fraction(2 ** ((key-pitch)/12)).limit_denominator(1200)
    sound = resample_poly(read_source(CACHE/bank[key]), ratio.numerator,
                          ratio.denominator, axis=0)[:round(duration*SR)].copy()
    attack = min(len(sound), round(.003*SR))
    release = min(len(sound)//2, round(.10*SR))
    sound[:attack] *= np.sin(np.linspace(0, np.pi/2, attack))[:, None]**2
    sound[-release:] *= np.cos(np.linspace(0, np.pi/2, release))[:, None]**2
    # Balance real recorded attacks; do not turn the harp's decay into a sustained pad.
    rms = np.sqrt(np.mean(sound[:min(len(sound), round(.12*SR))]**2))
    sound *= gain/max(rms, 1e-8)
    sound = sosfilt(butter(2, 9000 if instrument != 'bass' else 1700,
                          fs=SR, btype='lowpass', output='sos'), sound, axis=0)
    mid = sound.mean(axis=1)
    side = (sound[:, 0]-sound[:, 1])*.20
    angle = (pan+1)*np.pi/4
    sound = np.column_stack((mid*np.cos(angle)+side, mid*np.sin(angle)-side))*np.sqrt(2)
    add(bus, sound, time)
    notes.append(dict(instrument=instrument, time=time, pitch=pitch,
                      duration=duration, gain=gain, pan=pan, sample=bank[key]))


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    win = ROOT/'Assets/Resources/Audio/SFX_WinScreen.ogg'
    win_hash = hashlib.sha256(win.read_bytes()).hexdigest()
    music = np.zeros((N, 2))
    foley = np.zeros((N, 2))

    # A small D-major pentatonic phrase follows the nine bouncing letters.
    for i, (pitch, gain) in enumerate(zip(
            [74, 78, 81, 83, 81, 78, 76, 78, 81],
            [.040, .026, .033, .025, .039, .026, .031, .026, .038])):
        note(music, 'harp', .10+i*.085, pitch, .44, gain, -.17+i*.0425)

    # SORT opens into a warm D6/9 voicing; the rebound receives one tiny bell accent.
    for i, pitch in enumerate([50, 62, 66, 69, 76]):
        note(music, 'harp', .72+i*.008, pitch, 1.12, .022 if i else .027, (i-2)*.07)
    note(music, 'bass', .73, 38, .67, .025)
    note(music, 'glock', .97, 86, .75, .012, .14)

    # A quiet answering pair leads into contact without masking the actual glass transient.
    note(music, 'harp', 1.27, 78, .36, .022, -.16)
    note(music, 'harp', 1.39, 81, .34, .024, .16)

    # The previously isolated CC0 glass recording contains no victory music in this time range.
    master, rate = sf.read(ROOT/'AudioPreviews/BartenderSortV3/'
                          'BartenderSort_Win_LogoOpening_v3_FinalCheers.wav', always_2d=True)
    assert rate == SR
    clink = master[round(4.65*SR):].copy()
    add(foley, clink*.80, 1.65)

    # A soft closing reply, leaving space for the menu's own music at 3.30 seconds.
    for i, pitch in enumerate([66, 69, 74, 78, 83]):
        note(music, 'harp', 2.05+i*.014, pitch, 1.02, .016 if i < 3 else .012, (i-2)*.055)

    # Short stereo room: low reflection level, no artificial phase widening in the dry signal.
    rng = np.random.default_rng(934112)
    count = round(.70*SR)
    t = np.arange(count)/SR
    wet = np.zeros_like(music)
    for ch in range(2):
        ir = rng.normal(0, 1, count)*np.exp(-t/.12)
        ir[:round(.021*SR)] = 0
        ir = sosfilt(butter(2, 6500, fs=SR, btype='lowpass', output='sos'), ir)
        ir /= max(1e-9, np.sqrt(np.sum(ir*ir)))
        wet[:, ch] = fftconvolve(music[:, ch], ir)[:N]
    mix = music + wet*.11 + foley
    mix = sosfilt(butter(2, 45, fs=SR, btype='highpass', output='sos'), mix, axis=0)
    # Audible on phone speakers while leaving transient headroom.
    active_rms = np.sqrt(np.mean(mix[:round(2.8*SR)]**2))
    mix *= min(3.0, .095/max(active_rms, 1e-9))
    peak = np.max(np.abs(resample_poly(mix, 4, 1, axis=0)))
    mix *= min(1., 10**(-2.5/20)/peak)
    fade = round(.36*SR)
    mix[-fade:] *= np.cos(np.linspace(0, np.pi/2, fade))[:, None]**2
    mix[-8:] = 0
    wav = OUT/'BartenderSort_Startup_Logo.wav'
    ogg = OUT/'BartenderSort_Startup_Logo.ogg'
    sf.write(wav, mix, SR, subtype='PCM_24')
    sf.write(ogg, mix, SR, format='OGG', subtype='VORBIS', compression_level=0.)
    measures = {}
    for path in [wav, ogg]:
        decoded, sr = sf.read(path, always_2d=True)
        true_peak = np.max(np.abs(resample_poly(decoded, 4, 1, axis=0)))
        assert decoded.shape == (N, 2) and sr == SR
        assert np.isfinite(decoded).all() and true_peak < .95
        assert np.max(np.abs(decoded[-8:])) < .0001
        measures[path.suffix] = dict(seconds=N/SR, true_peak_dbfs=float(20*np.log10(true_peak)),
            rms_dbfs=float(20*np.log10(np.sqrt(np.mean(decoded*decoded)))), bytes=path.stat().st_size)
    assert hashlib.sha256(win.read_bytes()).hexdigest() == win_hash
    manifest = dict(duration=DURATION, contact=1.65, notes=notes, measurements=measures,
        win_asset_unchanged_sha256=win_hash,
        source_license='CC0-1.0', instruments='https://github.com/sgossner/VSCO-2-CE',
        glass='https://freesound.org/people/olehenriksen/sounds/771254/',
        composition='Original startup phrase; no victory music used.',
        wav_sha256=hashlib.sha256(wav.read_bytes()).hexdigest(),
        ogg_sha256=hashlib.sha256(ogg.read_bytes()).hexdigest())
    (OUT/'render_manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    print(json.dumps(measures, indent=2))


if __name__ == '__main__':
    main()
