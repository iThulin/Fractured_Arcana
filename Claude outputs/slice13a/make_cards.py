import json, pathlib

def seq(*steps):
    steps = [s for s in steps if s]
    return steps[0] if len(steps) == 1 else {"type": "sequence", "steps": list(steps)}
def weave(n): return {"type": "gain_weave", "amount": n} if n else None
def name(trigger, dmg, dur, **flags):
    d = {"type": "name_condition", "trigger": trigger, "damage": dmg, "duration": dur}
    d.update(flags); return d
def unit(rng=6, los=True): return {"type": "unit", "range": rng, "los": los, "enemies_only": True}
SELF = {"type": "self"}
def aoe(radius, do):
    return {"type": "retarget", "targeting": {"type": "aoe", "radius": radius, "enemies_only": True}, "do": do}
def tile(rng=6): return {"type": "tile", "range": rng}
def ifnamed(base, named):
    return {"type": "conditional", "if": {"type": "target_named"}, "then": named, "else": base}
def dmg(n): return {"type": "damage", "amount": n}
def draw(n): return {"type": "draw", "count": n}

def half(nm, mana, speed, text, targeting, effect, **extra):
    h = {"name": nm, "mana": mana, "speed": speed, "rules_text": text, "tags": ["enchant"],
         "targeting": targeting, "effect": effect}
    h.update(extra); return h

def tier_changes(side, spec):
    ch = []
    for k in ("targeting", "effect", "rules_text"):
        if k in spec:
            ch.append({"half": side, "field": k, "value": spec[k]})
    return ch

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

# ── 1. Oath-Breaker's Price (C) ─────────────────────────────────────────
made.append(card("enchanter_oath_breakers_price", "Oath-Breaker's Price", "Common",
    half("Oath-Breaker's Price", 1, "Studied",
         "Name an enemy for 2 turns: if it attacks, it takes 5. Gain 1 Weave.",
         unit(), seq(name("attack", 5, 2), weave(1))),
    half("Collect the Debt", 1, "Reflex",
         "Deal 3 damage, or 6 if the target is Named.",
         unit(5), ifnamed(dmg(3), dmg(6))),
    {"desc": "The oath binds tighter. The debt grows.",
     "top": {"name": "Oath-Breaker's Price+", "effect": seq(name("attack", 6, 2), weave(1)),
             "rules_text": "Name an enemy for 2 turns: if it attacks, it takes 6. Gain 1 Weave."},
     "bottom": {"name": "Collect the Debt+", "effect": ifnamed(dmg(4), dmg(8)),
                "rules_text": "Deal 4 damage, or 8 if the target is Named."}},
    [(2, "top", {"name": "Binding Oath", "desc": "Binding Oath: a heavier price, more Weave.",
                 "effect": seq(name("attack", 7, 2), weave(2)),
                 "rules_text": "Name an enemy for 2 turns: if it attacks, it takes 7. Gain 2 Weave."}),
     (2, "bottom", {"name": "Call the Debt", "desc": "Call the Debt: the Named pay more.",
                    "effect": ifnamed(dmg(4), dmg(9)),
                    "rules_text": "Deal 4 damage, or 9 if the target is Named."}),
     (3, "top", {"name": "Oath of Iron", "desc": "Oath of Iron: spells break it too.",
                 "effect": seq(name(["attack", "cast"], 7, 3), weave(2)),
                 "rules_text": "Name an enemy for 3 turns: if it attacks or casts, it takes 7. Gain 2 Weave."}),
     (3, "bottom", {"name": "Debt Collector", "desc": "Debt Collector: collecting pays you.",
                    "effect": ifnamed(dmg(5), seq(dmg(10), draw(1))),
                    "rules_text": "Deal 5 damage, or 10 and draw a card if the target is Named."}),
     (4, "top", {"name": "Unbreakable Oath", "desc": "Unbreakable Oath: every breach is paid.",
                 "effect": seq(name(["attack", "cast"], 9, 3, per_trigger=True), weave(2)),
                 "rules_text": "Name an enemy for 3 turns: each time it attacks or casts, it takes 9. Gain 2 Weave."}),
     (4, "bottom", {"name": "Final Notice", "desc": "Final Notice: the last demand.",
                    "effect": ifnamed(dmg(6), seq(dmg(13), draw(1), weave(1))),
                    "rules_text": "Deal 6 damage, or 13 if the target is Named. If it was Named, draw a card and gain 1 Weave."})]))

# ── 2. Forbidden Word (U) ───────────────────────────────────────────────
def echo(w, then_draw=0, else_w=0):
    then = seq(weave(w), draw(then_draw) if then_draw else None)
    c = {"type": "conditional", "if": {"type": "name_broken_recently"}, "then": then}
    if else_w:
        c["else"] = weave(else_w)
    return c
def fw(dmgv, dur, radius=0):
    n = name("cast", dmgv, dur, backfire=True)
    return aoe(radius, n) if radius else n
made.append(card("enchanter_forbidden_word", "Forbidden Word", "Uncommon",
    half("Forbidden Word", 2, "Studied",
         "Name an enemy for 2 turns: if it casts, the spell lands on its own tile instead.",
         unit(), fw(0, 2)),
    half("Echo of Breaking", 1, "Reflex",
         "If a Named enemy broke its condition this round or last, gain 2 Weave.",
         SELF, echo(2)),
    {"desc": "The word lingers. The echo carries.",
     "top": {"name": "Forbidden Word+", "effect": fw(0, 3),
             "rules_text": "Name an enemy for 3 turns: if it casts, the spell lands on its own tile instead."},
     "bottom": {"name": "Echo of Breaking+", "effect": echo(3),
                "rules_text": "If a Named enemy broke its condition this round or last, gain 3 Weave."}},
    [(2, "top", {"name": "Word of Recoil", "desc": "Word of Recoil: casting also hurts.",
                 "effect": fw(3, 3),
                 "rules_text": "Name an enemy for 3 turns: if it casts, the spell lands on its own tile instead, and it takes 3."}),
     (2, "bottom", {"name": "Resounding Echo", "desc": "Resounding Echo: the echo brings a card.",
                    "effect": echo(3, 1),
                    "rules_text": "If a Named enemy broke its condition this round or last, gain 3 Weave and draw 1 card."}),
     (3, "top", {"name": "Silencing Word", "desc": "Silencing Word: a whole circle of casters.",
                 "targeting": tile(), "effect": fw(3, 3, 1),
                 "rules_text": "Name every enemy within 1 of the target for 3 turns: if it casts, the spell lands on its own tile instead, and it takes 3."}),
     (3, "bottom", {"name": "Echoing Doom", "desc": "Echoing Doom: never silent.",
                    "effect": echo(3, 1, 1),
                    "rules_text": "If a Named enemy broke its condition this round or last, gain 3 Weave and draw 1 card. Otherwise, gain 1 Weave."}),
     (4, "top", {"name": "The Unspoken", "desc": "The Unspoken: a field where no spell is safe.",
                 "targeting": tile(), "effect": fw(5, 3, 2),
                 "rules_text": "Name every enemy within 2 of the target for 3 turns: if it casts, the spell lands on its own tile instead, and it takes 5."}),
     (4, "bottom", {"name": "Chorus of Breaking", "desc": "Chorus of Breaking: every break sings.",
                    "effect": echo(4, 2, 1),
                    "rules_text": "If a Named enemy broke its condition this round or last, gain 4 Weave and draw 2 cards. Otherwise, gain 1 Weave."})]))

# ── 3. Terms and Conditions (U) ─────────────────────────────────────────
def terms(dmgv, dur, radius, w):
    return seq(aoe(radius, name("act", dmgv, dur, grouped=True)), weave(w))
def fine(cap, w=0, d=0):
    return seq(draw(d) if d else None, {"type": "draw_on_break", "cap": cap}, weave(w))
made.append(card("enchanter_terms_and_conditions", "Terms and Conditions", "Uncommon",
    half("Terms and Conditions", 2, "Studied",
         "Name every enemy within 2 of the target for 1 turn: whichever moves, attacks or casts first takes 6, and the others go free.",
         tile(), terms(6, 1, 2, 0)),
    half("Fine Print", 1, "Reflex",
         "Until your next turn, whenever a Named enemy breaks its condition, draw 1 card (up to 3).",
         SELF, fine(3)),
    {"desc": "Harsher terms. Finer print.",
     "top": {"name": "Terms and Conditions+", "effect": terms(8, 1, 2, 0),
             "rules_text": "Name every enemy within 2 of the target for 1 turn: whichever moves, attacks or casts first takes 8, and the others go free."},
     "bottom": {"name": "Fine Print+", "effect": fine(4),
                "rules_text": "Until your next turn, whenever a Named enemy breaks its condition, draw 1 card (up to 4)."}},
    [(2, "top", {"name": "Binding Terms", "desc": "Binding Terms: the signing feeds the Weave.",
                 "effect": terms(8, 1, 2, 1),
                 "rules_text": "Name every enemy within 2 of the target for 1 turn: whichever moves, attacks or casts first takes 8, and the others go free. Gain 1 Weave."}),
     (2, "bottom", {"name": "Small Print", "desc": "Small Print: and a little Weave.",
                    "effect": fine(4, 1),
                    "rules_text": "Until your next turn, whenever a Named enemy breaks its condition, draw 1 card (up to 4). Gain 1 Weave."}),
     (3, "top", {"name": "Ironclad Terms", "desc": "Ironclad Terms: two turns to slip.",
                 "effect": terms(10, 2, 2, 1),
                 "rules_text": "Name every enemy within 2 of the target for 2 turns: whichever moves, attacks or casts first takes 10, and the others go free. Gain 1 Weave."}),
     (3, "bottom", {"name": "Hidden Clause", "desc": "Hidden Clause: a card up front.",
                    "effect": fine(4, 1, 1),
                    "rules_text": "Draw 1 card. Until your next turn, whenever a Named enemy breaks its condition, draw 1 card (up to 4). Gain 1 Weave."}),
     (4, "top", {"name": "Terms of Surrender", "desc": "Terms of Surrender: the whole field signs.",
                 "effect": terms(12, 2, 3, 2),
                 "rules_text": "Name every enemy within 3 of the target for 2 turns: whichever moves, attacks or casts first takes 12, and the others go free. Gain 2 Weave."}),
     (4, "bottom", {"name": "The Whole Contract", "desc": "The Whole Contract: nothing left unread.",
                    "effect": fine(5, 1, 1),
                    "rules_text": "Draw 1 card. Until your next turn, whenever a Named enemy breaks its condition, draw 1 card (up to 5). Gain 1 Weave."})]))

# ── 4. The Price Compounds (R) ──────────────────────────────────────────
def comp(g, w=0): return seq({"type": "compound_names", "growth": g}, weave(w))
def usury(d, dur, w=0): return seq(name("act", d, dur, per_trigger=True), weave(w))
CT = "For the rest of the fight, each time a Named enemy breaks its condition, its Name lasts 1 more turn and its penalty grows by {g}."
UT = "Name an enemy for {dur} turns: each time it moves, attacks or casts, it takes {d}."
made.append(card("enchanter_the_price_compounds", "The Price Compounds", "Rare",
    half("The Price Compounds", 3, "Studied", CT.format(g=2), SELF, comp(2)),
    half("Usury", 2, "Reflex", UT.format(dur=2, d=4), unit(), usury(4, 2)),
    {"desc": "Interest accrues faster.",
     "top": {"name": "The Price Compounds+", "effect": comp(3), "rules_text": CT.format(g=3)},
     "bottom": {"name": "Usury+", "effect": usury(5, 2), "rules_text": UT.format(dur=2, d=5)}},
    [(2, "top", {"name": "Mounting Debt", "desc": "Mounting Debt: the ledger opens with Weave.",
                 "effect": comp(3, 2), "rules_text": CT.format(g=3) + " Gain 2 Weave."}),
     (2, "bottom", {"name": "Predatory Terms", "desc": "Predatory Terms: a longer loan.",
                    "effect": usury(5, 3), "rules_text": UT.format(dur=3, d=5)}),
     (3, "top", {"name": "Debt Spiral", "desc": "Debt Spiral: each breach costs more.",
                 "effect": comp(4, 2), "rules_text": CT.format(g=4) + " Gain 2 Weave."}),
     (3, "bottom", {"name": "Crushing Usury", "desc": "Crushing Usury: and the Weave grows.",
                    "effect": usury(6, 3, 1), "rules_text": UT.format(dur=3, d=6) + " Gain 1 Weave."}),
     (4, "top", {"name": "The Ledger Never Closes", "desc": "The Ledger Never Closes.",
                 "effect": comp(5, 3), "rules_text": CT.format(g=5) + " Gain 3 Weave."}),
     (4, "bottom", {"name": "Ruinous Usury", "desc": "Ruinous Usury: ruin, at interest.",
                    "effect": usury(7, 3, 2), "rules_text": UT.format(dur=3, d=7) + " Gain 2 Weave."})]))

# ── 5. Puppet's Errand (C) ──────────────────────────────────────────────
def errand_t(n): return {"type": "unit_then_tile", "range": 6, "dest_range": n, "enemies_only": True}
def errand(n, w=0, nm=None): return seq({"type": "compel_walk", "tiles": n, "mode": "chosen_path"}, nm, weave(w))
ET = "Compel target enemy to walk up to {n} tiles to a tile you choose, by the shortest path. Your glyphs on its path trigger."
def guard(sh, w): return seq({"type": "shield", "amount": sh}, weave(w))
GT = "Gain {sh} shield. Gain {w} Weave."
made.append(card("enchanter_puppets_errand", "Puppet's Errand", "Common",
    half("Puppet's Errand", 1, "Studied", ET.format(n=3), errand_t(3), errand(3)),
    half("Cut Strings", 1, "Reflex", GT.format(sh=3, w=1), SELF, guard(3, 1)),
    {"desc": "A longer errand. A thicker guard.",
     "top": {"name": "Puppet's Errand+", "targeting": errand_t(4), "effect": errand(4), "rules_text": ET.format(n=4)},
     "bottom": {"name": "Cut Strings+", "effect": guard(4, 1), "rules_text": GT.format(sh=4, w=1)}},
    [(2, "top", {"name": "Long Errand", "desc": "Long Errand: every step feeds the Weave.",
                 "targeting": errand_t(4), "effect": errand(4, 1), "rules_text": ET.format(n=4) + " Gain 1 Weave."}),
     (2, "bottom", {"name": "Silk Guard", "desc": "Silk Guard: more shield.",
                    "effect": guard(5, 1), "rules_text": GT.format(sh=5, w=1)}),
     (3, "top", {"name": "Marionette", "desc": "Marionette: it walks the whole stage.",
                 "targeting": errand_t(5), "effect": errand(5, 1), "rules_text": ET.format(n=5) + " Gain 1 Weave."}),
     (3, "bottom", {"name": "Woven Guard", "desc": "Woven Guard: woven tighter.",
                    "effect": guard(6, 2), "rules_text": GT.format(sh=6, w=2)}),
     (4, "top", {"name": "Master of Strings", "desc": "Master of Strings: and it stays where you left it.",
                 "targeting": errand_t(5), "effect": errand(5, 1, name("move", 4, 2)),
                 "rules_text": ET.format(n=5) + " Then Name it for 2 turns: if it moves, it takes 4. Gain 1 Weave."}),
     (4, "bottom", {"name": "Puppet's Aegis", "desc": "Puppet's Aegis.",
                    "effect": guard(8, 2), "rules_text": GT.format(sh=8, w=2)})]))

# ── 6. Speak It Wrongly (U) ─────────────────────────────────────────────
def mis(d, w=0, radius=0):
    m = {"type": "misdirect_attack", "damage": d, "duration": 1}
    return seq(aoe(radius, m) if radius else m, weave(w))
def step(n, w=0, nm=None): return seq({"type": "compel_walk", "tiles": n}, nm, weave(w))
MT = "Name an enemy for 1 turn: its locked attack moves onto the nearest other enemy."
made.append(card("enchanter_speak_it_wrongly", "Speak It Wrongly", "Uncommon",
    half("Speak It Wrongly", 2, "Studied", MT, unit(6, False), mis(0)),
    half("Misstep", 1, "Reflex", "Compel target enemy to walk 1 tile toward you.", unit(4, False), step(1)),
    {"desc": "The wrong name, the right Weave. A longer misstep.",
     "top": {"name": "Speak It Wrongly+", "effect": mis(0, 1), "rules_text": MT + " Gain 1 Weave."},
     "bottom": {"name": "Misstep+", "effect": step(2), "rules_text": "Compel target enemy to walk up to 2 tiles toward you."}},
    [(2, "top", {"name": "Misnomer", "desc": "Misnomer: the blow costs it too.",
                 "effect": mis(3, 1),
                 "rules_text": "Name an enemy for 1 turn: its locked attack moves onto the nearest other enemy, and if it attacks it takes 3. Gain 1 Weave."}),
     (2, "bottom", {"name": "Stumble", "desc": "Stumble: and the Weave tightens.",
                    "effect": step(2, 1), "rules_text": "Compel target enemy to walk up to 2 tiles toward you. Gain 1 Weave."}),
     (3, "top", {"name": "Word of Discord", "desc": "Word of Discord: it hurts more.",
                 "effect": mis(5, 2),
                 "rules_text": "Name an enemy for 1 turn: its locked attack moves onto the nearest other enemy, and if it attacks it takes 5. Gain 2 Weave."}),
     (3, "bottom", {"name": "Lured Step", "desc": "Lured Step: three tiles.",
                    "effect": step(3, 1), "rules_text": "Compel target enemy to walk up to 3 tiles toward you. Gain 1 Weave."}),
     (4, "top", {"name": "Babel", "desc": "Babel: a whole knot of enemies, all misnamed.",
                 "targeting": tile(), "effect": mis(4, 2, 1),
                 "rules_text": "Name every enemy within 1 of the target for 1 turn: each one's locked attack moves onto its nearest ally, and if it attacks it takes 4. Gain 2 Weave."}),
     (4, "bottom", {"name": "Beckoning Word", "desc": "Beckoning Word: come, and stay.",
                    "effect": step(3, 1, name("move", 3, 1)),
                    "rules_text": "Compel target enemy to walk up to 3 tiles toward you. Then Name it for 1 turn: if it moves, it takes 3. Gain 1 Weave."})]))

# ── 7. Borrowed Will (R) ────────────────────────────────────────────────
def will_t(n): return {"type": "unit_then_tile", "range": 6, "dest_range": n, "enemies_only": True, "any_tile": True}
def will(w=0, nm=None): return seq({"type": "borrow_will"}, nm, weave(w))
WT = "Seize a non-elite enemy's next attack: choose any tile within {n} of it for it to strike. It moves to reach that tile on its turn."
def notme(dur, w=0, atk=0):
    n = name("attack" if atk else "none", atk, dur, shun=True)
    return seq(n, weave(w))
NT = "Name an enemy for {dur} turn{s}: it can't target you (it must pick another unit)."
REQ = {"requires": ["non_elite_target"]}
made.append(card("enchanter_borrowed_will", "Borrowed Will", "Rare",
    half("Borrowed Will", 3, "Studied", WT.format(n=4), will_t(4), will(), **REQ),
    half("Not Me", 2, "Reflex", NT.format(dur=1, s=""), unit(), notme(1)),
    {"desc": "A longer reach. A firmer refusal.",
     "top": {"name": "Borrowed Will+", "targeting": will_t(5), "effect": will(), "rules_text": WT.format(n=5)},
     "bottom": {"name": "Not Me+", "effect": notme(1, 1), "rules_text": NT.format(dur=1, s="") + " Gain 1 Weave."}},
    [(2, "top", {"name": "Stolen Will", "desc": "Stolen Will: the theft feeds the Weave.",
                 "targeting": will_t(5), "effect": will(1), "rules_text": WT.format(n=5) + " Gain 1 Weave."}),
     (2, "bottom", {"name": "Not Me, Not Now", "desc": "Not Me, Not Now: two turns.",
                    "effect": notme(2, 1), "rules_text": NT.format(dur=2, s="s") + " Gain 1 Weave."}),
     (3, "top", {"name": "Puppet Will", "desc": "Puppet Will: and it pays for the blow.",
                 "targeting": will_t(5), "effect": will(2, name("attack", 4, 1)),
                 "rules_text": WT.format(n=5) + " Name it for 1 turn: if it attacks, it takes 4. Gain 2 Weave."}),
     (3, "bottom", {"name": "Look Elsewhere", "desc": "Look Elsewhere: more Weave.",
                    "effect": notme(2, 2), "rules_text": NT.format(dur=2, s="s") + " Gain 2 Weave."}),
     (4, "top", {"name": "Usurped Will", "desc": "Usurped Will: the whole field is in reach.",
                 "targeting": will_t(6), "effect": will(2, name("attack", 6, 1)),
                 "rules_text": WT.format(n=6) + " Name it for 1 turn: if it attacks, it takes 6. Gain 2 Weave."}),
     (4, "bottom", {"name": "Beneath Notice", "desc": "Beneath Notice: and attacking costs it.",
                    "effect": notme(2, 1, 4),
                    "rules_text": "Name an enemy for 2 turns: it can't target you (it must pick another unit), and if it attacks it takes 4. Gain 1 Weave."})]))
print("\n".join(made))
