"""Checker-unmatte from scratch: local-phase checker model + per-cell gray correction + side-window regression."""
import numpy as np, os, sys, time
from PIL import Image
from scipy import ndimage as ndi
np.set_printoptions(linewidth=180, precision=4, suppress=True)
# usage: python3 unmatte_v2.py <source_with_checker.png> <clean_out.png> [diag_dir|-]
#   optional tuning overrides after diag_dir: CUT TAUM GF_R GF_EPS NB SIG_R ST0 TL CRK
SRC=sys.argv[1]; OUTPNG=sys.argv[2]
DIAG=sys.argv[3] if len(sys.argv)>3 and sys.argv[3]!='-' else None
if DIAG: os.makedirs(DIAG,exist_ok=True)
sys.argv=sys.argv[:3]+sys.argv[4:]   # tuning args keep their indices argv[3..]
t0=time.time()
def log(s): print(f'[{time.time()-t0:6.1f}s] {s}', flush=True)
def save(name,img):
    if DIAG: Image.fromarray(np.clip(img*255+0.5,0,255).astype(np.uint8)).save(os.path.join(DIAG,name))
def npsave(name,arr):
    if DIAG: np.save(os.path.join(DIAG,name),arr)
C=np.asarray(Image.open(SRC).convert('RGB')).astype(np.float64)/255.0
H,W,_=C.shape; lum=C.mean(2)
FR=50; frame=np.zeros((H,W),bool); frame[:FR,:]=1; frame[-FR:,:]=1; frame[:,:FR]=1; frame[:,-FR:]=1

# ---- 1. gray levels ----
v=lum[frame]; thr=np.median(v)
for _ in range(6):
    lo=v[v<thr].mean(); hi=v[v>=thr].mean(); thr=(lo+hi)/2
mid=(lo+hi)/2; D=hi-lo
log(f'gray levels lo={lo*255:.1f} hi={hi*255:.1f} mid={mid*255:.1f}')

# ---- 2. period ----
def cross(profile):
    s=profile-mid; idx=np.where(np.sign(s[:-1])!=np.sign(s[1:]))[0]
    return np.array([i + s[i]/(s[i]-s[i+1]) for i in idx])
dd=[]
for r in list(range(2,FR,4))+list(range(H-FR+2,H,4)): dd+=list(np.diff(cross(lum[r,:])))
for c in list(range(2,FR,4))+list(range(W-FR+2,W,4)): dd+=list(np.diff(cross(lum[:,c])))
dd=np.array(dd); dd=dd[(dd>4)&(dd<16)]
cell=np.median(dd); P=2*cell; k=2*np.pi/P
log(f'cell={cell:.3f}px period={P:.3f}px (spread std {dd.std():.2f})')

# ---- 3. local phase ----
yy,xx=np.mgrid[0:H,0:W].astype(np.float64)
Pi=int(round(P))
hp=lum-ndi.uniform_filter(lum,Pi)
def gsm(z,s): return ndi.gaussian_filter(z.real,s)+1j*ndi.gaussian_filter(z.imag,s)
R1=gsm(hp*np.exp(-1j*k*(xx+yy)),8.0); R2=gsm(hp*np.exp(-1j*k*(xx-yy)),8.0)
A0=np.sqrt(np.abs(R1)**2+np.abs(R2)**2); Abg=np.median(A0[frame])
R1s,R2s=gsm(R1,6.0),gsm(R2,6.0); R1L,R2L=gsm(R1,30.0),gsm(R2,30.0)
conf=np.sqrt(np.abs(R1s)**2+np.abs(R2s)**2)/Abg; wgt=np.clip(conf/0.15,0,1)
unit=lambda z: z/np.maximum(np.abs(z),1e-12)
p=np.angle(wgt*unit(R1s)+(1-wgt)*unit(R1L)); q=np.angle(wgt*unit(R2s)+(1-wgt)*unit(R2L))
u=k*xx+(p+q)/2; v_=k*yy+(p-q)/2
# cell ids
ix=np.floor((u+np.pi/2)/np.pi).astype(np.int64); iy=np.floor((v_+np.pi/2)/np.pi).astype(np.int64)
ix-=ix.min(); iy-=iy.min(); cid=iy*(ix.max()+1)+ix
interior=(np.abs(np.cos(u))>0.4)&(np.abs(np.cos(v_))>0.4)     # pixels away from cell borders
# phase coherence: drops where the drawn grid jumps (merged/missing cells) -> model unreliable there
coh=np.minimum(np.abs(R1s)/np.maximum(gsm(np.abs(R1)+0j,6.0).real,1e-9), np.abs(R2s)/np.maximum(gsm(np.abs(R2)+0j,6.0).real,1e-9))
CRK=float(sys.argv[11]) if len(sys.argv)>11 else 0.7
crack=ndi.binary_dilation((coh<CRK)&(conf>0.3),iterations=3)
save('crack.png',crack.astype(float)); save('coh.png',coh)
log(f'phase model done; crack pixels {crack.mean()*100:.2f}%')

# ---- 4. background model, edge width fit, contrast calibration ----
def model(w,delta=None):
    Sx=np.clip(np.cos(u)/(k*w),-1,1); Sy=np.clip(np.cos(v_)/(k*w),-1,1)
    B=mid+(D/2)*Sx*Sy
    if delta is not None: B=B+delta
    return B
best=min(((np.sqrt(((model(w)-lum)[frame]**2).mean()),w) for w in [0.4,0.5,0.6,0.7,0.8,1.0]))
EW=best[1]; log(f'edge width {EW} (frame rms {best[0]*255:.2f}/255)')
B=model(EW)

K=int(round(P))+1; RAD=(K-1)//2; STEP=3
CUT=float(sys.argv[3]) if len(sys.argv)>3 else 0.3
TAUM=float(sys.argv[4]) if len(sys.argv)>4 else 1.0
wpx=((np.abs(np.cos(u))>CUT)&(np.abs(np.cos(v_))>CUT)&(~crack)).astype(np.float64)   # trust only cell interiors, off cracks
box0=lambda z: ndi.uniform_filter(z,K,mode='reflect')
bw=box0(wpx)
box=lambda z: box0(wpx*z)/np.maximum(bw,1e-6)
mC=np.stack([box(C[...,c]) for c in range(3)],2); vC=np.stack([box(C[...,c]**2) for c in range(3)],2)-mC**2
def regress(B):
    mB=box(B); vB=box(B*B)-mB**2
    cov=np.stack([box(B*C[...,c]) for c in range(3)],2)-mB[...,None]*mC
    b=cov.sum(2)/(3*np.maximum(vB,1e-9)); a=mC-b[...,None]*mB[...,None]
    resid=np.maximum((vC-2*b[...,None]*cov+(b**2)[...,None]*vB[...,None]).sum(2),0)
    return b,a,resid
b,a,resid=regress(B)
s_cal=np.median(b[frame]); D*=s_cal; B=model(EW)
log(f'contrast calibration x{s_cal:.4f}')

def shift(z,dy,dx): return np.roll(np.roll(z,dy,0),dx,1)
def side_window(b,a,resid,tau):
    nb=np.zeros((H,W)); na=np.zeros((H,W,3)); den=np.zeros((H,W))
    for dy in range(-RAD+1,RAD,STEP):
        for dx in range(-RAD+1,RAD,STEP):
            w=np.exp(-shift(resid,dy,dx)/tau); nb+=w*shift(b,dy,dx); na+=w[...,None]*shift(a,dy,dx); den+=w
    return nb/den, na/den[...,None]

# ---- 5. two passes with per-cell gray correction ----
delta=np.zeros((H,W))
for it in range(2):
    B=model(EW,delta)
    b,a,resid=regress(B)
    tau=TAUM*np.median(resid[frame])
    bS,aS=side_window(b,a,resid,tau)
    alpha=np.clip(1-bS,0,1)
    log(f'pass {it}: bg resid median {np.median(resid[frame]):.6f}; frame alpha mean {alpha[frame].mean():.4f} p99 {np.percentile(alpha[frame],99):.4f}; frame model rms {np.sqrt(((B-lum)[frame]**2).mean())*255:.2f}/255')
    if it==1: break
    # residual -> per-cell gray deviation, only where checker is visible enough
    F0=aS/np.maximum(alpha,1e-3)[...,None]
    r=(C-alpha[...,None]*F0-(1-alpha)[...,None]*B[...,None]).mean(2)
    ok=interior&(alpha<0.75)
    rr=np.clip(r/np.maximum(1-alpha,0.25),-0.08,0.08)
    cnt=np.bincount(cid[ok],minlength=cid.max()+1); sm=np.bincount(cid[ok],weights=rr[ok],minlength=cid.max()+1)
    dcell=np.where(cnt>=12, sm/np.maximum(cnt,1), 0.0)
    delta=dcell[cid]
    delta[alpha>=0.75]=0   # opaque: irrelevant
    log(f'  per-cell correction: cells corrected {int((cnt>=12).sum())}, |delta| p95 {np.percentile(np.abs(dcell[cnt>=12]),95)*255:.2f}/255')
npsave('alpha_raw.npy',alpha); npsave('B_final.npy',B)
# ---- 5b. edge-aware denoise of alpha, guided by the de-checkered image ----
GF_R=int(sys.argv[5]) if len(sys.argv)>5 else 10
GF_EPS=float(sys.argv[6]) if len(sys.argv)>6 else 0.001
if GF_R>0:
    G=np.clip(C-(1-alpha)[...,None]*(B-mid)[...,None],0,1).mean(2)
    gb=lambda z: ndi.uniform_filter(z,2*GF_R+1,mode='reflect')
    mI=gb(G); mp=gb(alpha); vI=gb(G*G)-mI**2; cIp=gb(G*alpha)-mI*mp
    ga=cIp/(vI+GF_EPS); gbb=mp-ga*mI
    alpha_gf=np.clip(gb(ga)*G+gb(gbb),0,1)
    alpha=np.where(alpha>0.97,alpha,alpha_gf)
    log(f'guided filter r={GF_R} eps={GF_EPS}')
    save('alpha_gf.png',alpha)
save('alpha_raw.png',alpha); save('B_final.png',B); save('delta.png',0.5+delta*4)

# ---- 5c. bilateral-pooled regression: pixels regress with neighbours of similar de-checkered colour ----
NB=int(sys.argv[7]) if len(sys.argv)>7 else 3
SIG_R=float(sys.argv[8]) if len(sys.argv)>8 else 0.05
SIG_S=12.0; BR=16; BSTEP=2
# per-pixel lower bound on alpha from colour extremity (F must lie in [0,1])
Bc=B[...,None]
amin=np.max(np.where(C<Bc,(Bc-C)/np.maximum(Bc,1e-3),(C-Bc)/np.maximum(1-Bc,1e-3)),axis=2)
amin=np.clip(amin,0,1)
alpha_box=alpha.copy()
# structure weight: where the de-checkered image has local detail, pooled regression is needed; flat areas keep box alpha
Gflat=np.clip(C-(1-alpha_box)[...,None]*(B-mid)[...,None],0,1)
gl=Gflat.mean(2); lstd=np.sqrt(np.maximum(ndi.gaussian_filter(gl*gl,3.0)-ndi.gaussian_filter(gl,3.0)**2,0))
ST0=float(sys.argv[9]) if len(sys.argv)>9 else 0.035
wst=np.clip((ndi.maximum_filter(lstd,7)-ST0)/0.03,0,1)
save('struct_w.png',wst)
offs=[(dy,dx) for dy in range(-BR,BR+1,BSTEP) for dx in range(-BR,BR+1,BSTEP)]
Bm=B-mid
for it in range(NB):
    G=np.clip(C-(1-alpha)[...,None]*Bm[...,None],0,1)
    S=np.zeros((H,W)); SB=np.zeros((H,W)); SBB=np.zeros((H,W)); SC=np.zeros((H,W,3)); SBC=np.zeros((H,W,3))
    last=(it==NB-1)
    if last:
        Pcur=C-(1-alpha)[...,None]*Bc; SP=np.zeros((H,W,3)); SA=np.zeros((H,W))
    for dy,dx in offs:
        Gs=shift(G,dy,dx)
        w=shift(wpx,dy,dx)*np.exp(-((Gs-G)**2).sum(2)/(2*SIG_R**2)-(dy*dy+dx*dx)/(2*SIG_S**2))
        Bs=shift(Bm,dy,dx); Cs=shift(C,dy,dx)
        S+=w; SB+=w*Bs; SBB+=w*Bs*Bs; SC+=w[...,None]*Cs; SBC+=(w*Bs)[...,None]*Cs
        if last: SP+=w[...,None]*shift(Pcur,dy,dx); SA+=w*shift(alpha,dy,dx)
    mB=SB/S; vB=SBB/S-mB**2; mCb=SC/S[...,None]; cov=SBC/S[...,None]-mB[...,None]*mCb
    bb_=cov.sum(2)/(3*np.maximum(vB,1e-9))
    ok=(vB>0.15*(D/2)**2)&(S>6.0)
    a_bil=np.clip(1-bb_,0,1)
    a_bil=np.maximum(np.where(ok,a_bil,alpha_box),amin)
    alpha=wst*a_bil+(1-wst)*alpha_box
    log(f'bilateral pass {it}: reliable {ok.mean()*100:.1f}% px; frame alpha mean {alpha[frame].mean():.4f} p99 {np.percentile(alpha[frame],99):.4f}; mean|Δ| vs box {np.abs(alpha-alpha_box).mean():.4f}')
Ps_bil=SP/S[...,None]; As_bil=SA/S
save('alpha_bil.png',alpha)
# ---- 5d. tangential smoothing: average alpha along the local edge direction in transition zones ----
TL=float(sys.argv[10]) if len(sys.argv)>10 else 4.0
if TL>0:
    asg=ndi.gaussian_filter(alpha,2.5)
    gy=ndi.sobel(asg,0); gx=ndi.sobel(asg,1); gn=np.hypot(gx,gy)+1e-9
    tx=-gy/gn; ty=gx/gn            # unit tangent
    acc=np.zeros((H,W)); wsum=0.0
    for t in np.arange(-2*TL,2*TL+0.01,1.0):
        wt=np.exp(-t*t/(2*TL*TL))
        acc+=wt*ndi.map_coordinates(alpha,[yy+t*ty,xx+t*tx],order=1,mode='nearest'); wsum+=wt
    a_tan=acc/wsum
    band=np.clip((asg-0.03)/0.03,0,1)*np.clip((0.97-asg)/0.03,0,1)
    alpha=band*a_tan+(1-band)*alpha
    log(f'tangential smoothing sigma={TL}px on {(band>0.5).mean()*100:.1f}% px')
    save('alpha_tan.png',alpha)

# ---- 6. object mask (kill background noise / grid glitches) ----
asm=ndi.gaussian_filter(alpha,2.0)
mask=asm>0.06
ry,rx=np.ogrid[-7:8,-7:8]; disk=(rx*rx+ry*ry)<=49
mask=ndi.binary_opening(mask,structure=disk)
lab,n=ndi.label(mask); sizes=ndi.sum(mask,lab,range(1,n+1)); big=lab==(1+int(np.argmax(sizes)))
big=ndi.binary_fill_holes(big)
big=ndi.binary_dilation(big,structure=disk,iterations=1); big=ndi.binary_dilation(big,iterations=3)
alpha=np.where(big,alpha,0.0)
alpha[alpha>0.985]=1.0; alpha[alpha<0.012]=0.0
log(f'object mask: {big.sum()} px; components found {n}')

# ---- 7. foreground ----
Pm=C-(1-alpha)[...,None]*B[...,None]            # premultiplied colour, exact per pixel
# border pixels are unreliable where the checker shows through: normalized-convolution fill from interiors
wfill=((np.abs(np.cos(u))>0.35)&(np.abs(np.cos(v_))>0.35)&(~crack)).astype(np.float64)
nc=lambda z,s: ndi.gaussian_filter(wfill*z,s)/np.maximum(ndi.gaussian_filter(wfill,s),1e-6)
Pf=np.stack([nc(Pm[...,c],2.0) for c in range(3)],2)
tb=np.clip((alpha-0.55)/0.3,0,1)[...,None]          # opaque: keep per-pixel detail everywhere
Pm=np.where((wfill>0)[...,None],Pm,tb*Pm+(1-tb)*Pf)
if NB>0:
    Ps_flat=np.stack([nc(Pm[...,c],1.5) for c in range(3)],2); As_flat=nc(alpha,1.5)
    Ps=wst[...,None]*Ps_bil+(1-wst[...,None])*Ps_flat; As=wst*As_bil+(1-wst)*As_flat
else:
    Ps=np.stack([ndi.gaussian_filter(Pm[...,c],1.5) for c in range(3)],2); As=ndi.gaussian_filter(alpha,1.5)
F_hi=Pm/np.maximum(alpha,1e-3)[...,None]; F_lo=Ps/np.maximum(As,1e-3)[...,None]
t=np.clip((alpha-0.25)/0.35,0,1)[...,None]
F=np.clip(t*F_hi+(1-t)*F_lo,0,1)
valid=alpha>0.05
idx=ndi.distance_transform_edt(~valid,return_indices=True,return_distances=False)
F=np.where(valid[...,None],F,F[idx[0],idx[1]])
recon=alpha[...,None]*F+(1-alpha)[...,None]*B[...,None]
err=np.abs(recon-C).max(2)
log(f'reconstruction error vs source (inside object): mean {err[big].mean()*255:.2f}/255 p99 {np.percentile(err[big],99)*255:.2f}/255')

# ---- 8. crop + save ----
ys,xs=np.where(alpha>0)
pad=6; y0=max(ys.min()-pad,0); y1=min(ys.max()+pad+1,H); x0=max(xs.min()-pad,0); x1=min(xs.max()+pad+1,W)
rgba=np.concatenate([F,alpha[...,None]],2)[y0:y1,x0:x1]
Image.fromarray(np.clip(rgba*255+0.5,0,255).astype(np.uint8)).save(OUTPNG)
log(f'saved {OUTPNG} size {x1-x0}x{y1-y0} (crop from {W}x{H})')
npsave('alpha.npy',alpha); npsave('F.npy',F)
navy=np.array([0.10,0.12,0.30]); white=np.ones(3)
comp=lambda bg: alpha[...,None]*F+(1-alpha)[...,None]*bg
save('comp_navy.png',comp(navy)); save('comp_white.png',comp(white)); save('alpha.png',alpha)
# checker leak: diagonal-frequency amplitude of the flat-gray composite vs the source checker, away from strong edges
cg=comp(np.array([mid]*3)).mean(2); hpc=cg-ndi.uniform_filter(cg,Pi)
Al=np.sqrt(np.abs(gsm(hpc*np.exp(-1j*k*(xx+yy)),6.0))**2+np.abs(gsm(hpc*np.exp(-1j*k*(xx-yy)),6.0))**2)/Abg
gm=np.hypot(ndi.sobel(cg,0),ndi.sobel(cg,1)); flat=(ndi.maximum_filter(gm,9)<0.35)&big&(alpha<0.97)
log(f'checker leak (semi-transparent, flat areas, n={flat.sum()}): median {np.median(Al[flat]):.3f} p95 {np.percentile(Al[flat],95):.3f} max {Al[flat].max():.3f}  [source there: median {np.median(A0[flat]/Abg):.3f}]')
save('leak.png',np.clip(Al*3,0,1))
