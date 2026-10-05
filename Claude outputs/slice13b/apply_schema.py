import pathlib
p = pathlib.Path('Schemas/card.schema.json')
s = p.read_text(encoding='utf-8')
def rep(o, n):
    global s
    assert s.count(o) == 1, (o[:60], s.count(o))
    s = s.replace(o, n)
def B(k, d): return f'''				"{k}": {{
					"type": "boolean",
					"description": "{d}"
				}},
'''
def I(k, d): return f'''				"{k}": {{
					"type": "integer",
					"description": "{d}"
				}},
'''
props = (B("allies", "name_condition: Name allies (a boon that never breaks) instead of enemies.")
 + B("all_allies", "name_condition: Name every ally on the board, whatever the targets.")
 + B("all_enemies", "name_condition: Name every enemy on the board, whatever the targets.")
 + B("immune_weakened", "name_condition boon: the ally cannot be Weakened (Name of Courage).")
 + B("warding", "name_condition boon: whoever strikes the ally is Weakened 1 turn and the Namer gains 1 Weave (Name of Warding).")
 + I("first_card_discount", "name_condition boon: the ally's first card each turn costs this much less (Litany of Names).")
 + I("per_weave", "shield_named_allies: shield per point of the caster's Weave.")
 + I("fallback_damage", "inscribe: damage when no card was inscribed.")
 + I("weave_on_trigger", "inscribe: Weave the owner gains when the inscription goes off.")
 + I("extra_turns", "seventh_name: extra turns on every Name your side writes.")
 + I("glyph_damage", "seventh_name: damage of the glyph prepared beneath each Named enemy.")
 + I("layers", "prepare_glyph: Seven-Layer Ward. Each trigger peels one; the glyph stays until all are gone.")
 + B("pair", "prepare_glyph: the glyphs placed together form a pair; triggering one spends the others (Tripwire Sentence)."))
rep('''				"halt_movement": {''', props + '''				"halt_movement": {''')
rep('''						"compound_names"
					]''', '''						"compound_names",
						"weave_per_named",
						"move_name",
						"shield_named_allies",
						"inscribe",
						"seventh_name"
					]''')
p.write_text(s, encoding='utf-8')
print("schema ok")

s = p.read_text(encoding='utf-8')
rep('''				"any_tile": {''', '''				"friendly_glyphs": {
					"type": "boolean",
					"description": "tile_then_tile: both picks must hold one of your glyphs (default true; Glyph Warp). False for Tripwire Sentence."
				},
				"any_tile": {''')
p.write_text(s, encoding='utf-8')
