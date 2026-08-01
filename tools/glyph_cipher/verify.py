"""Assertions over the whole Enchanter corpus. Must print ALL ASSERTIONS PASS."""
import math, sys, collections
import glyph_cipher_ref as V, corpus

rows = corpus.load(); fails = []
def ck(c, m):
    if not c: fails.append(m)

built = {(r['id'], r['half']): V.build(r['id'], r['half'], r['name'], r['target'], r['verbs'])
         for r in rows}

maxr = (0, None); armhist = collections.Counter(); sil = collections.Counter()
for (cid, h), (st, m) in built.items():
    where = f"{cid}#{h}"
    ck(m['bars'] == len(m['letters']), f"INV-1 {where}: {m['bars']} crossbars for {len(m['letters'])} letters")
    ck(m['arms'] <= V.MAX_ARMS, f"INV-2 {where}: {m['arms']} arms exceeds MAX_ARMS")
    ck(sum(len(c) for c in V.layout(m['letters'])) == len(m['letters']),
       f"INV-2 {where}: layout drops letters")
    starts = sum(1 for s in st if s.mark == "Start")
    terms  = sum(1 for s in st if s.mark == "Terminal")
    hubs   = sum(1 for s in st if s.mark == "Hub")
    tips   = sum(1 for s in st if s.mark == "SpokeTip")
    retr   = sum(1 for s in st if s.mark == "Retrace")
    ck(starts == 1 and terms == 1 and hubs == 1, f"INV-4 {where}: start{starts} term{terms} hub{hubs}")
    ck(retr == m['retraces'], f"INV-4 {where}: {retr} retrace marks vs {m['retraces']}")
    ck(tips == len(m['verbs']), f"INV-5 {where}: {tips} spoke tips for {len(m['verbs'])} verbs")
    orders = sorted(s.order for s in st if s.order >= 0)
    ck(orders == list(range(len(orders))), f"INV-6 {where}: reveal indices not dense/unique")
    for s in st:
        for p in s.points:
            rr = math.hypot(*p)
            if rr > maxr[0]: maxr = (rr, f"{where}:{s.layer}")
    armhist[m['arms']] += 1
    sil[(m['arms'], m['deepest'])] += 1
ck(maxr[0] <= 1.01, f"INV-3 point at r={maxr[0]:.4f} escapes the rim ({maxr[1]})")

for a in V.VERB_ANGLE.values():
    for arm in V.ARM_ANGLES:
        ck(abs(((a - arm) % 360.0)) > 1e-9, f"INV-7 spoke at {a} collides with arm at {arm}")

for r in rows[:6]:
    s1, _ = built[(r['id'], r['half'])]
    s2, _ = V.build(r['id'], r['half'], r['name'], r['target'], r['verbs'])
    ck(all(a.points == b.points for a, b in zip(s1, s2)), f"A5 nondeterministic {r['id']}#{r['half']}")

a, _ = V.build("card_x", "top",    "Snare Glyph", "TILE", ["INSCRIBE"])
b, _ = V.build("card_y", "top",    "Snare Glyph", "TILE", ["INSCRIBE"])
c, _ = V.build("card_x", "bottom", "Snare Glyph", "TILE", ["INSCRIBE"])
ck(any(x.points != y.points for x, y in zip(a, b)), "A6 different card ids produced identical glyphs")
ck(any(x.points != y.points for x, y in zip(a, c)), "A6 different halves produced identical glyphs")

for name in ("A", "Zz", "  spaced  out  ", "O'Keeffe's Ward",
             "A very long spell name indeed that exceeds the cap"):
    try:
        st, m = V.build("edge_case", "top", name, "SELF", [])
        ck(len(m['letters']) <= V.MAX_LETTERS, f"edge '{name}': exceeds MAX_LETTERS")
    except Exception as e:
        fails.append(f"edge '{name}': raised {type(e).__name__}: {e}")
try:
    V.build("edge_case", "top", "1234 !!", "SELF", [])
    fails.append("edge: a name with no A-Z letters should raise, but did not")
except ValueError:
    pass

print(f"max point radius : {maxr[0]:.4f}  ({maxr[1]})")
print(f"arm-count spread : {dict(sorted(armhist.items()))}")
print(f"silhouette classes (arms, depth): {len(sil)} -> {dict(sorted(sil.items()))}")
print()
if fails:
    print("FAILURES:")
    for f in fails: print("  -", f)
    sys.exit(1)
print("ALL ASSERTIONS PASS")
