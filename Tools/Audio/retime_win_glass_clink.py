#!/usr/bin/env python3
"""I move the clink from 4.65 to 1.65 s without changing music samples, pitch or gain.
Needs numpy/soundfile; ffmpeg checks 4x peaks.
Install after checks: --install --backup-dir PATH.
"""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess

import numpy as np
import soundfile as sf


ROOT = Path(__file__).resolve().parents[2]
PRODUCTION = ROOT / 'AudioProduction/BartenderSortV3'
PREVIEWS = ROOT / 'AudioPreviews/BartenderSortV3'
SOURCE = PREVIEWS / 'BartenderSort_Win_LogoOpening_v3_FinalCheers.wav'
HISTORY = PRODUCTION / 'render_manifest.json'
ASSET = ROOT / 'Assets/Resources/Audio/SFX_WinScreen.ogg'
OUTPUT = PREVIEWS / 'BartenderSort_Win_LogoOpening_v3_EarlyCheers'
MANIFEST = PRODUCTION / 'toast_sync_manifest.json'
SOURCE_SHA256 = '2e57e200e3ddb224f4f436203a78d2c3c03ab66bead950a760081991835b2f9e'
SAMPLE_RATE = 48000
OLD_CONTACT = 4.65
CONTACT = 1.65
VISUAL_DURATION = 3.30


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def peak_dbfs(data, rate, ffmpeg):
    """I check 4x SWR-resampled peaks without saving files or playing audio."""
    result = subprocess.run(
        [ffmpeg, '-nostdin', '-v', 'error', '-f', 'f64le', '-ar', str(rate),
         '-ac', str(data.shape[1]), '-i', 'pipe:0',
         '-af', f'aresample={rate * 4}:resampler=swr',
         '-f', 'f64le', 'pipe:1'],
        input=np.asarray(data, dtype='<f8').tobytes(),
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    upsampled = np.frombuffer(result.stdout, dtype='<f8')
    return float(20 * np.log10(max(1e-15, np.max(np.abs(upsampled)))))


def backup_once(source, destination):
    if destination.exists():
        if sha256(destination) != sha256(source):
            raise ValueError(f'Refusing to overwrite a different backup: {destination}')
    else:
        shutil.copy2(source, destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ffmpeg', default=shutil.which('ffmpeg'))
    parser.add_argument('--install', action='store_true')
    parser.add_argument('--backup-dir', type=Path)
    args = parser.parse_args()
    if not args.ffmpeg:
        parser.error('Pass --ffmpeg with an existing ffmpeg executable.')
    if args.install and args.backup_dir is None:
        parser.error('--install requires --backup-dir.')

    history = json.loads(HISTORY.read_text())
    history_hash = sha256(HISTORY)
    if sha256(SOURCE) != SOURCE_SHA256:
        raise ValueError('The approved v3 WAV differs from the inspected master.')
    if history['final_glass_contact_seconds'] != OLD_CONTACT:
        raise ValueError('Historical v3 stem boundary changed.')
    current_asset_hash = sha256(ASSET)
    previous_sync = json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {}
    if current_asset_hash not in {
        history['output_ogg_sha256'], previous_sync.get('output_ogg_sha256')
    }:
        raise ValueError('Installed win cue changed independently; not overwriting it.')
    importer_hash = sha256(ASSET.with_suffix('.ogg.meta'))

    if args.install:
        args.backup_dir.mkdir(parents=True, exist_ok=True)
        asset_backup = args.backup_dir / 'SFX_WinScreen.before_early_clink.ogg'
        if current_asset_hash == history['output_ogg_sha256']:
            backup_once(ASSET, asset_backup)
        elif not asset_backup.exists() or sha256(asset_backup) != history['output_ogg_sha256']:
            raise ValueError('The original asset backup is required for a re-install.')
        backup_once(HISTORY, args.backup_dir / 'render_manifest.before_early_clink.json')
        backup_once(ASSET.with_suffix('.ogg.meta'),
                    args.backup_dir / 'SFX_WinScreen.before_early_clink.ogg.meta')

    original, rate = sf.read(SOURCE, dtype='float64', always_2d=True)
    if rate != SAMPLE_RATE or original.shape != (302400, 2):
        raise ValueError('Expected the approved 6.30-second 48 kHz stereo WAV.')
    split = round(OLD_CONTACT * rate)
    start = round(CONTACT * rate)
    music = original[:split].copy()
    clink = original[split:]
    end = start + len(clink)
    if end > len(music):
        raise ValueError('The shifted glass tail must fit within the musical bed.')
    output = music.copy()
    output[start:end] += clink
    if not np.isfinite(output).all() or np.max(np.abs(output)) >= 1:
        raise ValueError('The unchanged-gain mix would clip; inspect before proceeding.')

    # I keep new masters separate from the old v3 exports.
    sf.write(OUTPUT.with_suffix('.wav'), output, rate, subtype='PCM_24')
    sf.write(OUTPUT.with_suffix('.ogg'), output, rate,
             format='OGG', subtype='VORBIS', compression_level=0.0)
    wav, _ = sf.read(OUTPUT.with_suffix('.wav'), always_2d=True)
    assert np.array_equal(wav[:start], music[:start])
    assert np.array_equal(wav[end:], music[end:])
    assert np.array_equal(wav[start:end] - clink, music[start:end])
    assert np.max(np.abs(wav[-16:])) < 3e-6

    verification = {}
    for suffix in ('.wav', '.ogg'):
        decoded, decoded_rate = sf.read(OUTPUT.with_suffix(suffix), always_2d=True)
        assert decoded.shape == (223200, 2) and decoded_rate == rate
        assert np.isfinite(decoded).all()
        peak = peak_dbfs(decoded, rate, args.ffmpeg)
        if peak >= 0:
            raise ValueError(f'{suffix} has an intersample peak above full scale.')
        verification[suffix] = {
            'frames': len(decoded), 'sample_rate': decoded_rate,
            'seconds': len(decoded) / decoded_rate,
            'four_times_resampled_peak_dbfs': peak,
            'last_16_sample_peak': float(np.max(np.abs(decoded[-16:])))
        }

    transient = int(np.flatnonzero(np.max(np.abs(clink), axis=1)
                                  > 10 ** (-35 / 20))[0])
    manifest = {
        'source_master': str(SOURCE.relative_to(ROOT)),
        'source_master_sha256': SOURCE_SHA256,
        'historical_manifest_sha256': history_hash,
        'previous_installed_ogg_sha256': history['output_ogg_sha256'],
        'old_glass_contact_seconds': OLD_CONTACT,
        'glass_contact_seconds': CONTACT,
        'measured_glass_transient_seconds': CONTACT + transient / rate,
        'glass_tail_end_seconds': end / rate,
        'total_seconds': len(output) / rate,
        'visual_duration_seconds': VISUAL_DURATION,
        'music_bed_unchanged': True,
        'music_pitch_and_gain_unchanged': True,
        'glass_pitch_and_gain_unchanged': True,
        'wav_sample_identical_outside_glass_window': [[0, CONTACT], [end / rate, OLD_CONTACT]],
        'wav_music_residual_sample_identical_inside_glass_window': True,
        'ogg_note': 'Vorbis is lossy: sample identity is verified on the WAV master, not re-encoded OGG.',
        'trim_note': 'Only the vacated appended glass tail from 4.65 to 6.30 was removed; all musical samples remain.',
        'foley_credit_manifest': 'AudioProduction/BartenderSortV2/foley/final_cheers_manifest.json',
        'verification': verification,
        'output_wav': str(OUTPUT.with_suffix('.wav').relative_to(ROOT)),
        'output_wav_sha256': sha256(OUTPUT.with_suffix('.wav')),
        'output_ogg': str(OUTPUT.with_suffix('.ogg').relative_to(ROOT)),
        'output_ogg_sha256': sha256(OUTPUT.with_suffix('.ogg')),
        'installed_asset': str(ASSET.relative_to(ROOT)) if args.install else None,
        'backup_directory': str(args.backup_dir.resolve()) if args.install else None,
    }
    if args.install:
        shutil.copy2(OUTPUT.with_suffix('.ogg'), ASSET)
        assert sha256(ASSET) == manifest['output_ogg_sha256']
        assert sha256(ASSET.with_suffix('.ogg.meta')) == importer_hash
    assert sha256(HISTORY) == history_hash
    MANIFEST.write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
