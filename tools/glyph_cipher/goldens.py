"""Regenerates the golden table pasted into Scripts/Dev/GlyphCipherSelfTest.cs.
Only run this deliberately, and bump the spec version when you do."""
import glyph_cipher_ref as V, corpus

def q(v): return f"{round(v, 4) + 0.0:.4f}"

def checksum(strokes):
    h = 0x811C9DC5
    def feed(b):
        nonlocal h
        for c in b: h = ((h ^ c) * 0x01000193) & 0xFFFFFFFF
    for s in strokes:
        feed(s.layer.encode()); feed(s.mark.encode())
        feed(f"{s.weight:.6f}".encode()); feed(str(s.order).encode())
        for (x, y) in s.points:
            feed(q(x).encode()); feed(q(y).encode())
    return h

TG = {'SELF':'Self','ALLY':'Ally','TILE':'Tile','ENEMY':'Enemy'}
VB = {'WARD':'Ward','MOVE':'Move','INSCRIBE':'Inscribe','INVOKE':'Invoke','BIND':'Bind','STRIKE':'Strike'}

lines = []; agg = 0x811C9DC5
for r in sorted(corpus.load(), key=lambda r: (r['id'], r['half'])):
    st, m = V.build(r['id'], r['half'], r['name'], r['target'], r['verbs'])
    cs = checksum(st)
    for c in f"{cs:08X}".encode(): agg = ((agg ^ c) * 0x01000193) & 0xFFFFFFFF
    verbs = " | ".join("CipherVerb." + VB[v] for v in m['verbs']) or "CipherVerb.None"
    lines.append(f'        new("{r["id"]}", "{r["half"]}", "{r["name"]}", "{m["letters"]}", '
                 f'CipherTarget.{TG[m["target"]]}, {verbs}, {m["arms"]}, {m["deepest"]}, '
                 f'{m["bars"]}, {m["retraces"]}, {len(st)}, 0x{cs:08X}u),')

print("\n".join(lines))
print(f"\n// AggregateChecksum = 0x{agg:08X}u   ({len(lines)} rows)")
