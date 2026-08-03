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

SEG=6
# cnt, where, rad, hgt, vary, wid, taper, tipfrac, bulge, tilt, curl, twist, cross, base, tip, lift
TABLE={
 "Fire":      (14,"cluster",0.72,0.86,0.52,0.115,1.35,0.00,0.85,  7,-0.34,30,2,1.00,0.00,0.00),
 "Frost":     (11,"edge",  0.84,0.44,0.40,0.130,1.00,0.00,0.00,-34, 0.00, 8,2,1.00,0.88,0.00),
 "Lightning": (6,"cluster",0.78,0.52,0.42,0.075,1.15,0.00,0.00,  6, 0.00,40,2,0.72,0.52,0.00),
 "Earth":     (6,"corner", 0.88,0.32,0.28,0.260,0.90,0.72,0.00,-24, 0.08,12,2,1.00,0.95,0.00),
 "Water":     (10,"ring",  0.60,0.20,0.22,0.300,0.70,0.45,0.30,-10, 0.20, 8,1,0.95,0.50,0.00),
 "Air":       (6,"ring",   0.55,0.55,0.35,0.100,1.10,0.03,0.75, 24,-0.85,60,1,0.70,0.12,0.10),
 "Arcane":    (7,"ring",   0.38,0.40,0.45,0.100,1.20,0.00,0.50, 14, 0.12,34,2,0.85,0.55,0.20),
 "Shadow":    (8,"ring",   0.42,0.46,0.40,0.150,1.20,0.02,0.45, 26, 1.05,34,1,1.00,0.10,0.00),
}
TAU=math.tau
def anchor(row,i,rng):
    cnt,where,rad=row[0],row[1],row[2]
    if where=="edge":
        slot=i%6; band=i//6
        a=TAU*slot/6 + (0 if band==0 else TAU/12)
        r=rad*(1 if band==0 else 0.62)
    elif where=="corner":
        a=TAU*(i%6)/6 + TAU/12
        r=rad*(1 if i<6 else 0.55)
    elif where=="cluster":
        a=i*2.39996323+rng.sym()*0.40
        r=rad*math.sqrt((i+0.5)/max(1,cnt))*(0.78+rng.unit()*0.36)
    else:
        a=TAU*i/max(1,cnt); r=rad
    return (math.sin(a)*r, 0.0, math.cos(a)*r), a

def build(name):
    row=TABLE[name]
    (cnt,where,rad,hgt,vary,wid,taper,tipfrac,bulge,tilt,curl,twist,cross,bs,ts,lift)=row
    rng=Rng(fnv1a32("imbuement_form:"+name))
    tris=[]
    for i in range(cnt):
        base,oyaw=anchor(row,i,rng)
        yaw=oyaw+math.radians(twist*rng.sym())
        h=hgt*(1+vary*rng.sym())
        base=(base[0],base[1]+lift,base[2])
        out=(math.sin(yaw),0.0,math.cos(yaw)); side=(out[2],0.0,-out[0])
        ph=rng.unit()*TAU
        spine=[];width=[];sol=[]
        for s in range(SEG+1):
            t=s/SEG
            lean=math.radians(tilt)*t+curl*t*t
            y=math.cos(lean)*h*t; oa=math.sin(lean)*h*t
            wob=math.sin(ph+t*3.1)*h*0.035
            spine.append((base[0]+out[0]*oa+side[0]*wob, base[1]+y, base[2]+out[2]*oa+side[2]*wob))
            f=(1-t)**taper
            width.append(wid*(tipfrac+(1.0-tipfrac)*f)*(1+bulge*math.sin(math.pi*t)))
            sol.append(bs+(ts-bs)*t)
        for across in ((side,out) if cross==2 else (side,)):
            for s in range(SEG):
                a0=tuple(spine[s][j]-across[j]*width[s] for j in range(3))
                a1=tuple(spine[s][j]+across[j]*width[s] for j in range(3))
                b0=tuple(spine[s+1][j]-across[j]*width[s+1] for j in range(3))
                b1=tuple(spine[s+1][j]+across[j]*width[s+1] for j in range(3))
                tris.append((a0,a1,b1,(sol[s]+sol[s+1])/2))
                tris.append((a0,b1,b0,(sol[s]+sol[s+1])/2))
    if name=="Lightning":
        tips=[]
        rng2=Rng(fnv1a32("imbuement_form:"+name))
        SAT=(5,0.34,0.12,24.0)
        def shard(base,out,side,h,tilt,wmul):
            ph=rng2.unit()*TAU; rng2.unit()
            sp=[];wd=[]
            for sgm in range(SEG+1):
                t=sgm/SEG
                lean=tilt*t+curl*t*t
                y=math.cos(lean)*h*t; oa=math.sin(lean)*h*t
                wob=math.sin(ph+t*3.1)*h*0.035
                sp.append((base[0]+out[0]*oa+side[0]*wob, base[1]+y, base[2]+out[2]*oa+side[2]*wob))
                f=(1-t)**taper
                wd.append(wid*wmul*(tipfrac+(1.0-tipfrac)*f)*(1+bulge*math.sin(math.pi*t)))
            for ac in (side,out):
                for sgm in range(SEG):
                    a0=tuple(sp[sgm][q]-ac[q]*wd[sgm] for q in range(3)); a1=tuple(sp[sgm][q]+ac[q]*wd[sgm] for q in range(3))
                    b0=tuple(sp[sgm+1][q]-ac[q]*wd[sgm+1] for q in range(3)); b1=tuple(sp[sgm+1][q]+ac[q]*wd[sgm+1] for q in range(3))
                    tris.append((a0,a1,b1,0.62)); tris.append((a0,b1,b0,0.62))
            return sp[SEG]
        tris.clear()
        for i in range(cnt):
            base,oyaw=anchor(row,i,rng2)
            yaw=oyaw+math.radians(twist*rng2.sym()); h=hgt*(1+vary*rng2.sym())
            out=(math.sin(yaw),0.0,math.cos(yaw)); side=(out[2],0.0,-out[0])
            tips.append(shard(base,out,side,h,math.radians(tilt),1.0))
            for k in range(SAT[0]):
                sa=TAU*k/SAT[0]+rng2.sym()*0.5
                so=(math.sin(sa),0.0,math.cos(sa)); ss=(so[2],0.0,-so[0])
                sb=tuple(base[q]+so[q]*SAT[2]*(0.6+rng2.unit()*0.7) for q in range(3))
                sh=h*SAT[1]*(0.55+rng2.unit()*0.55)
                st_=math.radians(SAT[3]*(0.5+rng2.unit()))
                shard(sb,so,ss,sh,st_,SAT[1]+0.55)
        n=len(tips)
        for a in range(7):
            i=a%n
            ca=(i+1+int(rng2.unit()*(n-1)))%n; cb=(i+1+int(rng2.unit()*(n-1)))%n
            j = cb if ca==i else (ca if cb==i else (ca if math.dist(tips[i],tips[ca])<=math.dist(tips[i],tips[cb]) else cb))
            if i==j: continue
            p0,p1=tips[i],tips[j]
            d=tuple(p1[k]-p0[k] for k in range(3)); L=math.dist(p0,p1)
            if L<1e-4: continue
            fwd=tuple(x/L for x in d); side=(-fwd[2],0,fwd[0])
            jag=L*0.11; sp=[]
            for sgm in range(10):
                t=sgm/9; k=0 if sgm in (0,9) else 1
                bow=math.sin(math.pi*t)*L*0.055
                sp.append((p0[0]+d[0]*t+side[0]*rng2.sym()*jag*k,
                           p0[1]+d[1]*t+bow+rng2.sym()*jag*0.5*k,
                           p0[2]+d[2]*t+side[2]*rng2.sym()*jag*k))
            for sgm in range(9):
                w0=0.013*(0.35+math.sin(math.pi*(sgm/9))); w1=0.013*(0.35+math.sin(math.pi*((sgm+1)/9)))
                for ac in (side,(0,1,0)):
                    a0=tuple(sp[sgm][q]-ac[q]*w0 for q in range(3)); a1=tuple(sp[sgm][q]+ac[q]*w0 for q in range(3))
                    b0=tuple(sp[sgm+1][q]-ac[q]*w1 for q in range(3)); b1=tuple(sp[sgm+1][q]+ac[q]*w1 for q in range(3))
                    tris.append((a0,a1,b1,1.0)); tris.append((a0,b1,b0,1.0))
    return tris
