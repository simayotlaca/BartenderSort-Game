#!/usr/bin/env python3
"""Give only the opening a cocktail-lounge feel; preserve the clink and ending exactly."""
from pathlib import Path
from fractions import Fraction
import hashlib
import json

import numpy as np
import soundfile as sf
from scipy.signal import butter, fftconvolve, resample_poly, sosfilt

import compose_startup_logo as base

ROOT, SR, OUT = base.ROOT, base.SR, base.OUT
BANK = ROOT/'AudioProduction/BartenderStartup/samples/Rhodes'
SAMPLES = {50:'A_050__D3_4.flac', 55:'A_055__G3_4.flac',
           62:'A_062__D4_4.flac', 71:'A_071__B4_4.flac', 76:'A_076__E5_4.flac'}


def main():
    source = OUT/'BartenderSort_Startup_Logo.wav'
    old, sr = sf.read(source, always_2d=True)
    assert sr == SR and old.shape == (round(3.3*SR), 2)
    opening = np.zeros_like(old)
    events = []

    def rhodes(time, pitches, duration, gain):
        for index, pitch in enumerate(pitches):
            root = min(SAMPLES, key=lambda k:abs(k-pitch))
            ratio = Fraction(2**((root-pitch)/12)).limit_denominator(1200)
            x = resample_poly(base.read_source(BANK/SAMPLES[root]),
                             ratio.numerator, ratio.denominator, axis=0)[:round(duration*SR)].copy()
            x[:round(.003*SR)] *= np.sin(np.linspace(0, np.pi/2, round(.003*SR)))[:,None]**2
            x[-round(.13*SR):] *= np.cos(np.linspace(0, np.pi/2, round(.13*SR)))[:,None]**2
            x = sosfilt(butter(2, 4600, fs=SR, btype='lowpass', output='sos'),x,axis=0)
            rms = np.sqrt(np.mean(x[:round(.14*SR)]**2))
            x *= gain/max(rms,1e-8)
            pan=(index/max(1,len(pitches)-1)-.5)*.25
            x[:,0]*=1-pan; x[:,1]*=1+pan
            base.add(opening,x,time+index*.006)
        events.append(dict(time=time,pitches=pitches,instrument='Recorded Rhodes Mark I',gain=gain))

    # Dmaj9, a small offbeat reply, and D6/9 under SORT. Midrange voicings feel intimate.
    rhodes(.10,[54,57,61,64,69],.57,.031)
    rhodes(.43,[69,73],.42,.035)
    rhodes(.72,[54,59,61,64,69],.70,.037)
    base.note(opening,'bass',.10,38,.33,.040,-.04)
    base.note(opening,'bass',.44,45,.25,.028,.04)
    base.note(opening,'bass',.72,38,.47,.046)

    # Dry, soft shaker gestures rather than a long noisy sweep.
    rng=np.random.default_rng(272741)
    for time,level,duration in [(.065,.017,.115),(.28,.011,.08),(.48,.015,.11),(.665,.014,.11)]:
        n=round(duration*SR)
        x=rng.normal(0,1,n)
        x=sosfilt(butter(2,[2100,7500],fs=SR,btype='bandpass',output='sos'),x)
        x*=np.sin(np.linspace(0,np.pi,n))**1.6
        x*=level/max(1e-8,np.sqrt(np.mean(x*x)))
        base.add(opening,np.column_stack((x*.94,x*1.06)),time)

    # Sparse early room reflections retain the acoustic attack and stay mono-compatible.
    dry=opening.copy()
    for delay,gain in [(.033,.08),(.061,.055),(.099,.035)]:
        at=round(delay*SR)
        opening[at:]+=dry[:-at]*gain
    opening=sosfilt(butter(2,45,fs=SR,btype='highpass',output='sos'),opening,axis=0)
    rms=np.sqrt(np.mean(opening[:round(1.10*SR)]**2))
    opening*=.078/max(rms,1e-8)
    peak=np.max(np.abs(resample_poly(opening,4,1,axis=0)))
    opening*=min(1.,.56/peak)

    start,end=round(1.10*SR),round(1.27*SR)
    mix=old.copy()
    mix[:start]=opening[:start]
    u=np.linspace(0,1,end-start)[:,None]
    blend=u*u*(3-2*u)
    mix[start:end]=opening[start:end]*(1-blend)+old[start:end]*blend
    wav=OUT/'BartenderSort_Startup_Bar_v2.wav'
    ogg=wav.with_suffix('.ogg')
    sf.write(wav,mix,SR,subtype='PCM_24')
    sf.write(ogg,mix,SR,format='OGG',subtype='VORBIS',compression_level=0.)
    rendered,_=sf.read(wav,always_2d=True)
    assert np.array_equal(rendered[end:],old[end:]),'Clink or ending changed.'
    measurements={}
    for path in [wav,ogg]:
        x,rate=sf.read(path,always_2d=True)
        peak=np.max(np.abs(resample_poly(x,4,1,axis=0)))
        assert np.isfinite(x).all() and peak<.95 and x.shape==old.shape and rate==SR
        assert np.max(np.abs(x[-8:]))<.0001
        measurements[path.suffix]=dict(seconds=len(x)/rate,true_peak_dbfs=float(20*np.log10(peak)),
                                      rms_dbfs=float(20*np.log10(np.sqrt(np.mean(x*x)))))
    manifest=dict(version='Bar v2',events=events,measurements=measurements,
        changed_interval=[0,1.27],clink_and_ending_sample_identical=True,contact=1.65,
        rhodes_source='https://github.com/sfzinstruments/jlearman.jRhodes3d',
        rhodes_license='Musical works: CC0. Raw samples: CC BY-NC; raw samples are outside Assets and are not bundled in the game.',
        other_sources='See render_manifest.json for bass and glass credits.',
        ogg_sha256=hashlib.sha256(ogg.read_bytes()).hexdigest())
    (OUT/'bar_v2_manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    installed=ROOT/'Assets/Resources/Audio/SFX_StartupLogo.ogg'
    allowed={json.loads((OUT/'render_manifest.json').read_text())['ogg_sha256'],manifest['ogg_sha256']}
    assert hashlib.sha256(installed.read_bytes()).hexdigest() in allowed, 'Startup asset changed independently.'
    installed.write_bytes(ogg.read_bytes())
    print(json.dumps(measurements,indent=2))
    print('Installed bar opening; audio from 1.27 s onward preserved in WAV.')


if __name__=='__main__':
    main()
