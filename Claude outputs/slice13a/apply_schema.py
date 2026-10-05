import pathlib
p = pathlib.Path('Schemas/card.schema.json')
s = p.read_text(encoding='utf-8')
def rep(o, n):
    global s
    assert s.count(o) == 1, (o[:60], s.count(o))
    s = s.replace(o, n)
rep('''							"memorial_or_spirit_nearby",
							"friendly_glyph_tile"
						]''', '''							"memorial_or_spirit_nearby",
							"friendly_glyph_tile",
							"non_elite_target"
						]''')
rep('''						"dispel_walk",
						"name_condition"
					]''', '''						"dispel_walk",
						"name_condition",
						"misdirect_attack",
						"borrow_will",
						"draw_on_break",
						"compound_names"
					]''')
rep('''					"description": "Conduit Link redistribution mode. Used by: create_link.",
					"enum": [
						"split",
						"mirror",
						"halved"
					]
				},''', '''					"description": "Conduit Link redistribution mode. Used by: create_link. Also compel_walk: direction, toward_caster or chosen_path.",
					"enum": [
						"split",
						"mirror",
						"halved",
						"direction",
						"toward_caster",
						"chosen_path"
					]
				},
				"backfire": {
					"type": "boolean",
					"description": "name_condition: the Named unit's spells land on its own tile (Forbidden Word)."
				},
				"shun": {
					"type": "boolean",
					"description": "name_condition: the Named unit cannot target the caster (Not Me)."
				},
				"per_trigger": {
					"type": "boolean",
					"description": "name_condition: pays once per kind of action each round, not once per round."
				},
				"grouped": {
					"type": "boolean",
					"description": "name_condition: the targets' Names form a group; the first broken pays and the group ends (Terms and Conditions)."
				},
				"halve_attack": {
					"type": "boolean",
					"description": "name_condition: the Named unit's attacks deal half damage (Binding Chains)."
				},
				"growth": {
					"type": "integer",
					"description": "compound_names: penalty growth per break.",
					"minimum": 1
				},''')
rep('''					"description": "New simultaneous-construct cap. Used by: set_construct_cap.",''',
    '''					"description": "New simultaneous-construct cap. Used by: set_construct_cap. Also draw_on_break: the most cards drawn.",''')
rep('''				"dest_range": {
					"type": "integer",
					"description": "unit_then_tile: max distance from the victim for the destination tile."
				},''', '''				"dest_range": {
					"type": "integer",
					"description": "unit_then_tile: max distance from the victim for the destination tile."
				},
				"any_tile": {
					"type": "boolean",
					"description": "unit_then_tile: any tile within dest_range, occupied ones included (Borrowed Will)."
				},''')
p.write_text(s, encoding='utf-8')
print("schema ok")

# Catch-up: keys earlier slices added to cards without the schema.
s = p.read_text(encoding='utf-8')
rep('''					"description": "Optional override for the glyph tooltip's trigger line."
				},''', '''					"description": "Optional override for the glyph tooltip's trigger line."
				},
				"once_per_fight": {
					"type": "boolean",
					"description": "Once keyword: this half can be cast once per fight."
				},''')
rep('''				"growth": {
					"type": "integer",
					"description": "compound_names: penalty growth per break.",
					"minimum": 1
				},''', '''				"growth": {
					"type": "integer",
					"description": "compound_names: penalty growth per break.",
					"minimum": 1
				},
				"halt_movement": {
					"type": "boolean",
					"description": "prepare_glyph: the unit that triggers it stops moving (Snare Glyph)."
				},
				"name_on_trigger": {
					"type": "boolean",
					"description": "prepare_glyph: Name the unit that triggers it (Binding Rune)."
				},''')
p.write_text(s, encoding='utf-8')
print("catch-up ok")
