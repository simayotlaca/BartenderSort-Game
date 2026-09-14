import sys, os, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bssolver import *

def plausible_player(lv, rng, max_moves=300):
    st=initial(lv); seen={st.key()}
    for step in range(max_moves):
        if is_win(st,lv): return True
        cand=[(ns,mv) for ns,mv in successors(st,lv) if ns.key() not in seen]
        if not cand: return False
        d=[x for x in cand if x[1].startswith('TESLIM')]
        if d: st=d[0][0]; seen.add(st.key()); continue
        def rank(item):
            ns,mv=item
            i=int(mv.split('g')[1].split('-')[0]); j=int(mv.split('->g')[1].split(' ')[0])
            si,sj=st.g[i][1],st.g[j][1]
            same=1 if (si and sj and si[-1][0]==sj[-1][0]) else 0
            amt=int(mv.split('(')[1].split(' ')[0])
            empties=1 if len(si)==amt else 0
            return (-same,-empties,-amt,rng.random())
        st=min(cand,key=rank)[0]; seen.add(st.key())
    return False

def naive_rate(lv, n=12, seed=500):
    return sum(plausible_player(lv, random.Random(seed+i)) for i in range(n))/n
