"""
Reference implementation of the Enchanter Glyph Cipher, v2 (radial stave).
Line-for-line mirror of GlyphCipher.cs. Any change here MUST be mirrored there.
"""
import math

# ── Geometry (unit space: centre (0,0), rim radius 1.0, +Y down) ────────
R_RIM   = 1.00
ARM_R0  = 0.19        # arms start here; the plaza inside is the hub's
ARM_R1  = 0.87        # the deepest arm ends here
ARM_IN  = ARM_R0*0.35 # arms are drawn from here so they meet under the hub
ARM_OVER= 0.17        # fraction of arm length reserved past the last crossbar
BAR_MIN, BAR_K = 0.045, 0.0160   # crossbar half-length = BAR_MIN + slot*BAR_K
HALF_STUB = 0.12      # rare-letter crossbars are one-sided: this much overhang
SHORT_ARM = 0.55      # a non-deepest arm ends this far toward the rim
HUB, SPOKE = 0.135, 0.52
MAX_ARMS = 6

W_RIM, W_IDENT, W_FUNC = 0.016, 0.017, 0.032
# Marker sizes. Enlarged from (0.034, 0.044, 0.042) / (0.028, 0.040) once the tile
# LOD started drawing the stave at 1.7x width: an open ring whose radius stays put
# while its stroke thickens closes into a blob, and the arm-tip ornaments are the
# channel carrying silhouette variety, so losing them costs distinctiveness.
M_START, M_TERMINAL, M_RETRACE = 0.042, 0.054, 0.050
M_DOT, M_OPENDOT, M_SPOKETIP = 0.038, 0.055, 0.050
HUB_SIZE = {"SELF":HUB, "ALLY":HUB, "TILE":HUB*1.30, "ENEMY":HUB*1.40}

ARM_JIT, BAR_JIT = 0.008, 0.005
MAX_LETTERS = 24

ARM_ANGLES = [0.0, 60.0, 120.0, 180.0, 240.0, 300.0]
TARGETS = ["SELF","ALLY","TILE","ENEMY"]
VERBS   = ["WARD","MOVE","INSCRIBE","INVOKE","BIND","STRIKE"]
VERB_ANGLE = {v: 30.0 + 60.0*i for i,v in enumerate(VERBS)}   # interleaved with the arms

OUTER_LETTERS = "ACDEHILNORSTU"   # 13 most common
INNER_LETTERS = "BFGJKMPQVWXYZ"   # 13 least common

def pt(th, r):
    t = math.radians(th)
    return (r*math.sin(t), -r*math.cos(t))

def letter_slot(ch):
    i = OUTER_LETTERS.find(ch)
    if i >= 0: return i+1, True
    i = INNER_LETTERS.find(ch)
    if i >= 0: return i+1, False
    raise ValueError(ch)

M32 = 0xFFFFFFFF
def fnv1a32(s):
    h = 0x811C9DC5
    for c in s.encode("utf-8"): h = ((h ^ c) * 0x01000193) & M32
    return h

class Rng:
    def __init__(self, seed): self.s = (seed & M32) or 0x9E3779B9
    def next_u32(self):
        x = self.s
        x = (x ^ (x << 13)) & M32
        x ^= x >> 17
        x = (x ^ (x << 5)) & M32
        self.s = x; return x
    def unit(self): return self.next_u32()/4294967296.0
    def sym(self):  return self.unit()*2.0 - 1.0

RIM, IDENTITY, FUNCTION = "Rim","Identity","Function"

class Stroke:
    __slots__=("layer","points","weight","mark","closed","order")
    def __init__(s,layer,points,weight,mark="None",closed=False,order=-1):
        s.layer,s.points,s.weight,s.mark,s.closed,s.order = layer,points,weight,mark,closed,order

def _line(rng,p0,p1,n=4,a=BAR_JIT):
    out=[]
    for j in range(n):
        t=j/(n-1)
        x=p0[0]+(p1[0]-p0[0])*t; y=p0[1]+(p1[1]-p0[1])*t
        if 0<j<n-1: x+=rng.sym()*a; y+=rng.sym()*a
        out.append((x,y))
    return out

def normalise(name):
    return "".join(c for c in (name or "").upper() if "A"<=c<="Z")[:MAX_LETTERS]

def layout(letters):
    n=len(letters)
    m=max(1,(n+MAX_ARMS-1)//MAX_ARMS)
    return [letters[i:i+m] for i in range(0,n,m)][:MAX_ARMS]

# Terminal ornament, chosen by the last letter's slot. This is where silhouette
# variety lives: two names that both fill six arms still end in six different shapes.
def _ornament(rng, ang, r, kind):
    ux,uy = pt(ang,1.0); px,py = -uy,ux
    tip = pt(ang,r)
    segs=[]; marks=[]
    if kind==0: pass
    elif kind==1: marks.append((tip, M_DOT, "Dot"))
    elif kind==2:
        for s in (-1,1):
            segs.append(_line(rng, tip, (tip[0]+ux*0.10+px*0.085*s, tip[1]+uy*0.10+py*0.085*s)))
    elif kind==3:
        segs.append(_line(rng, (tip[0]-px*0.11, tip[1]-py*0.11), (tip[0]+px*0.11, tip[1]+py*0.11)))
    elif kind==4: marks.append((tip, M_OPENDOT, "OpenDot"))
    else:
        for s in (-1,1):
            segs.append(_line(rng, tip, (tip[0]-ux*0.085+px*0.085*s, tip[1]-uy*0.085+py*0.085*s)))
    return segs, marks

def build(card_id, half, cipher_name, target, verbs):
    letters = normalise(cipher_name)
    if not letters: raise ValueError("cipher name has no A-Z letters: "+repr(cipher_name))
    seed_key = f"{card_id}#{half}"
    rng = Rng(fnv1a32(seed_key))
    arms = layout(letters)
    deepest = max(len(c) for c in arms)
    usable  = (ARM_R1 - ARM_R0)*(1.0 - ARM_OVER)
    dr      = usable/(deepest-1) if deepest > 1 else 0.0
    r_first = ARM_R0 + (usable*0.5 if deepest == 1 else 0.0)

    strokes=[]
    rim=[]
    for i in range(97):
        a=2*math.pi*i/96
        r=R_RIM + rng.sym()*0.006
        rim.append((r*math.sin(a), -r*math.cos(a)))
    rim[-1]=rim[0]
    strokes.append(Stroke(RIM, rim, W_RIM, closed=True))

    order=0; retraces=0; bars=0; prev=None
    for ai,chunk in enumerate(arms):
        ang=ARM_ANGLES[ai]
        last_r = r_first + dr*(len(chunk)-1)
        arm_end = ARM_R1 if len(chunk)==deepest else last_r + (ARM_R1-last_r)*SHORT_ARM
        strokes.append(Stroke(IDENTITY, _line(rng, pt(ang,ARM_IN), pt(ang,arm_end), 7, ARM_JIT), W_IDENT, order=order)); order+=1
        ux,uy = pt(ang,1.0); px,py = -uy,ux
        for d,ch in enumerate(chunk):
            slot,common = letter_slot(ch)
            r = r_first + dr*d
            c = pt(ang,r)
            half_len = BAR_MIN + slot*BAR_K
            back = half_len if common else half_len*HALF_STUB
            a0=(c[0]-px*back,     c[1]-py*back)
            a1=(c[0]+px*half_len, c[1]+py*half_len)
            strokes.append(Stroke(IDENTITY, _line(rng,a0,a1,4), W_IDENT, order=order)); order+=1; bars+=1
            if ch==prev:
                strokes.append(Stroke(IDENTITY,[c],M_RETRACE,mark="Retrace")); retraces+=1
            prev=ch
        osegs,omarks = _ornament(rng, ang, arm_end, letter_slot(chunk[-1])[0] % 6)
        for s in osegs: strokes.append(Stroke(IDENTITY, s, W_IDENT, order=order)); order+=1
        for p,sz,kind in omarks: strokes.append(Stroke(IDENTITY,[p],sz,mark=kind))

    strokes.append(Stroke(IDENTITY,[pt(ARM_ANGLES[0], ARM_R0*0.62)], M_START, mark="Start"))
    end_arm = ARM_ANGLES[len(arms)-1]
    strokes.append(Stroke(IDENTITY,[pt(end_arm, r_first + dr*(len(arms[-1])-1))], M_TERMINAL, mark="Terminal"))

    for v in VERBS:
        if v not in verbs: continue
        a=VERB_ANGLE[v]
        strokes.append(Stroke(FUNCTION, _line(rng, pt(a,HUB*1.15), pt(a,SPOKE), 5, 0.006), W_FUNC, order=order)); order+=1
        strokes.append(Stroke(FUNCTION,[pt(a,SPOKE)], M_SPOKETIP, mark="SpokeTip"))
    strokes.append(Stroke(FUNCTION,[(0.0,0.0)], HUB_SIZE[target], mark="Hub"))

    meta=dict(letters=letters, arms=len(arms), deepest=deepest, bars=bars,
              retraces=retraces, strokes=order, target=target,
              verbs=[v for v in VERBS if v in verbs], seed_key=seed_key)
    return strokes, meta
