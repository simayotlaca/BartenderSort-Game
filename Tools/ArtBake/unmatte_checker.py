"""Remove a baked checkerboard using phase-free Gabor magnitude for alpha."""
import sys, numpy as np
from PIL import Image
SRC,OUT=sys.argv[1],sys.argv[2]
OPAQUE_ONLY="--opaque-only" in sys.argv[3:]   # no semi-transparent content expected away from the object
D=np.array(Image.open(SRC).convert('RGB')).astype(np.float32); h,w=D.shape[:2]
R,G,B=D[...,0],D[...,1],D[...,2]; V=(R+G+B)/3; nd=np.maximum(np.maximum(np.abs(R-G),np.abs(G-B)),np.abs(R-B))
def box(a,k):
    a=a.astype(np.float64); pad=np.pad(a,((k+1,k),(k+1,k)),mode='edge'); c=pad.cumsum(0).cumsum(1)
    s=c[2*k+1:,2*k+1:]-c[:-2*k-1,2*k+1:]-c[2*k+1:,:-2*k-1]+c[:-2*k-1,:-2*k-1]; return (s/((2*k+1)**2)).astype(np.float32)
strip=np.zeros((h,w),bool); strip[:48]=True; strip[-48:]=True; strip[:,:48]=True; strip[:,-48:]=True
ok=strip&(nd<10)
ok2=strip&(nd<12)
# two dominant luminance peaks of the neutral border pixels = the two checker levels
hist,edges=np.histogram(V[ok2],bins=np.arange(40,256,3))
centers=(edges[:-1]+edges[1:])/2
sm=np.convolve(hist,np.ones(3)/3,'same')
order=np.argsort(sm)[::-1]; p1=order[0]; p2=None
for k in order[1:]:
    if abs(centers[k]-centers[p1])>=20: p2=k; break
lv=sorted([centers[p1],centers[p2]])
selD=ok2&(np.abs(V-lv[0])<8); selL=ok2&(np.abs(V-lv[1])<8)
Bd_rgb=np.median(D[selD],axis=0).astype(np.float32); Bl_rgb=np.median(D[selL],axis=0).astype(np.float32)
Ld=float(Bd_rgb.mean()); Ll=float(Bl_rgb.mean()); mid=(Ld+Ll)/2
seg=Bl_rgb-Bd_rgb; segl=float((seg*seg).sum())
tproj=np.clip(((D-Bd_rgb)*seg).sum(axis=2)/max(segl,1e-6),0,1)
resid=np.sqrt(((D-(Bd_rgb+tproj[...,None]*seg))**2).sum(axis=2))
tol=float(min(26.0,max(12.0,np.percentile(resid[ok2],97)+3.0)))
print("checker colours dark",Bd_rgb.round(0),"light",Bl_rgb.round(0),"tol",round(tol,1))
# checker period from row autocorrelation
line=V[10,:]-V[10,:].mean(); ac=[np.corrcoef(line[:-k],line[k:])[0,1] for k in range(12,40)]; P=float(np.argmax(ac)+12)
print("levels",round(Ld,1),round(Ll,1),"period",P)
# ---- checker-coloured pixels (neutral grey within the two levels incl. their AA blends)
checkerish0=(resid<tol)&(V>=Ld-13)&(V<=Ll+13)
# ---- Gabor visibility (phase-free): two complex filters at (f,f) and (f,-f), sigma ~0.6P
f=1.0/P; sig=0.6*P; rad=int(3*sig); k=np.arange(-rad,rad+1,dtype=np.float32)
gauss=np.exp(-k*k/(2*sig*sig)); gauss/=gauss.sum()
def fftconv(img,ky,kx):   # separable complex kernels via FFT
    Fi=np.fft.fft2(img.astype(np.complex64),s=(h+2*rad,w+2*rad))
    Ky=np.fft.fft(np.pad(ky,(0,h+2*rad-len(ky))),n=h+2*rad); Kx=np.fft.fft(np.pad(kx,(0,w+2*rad-len(kx))),n=w+2*rad)
    out=np.fft.ifft2(Fi*Ky[:,None]*Kx[None,:]); return out[rad:rad+h,rad:rad+w]
Vc=V-mid
c1=np.exp(1j*2*np.pi*f*k).astype(np.complex64)*gauss; c2=np.exp(-1j*2*np.pi*f*k).astype(np.complex64)*gauss
r1=fftconv(Vc,c1,c1); r2=fftconv(Vc,c1,c2)
mag=np.abs(r1)+np.abs(r2)
m0=float(np.median(mag[strip&checkerish0])); print("background gabor magnitude m0",round(m0,2))
checkerish=checkerish0
kc=int(np.ceil(P/2))+2
pure_bg=checkerish0&(box(checkerish0.astype(np.float32),kc)>0.999)   # whole window (> one cell) is checker-coloured
visib=np.clip(mag/m0,0,1.2)
alpha_g=np.clip(1.0-visib,0,1)
# ---- assemble alpha
A0=(Ll-Ld)/2
pattern=np.real(r1+r2)/m0
near_chk3=box(checkerish.astype(np.float32),3)>0
near_chk6=box(checkerish.astype(np.float32),6)>0
near_chk1=box(checkerish.astype(np.float32),1)>0
# provisional opaque (saturated colour away from checker) to mask object pixels out of the local means
op_pre=(nd>18)&~near_chk3&(resid>tol+10)
op_mask=(nd>18)&(resid>tol)                       # looser object estimate, only for masking the local means
nearop4_pre=box(op_mask.astype(np.float32),5)>0
# two-level local regression: mean colour over light cells vs dark cells (13x13), object pixels masked out
light=((pattern>0.04)&~nearop4_pre).astype(np.float32); dark=((pattern<-0.04)&~nearop4_pre).astype(np.float32)
KL=6
nl=box(light,KL); ndk=box(dark,KL)
Cl=np.dstack([box(D[...,c]*light,KL) for c in range(3)])/np.maximum(nl,1e-3)[...,None]
Cd=np.dstack([box(D[...,c]*dark,KL) for c in range(3)])/np.maximum(ndk,1e-3)[...,None]
okloc=(nl>0.06)&(ndk>0.06)
vis_loc=np.clip((Cl.mean(axis=2)-Cd.mean(axis=2))/(Ll-Ld),0,1)
a_loc=1.0-vis_loc
# pure background by connected flood: seeds = confident checker (full local contrast) + border; passable = checker-coloured with decent contrast
conf=checkerish&okloc&(a_loc<0.12)
fillable=checkerish&((okloc&(a_loc<0.38))|(~okloc&(alpha_g<0.35)))
border=np.zeros((h,w),bool); border[:2]=True; border[-2:]=True; border[:,:2]=True; border[:,-2:]=True
m=conf|(fillable&border)
for it in range(120):
    grown=(box(m.astype(np.float32),1)>0)&fillable
    if (grown==m).all(): break
    m=grown
pure_bg=m
# close pin holes inside objects (near-white checkers vs white highlights): erode 2 px then dilate 2 px, within fillable
pure_bg=(box((box(pure_bg.astype(np.float32),2)>0.999).astype(np.float32),2)>0)&fillable
print("flood iterations",it,"pure_bg px",int(pure_bg.sum()))
alpha=np.where(okloc,a_loc,alpha_g).astype(np.float32)   # local two-level contrast is robust near edges; Gabor as fallback
alpha[pure_bg]=0.0
nearop_pre=box(op_pre.astype(np.float32),4)>0
opaque=op_pre|((alpha_g>0.97)&~near_chk1&~checkerish)|((V>240)&~checkerish&nearop_pre)
near_bg6=box(pure_bg.astype(np.float32),6)>0
pure_bg|=checkerish&~pure_bg&near_bg6&((~okloc)|(a_loc<0.75))   # unflooded checker cells hugging the flood (edge fringe, pockets)
near_bg3=box(pure_bg.astype(np.float32),3)>0
border_zone=np.zeros((h,w),bool); bz=int(kc)+4; border_zone[:bz]=True; border_zone[-bz:]=True; border_zone[:,:bz]=True; border_zone[:,-bz:]=True
opaque|=checkerish&~pure_bg&((~okloc)|(a_loc>0.9))&~near_bg3&~border_zone   # white highlights on a near-white checker: no local contrast -> opaque
alpha[opaque]=1.0
def fillnearest(mask,vals,iters):
    out=np.where(mask[...,None],vals,0).astype(np.float32); m=mask.astype(np.float32)
    for it in range(iters):
        mm=box(m,1); bl=np.dstack([box(out[...,c],1) for c in range(3)])
        new=bl/np.maximum(mm,1e-6)[...,None]; grow=(m<0.5)&(mm>0); out[grow]=new[grow]; m[grow]=1
    return out,m>0.5
Bn,hasB=fillnearest(pure_bg,D,12)
Fn,hasF=fillnearest(opaque,D,12)
Bref=np.where(hasB[...,None],Bn,mid)
nearop4=box(opaque.astype(np.float32),4)>0
nearop6=box(opaque.astype(np.float32),6)>0
nearop8=box(opaque.astype(np.float32),8)>0
# Add a clean antialiased edge around the opaque core.
dils=[opaque]
for k in range(6): dils.append(box(dils[-1].astype(np.float32),1)>0)
ramp=np.zeros((h,w),np.float32)
for k,v in zip(range(1,7),(0.9,0.72,0.52,0.32,0.15,0.05)): ramp[dils[k]&~dils[k-1]]=v
band=(~opaque)&dils[6]
band_alpha=np.where((nd>25)&(resid>tol+10)&band,1.0,ramp)
alpha[band]=band_alpha[band]
use=band
# interior semi-transparent pixels: local two-level alpha
part=(alpha>0)&(alpha<1)&~use
alpha[part&(alpha<0.04)]=0.0
# faint leftovers away from objects: drop unless bright reflection
part=(alpha>0)&(alpha<1)&~use
faint=part&~nearop6&((V<=200)|(alpha<0.35)|(nd>25)|OPAQUE_ONLY)
alpha[faint]=0.0
# isolated specks
nz=(alpha>0).astype(np.float32); cnt=box(nz,1)*9
alpha[(alpha>0)&(alpha<1)&(cnt<4)]=0.0
part=(alpha>0)&(alpha<1)&~use
a3=alpha[...,None]
F=D.copy()
wq=np.clip(pattern/0.2,-1,1)*0.5+0.5   # 0=dark cell,1=light cell
Bq=Bd_rgb[None,None,:]*(1-wq[...,None])+Bl_rgb[None,None,:]*wq[...,None]
F[part]=np.clip((D[part]-(1-a3[part])*Bq[part])/np.maximum(a3[part],0.25),0,255)
F[use]=Fn[use]
# interior reflections: smooth colour and alpha with a masked 9x9 mean
interior=part&~nearop8
if interior.any():
    wsum=box(interior.astype(np.float32),4)
    Fm=np.dstack([box(F[...,c]*interior,4) for c in range(3)])/np.maximum(wsum,1e-6)[...,None]
    Am=box(alpha*interior,4)/np.maximum(wsum,1e-6)
    F[interior]=Fm[interior]; alpha[interior]=Am[interior]
# far partial pixels whose un-mixed colour is not bright (blurred checker smudges, not glass reflections) -> transparent
farpart=part&~nearop8&(F.mean(axis=2)<225)
alpha[farpart]=0.0
part=(alpha>0)&(alpha<1)&~use
interior=part&~nearop8
# feather the boundary of interior reflections (cell-quantised flood edge) over ~9 px
if interior.any():
    near_int=(box(interior.astype(np.float32),4)>0)&~opaque&~use
    a_f=box(alpha*(interior|pure_bg),4)/np.maximum(box((interior|pure_bg).astype(np.float32),4),1e-6)
    alpha[near_int]=np.maximum(alpha[near_int]*0,a_f[near_int])
    Ff=np.dstack([box(F[...,c]*interior,4) for c in range(3)])/np.maximum(box(interior.astype(np.float32),4),1e-6)[...,None]
    grow=near_int&~interior&(alpha>0); F[grow]=Ff[grow]
# speck clusters in the background narrower than ~5 px and away from opaque -> transparent
pm0=((alpha>0)&(alpha<1)&~nearop8)
er=box(pm0.astype(np.float32),3)>0.999
keep=box(er.astype(np.float32),7)>0
alpha[pm0&~keep]=0.0
np.save(OUT.replace('.png','_alpha.npy'),alpha)
img=np.dstack([np.clip(F,0,255),np.clip(alpha*255,0,255)]).astype(np.uint8)
filled=np.where((alpha>0)[...,None],img[...,:3],0).astype(np.float32); mk=alpha>0.02
for it in range(8):
    mm=box(mk.astype(np.float32),1); bl=np.dstack([box(filled[...,c]*mk,1) for c in range(3)])
    new=bl/np.maximum(mm,1e-6)[...,None]; grow=(~mk)&(mm>0); filled[grow]=new[grow]; mk=mk|grow
Image.fromarray(np.dstack([np.clip(filled,0,255).astype(np.uint8),img[...,3]]),'RGBA').save(OUT); print("saved",OUT)
print("alpha hist [0,(0,.1),(.1,.3),(.3,.6),(.6,.9),(.9,1),1]:",np.histogram(alpha,bins=[0,0.001,0.1,0.3,0.6,0.9,0.999,1.01])[0].tolist())
