from PIL import Image, ImageDraw
import forms_ref as F, math
CELL=300; COLS=4; ROWS=2; SS=3
W,H=COLS*CELL,ROWS*CELL
im=Image.new("RGB",(W*SS,H*SS),(38,42,36)); d=ImageDraw.Draw(im,"RGBA")
# 3/4 view: yaw 25deg, pitch 32deg  (roughly the game camera)
YAW=math.radians(25); PIT=math.radians(32)
def proj(p,cx,cy,sc):
    x,y,z=p
    xr=x*math.cos(YAW)-z*math.sin(YAW); zr=x*math.sin(YAW)+z*math.cos(YAW)
    sx=cx+xr*sc
    sy=cy-y*sc*math.cos(PIT)+zr*sc*math.sin(PIT)
    return sx,sy,(zr*math.cos(PIT)+y*math.sin(PIT))
for idx,name in enumerate(F.TABLE):
    cx=((idx%COLS)*CELL+CELL/2)*SS; cy=((idx//COLS)*CELL+CELL*0.62)*SS
    sc=CELL*0.40*SS
    # hex footprint
    hexpts=[proj((math.sin(math.tau*k/6),0,math.cos(math.tau*k/6)),cx,cy,sc)[:2] for k in range(6)]
    d.polygon(hexpts,fill=(58,66,52),outline=(80,90,72))
    tris=F.build(name)
    tris=[(proj(a,cx,cy,sc),proj(b,cx,cy,sc),proj(c,cx,cy,sc),s) for a,b,c,s in tris]
    tris.sort(key=lambda t:-(t[0][2]+t[1][2]+t[2][2]))
    for a,b,c,s in tris:
        n=max(40,int(215*s))
        d.polygon([a[:2],b[:2],c[:2]],fill=(n,n,n,int(200*max(0.15,s))))
    d.text((cx-30,(idx//COLS)*CELL*SS+12*SS),name,fill=(170,180,160))
im=im.resize((W,H),Image.LANCZOS)
im.save("forms.png"); print("ok")
