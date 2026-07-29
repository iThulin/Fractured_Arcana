# Card Text Style Guide — v1

*The language contract for every card face: base `rules_text` and every upgrade
tier's `rules_text` patch. Adopted 2026-07-29 (pre-playtest unification pass).
When authoring a new card, match these templates; the audit's rule is that a
schema entry and a style-conformant face land in the same commit as the effect.*

## Voice

Full sentences. Every sentence starts with a capital, ends with a period, and
leads with the verb the caster performs: **Deal, Gain, Draw, Push, Pull, Summon,
Teleport, Move, Heal, Look, Prepare, Imbue, Harvest, Repair, Etch, Seek**.
Statuses are stated, not performed: "**The target is Slowed for 1 turn.**"
No semicolons; split into sentences. Em-dashes only in flavor, never in rules.
No ALL-CAPS emphasis — keywords and numbers carry the weight.

## Capitalization

- **Capitalized — status keywords:** Slowed, Rooted, Frozen, Stunned, Burned,
  Poisoned, Weakened, Blinded, Haunted, Hexed, Named, Delayed, Shrouded, Undying.
- **Capitalized — school resources:** Charge, Foresight, Wilding, Grief, Weave,
  Heat, Schematics.
- **Capitalized — summoned unit species and named objects:** Wolf, Boar, Bear,
  Crows, Spirit Wall, Turret, Cannon, Sentinel, Drone, Barrier, Colossus,
  Familiar, Revenant, Decoy, Nexus, Prism, plus named zones (Old Growth,
  Memorial Ground, Sanctuary) and card-created objects (Conduit Link, Lattice
  Node, Shield Wall).
- **Lowercase — generic game nouns:** mana, armor, shield, damage, memorial,
  glyph, spirit (as a class), construct, tile, deck, hand, discard, turn.

## Numbers, distance, duration

- Digits for every count and amount: "2 Cannons", "Draw 1 card", "Gain 2 mana".
  The adverb "twice" stays a word.
- Distance: bare "within N" ("enemies within 3"). Tile counts on movement keep
  the noun: "Move 2 tiles.", "Push target 2 tiles."
- Range on the face only when it is the point: "(range 3)".
- Duration: "for N turn(s)". Windows: "this turn", "next turn", "until your next
  turn". Permanence: "for the rest of the fight" (never "rest of combat",
  "Permanent:", or bare "permanently").

## Templates by effect family

- **Damage:** "Deal N damage." Scope only when wider than the target: "Deal N
  damage to all enemies within 3." Never "to target" — the targeter says that.
  Collisions: "Collisions deal N damage."
- **Aimed displacement:** "Push target N tiles in a direction you choose."
- **Defense:** "Gain N armor." / "Gain N shield." Allies: "Allies within N gain…"
- **Heal:** "Heal N HP." / "Heal an ally N HP." / "heal N HP per tile."
- **Economy:** "Gain N mana." / "Gain N Charge." / "Draw N card(s)."
- **Selection:** "Look at the top N cards of your deck. Keep 1. The rest go to
  the bottom." Reorder: "…Put 1 back on top. The rest go to the bottom." Seek:
  "…Keep a construct card. The rest go to the bottom."
- **Status:** "The target is Slowed for 1 turn." / "Enemies within 2 are Rooted
  for 1 turn." Damage-over-time statuses state their rate once, at the source
  that defines them: "Haunted (3 damage at the start of its turn)".
- **Summons:** "Summon a Wolf beside living ground (HP 12 / DMG 5 / SPD 2)."
  Stat block order HP / DMG / SPD / RNG, only the stats the unit has. Setup time
  in prose after the block: "1 turn of setup before it can fire."
- **Zones/glyphs:** "Prepare a glyph on a tile within 3. When an enemy enters:
  deal 4 damage and it is Rooted for 1 turn." Trigger clause after a colon.
- **Choose one:** "Choose one — X, or Y." (option labels carry the details).
- **Conditions:** "If you control a construct, …" / "If the target stands on
  Ice, …" Requirements the engine enforces stay in prose exactly as gated:
  "Requires living ground within 4." at the END of the text.

## The three terms people drift on

- "living ground" is the condition; "a living tile" is the object.
- "imbue X with Fire" (verb), "a Fire tile" (adjective) — element capitalized as
  the element name.
- One card, one referent: first mention "target enemy" / "target ally", then
  "it" / "they". Never "the victim", "the foe", "them" for a single target.

## Keyword verbs

Some school actions ARE verbs and stay verbs: **Name** (Enchanter — "Name an
enemy for 2 turns"; the resulting condition is the Named status, "is Named" in
riders), **Seek**, **Foretell**, **Harvest**, **Imbue**, **Scry-family "Look
at"**, **Rewind / Fast-Forward** (Chronomancer event control). The stated-status
rule applies to conditions, not to these acts.
