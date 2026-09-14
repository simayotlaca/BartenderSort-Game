#!/usr/bin/env python3
"""I write this short orchestral cue with selected VSCO 2 CE CC0 samples. I do not reuse project audio or reference recordings."""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import math
import re
import urllib.parse
import urllib.request
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path

import numpy as np
import soundfile as sf
from pedalboard import Compressor, HighpassFilter, Limiter, LowpassFilter, Pedalboard, Reverb
from scipy.signal import resample_poly, sosfilt, butter

SR = 48000
LENGTH = 4.65
N = round(SR * LENGTH)
RAW = 'https://raw.githubusercontent.com/sgossner/VSCO-2-CE/SFZ/'
RNG = np.random.default_rng(947103)


@dataclass(frozen=True)
class Note:
    instrument: str
    time: float
    pitch: int
    duration: float
    velocity: int
    level: float = 1.0
    pan: float = 0.0


SETTINGS = {
    'harp': ('Harp.sfz', .039, -.18, .19, 9000),
    'flute': ('FluteStac.sfz', .035, -.12, .22, 10500),
    'clarinet': ('ClarinetStac.sfz', .034, .12, .23, 9500),
    'strings': ('ViolinEnsSpic.sfz', .036, -.32, .24, 10500),
    'strings_long': ('ViolinEnsSusVib.sfz', .024, -.25, .28, 8500),
    'cello': ('CelloEnsSpic.sfz', .042, .22, .22, 8000),
    'bass': ('ContrabassPizz.sfz', .050, .04, .12, 4500),
    'horn': ('FHornStac.sfz', .044, -.09, .29, 8000),
    'trumpet': ('TrumpetStac.sfz', .041, .22, .24, 9200),
    'trumpet_long': ('TrumpetSus.sfz', .037, .22, .28, 9200),
    'glock': ('Glockenspiel.sfz', .019, .06, .24, 14000),
    'timpani': ('Timpani.sfz', .064, -.04, .23, 6800),
}


def score() -> list[Note]:
    notes = []
    def note(i, t, p, d, v=95, level=1., pan=0.):
        notes.append(Note(i, t, p, d, v, level, pan))
    def chord(i, t, pitches, d, v=90, level=1.):
        for ix, p in enumerate(pitches):
            note(i, t + ix * .0025, p, d, v - ix * 2, level)

    # A short pickup opens the logo, then two musical replies follow.
    motif = [
        (.05, 69, .080, 63), (.15, 73, .075, 75),
        (.25, 74, .140, 100), (.45, 78, .105, 84), (.60, 81, .255, 103),
        (1.00, 83, .135, 94), (1.20, 81, .105, 84), (1.35, 78, .245, 90),
        (1.70, 79, .115, 88), (1.90, 83, .105, 98),
        (2.05, 86, .090, 104), (2.20, 85, .070, 112),
    ]
    for index, (t, p, d, v) in enumerate(motif):
        note('flute', t, p, d, v, .96)
        note('strings', t+.008, p-12 if p>84 else p, d+.016, min(115,v+4), .73)
        note('harp', t+.002, p, d+.12, v, .48 if t<1.7 else .30)
        if index in (2, 4, 5, 7):
            note('clarinet', t+.004, p-12, d+.02, v, .55)

    # I use D -> G -> A7 -> D with soft low instruments.
    chord('cello', .25, [50,57], .27, 95, .63)
    chord('horn', .25, [62,66,69], .23, 90, .41)
    note('bass', .25, 38, .38, 94, .85)
    chord('strings', .60, [62,66], .18, 83, .50)
    chord('cello', 1.00, [43,50], .23, 93, .62)
    chord('horn', 1.00, [59,62,67], .26, 93, .46)
    note('bass', 1.00, 43, .30, 96, .77)
    chord('strings', 1.35, [62,67], .20, 84, .43)

    # The two musical replies follow the glasses' movement.
    for t,p,v,pan in [(1.70,55,88,-.45),(1.90,59,94,.45),(2.05,61,103,-.24),(2.20,64,111,.24)]:
        note('cello',t,p,.105,v,.77,pan)
    chord('horn',1.70,[55,59,62],.20,91,.50)
    note('bass',1.70,43,.23,98,.80)
    chord('horn',2.05,[55,61,64],.19,103,.62)
    chord('trumpet',2.05,[67,73],.15,94,.43)
    note('bass',2.05,45,.20,105,.87)

    # At 2.31-2.50 s, only the low room tail remains so the impact gets the peak.
    chord('horn',2.50,[62,66,69],.36,115,.81)
    chord('trumpet',2.50,[74,78],.30,111,.85)
    chord('strings',2.50,[74,78,81],.34,116,.75)
    chord('cello',2.50,[50,57],.37,115,.84)
    note('bass',2.50,38,.64,116,1.05)
    note('timpani',2.49,38,.85,112,.75)
    note('glock',2.50,86,.94,105,.73)
    chord('harp',2.50,[62,69,74,78,86],.66,104,.40)

    # A short two-note ending confirms the win.
    note('trumpet',2.84,69,.08,94,.67)
    note('flute',2.84,81,.08,96,.83)
    note('strings',2.85,81,.08,102,.66)
    chord('trumpet_long',3.00,[74,78],.27,108,.65)
    chord('strings_long',2.97,[74,78,81],.34,100,.64)
    chord('horn',3.00,[62,66,69],.27,103,.59)
    chord('cello',3.00,[50,57],.29,110,.72)
    note('bass',3.00,38,.55,111,.90)
    note('glock',3.00,86,1.05,98,.56)
    chord('harp',3.01,[62,69,78,86],.83,101,.42)
    return notes


def fetch_text(path: str) -> str:
    with urllib.request.urlopen(RAW + urllib.parse.quote(path, safe='/'), timeout=40) as response:
        return response.read().decode('utf-8', errors='replace')


def parse_sfz(text: str):
    path = re.search(r'default_path=([^\r\n]+)', text).group(1).strip().replace('\\','/')
    regions = []
    for block in text.split('<region>')[1:]:
        block = block.split('<group>')[0]
        fields = dict(re.findall(r'(\w+)=([^\r\n]+)',block))
        if 'sample' not in fields:
            continue
        region = {'path':path+fields['sample'].strip()}
        for key, default in [('lokey',0),('hikey',127),('pitch_keycenter',60),('lovel',0),('hivel',127)]:
            region[key]=int(fields.get(key,default))
        regions.append(region)
    return regions


def select_region(regions, note, round_robin):
    candidates = [r for r in regions if r['lokey'] <= note.pitch <= r['hikey'] and r['lovel'] <= note.velocity <= r['hivel']]
    if not candidates:
        candidates = sorted(regions,key=lambda r:abs(note.pitch-r['pitch_keycenter'])+ .03*max(0,r['lovel']-note.velocity,note.velocity-r['hivel']))[:1]
    return candidates[round_robin % len(candidates)]


def download_sample(path: str, cache: Path):
    destination=cache/path
    if not destination.exists():
        destination.parent.mkdir(parents=True,exist_ok=True)
        url=RAW+urllib.parse.quote(path,safe='/')
        with urllib.request.urlopen(url,timeout=45) as response:
            destination.write_bytes(response.read())
    return destination


def sample_audio(path: Path):
    audio, sr = sf.read(path,always_2d=True,dtype='float64')
    if audio.shape[1]>2:
        audio=audio[:,:2]
    if audio.shape[1]==1:
        audio=np.repeat(audio,2,axis=1)
    if sr!=SR:
        divisor=math.gcd(sr,SR)
        audio=resample_poly(audio,SR//divisor,sr//divisor,axis=0)
    audio=sosfilt(butter(2,25,fs=SR,btype='highpass',output='sos'),audio,axis=0)
    # I align the short RMS envelope without flattening the natural attack.
    env=np.sqrt(np.convolve(np.mean(audio**2,axis=1),np.ones(240)/240,mode='same'))
    maximum=np.max(env)
    crossings=np.flatnonzero(env>maximum*.075)
    start=max(0,int(crossings[0])-round(.006*SR)) if len(crossings) else 0
    audio=audio[start:]
    return audio


def shaped_note(source, event, root):
    ratio=Fraction(2**((root-event.pitch)/12)).limit_denominator(1600)
    sound=resample_poly(source,ratio.numerator,ratio.denominator,axis=0)
    short=event.instrument in ('flute','clarinet','strings','cello','horn','trumpet')
    release=.058 if short else .14
    if event.instrument in ('harp','bass','glock','timpani'):
        release=.14
    length=min(len(sound),round((event.duration+release)*SR))
    sound=sound[:length].copy()
    attack=round((.005 if short else .010)*SR)
    if event.instrument.endswith('_long'):
        attack=round(.026*SR)
    sound[:attack] *= np.sin(np.linspace(0,np.pi/2,attack))[:,None]**2
    rel=min(round(release*SR),len(sound))
    sound[-rel:] *= np.cos(np.linspace(0,np.pi/2,rel))[:,None]**2
    # I calibrate each note while keeping its attack, release and tone changes.
    window=sound[:min(len(sound),round(.22*SR))]
    rms=np.sqrt(np.mean(window**2))
    desired=SETTINGS[event.instrument][1]*(event.velocity/108.)**1.25*event.level
    gain=min(desired/max(rms,1e-5),12.)
    sound*=gain
    sound=sosfilt(butter(2,SETTINGS[event.instrument][4],fs=SR,btype='lowpass',output='sos'),sound,axis=0)
    # I keep the recorded stereo and use balance without phase widening.
    pan=np.clip(SETTINGS[event.instrument][2]+event.pan,-.8,.8)
    mid=np.mean(sound,axis=1)
    side=(sound[:,0]-sound[:,1])*.24
    a=(pan+1)*np.pi/4
    sound=np.column_stack((mid*np.cos(a)+side,mid*np.sin(a)-side))*np.sqrt(2)
    return sound


def add(bus,sound,t):
    start=round(t*SR)
    count=min(len(sound),len(bus)-start)
    if count>0:
        bus[start:start+count]+=sound[:count]


def render(cache,output):
    events=score()
    sfzs={}
    for instrument,settings in SETTINGS.items():
        sfzs[instrument]=parse_sfz(fetch_text(settings[0]))
    selected=[select_region(sfzs[e.instrument],e,i) for i,e in enumerate(events)]
    paths=sorted({r['path'] for r in selected})
    print(f'Fetching {len(paths)} recorded instrument samples.',flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        list(pool.map(lambda p:download_sample(p,cache),paths))
    audio={p:sample_audio(cache/p) for p in paths}
    buses={k:np.zeros((N,2)) for k in SETTINGS}
    for event,region in zip(events,selected):
        sound=shaped_note(audio[region['path']],event,region['pitch_keycenter'])
        add(buses[event.instrument],sound,event.time)

    # I use a recorded cymbal for a short breath and final swell.
    crash_path='VSCO 1 Percussion/varMetal/Cymbals/clash/crash_hit_mp_loose.wav'
    crash=sample_audio(download_sample(crash_path,cache))
    crash/=max(.001,np.max(np.abs(crash)))
    crash=crash[:round(1.55*SR)]
    crash*=np.exp(-np.arange(len(crash))/SR/0.52)[:,None]
    crash[-round(.18*SR):]*=np.linspace(1,0,round(.18*SR))[:,None]
    percussion=np.zeros((N,2))
    add(percussion,crash*.082,2.50)
    swell=crash[:round(.30*SR)][::-1].copy()
    swell*=np.sin(np.linspace(0,np.pi/2,len(swell)))[:,None]**2
    swell[-round(.035*SR):]*=np.linspace(1,0,round(.035*SR))[:,None]
    add(percussion,swell*.047,2.015)

    # I lower note tails before contact to leave a brief gap.
    gap_start=round(2.305*SR); gap_end=round(2.50*SR)
    for bus in buses.values():
        bus[gap_start:gap_start+round(.025*SR)]*=np.linspace(1,.03,round(.025*SR))[:,None]
        bus[gap_start+round(.025*SR):gap_end]*=.03

    dry=sum(buses.values())+percussion
    send=sum(bus*SETTINGS[name][3] for name,bus in buses.items())+percussion*.36
    fx=Pedalboard([Reverb(room_size=.61,damping=.56,wet_level=1.,dry_level=0.,width=.90),
                  HighpassFilter(cutoff_frequency_hz=200),LowpassFilter(cutoff_frequency_hz=7800)])
    wet=fx(send.T.astype(np.float32),SR).T
    # I keep the early room return low and give the impact more reverb.
    mix=dry+wet*.92
    mix=sosfilt(butter(2,38,fs=SR,btype='highpass',output='sos'),mix,axis=0)
    active_rms=np.sqrt(np.mean(mix[:round(3.45*SR)]**2))
    mix*=.149/max(active_rms,1e-8)
    compressor=Pedalboard([Compressor(threshold_db=-13.5,ratio=1.7,attack_ms=16,release_ms=130)])
    mix=compressor(mix.T.astype(np.float32),SR).T.astype(np.float64)
    mix*=10**(-1.4/20)/np.max(np.abs(mix))
    # I check oversampled peaks so sample peaks alone cannot miss overs.
    true_peak=np.max(np.abs(resample_poly(mix,4,1,axis=0)))
    if true_peak>10**(-1.2/20):
        mix*=10**(-1.2/20)/true_peak
    fade=round(.36*SR)
    mix[-fade:]*=np.cos(np.linspace(0,np.pi/2,fade))[:,None]**2
    mix[:round(.009*SR)]*=np.linspace(0,1,round(.009*SR))[:,None]

    output.mkdir(parents=True,exist_ok=True)
    base=output/'BartenderSort_Win_Orchestral_v2'
    sf.write(base.with_suffix('.wav'),mix,SR,subtype='PCM_24')
    sf.write(base.with_suffix('.ogg'),mix,SR,format='OGG',subtype='VORBIS',compression_level=0.0)
    manifest={'source':'https://github.com/sgossner/VSCO-2-CE','license':'CC0-1.0',
              'sfz_branch':'SFZ','samples':paths+[crash_path],
              'duration':LENGTH,'contact_time':2.50,'notes':[e.__dict__ for e in events]}
    (cache.parent/'render_manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    for suffix in ('.wav','.ogg'):
        rendered,rate=sf.read(base.with_suffix(suffix),always_2d=True)
        peak=np.max(np.abs(resample_poly(rendered,4,1,axis=0)))
        print(suffix,'seconds',len(rendered)/rate,'true_peak_db',round(20*np.log10(peak),2),
              'rms_db',round(20*np.log10(np.sqrt(np.mean(rendered**2))),2),flush=True)
    print(base,flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--cache',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    render(args.cache,args.output)
