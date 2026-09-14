#!/usr/bin/env python3
"""Fit the existing win/startup sound identities to the approved 2-second toast.

No global time stretch or pitch change. The win keeps lossless orchestral opening
and cadence samples; startup keeps its existing NeonMatch3 synth instruments and
motif. Both use one copy of the original isolated CC0 glass at 0.300 seconds.
Requires numpy, scipy and soundfile. Run with --install after rendering checks.
"""
from pathlib import Path
import argparse
import hashlib
import json
import shutil

import numpy as np
import soundfile as sf
from scipy.signal import butter, fftconvolve, resample_poly, sosfilt

import compose_match3_startup as neon


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'AudioProduction/CheersRefinedMigration'
BACKUP = ROOT / 'output/CheersRefinedMigration'
SOURCE = ROOT / 'AudioPreviews/BartenderSortV3/BartenderSort_Win_LogoOpening_v3_FinalCheers.wav'
SR, DURATION, CONTACT, FADE_START = 48000, 2.0, .30, 1.60
N = round(SR * DURATION)
SOURCE_SHA = '2e57e200e3ddb224f4f436203a78d2c3c03ab66bead950a760081991835b2f9e'
ORIGINAL_HASHES = {
    'SFX_WinScreen': '2d8852ad3e79494de124805b8b45d976139c3443fffacf270d410dc7c25fd49c',
    'SFX_StartupLogo': 'c844dc3060fc3371b27dceddd532b3caf2d950f9b1242ed88a2e0a762f47896a',
}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def db(value):
    return float(20 * np.log10(max(1e-15, float(value))))


def smooth(values):
    u = np.clip(values, 0., 1.)
    return u * u * (3. - 2. * u)


def add(bus, sound, time):
    first = round(time * SR)
    count = min(len(sound), len(bus) - first)
    if count > 0:
        bus[first:first + count] += sound[:count]


def win_music(source):
    # The original file's first 4.65 s contain only the musical bed. Its glass
    # is appended after that boundary, unlike the installed EarlyCheers mix.
    bed = source[:round(4.65 * SR)]
    opening = np.zeros((N, 2))
    opening[:round(.98 * SR)] = bed[:round(.98 * SR)]
    ending = np.zeros_like(opening)
    add(ending, bed[round(2.94 * SR):], .90)
    u = smooth((np.arange(N) / SR - .90) / .08)[:, None]
    return opening * (1. - u) + ending * u


def startup_music():
    # Same F#-A-B-A-D motif and rounded mallet/pluck voices as NeonMatch3 v3.
    # The two quiet pickups lead into glass contact; the melody answers at the
    # shared logo arrival, and the final D rings through the screen handoff.
    music = np.zeros((N, 2))
    percussion = np.zeros_like(music)
    events = []
    melody = [
        (.070, 78, .085, .24, -.13), (.180, 81, .075, .24, -.07),
        (.505, 83, .120, .40, .06), (.620, 81, .095, .39, .12),
        (.735, 86, .115, .60, .04), (.960, 81, .065, .30, .12),
        (1.090, 78, .055, .25, -.08), (1.240, 86, .072, .68, .08),
    ]
    for time, note, level, length, pan in melody:
        neon.add(music, neon.mallet(note, length, level), time, pan)
        events.append(dict(kind='candy_mallet', time=time, midi=note, level=level))
    for time, pitches, level, length in [
            (.070, [62, 66, 69], .013, .25),
            (.505, [62, 66, 69, 74], .024, .62),
            (1.240, [66, 69, 74], .013, .66)]:
        for i, note in enumerate(pitches):
            neon.add(music, neon.plush_pluck(note, length, level),
                     time + i * .004, (i - 1.5) * .075)
        events.append(dict(kind='soft_chord', time=time, midi=pitches))
    for time, note, level in [(.070, 50, .070), (.505, 50, .12), (1.240, 50, .070)]:
        neon.add(music, neon.round_bass(note, level), time)
    for i, time in enumerate([.425, .575, .840]):
        neon.add(percussion, neon.ice_tick(level=.008, pitch=2200 + i * 230),
                 time, -.22 if i % 2 else .22)

    # Keep the original short, dark stereo room and mono-compatible dry signal.
    rng = np.random.default_rng(583209)
    wet = np.zeros_like(music)
    count = round(.54 * SR)
    t = np.arange(count) / SR
    for channel in range(2):
        ir = rng.normal(0, 1, count) * np.exp(-t / .082)
        ir[:round(.025 * SR)] = 0
        ir = sosfilt(butter(2, 5800, btype='lowpass', fs=SR, output='sos'), ir)
        ir /= max(1e-9, np.sqrt(np.sum(ir * ir)))
        wet[:, channel] = fftconvolve(music[:, channel], ir)[:N] * .105
    music = music + percussion + wet
    music = sosfilt(butter(2, 48, btype='highpass', fs=SR, output='sos'), music, axis=0)
    active_rms = np.sqrt(np.mean(music[:round(1.55 * SR)] ** 2))
    music *= .095 / max(1e-9, active_rms)
    return music, events


def master(music, isolated, glass_gain):
    glass = np.zeros_like(music)
    add(glass, isolated * glass_gain, CONTACT)
    # Preserve the actual glass transient and its pitch. Only the late room tail
    # joins the visual fade, so there is no second embedded impact at 1.65 s.
    fade = np.cos(np.clip((np.arange(N) / SR - FADE_START)
                         / (DURATION - FADE_START), 0., 1.) * np.pi / 2.) ** 2
    fade[-16:] = 0
    music = music * fade[:, None]
    glass = glass * fade[:, None]
    mix = music + glass
    peak = np.max(np.abs(resample_poly(mix, 4, 1, axis=0)))
    gain = min(1., 10 ** (-2.2 / 20.) / max(1e-9, peak))
    return mix * gain, music * gain, glass * gain, gain


def measure(path):
    decoded, rate = sf.read(path, dtype='float64', always_2d=True)
    peak = float(np.max(np.abs(resample_poly(decoded, 4, 1, axis=0))))
    assert decoded.shape == (N, 2) and rate == SR, str(path)
    assert np.isfinite(decoded).all() and peak < .90, str(path)
    assert np.max(np.abs(decoded[-16:])) < .0001, str(path)
    return dict(seconds=len(decoded) / rate, sample_rate=rate, channels=2,
                sample_peak_dbfs=db(np.max(np.abs(decoded))),
                four_times_true_peak_dbfs=db(peak),
                rms_dbfs=db(np.sqrt(np.mean(decoded ** 2))),
                tail_last_16_peak=float(np.max(np.abs(decoded[-16:]))),
                bytes=path.stat().st_size, sha256=sha(path))


def decoded_contact(ogg, glass):
    # Correlate the actual decoded OGG transient against the isolated waveform;
    # a first-above-threshold measurement alone could mistake a musical note.
    decoded, _ = sf.read(ogg, always_2d=True)
    lead = round(CONTACT * SR)
    width = round(.060 * SR)
    reference = glass[lead:lead + width].mean(axis=1)
    search_start = round(.275 * SR)
    search_end = round(.325 * SR) + width
    search = decoded[search_start:search_end].mean(axis=1)
    scores = np.correlate(search, reference, mode='valid')
    offset = int(np.argmax(scores))
    aligned = search[offset:offset + width]
    correlation = float(np.dot(aligned, reference)
                        / max(1e-12, np.linalg.norm(aligned) * np.linalg.norm(reference)))
    measured_start = (search_start + offset) / SR
    assert abs(measured_start - CONTACT) <= .001, measured_start
    assert correlation > .80, correlation
    transient = np.flatnonzero(np.max(np.abs(glass), axis=1) > 10 ** (-35 / 20.))[0] / SR
    return dict(decoded_waveform_start_seconds=measured_start,
                isolated_transient_above_minus35_dbfs_seconds=float(transient),
                decoded_reference_correlation=correlation,
                glass_event_count=1, old_contact_event_removed=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--install', action='store_true')
    args = parser.parse_args()
    assert sha(SOURCE) == SOURCE_SHA, 'The isolated master changed independently.'
    source, rate = sf.read(SOURCE, dtype='float64', always_2d=True)
    assert source.shape == (302400, 2) and rate == SR
    isolated = source[round(4.65 * SR):].copy()
    assert np.flatnonzero(np.max(np.abs(isolated), axis=1) > 10 ** (-35 / 20.))[0] == 120
    OUT.mkdir(parents=True, exist_ok=True)
    previous = json.loads((OUT / 'manifest.json').read_text()) if (OUT / 'manifest.json').exists() else {}
    asset_hashes, meta_hashes = {}, {}
    for name, original_hash in ORIGINAL_HASHES.items():
        asset = ROOT / 'Assets/Resources/Audio' / (name + '.ogg')
        asset_hashes[name] = sha(asset)
        meta_hashes[name] = sha(asset.with_suffix('.ogg.meta'))
        allowed = {original_hash, previous.get('cues', {}).get(name, {}).get('ogg_sha256')}
        assert asset_hashes[name] in allowed, 'Installed asset changed independently: ' + name
        if args.install:
            BACKUP.mkdir(parents=True, exist_ok=True)
            backup = BACKUP / (name + '.before_refined.ogg')
            if backup.exists():
                assert sha(backup) == original_hash, 'Original backup differs: ' + name
            else:
                assert asset_hashes[name] == original_hash
                shutil.copy2(asset, backup)
            backup_meta = BACKUP / (name + '.before_refined.ogg.meta')
            if not backup_meta.exists():
                shutil.copy2(asset.with_suffix('.ogg.meta'), backup_meta)

    startup, events = startup_music()
    # Win retains the source foley's gain; startup's glass remains gentler than
    # victory while being easy to hear above the short mallet phrase.
    designs = {'SFX_WinScreen': (win_music(source), 1.),
               'SFX_StartupLogo': (startup, .36 / np.max(np.abs(isolated)))}
    cues = {}
    for name, (music, glass_gain) in designs.items():
        mix, music_stem, glass_stem, master_gain = master(music, isolated, glass_gain)
        stem = OUT / name
        sf.write(stem.with_suffix('.wav'), mix, SR, subtype='PCM_24')
        sf.write(stem.with_suffix('.ogg'), mix, SR, format='OGG', subtype='VORBIS', compression_level=0.)
        sf.write(OUT / (name + '_music.wav'), music_stem, SR, subtype='PCM_24')
        sf.write(OUT / (name + '_glass.wav'), glass_stem, SR, subtype='PCM_24')
        measures = {ext: measure(stem.with_suffix(ext)) for ext in ['.wav', '.ogg']}
        cues[name] = dict(measurements=measures, contact=decoded_contact(stem.with_suffix('.ogg'), glass_stem),
                          ogg_sha256=sha(stem.with_suffix('.ogg')), master_gain=master_gain,
                          source_glass_gain=glass_gain,
                          previous_installed_ogg_sha256=asset_hashes[name],
                          original_installed_ogg_sha256=ORIGINAL_HASHES[name])

    manifest = dict(duration_seconds=DURATION, contact_seconds=CONTACT,
                    fade_seconds=[FADE_START, DURATION], sample_rate=SR,
                    source_lossless_master=str(SOURCE.relative_to(ROOT)), source_sha256=SOURCE_SHA,
                    cue_edits={
                        'win': 'Original music 0–0.98 s; source cadence 2.94 s onward placed at 0.90 s, with 80 ms complementary smooth crossfade. Final screen fade starts 1.60 s.',
                        'startup': 'Existing NeonMatch3 v3 synth instruments and F#–A–B–A–D motif rephrased around contact and logo arrival; no global stretch.',
                    }, startup_events=events, cues=cues,
                    glass_credit_manifest='AudioProduction/BartenderSortV2/foley/final_cheers_manifest.json',
                    win_music_credit='VSCO 2 CE, CC0-1.0; existing authored recording.',
                    installed=args.install, backup_directory=str(BACKUP.relative_to(ROOT)),
                    notes='Lossless masters and isolated audit stems retained. OGG is lossy; decoded waveform timing and 4x true peaks checked. Both cues end at 2.00 s; late musical/room tails are shortened with a fade, not time-stretched.')
    # Write the manifest before install so an interrupted run remains inspectable.
    (OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    if args.install:
        for name in cues:
            asset = ROOT / 'Assets/Resources/Audio' / (name + '.ogg')
            assert sha(asset) == asset_hashes[name], 'Asset changed during render: ' + name
            shutil.copy2(OUT / (name + '.ogg'), asset)
            assert sha(asset) == cues[name]['ogg_sha256']
            assert sha(asset.with_suffix('.ogg.meta')) == meta_hashes[name]
    print(json.dumps({name: {'ogg': data['measurements']['.ogg'], 'contact': data['contact']}
                      for name, data in cues.items()}, indent=2))


if __name__ == '__main__':
    main()
