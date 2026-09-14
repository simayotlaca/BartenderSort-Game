#!/usr/bin/env python3
"""Render a playful match-3 signature with a warm neon-bar colour, not a jazz phrase.

Original synthesized mallets, plucks, bass and tiny ice ticks; the existing isolated
CC0 glass recording remains aligned with contact. Requires numpy, scipy, soundfile.
"""
from pathlib import Path
import hashlib
import json

import numpy as np
import soundfile as sf
from scipy.signal import butter, fftconvolve, resample_poly, sosfilt

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'AudioPreviews/BartenderStartup'
SR, LENGTH = 48000, 3.30
N = round(SR*LENGTH)
RNG = np.random.default_rng(583209)
EVENTS = []


def add(bus, sound, time, pan=0.):
    if sound.ndim == 1:
        angle=(pan+1)*np.pi/4
        sound=np.column_stack((sound*np.cos(angle),sound*np.sin(angle)))*np.sqrt(2)
    start=round(time*SR)
    count=min(len(sound),len(bus)-start)
    if count>0:
        bus[start:start+count]+=sound[:count]


def frequency(note):
    return 440*2**((note-69)/12)


def envelope(t, attack, decay, release=.05):
    env=(1-np.exp(-t/attack))*np.exp(-t/decay)
    n=min(len(t),round(release*SR))
    env[-n:]*=np.cos(np.linspace(0,np.pi/2,n))**2
    return env


def mallet(note, length=.45, level=.10):
    """Rounded woody body with a short glassy overtone and soft pitch settling."""
    t=np.arange(round(length*SR))/SR
    f=frequency(note)
    # Mild FM at the attack supplies the pop; its fast decay leaves a clean pitched body.
    phase=2*np.pi*f*t + .028*(1-np.exp(-t/.025))
    index=.64*np.exp(-t/.035)
    body=np.sin(phase+index*np.sin(2*phase))*envelope(t,.0018,.14)
    upper=.16*np.sin(2.003*phase)*envelope(t,.0007,.038)
    tine=.065*np.sin(3.97*phase)*envelope(t,.0006,.025)
    return (body+upper+tine)*level


def plush_pluck(note, length, level):
    t=np.arange(round(length*SR))/SR
    f=frequency(note)
    sound=np.zeros_like(t)
    for harmonic,weight,decay in [(1,1.,.23),(2,.23,.12),(3,.09,.065),(4,.025,.035)]:
        sound+=weight*np.sin(2*np.pi*f*harmonic*t)*envelope(t,.003,decay)
    return sound*level


def round_bass(note, level=.13, length=.23):
    t=np.arange(round(length*SR))/SR
    f=frequency(note)
    phase=2*np.pi*f*(t+.0004*(1-np.exp(-t/.018)))
    # The second harmonic makes the bass audible on small phone speakers.
    sound=np.sin(phase)+.36*np.sin(2*phase)+.09*np.sin(3*phase)
    return sound*envelope(t,.004,.082)*level


def ice_tick(length=.065, level=.025, pitch=2400):
    t=np.arange(round(length*SR))/SR
    sound=np.zeros_like(t)
    for ratio,weight in [(1,1),(1.47,.55),(2.19,.18)]:
        sound+=np.sin(2*np.pi*pitch*ratio*t)*weight*np.exp(-t/(.014/ratio))
    return sound*(1-np.exp(-t/.0006))*level


def main():
    OUT.mkdir(parents=True,exist_ok=True)
    music=np.zeros((N,2))
    percussion=np.zeros_like(music)
    glass=np.zeros_like(music)

    # Two tiny pick-ups, then a memorable five-note motif. Light secondary taps follow letters.
    melody=[(.10,78,.12,.44,-.13),(.27,81,.11,.42,-.07),
            (.44,83,.12,.40,.06),(.61,81,.095,.39,.12),
            (.72,86,.115,.68,.04),(.97,81,.065,.34,.12),
            (1.10,78,.073,.32,-.08),(1.27,81,.062,.28,-.12),
            (1.40,78,.055,.24,.12),(2.10,81,.064,.38,-.09),
            (2.29,86,.072,.78,.08)]
    for time,note,level,length,pan in melody:
        add(music,mallet(note,length,level),time,pan)
        EVENTS.append(dict(kind='candy_mallet',time=time,midi=note,level=level))

    # Compact major-sixth voicings support SORT and the toast without a victory fanfare.
    for time,pitches,level,length in [(.10,[62,66,69],.019,.40),
            (.72,[62,66,69,74],.024,.60),
            (1.65,[62,66,69,71],.022,.75),
            (2.29,[66,69,74],.012,.86)]:
        for i,note in enumerate(pitches):
            add(music,plush_pluck(note,length,level),time+i*.004,(i-1.5)*.075)
        EVENTS.append(dict(kind='soft_chord',time=time,midi=pitches))

    for time,note,level in [(.10,50,.10),(.44,45,.073),(.72,50,.12),
                             (1.10,54,.065),(1.65,50,.09)]:
        add(music,round_bass(note,level),time)

    for i,time in enumerate([.185,.355,.525,.865]):
        add(percussion,ice_tick(level=.011 if i%2 else .016,pitch=2200+i*230),
            time,-.22 if i%2 else .22)
    for time,level in [(.255,.006),(.585,.009),(.93,.006),(1.36,.005)]:
        count=round(.07*SR)
        noise=RNG.normal(0,1,count)
        noise=sosfilt(butter(2,[3400,8200],btype='bandpass',fs=SR,output='sos'),noise)
        noise*=np.sin(np.linspace(0,np.pi,count))**2
        add(percussion,noise*level,time)

    # Real glass, at the established 1.65s animation contact. Keep room for its attack.
    raw,rate=sf.read(ROOT/'AudioPreviews/BartenderSortV3/'
                    'BartenderSort_Win_LogoOpening_v3_FinalCheers.wav',always_2d=True)
    assert rate==SR
    clink=raw[round(4.65*SR):].copy()
    clink*=.22/max(1e-9,np.max(np.abs(clink)))
    add(glass,clink,1.65)
    transient=np.flatnonzero(np.max(np.abs(clink),axis=1)>.015)[0]/SR+1.65

    # Short dark stereo room, plus a very quiet dotted reply from the high mallet line.
    wet=np.zeros_like(music)
    count=round(.54*SR)
    t=np.arange(count)/SR
    for channel in range(2):
        ir=RNG.normal(0,1,count)*np.exp(-t/.082)
        ir[:round(.025*SR)]=0
        ir=sosfilt(butter(2,5800,btype='lowpass',fs=SR,output='sos'),ir)
        ir/=max(1e-9,np.sqrt(np.sum(ir*ir)))
        wet[:,channel]=fftconvolve(music[:,channel],ir)[:N]*.105
    mix=music+percussion+wet+glass
    mix=sosfilt(butter(2,48,btype='highpass',fs=SR,output='sos'),mix,axis=0)
    # Bring the musical motif forward instead of letting a lone glass peak set its loudness.
    rms=np.sqrt(np.mean(mix[:round(2.75*SR)]**2))
    mix*=.115/max(1e-9,rms)
    peak=np.max(np.abs(resample_poly(mix,4,1,axis=0)))
    mix*=min(1.,10**(-2/20)/peak)
    fade=round(.31*SR)
    mix[-fade:]*=np.cos(np.linspace(0,np.pi/2,fade))[:,None]**2
    mix[-8:]=0
    stem=OUT/'BartenderSort_Startup_NeonMatch3_v3'
    sf.write(stem.with_suffix('.wav'),mix,SR,subtype='PCM_24')
    sf.write(stem.with_suffix('.ogg'),mix,SR,format='OGG',subtype='VORBIS',compression_level=0.)
    measurements={}
    for suffix in ['.wav','.ogg']:
        path=stem.with_suffix(suffix)
        x,sr=sf.read(path,always_2d=True)
        peak=np.max(np.abs(resample_poly(x,4,1,axis=0)))
        assert x.shape==(N,2) and sr==SR and np.isfinite(x).all() and peak<.95
        assert np.max(np.abs(x[-8:]))<.0001
        mono=x.mean(axis=1)
        measurements[suffix]=dict(seconds=N/SR,true_peak_dbfs=float(20*np.log10(peak)),
            rms_dbfs=float(20*np.log10(np.sqrt(np.mean(x*x)))),
            mono_rms_dbfs=float(20*np.log10(np.sqrt(np.mean(mono*mono)))),bytes=path.stat().st_size)
    prior=json.loads((OUT/'render_manifest.json').read_text())
    win=ROOT/'Assets/Resources/Audio/SFX_WinScreen.ogg'
    assert hashlib.sha256(win.read_bytes()).hexdigest()==prior['win_asset_unchanged_sha256']
    installed=ROOT/'Assets/Resources/Audio/SFX_StartupLogo.ogg'
    new_hash=hashlib.sha256(stem.with_suffix('.ogg').read_bytes()).hexdigest()
    allowed={prior['ogg_sha256'],json.loads((OUT/'bar_v2_manifest.json').read_text())['ogg_sha256'],new_hash}
    assert hashlib.sha256(installed.read_bytes()).hexdigest() in allowed,'Startup cue changed independently.'
    manifest=dict(version='Neon Match3 v3',events=EVENTS,measurements=measurements,
        contact_seconds=1.65,measured_glass_transient_seconds=transient,
        composition='Original playful synthesized mallet, rounded pluck and bass motif with tiny ice ticks.',
        glass_source='https://freesound.org/people/olehenriksen/sounds/771254/',glass_license='CC0-1.0',
        win_asset_unchanged_sha256=prior['win_asset_unchanged_sha256'],ogg_sha256=new_hash)
    (OUT/'neon_match3_v3_manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    installed.write_bytes(stem.with_suffix('.ogg').read_bytes())
    print(json.dumps(measurements,indent=2))
    print('Installed Neon Match3 v3; contact remains at 1.65 s; victory audio unchanged.')


if __name__=='__main__':
    main()
