import sys, os, random, time, itertools
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bssolver import *
from rebalance import verify
from generate import build_solved, scramble, apply_locks, CAP, NAME

def solver(lv):
    fd,_=first_delivery_reachable(lv, node_budget=120_000)
    if fd is not True: return False,'ilk teslim yok'
    r,mv,n,_=solve(lv,node_budget=200_000)
    return (r=='COZULDU'), (f'{mv} hamle/{n} dugum' if mv else r)

def presolved(lv,g):
    l2=dict(lv); l2['glasses']=list(g); st=initial(l2)
    return sum(1 for ty,ls,ua in st.g
               if not((ua>0 and st.d<ua) or has_locked(ls,st.d))
               and any(o is not None and matches(ty,ls,o) for o in st.slots))

def make_orders(rng, types, colors, layer_ratio):
    """types lists the order glass types; I build their contents from the colours."""
    out=[]
    for t in types:
        cap=CAP[t]
        # I sometimes use one colour for easier orders, otherwise a mix.
        if rng.random()<0.35:
            c=[rng.choice(colors)]*cap
        else:
            c=[rng.choice(colors) for _ in range(cap)]
        kind = 1 if rng.random()<layer_ratio else 0
        out.append((kind,t,tuple(c)))
    return out

def gen_level(idx, spec, seed0, budget_s=25):
    """spec: dict(types=[...], colors=n, spares=[...], scramble=n, locks, chains, hidden, layer)"""
    t0=time.time(); rng=random.Random(seed0); tries=0
    colors=list(range(spec['colors']))
    while time.time()-t0 < budget_s:
        tries+=1
        orders=make_orders(rng, spec['types'], colors, spec['layer'])
        base=build_solved(orders, spec['spares'])
        g=scramble(base, spec['scramble'], rng)
        g=apply_locks(g, orders, rng, spec['locks'], spec['chains'], spec['hidden'])
        lv={'index':idx,'glasses':list(g),'orders':orders,'slots':3,'path':''}
        if [e for e in verify(lv,list(g)) if not e.startswith('uyari')]: continue
        if presolved(lv,g): continue
        cap=sum(CAP[t] for t,_,_ in g); fil=sum(len(l) for _,l,_ in g)
        col=len(set(c for _,l,_ in g for c,_,_ in l))
        if col<spec['colors']-1: continue
        slack=(cap-fil)/col
        if not (spec['slack'][0] <= slack <= spec['slack'][1]): continue
        ok,info=solver(lv)
        if not ok: continue
        return lv, slack, info, tries, time.time()-t0
    return None, None, None, tries, time.time()-t0
