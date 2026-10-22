import pathlib, json
p = pathlib.Path('Schemas/card.schema.json')
s = p.read_text(encoding='utf-8')
def rep(o, n):
    global s
    assert s.count(o) == 1, (o[:60], s.count(o))
    s = s.replace(o, n)
rep('''				"halt_movement": {''', '''				"from": {
					"type": "string",
					"description": "pen: where the card comes from.",
					"enum": ["hand", "deck_top", "reveal"]
				},
				"up_to": {
					"type": "boolean",
					"description": "pen: the player may pen fewer than count."
				},
				"free_top": {
					"type": "boolean",
					"description": "pen: the penned card's top half costs no Charge to cast from the leaf (Overcharge's bottom)."
				},
				"reveal": {
					"type": "integer",
					"description": "pen from reveal: how many cards to reveal."
				},
				"halt_movement": {''')
rep('''						"seventh_name"
					]''', '''						"seventh_name",
						"pen",
						"free_leaf_cast",
						"memorize",
						"recite_oldest",
						"charge_lock",
						"spend_charge"
					]''')
p.write_text(s, encoding='utf-8')

b = pathlib.Path('Data/Register/barks.json')
t = b.read_text(encoding='utf-8')
anchor = '''      "note": "A compelled unit walks, it is not shoved: your glyphs trigger, Fire sears and Frost slides as it steps, but there is no collision damage and it cannot be walked off a ledge. A blocked step ends the walk."
    }'''
assert t.count(anchor) == 1
t = t.replace(anchor, anchor + ''',
    {
      "key": "arc.first_pen",
      "category": "explain",
      "once": true,
      "title": "Penned",
      "lines": [
        "Written down. I will hold it for you. I may also read it aloud. I cannot always help that."
      ],
      "note": "A penned card sits on your leaf, above your hand. Cast either half for its mana plus 1 Charge. The leaf holds 3 pages; if all 3 are full at the end of your turn, it reads the oldest page's top half aloud for free at the nearest target, and that card is lost for the rest of the fight."
    },
    {
      "key": "arc.babble",
      "category": "comment",
      "once": false,
      "cooldown": 30,
      "lines": [
        "I had to say it. It was right there on the page.",
        "Sorry. It came out. The page is blank now, if that helps."
      ]
    }''')
b.write_text(t, encoding='utf-8')
json.loads(t)
print("schema+barks ok")
