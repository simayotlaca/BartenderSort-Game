#!/usr/bin/env python3
"""I replace the win cue's opening with a short logo phrase. The WAV stays sample-identical from 0.98 s onward, including the melody and glass toast."""
from pathlib import Path
import concurrent.futures
import hashlib
import json

import numpy as np
import soundfile as sf
from pedalboard import Compressor, HighpassFilter, LowpassFilter, Pedalboard, Reverb
from scipy.signal import resample_poly

import compose_orchestral_win_v2 as orchestra


ROOT = Path(__file__).resolve().parents[2]
CACHE = ROOT / 'AudioProduction/BartenderSortV2/samples'
PRODUCTION = ROOT / 'AudioProduction/BartenderSortV3'
PREVIEWS = ROOT / 'AudioPreviews/BartenderSortV3'
APPROVED = ROOT / 'AudioPreviews/BartenderSortV2/BartenderSort_Win_Orchestral_v2_FinalCheers.wav'
SR = orchestra.SR
JOIN_START = .85
JOIN_END = .98
HIT = .55


def opening_score():
    events = []

    def note(instrument, time, pitch, duration, velocity, level=1., pan=0.):
        events.append(orchestra.Note(instrument, time, pitch, duration, velocity, level, pan))

    def chord(instrument, time, pitches, duration, velocity, level):
        for index, pitch in enumerate(pitches):
            note(instrument, time + index * .002, pitch, duration,
                 velocity - index, level)

    # Two bow strokes with a small gap; the second is stronger.
    note('strings', .045, 69, .070, 74, 1.10, -.06)
    note('cello', .050, 57, .065, 71, .55)
    note('strings', .180, 73, .075, 86, 1.16, .05)
    note('cello', .185, 57, .065, 78, .44)

    # The title lands on D major with A5 on top.
    chord('horn', HIT, [62, 66, 69], .210, 105, .75)
    chord('strings_long', HIT - .020, [74, 78, 81], .210, 100, .50)
    chord('strings', HIT + .003, [74, 78, 81], .160, 105, .64)
    chord('cello', HIT + .003, [50, 57], .215, 102, .61)
    note('bass', HIT, 38, .270, 100, .72)
    chord('harp', HIT + .008, [74, 78, 81], .220, 94, .28)
    note('flute', HIT + .009, 81, .220, 96, .38)
    return events


def make_opening():
    events = opening_score()
    instruments = sorted({event.instrument for event in events})
    region_maps = {
        name: orchestra.parse_sfz(orchestra.fetch_text(orchestra.SETTINGS[name][0]))
        for name in instruments
    }
    regions = [orchestra.select_region(region_maps[event.instrument], event, index)
               for index, event in enumerate(events)]
    paths = sorted({region['path'] for region in regions})
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(lambda path: orchestra.download_sample(path, CACHE), paths))
    recordings = {path: orchestra.sample_audio(CACHE / path) for path in paths}
    buses = {name: np.zeros((round(1.65 * SR), 2)) for name in instruments}
    for event, region in zip(events, regions):
        sound = orchestra.shaped_note(recordings[region['path']], event, region['pitch_keycenter'])
        orchestra.add(buses[event.instrument], sound, event.time)

    dry = sum(buses.values())
    send = sum(bus * orchestra.SETTINGS[name][3] for name, bus in buses.items())
    room = Pedalboard([
        Reverb(room_size=.51, damping=.59, wet_level=1., dry_level=0., width=.86),
        HighpassFilter(cutoff_frequency_hz=210),
        LowpassFilter(cutoff_frequency_hz=7700),
    ])
    opening = dry + room(send.T.astype(np.float32), SR).T * .72
    # I process only the new opening and leave the rest untouched.
    hit_region = opening[round(HIT * SR):round(.81 * SR)]
    opening *= .145 / max(1e-9, np.sqrt(np.mean(hit_region ** 2)))
    opening = Pedalboard([
        HighpassFilter(cutoff_frequency_hz=38),
        Compressor(threshold_db=-13.5, ratio=1.55, attack_ms=13, release_ms=110),
    ])(opening.T.astype(np.float32), SR).T.astype(np.float64)
    true_peak = np.max(np.abs(resample_poly(opening, 4, 1, axis=0)))
    opening *= min(1., 10 ** (-4.0 / 20) / true_peak)
    return opening, events, paths


def main():
    approved, sample_rate = sf.read(APPROVED, always_2d=True, dtype='float64')
    if sample_rate != SR or approved.shape != (302400, 2):
        raise ValueError('The approved 6.3-second stereo master has changed.')
    opening, events, paths = make_opening()
    output = approved.copy()
    first = round(JOIN_START * SR)
    last = round(JOIN_END * SR)
    output[:first] = opening[:first]
    # I use complementary zero-slope fades to avoid a loudness bump from matching tails.
    blend = np.linspace(0., 1., last - first)
    blend = (blend ** 2 * (3. - 2. * blend))[:, None]
    output[first:last] = opening[first:last] * (1. - blend) + approved[first:last] * blend

    PREVIEWS.mkdir(parents=True, exist_ok=True)
    PRODUCTION.mkdir(parents=True, exist_ok=True)
    base = PREVIEWS / 'BartenderSort_Win_LogoOpening_v3_FinalCheers'
    sf.write(base.with_suffix('.wav'), output, SR, subtype='PCM_24')
    sf.write(base.with_suffix('.ogg'), output, SR, format='OGG', subtype='VORBIS', compression_level=0.)
    wav, _ = sf.read(base.with_suffix('.wav'), always_2d=True)
    assert np.array_equal(wav[last:], approved[last:]), 'Approved remainder changed.'
    assert not np.array_equal(wav[:first], approved[:first]), 'Opening did not change.'

    measurements = {}
    for ext in ('.wav', '.ogg'):
        decoded, rate = sf.read(base.with_suffix(ext), always_2d=True)
        true_peak = np.max(np.abs(resample_poly(decoded, 4, 1, axis=0)))
        assert len(decoded) == len(approved) and rate == SR
        assert np.isfinite(decoded).all() and true_peak < .94
        assert np.max(np.abs(decoded[-16:])) < 3e-6
        measurements[ext] = {'seconds': len(decoded)/rate,
                             'true_peak_dbfs': float(20*np.log10(true_peak))}
        print(ext, measurements[ext], flush=True)
    for start, end in [(.045,.16),(.18,.30),(.34,.50),(.55,.81),(.85,.98),(2.50,2.80)]:
        section = wav[round(start*SR):round(end*SR)]
        print(f'{start:.3f}-{end:.3f}s RMS {20*np.log10(np.sqrt(np.mean(section**2))+1e-12):.2f} dBFS')

    manifest = {
        'source_master': str(APPROVED.relative_to(ROOT)),
        'source_master_sha256': hashlib.sha256(APPROVED.read_bytes()).hexdigest(),
        'opening_hits_seconds': [.045, .180, HIT],
        'crossfade_seconds': [JOIN_START, JOIN_END],
        'sample_identical_wav_from_seconds': JOIN_END,
        'final_glass_contact_seconds': 4.65,
        'total_seconds': 6.3,
        'instrument_source': 'VSCO 2 CE, CC0-1.0',
        'samples': paths,
        'notes': [event.__dict__ for event in events],
        'verification': measurements,
        'output_ogg_sha256': hashlib.sha256(base.with_suffix('.ogg').read_bytes()).hexdigest(),
    }
    (PRODUCTION / 'render_manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('WAV remainder and final glass toast verified sample-identical from 0.98 seconds.')
    print(base)


if __name__ == '__main__':
    main()
