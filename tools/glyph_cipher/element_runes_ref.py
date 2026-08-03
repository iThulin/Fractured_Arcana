import math
M32=0xFFFFFFFF
def fnv1a32(s):
    h=0x811C9DC5
    for c in s.encode("utf-8"): h=((h^c)*0x01000193)&M32
    return h
class Rng:
    def __init__(self,seed): self.s=(seed&M32) or 0x9E3779B9
    def u32(self):
        x=self.s; x=(x^(x<<13))&M32; x=x^(x>>17); x=(x^(x<<5))&M32; self.s=x; return x
    def unit(self): return self.u32()/4294967296.0
    def sym(self): return self.unit()*2.0-1.0

def At(th,r):
    t=math.radians(th); return (r*math.sin(t), -r*math.cos(t))
P=lambda x,y:(x,y)
RIM_R=0.90; JIT=0.007; RIM_JIT=0.004
W_RIM,W_IDENT=0.016,0.017
M_DOT,M_OPENDOT=0.038,0.055

def chain(rng,knots,perSeg,amp,close):
    segs=len(knots) if close else len(knots)-1
    pts=[]
    for k in range(segs):
        a=knots[k]; b=knots[(k+1)%len(knots)]
        for i in range(perSeg):
            t=i/perSeg
            x=a[0]+(b[0]-a[0])*t; y=a[1]+(b[1]-a[1])*t
            if i>0: x+=rng.sym()*amp; y+=rng.sym()*amp
            pts.append((x,y))
    pts.append(knots[0] if close else knots[-1])
    return pts

def stroke(pts,closed=False): return {"k":"poly","pts":pts,"w":W_IDENT,"closed":closed}
def dot(at,s): return {"k":"dot","at":at,"w":s}
def ring(at,s): return {"k":"ring","at":at,"w":s}
def Open(rng,S,knots,n,amp): S.append(stroke(chain(rng,knots,n,amp,False),False))
def Closed(rng,S,knots,n,amp): S.append(stroke(chain(rng,knots,n,amp,True),True))

def rim(rng,S):
    n=72; pts=[]
    for i in range(n+1):
        a=360.0*i/n
        r=RIM_R+(rng.sym()*RIM_JIT if 0<i<n else 0.0)
        pts.append(At(a,r))
    pts[n]=pts[0]
    S.append({"k":"poly","pts":pts,"w":W_RIM,"closed":True})

def branch(rng,S,spoke,r0,spread,ln):
    root=At(spoke,r0)
    for sign in (-1,1):
        d=At(spoke+spread*sign,1.0)
        tip=(root[0]+d[0]*ln, root[1]+d[1]*ln)
        Open(rng,S,[root,tip],3,JIT*0.7)

def arc(rng,S,r,a0,a1,n):
    pts=[]
    for i in range(n):
        t=i/(n-1); a=a0+(a1-a0)*t
        rr=r+(rng.sym()*JIT if 0<i<n-1 else 0.0)
        pts.append(At(a,rr))
    S.append(stroke(pts,False))

def fire(rng,S):
    Closed(rng,S,[P(0.03,-0.62),P(0.19,-0.36),P(0.25,-0.12),P(0.34,0.12),
                  P(0.29,0.36),P(0.15,0.51),P(0.00,0.55),P(-0.16,0.50),
                  P(-0.29,0.34),P(-0.34,0.10),P(-0.25,-0.14),P(-0.21,-0.32),
                  P(-0.07,-0.25)],3,JIT)
    Closed(rng,S,[P(0.01,0.41),P(0.14,0.25),P(0.12,0.03),P(0.00,-0.16),
                  P(-0.12,0.05),P(-0.11,0.27)],3,JIT*0.8)
    S.append(dot(P(-0.48,-0.12),M_DOT*0.85)); S.append(dot(P(0.46,0.08),M_DOT*0.7))

def frost(rng,S):
    for i in range(6):
        a=60.0*i
        Open(rng,S,[At(a,0.05),At(a,0.62)],6,JIT)
        branch(rng,S,a,0.28,40.0,0.18); branch(rng,S,a,0.46,40.0,0.13)
    S.append(dot(P(0,0),M_DOT*0.9))

def lightning(rng,S):
    Closed(rng,S,[P(0.18,-0.60),P(-0.22,0.00),P(-0.02,0.00),
                  P(-0.16,0.60),P(0.24,-0.02),P(0.04,-0.02)],3,JIT)

def earth(rng,S):
    Closed(rng,S,[P(0,-0.54),P(0.54,0),P(0,0.54),P(-0.54,0)],5,JIT)
    Open(rng,S,[P(-0.26,0.12),P(0.26,0.12)],5,JIT*0.8)
    Open(rng,S,[P(-0.16,0.28),P(0.16,0.28)],4,JIT*0.8)
    S.append(dot(P(0,-0.16),M_DOT))

def water(rng,S):
    for r,y0 in enumerate((-0.26,0.00,0.26)):
        n=15; pts=[]
        for i in range(n):
            u=i/(n-1); x=-0.52+1.04*u
            y=y0+0.075*math.sin(u*math.pi*3.0+r*0.9)
            if 0<i<n-1: x+=rng.sym()*JIT; y+=rng.sym()*JIT
            pts.append((x,y))
        S.append(stroke(pts,False))

def air(rng,S):
    arc(rng,S,0.56,20.0,265.0,16); arc(rng,S,0.38,140.0,385.0,14); arc(rng,S,0.20,260.0,505.0,12)
    S.append(ring(At(20.0,0.56),M_OPENDOT*0.8))

def arcane(rng,S):
    Closed(rng,S,[At(0,0.56),At(120,0.56),At(240,0.56)],5,JIT)
    Closed(rng,S,[At(60,0.56),At(180,0.56),At(300,0.56)],5,JIT)
    S.append(dot(P(0,0),M_DOT*0.85))

def shadow(rng,S):
    Open(rng,S,[P(-0.56,0.00),P(-0.30,-0.24),P(0.00,-0.32),P(0.30,-0.24),P(0.56,0.00)],4,JIT)
    Open(rng,S,[P(-0.56,0.00),P(-0.30,0.24),P(0.00,0.32),P(0.30,0.24),P(0.56,0.00)],4,JIT)
    S.append(ring(P(0,0),M_OPENDOT*1.15)); S.append(dot(P(0,0),M_DOT*0.85))

ELEMENTS=[("Fire",fire),("Frost",frost),("Lightning",lightning),("Earth",earth),
          ("Water",water),("Air",air),("Arcane",arcane),("Shadow",shadow)]

def build(name,fn):
    S=[]; rng=Rng(fnv1a32("element_rune:"+name))
    rim(rng,S); fn(rng,S); return S
