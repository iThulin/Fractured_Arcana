import json, pathlib

def seq(*steps):
    steps = [s for s in steps if s]
    return steps[0] if len(steps) == 1 else {"type": "sequence", "steps": list(steps)}
def weave(n): return {"type": "gain_weave", "amount": n} if n else None
def draw(n): return {"type": "draw", "count": n} if n else None
def shield(n): return {"type": "shield", "amount": n} if n else None
def boon(dur, **f):
    d = {"type": "name_condition", "trigger": "none", "damage": 0, "duration": dur, "allies": True}
    d.update(f); return d
def name(trigger, dmg, dur, **flags):
    d = {"type": "name_condition", "trigger": trigger, "damage": dmg, "duration": dur}
    d.update(flags); return d
SELF = {"type": "self"}
def ally(rng=6): return {"type": "unit", "range": rng, "los": False}
def enemy(rng=6): return {"type": "unit", "range": rng, "los": False, "enemies_only": True}
def tile(rng): return {"type": "tile", "range": rng}

def half(nm, mana, speed, text, targeting, effect, glyph=None, **extra):
    h = {"name": nm, "mana": mana, "speed": speed, "rules_text": text}
    if glyph:
        h["glyph_text"] = glyph
    h["tags"] = ["enchant", "glyph"] if glyph else ["enchant"]
    h["targeting"] = targeting
    h["effect"] = effect
    h.update(extra); return h

def tier_changes(side, spec):
    return [{"half": side, "field": k, "value": spec[k]}
            for k in ("targeting", "effect", "rules_text", "glyph_text") if k in spec]

def card(cid, nm, rarity, top, bottom, t1, tiers):
    ups = [{"tier": 1, "half": "both", "top_name": t1["top"]["name"], "bottom_name": t1["bottom"]["name"],
            "description": t1["desc"],
            "changes": tier_changes("top", t1["top"]) + tier_changes("bottom", t1["bottom"])}]
    for tier, side, spec in tiers:
        ups.append({"tier": tier, "half": side, f"{side}_name": spec["name"], "description": spec["desc"],
                    "changes": tier_changes(side, spec)})
    d = {"id": cid, "status": "ready", "name": nm, "school": "Enchanter", "rarity": rarity,
         "top": top, "bottom": bottom, "upgrades": ups}
    pathlib.Path(f"Data/Cards/{cid}.json").write_text(json.dumps(d, indent='\t', ensure_ascii=False) + '\n', encoding='utf-8')
    return cid

made = []

# ── Name of Courage (C) ─────────────────────────────────────────────────
def courage(dmg, dur, w=0): return seq(boon(dur, bonus_damage=dmg, immune_weakened=True), weave(w))
CT = "Name an ally for {dur} turns: it deals {d} more damage and can't be Weakened."
def count(mx, d=0): return seq({"type": "weave_per_named", "max": mx}, draw(d))
NT = "Gain 1 Weave per Named unit (max {mx})."
made.append(card("enchanter_name_of_courage", "Name of Courage", "Common",
    half("Name of Courage", 1, "Studied", CT.format(dur=3, d=2), ally(), courage(2, 3)),
    half("Count the Named", 1, "Reflex", NT.format(mx=3), SELF, count(3)),
    {"desc": "Braver still. More names to count.",
     "top": {"name": "Name of Courage+", "effect": courage(3, 3), "rules_text": CT.format(dur=3, d=3)},
     "bottom": {"name": "Count the Named+", "effect": count(4), "rules_text": NT.format(mx=4)}},
    [(2, "top", {"name": "Name of Valor", "desc": "Name of Valor: and the Weave answers.",
                 "effect": courage(3, 3, 1), "rules_text": CT.format(dur=3, d=3) + " Gain 1 Weave."}),
     (2, "bottom", {"name": "Roll Call", "desc": "Roll Call: and a card.",
                    "effect": count(4, 1), "rules_text": NT.format(mx=4) + " Draw 1 card."}),
     (3, "top", {"name": "Name of Heroes", "desc": "Name of Heroes: a hero's strength.",
                 "effect": courage(4, 3, 1), "rules_text": CT.format(dur=3, d=4) + " Gain 1 Weave."}),
     (3, "bottom", {"name": "Census of Names", "desc": "Census of Names: count them all.",
                    "effect": count(5, 1), "rules_text": NT.format(mx=5) + " Draw 1 card."}),
     (4, "top", {"name": "Name of Legends", "desc": "Name of Legends: a name remembered.",
                 "effect": courage(5, 4, 2), "rules_text": CT.format(dur=4, d=5) + " Gain 2 Weave."}),
     (4, "bottom", {"name": "The Great Roll", "desc": "The Great Roll.",
                    "effect": count(6, 1), "rules_text": NT.format(mx=6) + " Draw 1 card."})]))

# ── Name of Warding (U) ─────────────────────────────────────────────────
def ward(dur, w=0, **f): return seq(boon(dur, warding=True, **f), weave(w))
WT = "Name an ally for {dur} turns: whenever it's attacked, the attacker is Weakened for 1 turn and you gain 1 Weave."
PASS = {"type": "unit_then_unit", "range": 6, "dest_range": 6, "friendlies_only": True}
def passn(w=0, d=0): return seq({"type": "move_name"}, weave(w), draw(d))
PT = "Move one of your Names from one ally to another."
made.append(card("enchanter_name_of_warding", "Name of Warding", "Uncommon",
    half("Name of Warding", 2, "Studied", WT.format(dur=3), ally(), ward(3)),
    half("Pass the Name", 1, "Reflex", PT, PASS, passn()),
    {"desc": "The ward holds longer. The Name passes with a gift.",
     "top": {"name": "Name of Warding+", "effect": ward(4), "rules_text": WT.format(dur=4)},
     "bottom": {"name": "Pass the Name+", "effect": passn(1), "rules_text": PT + " Gain 1 Weave."}},
    [(2, "top", {"name": "Name of the Bulwark", "desc": "Name of the Bulwark: it can't be Weakened either.",
                 "effect": ward(4, 0, immune_weakened=True),
                 "rules_text": WT.format(dur=4) + " It can't be Weakened."}),
     (2, "bottom", {"name": "Passing Grace", "desc": "Passing Grace: and a card.",
                    "effect": passn(1, 1), "rules_text": PT + " Gain 1 Weave. Draw 1 card."}),
     (3, "top", {"name": "Name of Thorns", "desc": "Name of Thorns: it strikes back harder.",
                 "effect": ward(4, 0, immune_weakened=True, bonus_damage=2),
                 "rules_text": WT.format(dur=4) + " It can't be Weakened and deals 2 more damage."}),
     (3, "bottom", {"name": "Shared Name", "desc": "Shared Name: more Weave.",
                    "effect": passn(2, 1), "rules_text": PT + " Gain 2 Weave. Draw 1 card."}),
     (4, "top", {"name": "Name of the Fortress", "desc": "Name of the Fortress.",
                 "effect": ward(5, 1, immune_weakened=True, bonus_damage=2),
                 "rules_text": WT.format(dur=5) + " It can't be Weakened and deals 2 more damage. Gain 1 Weave."}),
     (4, "bottom", {"name": "Hand to Hand", "desc": "Hand to Hand: the Name never drops.",
                    "effect": passn(2, 2), "rules_text": PT + " Gain 2 Weave. Draw 2 cards."})]))

# ── Litany of Names (U) ─────────────────────────────────────────────────
def litany(dur, disc=1, dmg=0, w=0):
    f = {"all_allies": True, "first_card_discount": disc}
    if dmg: f["bonus_damage"] = dmg
    return seq(boon(dur, **f), weave(w))
def lt(dur, disc=1, dmg=0, w=0):
    t = f"Name every ally for {dur} turns: each one's first card each turn costs {disc} less"
    t += f" and it deals {dmg} more damage." if dmg else "."
    return t + (f" Gain {w} Weave." if w else "")
def shields(per, w=0): return seq(weave(w), {"type": "shield_named_allies", "per_weave": per})
def st(per, w=0):
    mult = {2: "twice", 3: "three times", 4: "four times"}[per]
    return (f"Gain {w} Weave. Then each" if w else "Each") + f" Named ally gains shield equal to {mult} your Weave."
made.append(card("enchanter_litany_of_names", "Litany of Names", "Uncommon",
    half("Litany of Names", 3, "Studied", lt(2), SELF, litany(2)),
    half("Litany of Shields", 2, "Reflex", st(2), SELF, shields(2)),
    {"desc": "A longer litany. A louder psalm.",
     "top": {"name": "Litany of Names+", "effect": litany(3), "rules_text": lt(3)},
     "bottom": {"name": "Litany of Shields+", "effect": shields(2, 1), "rules_text": st(2, 1)}},
    [(2, "top", {"name": "Litany of Courage", "desc": "Litany of Courage: the names give strength.",
                 "effect": litany(3, 1, 1), "rules_text": lt(3, 1, 1)}),
     (2, "bottom", {"name": "Psalm of Shields", "desc": "Psalm of Shields.",
                    "effect": shields(3), "rules_text": st(3)}),
     (3, "top", {"name": "Litany of Heroes", "desc": "Litany of Heroes.",
                 "effect": litany(3, 1, 2, 1), "rules_text": lt(3, 1, 2, 1)}),
     (3, "bottom", {"name": "Hymn of Shields", "desc": "Hymn of Shields.",
                    "effect": shields(3, 1), "rules_text": st(3, 1)}),
     (4, "top", {"name": "The Great Litany", "desc": "The Great Litany.",
                 "effect": litany(3, 2, 2, 1), "rules_text": lt(3, 2, 2, 1)}),
     (4, "bottom", {"name": "Choir of Shields", "desc": "Choir of Shields.",
                    "effect": shields(3, 2), "rules_text": st(3, 2)})]))

# ── Inscribe (C) ────────────────────────────────────────────────────────
def insc(fb, w=0):
    d = {"type": "inscribe", "fallback_damage": fb}
    if w: d["weave_on_trigger"] = w
    return d
def it(rng, w=0):
    t = f"Discard a card from your hand. Prepare a glyph within {rng} that resolves that card's bottom half on the enemy that triggers it."
    return t + (f" You gain {w} Weave when it does." if w else "")
IG = "It suffers the bottom half of the card you discarded."
def note(w, d, sh=0): return seq(weave(w), draw(d), shield(sh))
def nt(w, d, sh=0):
    t = f"Gain {w} Weave. Draw {d} card{'s' if d > 1 else ''}."
    return t + (f" Gain {sh} shield." if sh else "")
made.append(card("enchanter_inscribe", "Inscribe", "Common",
    half("Inscribe", 1, "Studied", it(3), tile(3), insc(3), glyph=IG),
    half("Margin Note", 1, "Reflex", nt(1, 1), SELF, note(1, 1)),
    {"desc": "A longer reach. A fuller margin.",
     "top": {"name": "Inscribe+", "targeting": tile(4), "effect": insc(3), "rules_text": it(4)},
     "bottom": {"name": "Margin Note+", "effect": note(2, 1), "rules_text": nt(2, 1)}},
    [(2, "top", {"name": "Deep Inscription", "desc": "Deep Inscription: the Weave answers it.",
                 "targeting": tile(4), "effect": insc(3, 2), "rules_text": it(4, 2)}),
     (2, "bottom", {"name": "Annotation", "desc": "Annotation: and a little shield.",
                    "effect": note(2, 1, 2), "rules_text": nt(2, 1, 2)}),
     (3, "top", {"name": "Indelible Inscription", "desc": "Indelible Inscription: further still.",
                 "targeting": tile(5), "effect": insc(5, 2), "rules_text": it(5, 2)}),
     (3, "bottom", {"name": "Footnote", "desc": "Footnote: two cards.",
                    "effect": note(2, 2), "rules_text": nt(2, 2)}),
     (4, "top", {"name": "Living Inscription", "desc": "Living Inscription.",
                 "targeting": tile(5), "effect": insc(5, 3), "rules_text": it(5, 3)}),
     (4, "bottom", {"name": "Marginalia", "desc": "Marginalia.",
                    "effect": note(3, 2), "rules_text": nt(3, 2)})]))

# ── Tripwire Sentence (C) ───────────────────────────────────────────────
def wire_t(rng): return {"type": "tile_then_tile", "range": rng, "dest_range": 1, "friendly_glyphs": False}
def wire(d, w=0):
    g = {"type": "prepare_glyph", "trigger": "enter", "damage": d, "halt_movement": True, "count": 2, "pair": True}
    return seq(g, weave(w))
def wt(rng, d, w=0):
    t = f"Prepare a pair of glyphs on 2 adjacent tiles within {rng}. When an enemy enters either: {d} damage and its movement ends. Then the other glyph is spent too."
    return t + (f" Gain {w} Weave." if w else "")
WG = "It takes {damage} damage and stops moving. The other end of the wire is spent."
def trip(n, w=0, nm=None): return seq({"type": "compel_walk", "tiles": n}, nm, weave(w))
def tt(n, w=0, extra=""):
    t = f"Compel target enemy within 3 to walk {'1 tile' if n == 1 else f'up to {n} tiles'} toward you."
    return t + extra + (f" Gain {w} Weave." if w else "")
made.append(card("enchanter_tripwire_sentence", "Tripwire Sentence", "Common",
    half("Tripwire Sentence", 1, "Studied", wt(3, 3), wire_t(3), wire(3), glyph=WG),
    half("Trip", 1, "Reflex", tt(1), enemy(3), trip(1)),
    {"desc": "A sharper wire. A longer stumble.",
     "top": {"name": "Tripwire Sentence+", "effect": wire(4), "rules_text": wt(3, 4)},
     "bottom": {"name": "Trip+", "effect": trip(2), "rules_text": tt(2)}},
    [(2, "top", {"name": "Snapping Wire", "desc": "Snapping Wire: and the Weave tightens.",
                 "effect": wire(5, 1), "rules_text": wt(3, 5, 1)}),
     (2, "bottom", {"name": "Stumble Forward", "desc": "Stumble Forward.",
                    "effect": trip(2, 1), "rules_text": tt(2, 1)}),
     (3, "top", {"name": "Garrote Line", "desc": "Garrote Line: strung further out.",
                 "targeting": wire_t(4), "effect": wire(6, 1), "rules_text": wt(4, 6, 1)}),
     (3, "bottom", {"name": "Pull Taut", "desc": "Pull Taut: three tiles.",
                    "effect": trip(3, 1), "rules_text": tt(3, 1)}),
     (4, "top", {"name": "The Long Sentence", "desc": "The Long Sentence.",
                 "targeting": wire_t(5), "effect": wire(8, 2), "rules_text": wt(5, 8, 2)}),
     (4, "bottom", {"name": "Hangman's Step", "desc": "Hangman's Step: and it stays put.",
                    "effect": trip(3, 1, name("move", 4, 1)),
                    "rules_text": tt(3, 1, " Then Name it for 1 turn: if it moves, it takes 4.")})]))

# ── Seven-Layer Ward (R) ────────────────────────────────────────────────
def layers(d, halt=False, w=0):
    g = {"type": "prepare_glyph", "trigger": "enter", "damage": d, "layers": 7}
    if halt: g["halt_movement"] = True
    return seq(g, weave(w))
def lyt(d, halt=False, w=0):
    t = (f"Prepare a glyph within 4 with seven layers. Each time an enemy enters it: {d} damage"
         + (" and its movement ends" if halt else "")
         + ", and one layer peels away. It remains until all seven are gone.")
    return t + (f" Gain {w} Weave." if w else "")
LG = "It takes {damage} damage and one layer peels away."
LGH = "It takes {damage} damage, stops moving, and one layer peels away."
def recite(bonus=0, w=0):
    d = {"type": "trigger_all_glyphs", "consume": False}
    if bonus: d["bonus_per_other"] = bonus
    return seq(d, weave(w))
def rt(bonus=0, w=0):
    t = "Trigger every glyph you control once, without consuming them."
    if bonus: t += f" Each deals {bonus} more per other glyph triggered."
    return t + (f" Gain {w} Weave." if w else "")
made.append(card("enchanter_seven_layer_ward", "Seven-Layer Ward", "Rare",
    half("Seven-Layer Ward", 3, "Studied", lyt(3), tile(4), layers(3), glyph=LG),
    half("Recite the Layers", 2, "Reflex", rt(), SELF, recite()),
    {"desc": "Sharper layers. A louder recital.",
     "top": {"name": "Seven-Layer Ward+", "effect": layers(4), "rules_text": lyt(4)},
     "bottom": {"name": "Recite the Layers+", "effect": recite(0, 1), "rules_text": rt(0, 1)}},
    [(2, "top", {"name": "Seven-Layer Bastion", "desc": "Seven-Layer Bastion: every layer stops them.",
                 "effect": layers(4, True), "rules_text": lyt(4, True), "glyph_text": LGH}),
     (2, "bottom", {"name": "Resonant Layers", "desc": "Resonant Layers.",
                    "effect": recite(1, 1), "rules_text": rt(1, 1)}),
     (3, "top", {"name": "Seven Seals", "desc": "Seven Seals.",
                 "effect": layers(5, True), "rules_text": lyt(5, True), "glyph_text": LGH}),
     (3, "bottom", {"name": "Echoing Layers", "desc": "Echoing Layers.",
                    "effect": recite(1, 2), "rules_text": rt(1, 2)}),
     (4, "top", {"name": "The Seven Walls", "desc": "The Seven Walls.",
                 "effect": layers(6, True, 2), "rules_text": lyt(6, True, 2), "glyph_text": LGH}),
     (4, "bottom", {"name": "All Layers Sing", "desc": "All Layers Sing.",
                    "effect": recite(2, 2), "rules_text": rt(2, 2)})]))

# ── The Seventh Name (L) ────────────────────────────────────────────────
def sev(extra, g, w=0): return seq({"type": "seventh_name", "extra_turns": extra, "glyph_damage": g}, weave(w))
def svt(extra, g, w=0):
    t = (f"For the rest of the fight, your Names last {extra} extra turn{'s' if extra > 1 else ''}, and Naming an enemy also "
         f"prepares a glyph beneath it: if it starts its turn there, it takes {g}.")
    return t + (f" Gain {w} Weave." if w else "")
def every(d, dur, w=0): return seq(name("act", d, dur, all_enemies=True, per_trigger=True), weave(w))
def evt(d, dur, w=0):
    t = f"Name every enemy for {dur} turn{'s' if dur > 1 else ''}: each takes {d} whenever it moves, attacks or casts."
    return t + (f" Gain {w} Weave." if w else "")
made.append(card("enchanter_the_seventh_name", "The Seventh Name", "Legendary",
    half("The Seventh Name", 4, "Studied", svt(1, 4), SELF, sev(1, 4), once_per_fight=True),
    half("Speak Every Name", 3, "Reflex", evt(4, 1), SELF, every(4, 1)),
    {"desc": "The Name cuts deeper.",
     "top": {"name": "The Seventh Name+", "effect": sev(1, 5), "rules_text": svt(1, 5)},
     "bottom": {"name": "Speak Every Name+", "effect": every(5, 1), "rules_text": evt(5, 1)}},
    [(2, "top", {"name": "The Seventh Name, Spoken", "desc": "Spoken aloud, it feeds the Weave.",
                 "effect": sev(1, 6, 2), "rules_text": svt(1, 6, 2)}),
     (2, "bottom", {"name": "Speak Every Name Twice", "desc": "Two turns.",
                    "effect": every(5, 2), "rules_text": evt(5, 2)}),
     (3, "top", {"name": "The Seventh Name, Written", "desc": "Written down, it lasts.",
                 "effect": sev(2, 6, 2), "rules_text": svt(2, 6, 2)}),
     (3, "bottom", {"name": "Every Name Known", "desc": "Every Name Known.",
                    "effect": every(6, 2), "rules_text": evt(6, 2)}),
     (4, "top", {"name": "The Name Beneath All Names", "desc": "The Name Beneath All Names.",
                 "effect": sev(2, 8, 3), "rules_text": svt(2, 8, 3)}),
     (4, "bottom", {"name": "Every Name, Every Hour", "desc": "Every Name, Every Hour.",
                    "effect": every(7, 2, 2), "rules_text": evt(7, 2, 2)})]))
print("\n".join(made))
