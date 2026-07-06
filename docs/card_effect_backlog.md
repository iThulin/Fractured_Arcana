# Card Effect Backlog — IMPLEMENTED 2026-07-06

*All keys below were implemented 2026-07-06 (NecromancerEffects.cs backlog section, ElementalistEffects.cs, CorePredicates.cs, TargetSelectors.cs; registrations in the school registry partials). All 162 cards now pass verification and are `ready`. Retained as the implementation contract record.*

**Implementation rulings (veto by editing the code):** pull_to_memorial claims each memorial once per cast (tier 3's "separate memorials" for free) · "passed through" counts only memorials that existed when the effect resolved (Spirit Trail can't pay for its own footprints) · mass_departure bursts hit adjacent (radius 1) · push_all_to_memorial "lands" = on or adjacent to the memorial · Ghost Road's phase = Unit.IsPhasing honored by the pathfinding zone BFS (traverse anything, end only on enterable tiles) · Consecrated Battlefield permanence = duration 99.

**Pre-existing bugs fixed in the same pass:** consume_memorials_for_champion was a no-op stub (now consumes nearest N in range, sparing the target tile, recording combined strength) · walk_between was a miswired hollow_mantle copy (now the spirits-heal-on-cast aura per Hollow Mantle tier 4) · DealDamageEffect/AoeAllEffect never set GameState.LastDamageDealt (heal_fraction_of_damage on Communion's base card healed 0).

---

## `push_all_from_memorial` (effect, 3 uses)

- **necromancer_the_honored_dead** tier 2 — "The Great Wave: Surviving enemies are pushed 2 tiles from nearest memorial."
  - node: `{"type": "push_all_from_memorial", "tiles": 2, "collision_damage": 2}`
- **necromancer_the_honored_dead** tier 3 — "Cascading Grief: Summons a Spirit from each memorial before consuming it."
  - node: `{"type": "push_all_from_memorial", "tiles": 2, "collision_damage": 2}`
- **necromancer_the_honored_dead** tier 4 — "The Flood of Memory: Wave does not consume memorials. Strengthens all memorials instead."
  - node: `{"type": "push_all_from_memorial", "tiles": 2, "collision_damage": 2}`

## `draw_per_memorial_passed` (effect, 3 uses)

- **necromancer_the_price_of_knowing** tier 2 — "Procession: Draws 1 card per memorial passed through."
  - node: `{"type": "draw_per_memorial_passed", "count_per": 1}`
- **necromancer_the_price_of_knowing** tier 3 — "The Guided: Gains 2 armor per memorial passed through."
  - node: `{"type": "draw_per_memorial_passed", "count_per": 1}`
- **necromancer_the_price_of_knowing** tier 4 — "Ghost Road: Move through terrain and units. Leave a memorial on each tile."
  - node: `{"type": "draw_per_memorial_passed", "count_per": 1}`

## `pull_to_memorial` (effect, 3 uses)

- **necromancer_the_procession** tier 2 — "Into the Memory: Pushes target onto the nearest memorial."
  - node: `{"type": "pull_to_memorial", "range": 6}`
- **necromancer_the_procession** tier 3 — "Put Them in Place: Targets 2 enemies. Each is pushed to a separate memorial."
  - node: `{"type": "pull_to_memorial", "range": 6}`
- **necromancer_unfinished_business** tier 2 — "Into the Memory: Pulls target onto the nearest memorial instead."
  - node: `{"type": "pull_to_memorial", "range": 6}`

## `attunement_per_nearby_element` (effect, 2 uses)

- **elementalist_worldshaper** tier 3 — "Elemental Sight: Gain bonus attunement stacks per unique element present in the area."
  - node: `{"type": "attunement_per_nearby_element", "radius": 3}`
- **elementalist_worldshaper** tier 4 — "Grand Confluence: The read becomes mastery — massive card advantage and attunement boost."
  - node: `{"type": "attunement_per_nearby_element", "radius": 4}`

## `heal_most_damaged_spirit` (effect, 2 uses)

- **necromancer_communion** tier 3 — "The Exchange: Also heals the most damaged spirit by the same amount."
  - node: `{"type": "heal_most_damaged_spirit", "amount": 12}`
- **necromancer_communion** tier 3 — "The Exchange: Also heals the most damaged spirit by the same amount."
  - node: `{"type": "heal_most_damaged_spirit", "amount": 4}`

## `imbue_path_memorial` (effect, 2 uses)

- **necromancer_grief_strike** tier 3 — "Spirit Trail: Leaves a memorial on every tile passed through."
  - node: `{"type": "imbue_path_memorial", "move": 3}`
- **necromancer_the_price_of_knowing** tier 4 — "Ghost Road: Move through terrain and units. Leave a memorial on each tile."
  - node: `{"type": "imbue_path_memorial", "move": 4, "phase": true}`

## `create_memorial_ground_area` (effect, 2 uses)

- **necromancer_march_and_remember** tier 3 — "The Garden: Imbues target tile and all adjacent tiles as Memorial Ground."
  - node: `{"type": "create_memorial_ground_area", "radius": 1, "duration": 5, "summon_discount": 2, "spirit_regen": 2}`
- **necromancer_march_and_remember** tier 4 — "Consecrated Battlefield: The entire battlefield becomes Memorial Ground permanently."
  - node: `{"type": "create_memorial_ground_area", "radius": 99, "duration": 99, "summon_discount": 2, "spirit_regen": 2}`

## `armor_per_grief_spent` (effect, 2 uses)

- **necromancer_the_departure** tier 3 — "Grief Made Weapon: Gains 1 armor per charge spent."
  - node: `{"type": "armor_per_grief_spent", "amount_per": 1}`
- **necromancer_the_departure** tier 4 — "The Flood of Grief: After the discharge, triggers the Flood."
  - node: `{"type": "armor_per_grief_spent", "amount_per": 1}`

## `commune_all_memorials` (effect, 2 uses)

- **necromancer_the_ossuary** tier 3 — "Many Voices: Communes with all memorials within range 3. Draw 1 and gain 1 Grief per memorial."
  - node: `{"type": "commune_all_memorials", "range": 3, "draw_per": 1, "grief_per": 1}`
- **necromancer_the_ossuary** tier 4 — "The Grand Seance: Communes with every memorial on the board. Per memorial: draw 1, gain 1 Grief, summon 1 Spirit. Memorials remain."
  - node: `{"type": "commune_all_memorials", "range": 99, "draw_per": 1, "grief_per": 1, "summon_per": {"unit": "Spirit", "hp": 8, "damage": 4, "speed": 1}, "consume": false}`

## `mark_on_death_memorial` (effect, 2 uses)

- **necromancer_unfinished_business** tier 3 — "Last Words: Dying while affected leaves a Strong memorial."
  - node: `{"type": "mark_on_death_memorial", "strength": "strong"}`
- **necromancer_unfinished_business** tier 4 — "Bound: Target cannot act or be freed until your next turn."
  - node: `{"type": "mark_on_death_memorial", "strength": "strong"}`

## `pull_all_to_memorial` (effect, 2 uses)

- **necromancer_unfinished_business** tier 3 — "Congregation Pull: Pulls all enemies within range 3 one tile toward nearest memorial."
  - node: `{"type": "pull_all_to_memorial", "range": 3, "tiles": 1}`
- **necromancer_unfinished_business** tier 4 — "The Summoning: Pulls every enemy on the board 2 tiles toward nearest memorial."
  - node: `{"type": "pull_all_to_memorial", "range": 99, "tiles": 2}`

## `grief_per_damage` (effect, 1 use)

- **necromancer_call_to_purpose** tier 3 — "Grief Drain: Gains 1 Grief per 3 damage dealt."
  - node: `{"type": "grief_per_damage", "damage_per_grief": 3}`

## `heal_fraction_of_total_damage` (effect, 1 use)

- **necromancer_call_to_purpose** tier 4 — "Soul Flood: Deals 5 damage to all enemies. Heals for total damage dealt."
  - node: `{"type": "heal_fraction_of_total_damage", "fraction": 1.0}`

## `grief_overflow_heal_spirits` (effect, 1 use)

- **necromancer_communion** tier 3 — "Overflowing: If Grief exceeds 4, refreshes all spirit HP."
  - node: `{"type": "grief_overflow_heal_spirits"}`

## `damage_equal_to_missing_hp` (effect, 1 use)

- **necromancer_communion** tier 4 — "Total Communion: Deals damage equal to target's missing HP. Heals for full amount."
  - node: `{"type": "damage_equal_to_missing_hp"}`

## `heal_equal_to_damage_dealt` (effect, 1 use)

- **necromancer_communion** tier 4 — "Total Communion: Deals damage equal to target's missing HP. Heals for full amount."
  - node: `{"type": "heal_equal_to_damage_dealt"}`

## `dirge_pulse_global` (effect, 1 use)

- **necromancer_dirge** tier 4 — "The Song That Ends All Things: Dirge hits the entire board. Enemies adjacent to spirits take double damage."
  - node: `{"type": "dirge_pulse_global", "damage": 4, "push": 2, "collision_damage": 3, "adjacent_spirit_multiplier": 2}`

## `teleport_all_spirits_to_nearest_memorial` (effect, 1 use)

- **necromancer_elegy** tier 4 — "The Grand Procession: All spirits also teleport to their nearest memorial."
  - node: `{"type": "teleport_all_spirits_to_nearest_memorial"}`

## `damage_per_memorial` (effect, 1 use)

- **necromancer_grief_strike** tier 3 — "Weight of Loss: Deals 1 bonus damage per memorial on the board."
  - node: `{"type": "damage_per_memorial", "amount_per": 1}`

## `spirit_swap_with_nearest_enemy` (effect, 1 use)

- **necromancer_last_rite** tier 3 — "Chain of Being: The spirit then swaps with the nearest enemy."
  - node: `{"type": "spirit_swap_with_nearest_enemy"}`

## `last_rite_aoe` (effect, 1 use)

- **necromancer_last_rite** tier 4 — "Mass Rites: Deals 7 damage to all enemies. Each kill performs the full rite."
  - node: `{"type": "last_rite_aoe", "damage": 7, "spirit_strike": 5, "summon_on_kill": {"unit": "Spirit", "hp": 8, "damage": 4, "speed": 1}}`

## `summon_spirit_from_new_memorials` (effect, 1 use)

- **necromancer_march_and_remember** tier 4 — "All Rise: Summons a Spirit (8HP 4DMG) from every memorial created this turn."
  - node: `{"type": "summon_spirit_from_new_memorials", "unit": "Spirit", "hp": 8, "damage": 4, "speed": 1}`

## `mass_departure` (effect, 1 use)

- **necromancer_the_departure** tier 4 — "The Grand Departure: Dismisses all spirits. Each bursts for 7 damage + push 2 and leaves a Strong memorial."
  - node: `{"type": "mass_departure", "damage": 7, "push": 2, "collision_damage": 2, "memorial_strength": "strong"}`

## `draw_per_memorial_global` (effect, 1 use)

- **necromancer_the_honored_dead** tier 4 — "The Flood of Memory: Wave does not consume memorials. Strengthens all memorials instead."
  - node: `{"type": "draw_per_memorial_global", "count_per": 1}`

## `strengthen_all_memorials` (effect, 1 use)

- **necromancer_the_honored_dead** tier 4 — "The Flood of Memory: Wave does not consume memorials. Strengthens all memorials instead."
  - node: `{"type": "strengthen_all_memorials"}`

## `armor_per_memorial_passed` (effect, 1 use)

- **necromancer_the_price_of_knowing** tier 3 — "The Guided: Gains 2 armor per memorial passed through."
  - node: `{"type": "armor_per_memorial_passed", "amount_per": 2}`

## `push_all_to_memorial` (effect, 1 use)

- **necromancer_the_procession** tier 4 — "The Trap Is Set: Deals 5 damage to all enemies and pushes each toward nearest memorial."
  - node: `{"type": "push_all_to_memorial", "damage_before": 5, "damage_on_land": 4}`

## `summon_spirit_scaled` (effect, 1 use)

- **necromancer_the_reckoning** tier 2 — "Grief Made Flesh: Champion scales with combined memorial strength (+4HP/+2DMG per point)."
  - node: `{"type": "summon_spirit_scaled", "unit": "Revenant_Champion", "base_hp": 28, "base_damage": 10, "hp_per_strength": 4, "damage_per_strength": 2, "speed": 1}`

## `consume_all_memorials_for_champions` (effect, 1 use)

- **necromancer_the_reckoning** tier 4 — "Legion of the Honored: Summons one Revenant Champion per 2 memorials consumed within range 3."
  - node: `{"type": "consume_all_memorials_for_champions", "range": 3, "unit": "Revenant_Champion", "base_hp": 24, "base_damage": 8, "speed": 1}`

## `summon_spirit_from_all_memorials_and_death_sites` (effect, 1 use)

- **necromancer_they_chose_to_stay** tier 4 — "All of Them: Spirits also rise from every tile a spirit fell on this combat."
  - node: `{"type": "summon_spirit_from_all_memorials_and_death_sites", "unit": "Spirit", "hp_per_spirit": true, "base_hp": 4, "damage": 6, "speed": 1, "on_arrive_advance": 1, "bonus_damage_per_strength": 2, "inherit_memorial_name": true}`

## `target_has_status` (predicate, 1 use)

- **elementalist_firestorm** tier 3 — "Supercooled: Flash Freeze shatters frozen enemies — dealing bonus damage if already frozen."
  - node: `{"type": "target_has_status", "status": "frozen"}`

## `nearest_memorial` (targeter, 2 uses)

- **necromancer_final_escort** tier 3 — "The Answering: Also summons a second Spirit at the nearest other memorial."
  - node: `{"type": "nearest_memorial"}`
- **necromancer_the_honored_dead** tier 3 — "The Council: Summons a second Revenant Elder at the nearest other memorial."
  - node: `{"type": "nearest_memorial"}`

