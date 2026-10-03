"""Slice 6 card data: Defer/Advance rewording (class_identity_chronomancer_v1 §4)
and the new card Written in the Stars (§3A). Round-trips the tab-indented format."""
import json, os, sys
root = sys.argv[1]

def load(name):
    return json.load(open(os.path.join(root, 'Data/Cards', name + '.json'), encoding='utf-8'))

def save(name, d):
    with open(os.path.join(root, 'Data/Cards', name + '.json'), 'w', encoding='utf-8') as f:
        f.write(json.dumps(d, indent='\t', ensure_ascii=False) + '\n')

def seq(*steps):
    return {"type": "sequence", "steps": list(steps)}

DEFER = {"type": "defer_intent"}
def defer(r=0): return {"type": "defer_intent", "radius": r} if r else dict(DEFER)
def advance(r=0): return {"type": "advance_intent", "radius": r} if r else {"type": "advance_intent"}
def fore(n): return {"type": "gain_foresight", "amount": n}
def status(s, d): return {"type": "apply_status", "status": s, "duration": d}
def draw(n): return {"type": "draw", "count": n}

def set_bottom_tier(d, tier, half, name, desc, effect, text):
    """Replace the bottom-half changes of one ladder entry, keeping any top-half ones."""
    for u in d['upgrades']:
        if u['tier'] == tier and u['half'] == half:
            u['changes'] = [c for c in u['changes'] if c['half'] != 'bottom']
            u['changes'] += [
                {"half": "bottom", "field": "effect", "value": effect},
                {"half": "bottom", "field": "rules_text", "value": text},
            ]
            u['bottom_name'] = name
            if half == 'bottom':
                u['description'] = desc
            return
    raise SystemExit(f"{d['id']}: no tier {tier}/{half}")

# ── Hasten & Hinder: bottom becomes Defer ────────────────────────────
d = load('chronomancer_hasten')
assert d['id'] == 'chronomancer_hasten_and_hinder'
d['bottom']['rules_text'] = "Defer target enemy's attack: it lands at the end of the round, on the tile it was aimed at."
d['bottom']['effect'] = defer()
set_bottom_tier(d, 1, 'both', 'Hinder+', None,
    seq(defer(), status('slowed', 1)),
    "Defer target enemy's attack. It is Slowed for 1 turn.")
set_bottom_tier(d, 2, 'bottom', 'Held Breath', "Defer, and bank the moment.",
    seq(defer(), status('slowed', 1), fore(1)),
    "Defer target enemy's attack. It is Slowed for 1 turn. Gain 1 Foresight.")
set_bottom_tier(d, 3, 'bottom', 'Gridlock', "Defer every attack around the target.",
    seq(defer(1), status('slowed', 1), fore(1)),
    "Defer the attacks of target enemy and every enemy within 1 of it. The target is Slowed for 1 turn. Gain 1 Foresight.")
set_bottom_tier(d, 4, 'bottom', 'The Frozen Stack', "Every attack waits for the end of the round.",
    seq(defer(99), fore(1)),
    "Defer every enemy's attack. Gain 1 Foresight.")
save('chronomancer_hasten', d)

# ── Sands of Time: bottom becomes Defer + Foresight ──────────────────
d = load('chronomancer_sands_of_time')
d['bottom']['rules_text'] = "Defer target enemy's attack: it lands at the end of the round, on the tile it was aimed at. Gain 1 Foresight."
d['bottom']['effect'] = seq(defer(), fore(1))
set_bottom_tier(d, 1, 'both', 'Drag+', None,
    seq(defer(), status('slowed', 1), fore(1)),
    "Defer target enemy's attack. It is Slowed for 1 turn. Gain 1 Foresight.")
set_bottom_tier(d, 2, 'bottom', 'Molasses', "The deferred enemy drags.",
    seq(defer(), status('temporal_drag', 2), fore(1)),
    "Defer target enemy's attack. It gains Temporal Drag for 2 turns. Gain 1 Foresight.")
set_bottom_tier(d, 3, 'bottom', 'Sandbag', "Drag the enemies around it too.",
    seq(defer(1), status('slowed', 1), fore(1)),
    "Defer the attacks of target enemy and every enemy within 1 of it. The target is Slowed for 1 turn. Gain 1 Foresight.")
set_bottom_tier(d, 4, 'bottom', 'Lost Hour', "A wide stretch of the round goes missing.",
    seq(defer(2), fore(2)),
    "Defer the attacks of target enemy and every enemy within 2 of it. Gain 2 Foresight.")
save('chronomancer_sands_of_time', d)

# ── New: Written in the Stars (U) ────────────────────────────────────
target_unit = {"type": "unit", "range": 6, "los": False, "enemies_only": True}
card = {
    "id": "chronomancer_written_in_the_stars",
    "status": "ready",
    "name": "Written in the Stars",
    "school": "Chronomancer",
    "rarity": "Uncommon",
    "top": {
        "name": "Written in the Stars",
        "mana": 2,
        "speed": "Studied",
        "rules_text": "Advance target enemy's attack: it lands now, on the tile it was aimed at. The enemy then takes its turn without it.",
        "tags": ["temporal"],
        "targeting": dict(target_unit),
        "effect": advance(),
    },
    "bottom": {
        "name": "Clear Sky",
        "mana": 1,
        "speed": "Reflex",
        "rules_text": "Gain 2 Foresight.",
        "tags": ["temporal"],
        "targeting": {"type": "self"},
        "effect": fore(2),
    },
    "upgrades": [
        {"tier": 1, "half": "both", "top_name": "Written in the Stars+", "bottom_name": "Clear Sky+",
         "description": "Advancing banks Foresight; the clear sky shows more.",
         "changes": [
             {"half": "top", "field": "effect", "value": seq(advance(), fore(1))},
             {"half": "top", "field": "rules_text", "value": "Advance target enemy's attack: it lands now, on the tile it was aimed at. Gain 1 Foresight."},
             {"half": "bottom", "field": "effect", "value": seq(fore(2), draw(1))},
             {"half": "bottom", "field": "rules_text", "value": "Gain 2 Foresight. Draw 1 card."},
         ]},
        {"tier": 2, "half": "top", "top_name": "Foretold", "description": "The enemy staggers after its early blow.",
         "changes": [
             {"half": "top", "field": "effect", "value": seq(advance(), status('slowed', 1), fore(1))},
             {"half": "top", "field": "rules_text", "value": "Advance target enemy's attack. It is Slowed for 1 turn. Gain 1 Foresight."},
         ]},
        {"tier": 2, "half": "bottom", "bottom_name": "Long Night", "description": "More Foresight.",
         "changes": [
             {"half": "bottom", "field": "effect", "value": seq(fore(3), draw(1))},
             {"half": "bottom", "field": "rules_text", "value": "Gain 3 Foresight. Draw 1 card."},
         ]},
        {"tier": 3, "half": "top", "top_name": "Fixed Star", "description": "Call a whole cluster of attacks forward.",
         "changes": [
             {"half": "top", "field": "effect", "value": seq(advance(1), fore(1))},
             {"half": "top", "field": "rules_text", "value": "Advance the attacks of target enemy and every enemy within 1 of it. Gain 1 Foresight."},
         ]},
        {"tier": 3, "half": "bottom", "bottom_name": "Starlit", "description": "Draw deeper.",
         "changes": [
             {"half": "bottom", "field": "effect", "value": seq(fore(3), draw(2))},
             {"half": "bottom", "field": "rules_text", "value": "Gain 3 Foresight. Draw 2 cards."},
         ]},
        {"tier": 4, "half": "top", "top_name": "The Appointed Hour", "description": "Everything near the target happens now.",
         "changes": [
             {"half": "top", "field": "effect", "value": seq(advance(2), fore(2))},
             {"half": "top", "field": "rules_text", "value": "Advance the attacks of target enemy and every enemy within 2 of it. Gain 2 Foresight."},
         ]},
        {"tier": 4, "half": "bottom", "bottom_name": "Every Star in Place", "description": "Fill the bank.",
         "changes": [
             {"half": "bottom", "field": "effect", "value": seq(fore(4), draw(2))},
             {"half": "bottom", "field": "rules_text", "value": "Gain 4 Foresight. Draw 2 cards."},
         ]},
    ],
}
path = os.path.join(root, 'Data/Cards/chronomancer_written_in_the_stars.json')
assert not os.path.exists(path), "card already exists"
with open(path, 'w', encoding='utf-8') as f:
    f.write(json.dumps(card, indent='\t', ensure_ascii=False) + '\n')
print("cards ok")
