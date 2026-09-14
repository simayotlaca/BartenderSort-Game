#!/usr/bin/env python3
"""I append a recorded glass toast to the orchestral v2 master."""
from pathlib import Path
import json
import math

import numpy as np
import soundfile as sf
from scipy.signal import butter, resample_poly, sosfilt


PROJECT = Path(__file__).resolve().parents[2]
PREVIEWS = PROJECT / 'AudioPreviews/BartenderSortV2'
SOURCE = PROJECT / 'AudioProduction/BartenderSortV2/foley/wine_glasses_clinking_cc0.mp3'
MUSIC = PREVIEWS / 'BartenderSort_Win_Orchestral_v2.wav'
OUTPUT = PREVIEWS / 'BartenderSort_Win_Orchestral_v2_FinalCheers'


def main():
    music, sr = sf.read(MUSIC, dtype='float64', always_2d=True)
    source, source_sr = sf.read(SOURCE, dtype='float64', always_2d=True)
    if source_sr != sr:
        divisor = math.gcd(source_sr, sr)
        source = resample_poly(source, sr // divisor, source_sr // divisor, axis=0)
    source = sosfilt(butter(2, 140, fs=sr, btype='highpass', output='sos'), source, axis=0)

    # I trim leading silence but keep the short natural onset.
    envelope = np.max(np.abs(source), axis=1)
    first = np.flatnonzero(envelope > np.max(envelope) * .025)[0]
    start = max(0, first - round(.0025 * sr))
    clink = source[start:start + round(1.65 * sr)].copy()
    clink *= 10 ** (-7.0 / 20) / np.max(np.abs(clink))
    attack = round(.0015 * sr)
    clink[:attack] *= np.sin(np.linspace(0, np.pi/2, attack))[:, None] ** 2
    fade = round(.28 * sr)
    clink[-fade:] *= np.cos(np.linspace(0, np.pi/2, fade))[:, None] ** 2

    # I leave music samples unchanged and start the toast at the original end.
    mixed = np.concatenate((music, clink), axis=0)
    sf.write(OUTPUT.with_suffix('.wav'), mixed, sr, subtype='PCM_24')
    sf.write(OUTPUT.with_suffix('.ogg'), mixed, sr, format='OGG', subtype='VORBIS', compression_level=0.0)
    sf.write(PREVIEWS / 'Glass_Cheers_Final_Only.wav', clink, sr, subtype='PCM_24')

    rendered, _ = sf.read(OUTPUT.with_suffix('.wav'), always_2d=True)
    assert np.array_equal(rendered[:len(music)], music)
    assert np.max(np.abs(rendered[-8:])) < 2e-6
    for extension in ('.wav', '.ogg'):
        data, rate = sf.read(OUTPUT.with_suffix(extension), always_2d=True)
        peak = np.max(np.abs(resample_poly(data, 4, 1, axis=0)))
        assert np.isfinite(data).all() and peak < 1
        print(extension, 'duration', len(data)/rate, 'true_peak_db', round(float(20*np.log10(peak)), 2))
    manifest = {
        'music': str(MUSIC.relative_to(PROJECT)),
        'music_unchanged': True,
        'glass_contact_seconds': len(music)/sr,
        'duration_seconds': len(mixed)/sr,
        'foley_title': 'Two Small Wine Glasses Clinking Sound',
        'foley_author': 'olehenriksen',
        'foley_license': 'CC0-1.0',
        'foley_source': 'https://freesound.org/people/olehenriksen/sounds/771254/',
        'foley_download': 'https://cdn.freesound.org/previews/771/771254_3316599-hq.mp3',
        'foley_source_offset_seconds': start/sr,
    }
    (SOURCE.parent / 'final_cheers_manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print('Glass contact:', len(music)/sr, 's; original music verified unchanged.')


if __name__ == '__main__':
    main()
