import json, pathlib

def seq(*steps):
    steps = [s for s in steps if s]
    return steps[0] if len(steps) == 1 else {"type": "sequence", "steps": list(steps)}
def charge(n): return {"type": "gain_charge", "amount": n} if n else None
def draw(n): return {"type": "draw", "count": n} if n else None
def pen(frm, count=1, up_to=False, free_top=False, reveal=None, cpb=0):
    d = {"type": "pen", "from": frm, "count": count}
    if up_to: d["up_to"] = True
    if free_top: d["free_top"] = True
    if reveal: d["reveal"] = reveal
    if cpb: d["charge_per_bottomed"] = cpb
    return d
SELF = {"type": "self"}

def rewrite(cid, side, base, tiers, speed=None, name=None, targeting=None, extra_base=None):
    """base: (rules_text, effect). tiers: {1: (name, text, effect, desc?), 2: ..., ...}
    Changes for `side` are replaced in every node; nodes for other halves stay."""
    p = pathlib.Path(f"Data/Cards/{cid}.json")
    d = json.loads(p.read_text(encoding='utf-8'))
    h = d[side]
    h["rules_text"], h["effect"] = base
    if name: h["name"] = name
    if speed: h["speed"] = speed
    if targeting is not None: h["targeting"] = targeting
    if extra_base: h.update(extra_base)
    ups = d.setdefault("upgrades", [])
    for u in ups:
        u["changes"] = [c for c in u["changes"] if c["half"] != side]
    for tier, spec in tiers.items():
        tname, text, eff = spec[0], spec[1], spec[2]
        desc = spec[3] if len(spec) > 3 else None
        changes = [{"half": side, "field": "effect", "value": eff},
                   {"half": side, "field": "rules_text", "value": text}]
        if targeting is not None:
            changes.insert(0, {"half": side, "field": "targeting", "value": targeting})
        node = None
        if tier == 1:
            node = next((u for u in ups if u["tier"] == 1 and u["half"] == "both"), None)
            if node is None:
                node = {"tier": 1, "half": "both", "description": desc or "", "changes": []}
                ups.insert(0, node)
        else:
            node = next((u for u in ups if u["tier"] == tier and u["half"] == side), None)
            if node is None:
                node = {"tier": tier, "half": side, "description": desc or "", "changes": []}
                ups.append(node)
        node[f"{side}_name"] = tname
        if desc:
            node["description"] = desc
        node["changes"].extend(changes)
    # drop per-half nodes left with no changes; keep tier order
    d["upgrades"] = sorted([u for u in ups if u["changes"]],
                           key=lambda u: (u["tier"], {"both": 0, "top": 1, "bottom": 2}[u["half"]]))
    p.write_text(json.dumps(d, indent='\t', ensure_ascii=False) + '\n', encoding='utf-8')

# ── Grimoire Dive ───────────────────────────────────────────────────────
GT = "Reveal the top {n} cards. Pen 1; the rest go to the bottom. Gain 1 Charge per card bottomed."
def gd(n, dr=0): return seq(pen("reveal", reveal=n, cpb=1), draw(dr))
rewrite("arcanist_grimoire_dive", "top", (GT.format(n=4), gd(4)), {
    1: ("Grimoire Dive+", GT.format(n=5), gd(5), "Dive deeper. The leaf opens wider."),
    2: ("Grimoire Plunge", GT.format(n=5) + " Draw 1 card.", gd(5, 1), "Grimoire Plunge: and a card in hand."),
    3: ("Bottomless Grimoire", GT.format(n=6) + " Draw 1 card.", gd(6, 1), "Bottomless Grimoire."),
    4: ("The Whole Grimoire", GT.format(n=6) + " Draw 2 cards.", gd(6, 2), "The Whole Grimoire."),
}, speed="Studied", targeting=SELF)
FT = "Your next {n} cast{s} from your leaf cost{v} no Charge."
def ft(n): return FT.format(n="" if n == 1 else str(n), s="" if n == 1 else "s", v="s" if n == 1 else "").replace("next  ", "next ")
def fl(n, dr=0): return seq({"type": "free_leaf_cast", "count": n}, draw(dr))
rewrite("arcanist_grimoire_dive", "bottom", (ft(1), fl(1)), {
    1: ("Open the Leaf+", ft(1) + " Draw 1 card.", fl(1, 1)),
    2: ("Open the Leaf Wide", ft(2), fl(2), "Open the Leaf Wide: two free casts."),
    3: ("Leaf Unbound", ft(2) + " Draw 1 card.", fl(2, 1), "Leaf Unbound."),
    4: ("The Open Book", ft(3) + " Draw 1 card.", fl(3, 1), "The Open Book."),
}, speed="Studied", name="Open the Leaf", targeting=SELF)

# ── Marginalia (top) ────────────────────────────────────────────────────
def mg(n, c, dr=0): return seq(pen("deck_top", n), charge(c), draw(dr))
def mt(n, c, dr=0):
    t = "Pen the top card of your deck." if n == 1 else f"Pen the top {n} cards of your deck."
    t += f" Gain {c} Charge."
    return t + (f" Draw {dr} card{'s' if dr > 1 else ''}." if dr else "")
rewrite("arcanist_marginalia", "top", (mt(1, 1), mg(1, 1)), {
    1: ("Marginalia+", mt(1, 2), mg(1, 2)),
    2: ("Annotated Tome", mt(1, 1, 1), mg(1, 1, 1), "Annotated Tome: and a card in hand."),
    3: ("Footnotes", mt(1, 2, 1), mg(1, 2, 1), "Footnotes: more Charge."),
    4: ("The Marginal Codex", mt(2, 2, 1), mg(2, 2, 1), "The Marginal Codex: two pages at once."),
}, targeting=SELF)

# ── Second Verse (bottom) ───────────────────────────────────────────────
rewrite("arcanist_second_verse", "bottom",
        ("Pen a card from your hand. Draw 1 card.", seq(pen("hand"), draw(1))), {
    1: ("Marginal Note+", "Pen a card from your hand. Draw 2 cards.", seq(pen("hand"), draw(2))),
}, targeting=SELF)

# ── Overcharge ──────────────────────────────────────────────────────────
OT = "Spend all Charge (min 2). Deal {d} damage per Charge spent to target. Your Charge can't increase until the end of your next turn."
OT1 = "Spend all Charge (min 2). Deal {d} damage per Charge spent to target. Your Charge can't increase for the rest of this turn."
def oc(d, lock=2, splash=0):
    steps = [{"type": "spend_charge_damage", "damage_per_charge": d, "min_spend": 2, "max_spend": 0},
             {"type": "charge_lock", "turns": lock}]
    if splash:
        steps.insert(1, {"type": "retarget", "targeting": {"type": "aoe", "radius": 2, "enemies_only": True},
                         "do": {"type": "damage", "amount": splash}})
    return {"type": "sequence", "steps": steps}
rewrite("arcanist_overcharge", "top", (OT.format(d=5), oc(5)), {
    1: ("Overcharge+", OT.format(d=6), oc(6)),
    2: ("Power Overwhelming", OT.format(d=7), oc(7), "Power Overwhelming: 7 damage per Charge."),
    3: ("Controlled Burn", OT1.format(d=7), oc(7, 1), "Controlled Burn: the lockout ends with this turn."),
    4: ("Singularity Blast", OT1.format(d=7).replace(" Your Charge", " Deal 7 to every enemy within 2 of it. Your Charge"),
        oc(7, 1, 7), "Singularity Blast: the overflow splashes."),
})
BT = "Pen {what} from your hand. {its} top half{s} cost{v} no Charge to cast from your leaf."
def bt(n, dr=0):
    t = BT.format(what="a card" if n == 1 else f"up to {n} cards", its="Its" if n == 1 else "Their",
                  s="" if n == 1 else "s", v="s" if n == 1 else "")
    return t + (f" Draw {dr} card." if dr else "")
def bind(n, dr=0): return seq(pen("hand", n, up_to=n > 1, free_top=True), draw(dr))
rewrite("arcanist_overcharge", "bottom", (bt(1), bind(1)), {
    1: ("Tome Bind+", bt(1, 1), bind(1, 1)),
    2: ("Spellbook Bind", bt(2), bind(2), "Spellbook Bind: two pages."),
    3: ("Grand Tome", bt(2, 1), bind(2, 1), "Grand Tome."),
    4: ("The Living Library", bt(3, 1), bind(3, 1), "The Living Library: the whole leaf."),
}, targeting=SELF)

# ── Infinity Loop (bottom): the Living Spell rework ─────────────────────
LT = ("Memorize a spell from your hand onto your leaf's fourth page for the rest of the fight. "
      "At the end of each of your turns, the leaf recites its top half at the nearest enemy for 1 Charge (skipped if you have none).")
def lv(c, dr=0): return seq({"type": "memorize"}, charge(c), draw(dr))
def lt(c, dr=0):
    t = LT + (f" Gain {c} Charge." if c else "")
    return t + (f" Draw {dr} card." if dr else "")
rewrite("arcanist_infinity_loop", "bottom", (lt(0), lv(0)), {
    1: ("Living Spell+", lt(1), lv(1)),
    2: ("Animate Spell", lt(2), lv(2), "Animate Spell: Charge to feed it."),
    3: ("Greater Living Spell", lt(2, 1), lv(2, 1), "Greater Living Spell."),
    4: ("Avatar of Magic", lt(3, 1), lv(3, 1), "Avatar of Magic."),
}, targeting=SELF)

# ── Cascade Bolt (bottom) ───────────────────────────────────────────────
def cs(dr, spend=True, sh=0):
    return seq({"type": "spend_charge", "amount": 1} if spend else None, draw(dr),
               {"type": "shield", "amount": sh} if sh else None)
def ct(dr, spend=True, sh=0):
    t = ("Spend 1 Charge. " if spend else "") + f"Draw {dr} card{'s' if dr > 1 else ''}."
    return t + (f" Gain {sh} shield." if sh else "")
rewrite("arcanist_cascade_bolt", "bottom", (ct(1), cs(1)), {
    1: ("Arcane Study+", ct(2), cs(2)),
    2: ("Deep Study", ct(2, True, 3), cs(2, True, 3), "Deep Study: and a little shield."),
    3: ("Study Tome", ct(3), cs(3), "Study Tome: three cards."),
    4: ("Endless Study", ct(3, False), cs(3, False), "Endless Study: no Charge needed."),
}, name="Arcane Study", targeting=SELF)
print("cards ok")
