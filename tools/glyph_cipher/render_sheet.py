"""
Contact sheet + LOD comparison, rendered from glyph_cipher_ref.build().

Renders the reference implementation's actual stroke output, so the sheet cannot
drift from the grammar the goldens assert. (It previously drew from a separate
prototype module, which could.)
"""
import math, html
import glyph_cipher_ref as G, corpus

INK, INK_LIGHT, ROSE, PAPER, DARK = "#1A1614", "#EDE4D3", "#C45B9E", "#E8DFCE", "#171312"

# Mirrors GlyphCipherView.ProfileFor. (identity_alpha, identity_w_mul, backing_alpha,
# function_w_mul, min_px_identity, min_px_function)
LOD = {
    "Card":       (1.00, 1.00, 0.00, 1.0, 1.0, 1.6),
    "Tile":       (1.00, 1.70, 0.00, 1.6, 1.0, 2.6),
    "Inspection": (1.00, 1.00, 0.00, 1.0, 1.0, 1.6),
}

def poly(pts, cx, cy, R, col, w, a=1.0):
    d = "M " + " L ".join(f"{cx+x*R:.3f},{cy+y*R:.3f}" for x, y in pts)
    return (f'<path d="{d}" fill="none" stroke="{col}" stroke-width="{max(0.5,w):.2f}" '
            f'stroke-linecap="round" stroke-linejoin="round" opacity="{a:.2f}"/>')

def disc(p, cx, cy, R, col, r, a=1.0):
    return f'<circle cx="{cx+p[0]*R:.2f}" cy="{cy+p[1]*R:.2f}" r="{r*R:.2f}" fill="{col}" opacity="{a:.2f}"/>'

def ring(p, cx, cy, R, col, r, w, a=1.0):
    return (f'<circle cx="{cx+p[0]*R:.2f}" cy="{cy+p[1]*R:.2f}" r="{r*R:.2f}" fill="none" '
            f'stroke="{col}" stroke-width="{max(0.5,w):.2f}" opacity="{a:.2f}"/>')

def hub(target, size, cx, cy, R, col, w, paper):
    def pts(ps): return " ".join(f"{cx+x*R:.2f},{cy+y*R:.2f}" for x, y in ps)
    if target == "SELF":  return disc((0, 0), cx, cy, R, col, size)
    if target == "ALLY":  return disc((0, 0), cx, cy, R, col, size) + disc((0, 0), cx, cy, R, paper, size*0.46)
    if target == "TILE":  return f'<polygon points="{pts([(0,-size),(size,0),(0,size),(-size,0)])}" fill="{col}"/>'
    return f'<polygon points="{pts([(0,-size*1.05),(size*0.91,size*0.58),(-size*0.91,size*0.58)])}" fill="{col}"/>'

def glyph(row, cx, cy, R, lod="Card", dark=False, paper=PAPER):
    ia, iw, _, fw, mpi, mpf = LOD[lod]
    ink = INK_LIGHT if dark else INK
    st, meta = G.build(row["id"], row["half"], row["name"], row["target"], row["verbs"])
    wi = max(mpi, G.W_IDENT * R * iw)
    wf = max(mpf, G.W_FUNC * R * fw)
    o = []
    for s in st:
        if s.layer != G.RIM: continue
        o.append(poly(s.points, cx, cy, R, ink, max(mpi, s.weight * R * iw), ia))
    for s in st:
        if s.layer != G.IDENTITY: continue
        if s.mark == "None":
            o.append(poly(s.points, cx, cy, R, ink, wi, ia))
        elif s.mark in ("Start", "Dot"):
            o.append(disc(s.points[0], cx, cy, R, ink, s.weight, ia))
        else:                                    # Terminal, Retrace, OpenDot
            o.append(ring(s.points[0], cx, cy, R, ink, s.weight, wi, ia))
    for s in st:
        if s.layer != G.FUNCTION: continue
        if s.mark == "None":
            o.append(poly(s.points, cx, cy, R, ROSE, wf))
        elif s.mark == "SpokeTip":
            o.append(disc(s.points[0], cx, cy, R, ROSE, s.weight))
        else:                                    # Hub
            o.append(hub(meta["target"], s.weight, cx, cy, R, ROSE, wf, paper))
    return o

def sheet(path):
    rows = corpus.load()
    COLS, CELL, R = 6, 215, 80
    W, H = COLS*CELL + 40, ((len(rows)+COLS-1)//COLS)*CELL + 110
    o = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">',
         f'<rect width="{W}" height="{H}" fill="{PAPER}"/>',
         f'<text x="20" y="36" font-family="Georgia,serif" font-size="24" fill="{INK}">Enchanter Glyph Cipher v2 &#8212; radial stave &#8212; all 42 spell halves</text>',
         f'<text x="20" y="58" font-family="Georgia,serif" font-size="13" fill="{INK}" opacity="0.65">black arms + crossbars = the encoded Name &#183; rose hub + spokes = casting function</text>']
    for i, r in enumerate(rows):
        cx = 20 + (i % COLS)*CELL + CELL/2
        cy = 90 + (i // COLS)*CELL + CELL/2 - 12
        o += glyph(r, cx, cy, R)
        o.append(f'<text x="{cx:.0f}" y="{cy+R+24:.0f}" text-anchor="middle" font-family="Georgia,serif" font-size="13" fill="{INK}">{html.escape(r["name"])}</text>')
        o.append(f'<text x="{cx:.0f}" y="{cy+R+39:.0f}" text-anchor="middle" font-family="Georgia,serif" font-size="10" fill="{ROSE}">{r["target"]} &#183; {"+".join(r["verbs"])}</text>')
    o.append('</svg>')
    open(path, "w").write("\n".join(o))

if __name__ == "__main__":
    sheet("v2_sheet.svg")
    print("wrote v2_sheet.svg")
