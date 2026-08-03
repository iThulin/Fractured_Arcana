from PIL import Image, ImageDraw
import runes_ref as R
SS=4
CELL=260; COLS=4; ROWS=2; PAD=20
W=COLS*CELL; H=ROWS*CELL
im=Image.new("RGB",(W*SS,H*SS),(42,38,34)); d=ImageDraw.Draw(im)
INK=(237,228,211)
for idx,(name,fn) in enumerate(R.ELEMENTS):
    cx=((idx%COLS)*CELL+CELL/2)*SS; cy=((idx//COLS)*CELL+CELL/2)*SS
    rad=((CELL-PAD*2)/2)*SS
    S=R.build(name,fn)
    sc=lambda p:(cx+p[0]*rad, cy+p[1]*rad)
    for s in S:
        if s["k"]=="poly":
            pts=[sc(p) for p in s["pts"]]; w=max(1,int(s["w"]*rad))
            d.line(pts,fill=INK,width=w,joint="curve")
            for e in (pts[0],pts[-1]):
                r=w/2; d.ellipse([e[0]-r,e[1]-r,e[0]+r,e[1]+r],fill=INK)
        elif s["k"]=="dot":
            x,y=sc(s["at"]); r=s["w"]*rad; d.ellipse([x-r,y-r,x+r,y+r],fill=INK)
        else:
            x,y=sc(s["at"]); r=s["w"]*rad; w=max(1,int(R.W_IDENT*rad))
            d.ellipse([x-r,y-r,x+r,y+r],outline=INK,width=w)
im=im.resize((W,H),Image.LANCZOS)
d2=ImageDraw.Draw(im)
for idx,(name,fn) in enumerate(R.ELEMENTS):
    cx=(idx%COLS)*CELL+CELL/2; cy=(idx//COLS)*CELL+CELL/2
    d2.text((cx-20,cy+CELL/2-16),name,fill=(150,142,132))
im.save("runes.png")
# small-scale legibility check: what they look like on a tile
sm=im.resize((W//4,H//4),Image.LANCZOS).resize((W//2,H//2),Image.NEAREST)
sm.save("runes_small.png")
print("ok")
