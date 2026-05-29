# Fractured Arcana

A tactical roguelite card game built in Godot 4 / C#. You play as a wizard leading a guild, exploring fog-of-war hex maps, resolving encounters through tactical card combat or negotiation, and building out a persistent campus between runs. Each wizard school has a distinct combat identity, exploration passive, and negotiation style.

**Current development phase: Phase 3 — Horizontal Content**
One school (Elementalist) is fully playable. Core systems (save/load, overworld, campus, negotiation skeleton, companion framework) are in place. Phase 3 widens content to all six schools, multiple regions, and full campus buildings.

---

## Table of Contents

1. [Prerequisites & Setup](#1-prerequisites--setup)
2. [Project Structure](#2-project-structure)
3. [Architecture Overview](#3-architecture-overview)
4. [How to Add Content](#4-how-to-add-content)
   - [Adding a Card](#41-adding-a-card)
   - [Adding a Region](#42-adding-a-region)
   - [Adding a Narrative Encounter](#43-adding-a-narrative-encounter)
   - [Adding a Building](#44-adding-a-building)
   - [Adding a Companion](#45-adding-a-companion)
5. [Card Schema Reference](#5-card-schema-reference)
   - [Root Fields](#51-root-fields)
   - [Card Half Fields](#52-card-half-fields)
   - [Targeting Types](#53-targeting-types)
   - [Effect Types](#54-effect-types)
   - [Predicate Types](#55-predicate-types)
   - [Card Status Lifecycle](#56-card-status-lifecycle)
6. [Systems Reference](#6-systems-reference)
7. [Known Gotchas & Recurring Bugs](#7-known-gotchas--recurring-bugs)
8. [Godot 4.6 Compatibility Rules](#8-godot-46-compatibility-rules)
9. [Git Conventions](#9-git-conventions)
10. [Code Style & Comment Conventions](#10-code-style--comment-conventions)

---

## 1. Prerequisites & Setup

### Requirements

- **Godot 4.6.1** (exact version — do not use 4.5 or any 4.7 build; compat mode rules are version-specific)
- **.NET 8 SDK** (not .NET 6 or .NET 9)
- A C# IDE: Rider or VS Code with the C# Dev Kit extension

### Opening the Project

1. Clone the repo.
2. Open **Godot 4.6.1**.
3. Click **Import** and select the repo root folder (the one containing `project.godot`).
4. Do **not** open the `.sln` in Godot. Use the `.sln` only from your IDE for code editing.
5. On first open, Godot will build the C# project automatically. If it fails, verify your .NET 8 SDK is on `PATH`.

### Building & Running

- Press **F5** in Godot to run, or use the play button in the top-right toolbar.
- Entry scene: `res://Scenes/Campus/CampusScene.tscn`
- Combat scene: `res://Scenes/Combat/Battlefield.tscn`
- Save data location on Windows: `%APPDATA%\Godot\app_userdata\FracturedArcana\saves\`
- Save data location on Mac: `~/Library/Application Support/Godot/app_userdata/FracturedArcana/saves/`

### JSON Schema Validation in VS Code

Drop the `Schemas/` folder and `.vscode/settings.json` from the repo into VS Code and all card JSON files will get live red-squiggle validation and autocomplete automatically. No plugin required.

---

## 2. Project Structure

```
FracturedArcana/
├── project.godot
├── FracturedArcana.csproj
├── FracturedArcana.sln
│
├── Data/                                   # JSON content files — no code lives here
│   ├── Cards/                              # One .json file per card
│   │   └── elementalist_fireball.json      # Convention: schoolname_cardname.json
│   ├── Regions/                            # One .json file per region
│   ├── Encounters/                         # Narrative encounter pools
│   │   ├── generic_encounters.json         # Fallback pool loaded in every region
│   │   └── {regionid}_encounters.json      # Region-specific pool
│   ├── Buildings/                          # One .json file per campus building
│   └── Companions/                         # One .json file per companion
│
├── Schemas/                                # JSON Schema files for content validation
│   └── card.schema.json                    # Validates Data/Cards/*.json
│
├── Scenes/                                 # Godot .tscn scene files
│   ├── Campus/
│   │   └── CampusScene.tscn                # Entry scene
│   ├── Combat/
│   │   ├── Battlefield.tscn                # Main combat scene
│   │   ├── HexTile.tscn                    # Hex tile prefab
│   │   ├── Players/
│   │   │   └── Unit.tscn                   # Player/enemy unit prefab
│   │   ├── Monsters/
│   │   │   └── TargetDummy.tscn
│   │   └── Summons/
│   │       ├── HexTile_Stone_Blocker.tscn
│   │       └── HexTile_Crystal_Blocker.tscn
│   ├── Negotiation/
│   │   └── NegotiationScene.tscn
│   ├── Cards/
│   │   ├── CardUI.tscn                     # Card visual prefab
│   │   └── DropSlot.tscn                   # Hand drop target
│   └── UI/
│       └── CombatUI.tscn
│
└── Scripts/                                # All C# source files
    ├── Cards/
    │   ├── CardData.cs                     # CardSchool, CardRarity, CardType enums
    │   ├── CardRuntime.cs                  # Card, CardHalf, PlaySpeed — runtime types
    │   ├── CardDatabase.cs                 # Blueprint registry; RegisterPrebuiltCard()
    │   ├── CardUi.cs                       # Card visual controller
    │   ├── DualCardData.cs                 # Node2D wrapper for editor-assigned card pairs
    │   ├── DeckManager.cs                  # Per-unit deck swap controller
    │   └── Loader/
    │       ├── JsonCardLoader.cs           # Parses card JSON → Card objects; status gate
    │       ├── CardScriptRegistry.cs       # Effect/targeter/predicate factory registry
    │       └── CardLoaderV2.cs             # Entry point; DevMode flag
    ├── Systems/
    │   ├── Combat/
    │   │   ├── CombatManager.cs
    │   │   ├── HexGridManager.cs
    │   │   ├── GameRunner.cs
    │   │   ├── GameState.cs
    │   │   └── ElementalAttunement.cs      # ISchoolAttunement, ElementalAttunement
    │   └── Negotiation/
    │       └── NegotiationManager.cs
    ├── Cards/
    │   └── Effects/
    │       ├── Effect.cs                   # EffectBase, DealDamageEffect, leaf effects
    │       └── CompositeEffects.cs         # SequenceEffect, ConditionalEffect, RetargetEffect
    ├── Data/
    │   ├── GuildSaveData.cs                # Central save data structure
    │   ├── SaveManager.cs                  # JSON serialization, 3-slot management
    │   ├── RegionDefinition.cs             # Region data model
    │   ├── RegionLoader.cs                 # Loads Data/Regions/*.json
    │   ├── NarrativeEncounterLoader.cs     # Loads Data/Encounters/*.json
    │   ├── BuildingDatabase.cs             # Loads Data/Buildings/*.json
    │   └── Companion.cs                    # Companion data model
    ├── Overworld/
    │   └── CampusScreen.cs
    └── UI/
        ├── CardLibraryUi.cs                # Card library browser
        ├── SchoolAttunementUI.cs           # Elementalist attunement bars
        ├── DeckUiManager.cs
        └── CameraController.cs
```

> **`Data/` vs `Scripts/Data/`**: `Data/` contains JSON files you author as game content. `Scripts/Data/` contains C# classes that load and represent that data at runtime. Adding content = work in `Data/`. Changing data structures = work in `Scripts/Data/`.

> **`Scripts/Cards/` vs `Scripts/Cards/Effects/`**: `Scripts/Cards/` holds runtime card types and the database. `Scripts/Cards/Effects/` holds the effect class implementations. `Scripts/Cards/Loader/` holds the JSON parser and registry.

---

## 3. Architecture Overview

Four layers flowing top-to-bottom. Never bypass a layer — effects don't touch the scene tree, UI doesn't parse JSON.

```
┌─────────────────────────────────────────────────────┐
│  DATA LAYER                                         │
│  Data/Cards/*.json  Data/Regions/*.json  etc.       │
│  Plain JSON. Authorable without building.           │
└───────────────┬─────────────────────────────────────┘
                │ loaded by
┌───────────────▼─────────────────────────────────────┐
│  REGISTRY / LOADER LAYER                            │
│  CardScriptRegistry  JsonCardLoader  RegionLoader   │
│  NarrativeEncounterLoader  BuildingDatabase         │
│  Parses JSON → C# objects. Caches results.          │
│  Effects, targeters, predicates registered here.    │
└───────────────┬─────────────────────────────────────┘
                │ populates
┌───────────────▼─────────────────────────────────────┐
│  RUNTIME LAYER                                      │
│  GameState  GameRunner  HexGridManager              │
│  DeckManager  UnitDeckData  ElementalAttunement     │
│  GuildSaveData  SaveManager                         │
│  Live game state. No JSON parsing here.             │
└───────────────┬─────────────────────────────────────┘
                │ drives
┌───────────────▼─────────────────────────────────────┐
│  UI LAYER                                           │
│  CombatUI  SchoolAttunementUI  CardLibraryUi        │
│  DeckUiManager  CampusScreen  NegotiationManager    │
│  Reads runtime state. Never touches JSON directly.  │
└─────────────────────────────────────────────────────┘
```

### Key Systems at a Glance

| System | Class(es) | What it does |
|--------|-----------|--------------|
| **Card loading** | `JsonCardLoader`, `CardScriptRegistry`, `CardLoaderV2` | Reads card JSON, gates on status, compiles effect chains |
| **Card effects** | `EffectBase`, `SequenceEffect`, `ConditionalEffect`, `RetargetEffect` | Leaf and composite effect tree evaluated at cast time |
| **Targeting** | `ITargetSelector` implementations | Range, cone, line, ring, AoE, element-tile selectors |
| **Combat loop** | `GameRunner`, `CombatManager` | Turn management, unit selection, card play, win/lose |
| **Hex grid** | `HexGridManager` | Map generation, terrain, tile data, pathfinding |
| **School mechanic** | `ISchoolAttunement`, `ElementalAttunement` | Per-school class mechanic (charges, thresholds, burst) |
| **Deck management** | `DeckManager` | Per-unit draw pile/hand/discard; swaps on unit select |
| **Persistence** | `GuildSaveData`, `SaveManager` | 3-slot JSON save with versioning + migration |
| **Negotiation** | `NegotiationManager` | Standalone scene; tension meter, 8 token types |
| **Campus** | `CampusScreen`, `BuildingDatabase` | Building construction/upgrade, school building unlocks |

---

## 4. How to Add Content

For all content types, no code changes are required — you author a JSON file and the loader picks it up automatically on next run.

---

### 4.1 Adding a Card

Create a new file in `Data/Cards/`. Filename convention: `schoolname_cardname.json` (lowercase, underscores).

See [Section 5](#5-card-schema-reference) for the full schema. Quick-start example:

```json
{
  "id": "elementalist_example",
  "status": "stub",
  "name": "Example Card",
  "school": "Elementalist",
  "rarity": "Common",
  "top": {
    "name": "Fireball",
    "mana": 2,
    "speed": "Sorcery",
    "rules_text": "Deal 4 damage to target.",
    "tags": ["fire"],
    "targeting": { "type": "unit", "range": 4, "enemies_only": true },
    "effect": { "type": "damage", "amount": 4 }
  },
  "bottom": {
    "name": "Ice Shield",
    "mana": 1,
    "speed": "Instant",
    "rules_text": "Gain 3 armor.",
    "tags": ["ice"],
    "targeting": { "type": "self" },
    "effect": { "type": "armor", "amount": 3 }
  }
}
```

When the card is fully built and tested, change `"status": "stub"` to `"status": "ready"`. It will then appear in `CardDatabase` and the CardLibrary UI.

---

### 4.2 Adding a Region

Create `Data/Regions/{regionid}.json`. The `id` field must match the filename (without extension).

```json
{
  "id": "haunted_ruins",
  "displayName": "Haunted Ruins",
  "description": "Ancient walls saturated with necromantic residue.",
  "schoolAffinity": "Necromancer",
  "atmosphere": "Hostile",
  "gridWidth": 15,
  "gridHeight": 15,
  "stepBudget": 20,
  "combatPOICount": 10,
  "restPOICount": 3,
  "narrativePOICount": 5,
  "negotiationPOICount": 0,
  "hasRiver": false,
  "hasMountainRange": true,
  "hasRoads": false,
  "riverCrossingCount": 0,
  "enemyDifficultyMult": 1.2,
  "goldRewardMult": 1.1,
  "biomes": [
    {
      "name": "Central Ruins",
      "centerQ": 7,
      "centerR": 7,
      "radius": 4,
      "primaryTerrain": "Ruins",
      "secondaryTerrain": "Grassland"
    }
  ]
}
```

`biomes` is optional — omit it for default layout. `schoolAffinity` is currently flavor; it will gate school bonuses in Phase 3.

---

### 4.3 Adding a Narrative Encounter

Encounter files are JSON arrays. Name them `{regionid}_encounters.json` for region-specific pools, or add to `generic_encounters.json` for content that appears in every region.

```json
[
  {
    "id": "ruins_echo_01",
    "title": "The Whispering Wall",
    "terrain": "Ruins",
    "isOneShot": true,
    "flavorText": "A crumbling wall bears faint inscriptions.",
    "choices": [
      {
        "id": "study",
        "label": "Study the inscriptions",
        "outcome": "You decipher a fragment of an old ward.",
        "effects": [{ "type": "add_lore", "entryId": "old_ward_fragment" }]
      },
      {
        "id": "leave",
        "label": "Leave it alone",
        "outcome": "Some things are better undisturbed.",
        "effects": []
      }
    ]
  }
]
```

`isOneShot: true` means this encounter is removed from the pool once completed. The loader combines the region-specific pool with `generic_encounters.json` automatically.

---

### 4.4 Adding a Building

Create `Data/Buildings/{buildingid}.json`.

```json
{
  "id": "training_grounds",
  "name": "Training Grounds",
  "category": "Core",
  "schoolAffinity": "",
  "description": "A yard where wizards and companions hone their skills between runs.",
  "tiers": [
    {
      "tier": 1,
      "displayName": "Basic Training Grounds",
      "goldCost": 80,
      "requirements": [],
      "passiveEffects": [{ "type": "run_start_buff", "stat": "mana", "amount": 1 }],
      "activeEffects": []
    },
    {
      "tier": 2,
      "displayName": "Advanced Training Grounds",
      "goldCost": 150,
      "requirements": ["training_grounds:1"],
      "passiveEffects": [{ "type": "run_start_buff", "stat": "mana", "amount": 2 }],
      "activeEffects": []
    }
  ]
}
```

`category` options: `Core`, `Magic`, `Economy`, `Reputation`, `School`. School buildings set `schoolAffinity` to the matching school name and are automatically unlocked when that school's wizard or companion joins.

`requirements` entries use the format `"buildingid:tier"` — e.g. `"training_grounds:1"` means tier 1 of Training Grounds must be built first.

---

### 4.5 Adding a Companion

Create `Data/Companions/{companionid}.json`.

```json
{
  "id": "elara_stormcaller",
  "name": "Elara Stormcaller",
  "school": "Elementalist",
  "personalityTrait": "Reckless",
  "backstory": "A former guild enforcer who burned her charter after a mission gone wrong.",
  "recruitmentCost": 120,
  "unlockCondition": "Complete 3 runs in the Frontier Wilds",
  "contributedCardIds": [
    "elementalist_chain_lightning",
    "elementalist_frost_nova"
  ]
}
```

`contributedCardIds` must reference `id` fields of card JSON files with `"status": "ready"`. These cards are added to the wizard's deck during combat when this companion is in the active party.

---

## 5. Card Schema Reference

All card JSON files must conform to `Schemas/card.schema.json`. VS Code validates automatically when `.vscode/settings.json` is present.

---

### 5.1 Root Fields

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `id` | string | ✓ | Unique. Lowercase + underscores only. Pattern: `^[a-z][a-z0-9_]*$` |
| `name` | string | ✓ | Display name in UI |
| `school` | string | ✓ | Must match `CardSchool` enum exactly (see values below) |
| `rarity` | string | ✓ | `Common`, `Uncommon`, `Rare`, `Legendary` |
| `status` | string | ✓ | `ready`, `wip`, or `stub` — controls whether the card loads (see Section 5.6) |
| `top` | CardHalf | — | Top half of the card. At least one of top/bottom should be present. |
| `bottom` | CardHalf | — | Bottom half of the card. |

**Valid `school` values** (must match `CardSchool` enum exactly, case-sensitive):
`Adept`, `Elementalist`, `Druid`, `Necromancer`, `Tinker`, `Enchanter`, `Arcanist`, `Chronomancer`

---

### 5.2 Card Half Fields

Both `top` and `bottom` use the same structure. The optional `channel` field inside each half uses the same structure recursively.

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `name` | string | ✓ | Display name for this half |
| `mana` | int | ✓ | Mana cost. Range: 0–10 |
| `speed` | string | ✓ | `Sorcery`, `Instant`, or `Reaction` |
| `rules_text` | string | ✓ | Text displayed on the card face. Uses snake_case — not camelCase. |
| `tags` | string[] | ✓ | Element/school tags. Open-ended string array. Use `[]` for halves with no tag identity. Common values: `"fire"`, `"ice"`, `"storm"`, `"stone"`. New tags can be added freely for new schools. |
| `targeting` | Targeting | ✓ | See Section 5.3 |
| `effect` | Effect | ✓ | See Section 5.4. Must have a non-empty `type`. |
| `requires` | string[] | — | Pre-cast validation. Card is unplayable unless all checks pass. Valid values: `"fire_tile"`, `"ice_tile"`, `"storm_tile"`, `"stone_tile"`, `"empty_tile"`, `"ally_adjacent"`, `"enemy_adjacent"` |
| `channel` | CardHalf | — | Optional. The channeled variant of this half. Same structure. Convention: `"speed": "Reaction"` on channel variants. |

---

### 5.3 Targeting Types

| Type | Parameters | Description |
|------|-----------|-------------|
| `self` | — | Caster only |
| `unit` | `range`, `enemies_only`, `los` | Single unit within range. `los: true` requires line of sight. |
| `tile` | `range` | Any tile within range, regardless of occupant |
| `aoe` | `radius`, `enemies_only`, `include_tiles` | All valid targets within radius of caster |
| `line` | `length`, `enemies_only`, `include_tiles` | All tiles/units in a line from caster |
| `cone` | `range`, `enemies_only` | Cone pattern in a chosen direction |
| `ring` | `radius`, `include_tiles` | Tiles/units at exactly `radius` distance |
| `adjacent` | — | All tiles immediately adjacent to caster |
| `nearest_to_target` | `range` | Closest enemy to the previous target. Used for chain effects inside `retarget`. |
| `adjacent_to_target` | `include_tiles` | Tiles/units adjacent to the previous target |
| `element_tile` | `element`, `range` | An imbued tile of the specified element within range |
| `by_tag` | `tag`, `enemies_only` | Units carrying a specific tag |

**Parameter types:**

| Parameter | Type | Notes |
|-----------|------|-------|
| `range` | int | Max tile distance |
| `radius` | int | Radius in tiles |
| `length` | int | Length in tiles |
| `enemies_only` | bool | Exclude allied units from selection |
| `allies_only` | bool | Exclude enemy units from selection |
| `los` | bool | Require unobstructed line of sight |
| `include_tiles` | bool | Include tile objects (not just unit occupants) |
| `element` | string | Element name: `"fire"`, `"ice"`, `"storm"`, `"stone"`, `"earth"`, `"lava"` |

---

### 5.4 Effect Types

#### Composite Effects

| Type | Parameters | Description |
|------|-----------|-------------|
| `sequence` | `steps: [Effect]` | Execute child effects in order. Each step shares the current target set unless a `retarget` changes it. |
| `conditional` | `if: Predicate`, `then: Effect`, `else: Effect` | Branch on a predicate. `else` is optional. |
| `retarget` | `targeting: Targeting`, `do: Effect` | Switch the active target set mid-sequence, then execute child effect on new targets. Core pattern for chain effects. |
| `for_each_target` | `do: Effect` | Execute child effect once per unit in the current target set. |
| `empty` | — | No-op. |

#### Core Leaf Effects

| Type | Key parameter(s) | Notes |
|------|-----------------|-------|
| `damage` | `amount` | Deal damage to targets |
| `heal` | `amount` | Restore HP |
| `armor` | `amount` | Grant armor to caster |
| `grant_armor` | `amount` | Grant armor to target |
| `shield` | `amount` | Grant shield |
| `self_damage` | `amount` | Deal damage to caster |
| `mana_gain` | `amount` | Restore mana to caster |
| `draw` | `count` | Draw cards |
| `move` | `tiles` | Move caster. NOTE: parameter is `tiles` not `amount`. |
| `push` | `tiles`, `collision_damage` | Push target away. `collision_damage` on hitting wall/unit. NOTE: `tiles` not `amount`. |
| `push_damage` | `tiles`, `damage_per_tile` | Push + damage per tile moved |
| `apply_status` | `status`, `duration` | Apply a status effect for `duration` turns |
| `imbue_tile` | `element`, `bonus_damage` | Imbue target tile. `bonus_damage` dealt to units crossing it each turn. |
| `imbue_area` | `element`, `radius` | Imbue all tiles within radius |
| `imbue_path` | `element`, `move`, `armor_per_tile` | Move caster, imbuing each tile traversed. Gain armor per tile. |
| `remove_armor` | `amount` | Strip armor from target |
| `consume_element_tile` | `element`, `radius`, `damage` | Destroy imbued tiles of element within radius, deal damage |
| `create_rubble` | — | Convert target tile to rubble (difficult terrain) |
| `raise_terrain` | `height` | Raise target tile's elevation |
| `summon` | `unit`, `count` | Summon units via `GameState.OnSummonRequested` delegate. `unit` is a string key. |

#### Elementalist-Specific Effects

| Type | Key parameter(s) | Notes |
|------|-----------------|-------|
| `terraform` | `radius`, `damage` | Reshape terrain in radius, deal damage |
| `elemental_convergence` | `radius`, `attunement_set_to` | Imbue tiles randomly, set all attunement counters |
| `ragnarok` | `damage_per_element`, `half_to_allies` | Damage all units × unique elements on board. Purges tiles. |
| `cataclysm` | `radius`, `damage_per_tile`, `tiles_per_draw` | Clear all imbued tiles, deal damage per tile cleared, draw cards |
| `primordial_surge` | `radius` | Imbue all tiles in radius with random elements |
| `tectonic_shatter` | `radius`, `damage_per_tile` | Shatter stone tiles for damage |
| `avatar_transform` | `turns`, `bonus_damage`, `armor`, `bonus_speed` | Transform for N turns with stat bonuses |
| `create_maelstrom` | `radius`, `damage`, `turns`, `freezes` | Persistent storm zone: damages, pushes clockwise, imbues Storm each turn |

**`apply_status` valid values for `status`:**
`"burn"`, `"frozen"`, `"slowed"`, `"stunned"`, `"rooted"`, `"poisoned"`, `"weakened"`

---

### 5.5 Predicate Types

Used inside `conditional` effects.

| Type | Parameters | Description |
|------|-----------|-------------|
| `always_true` | — | Always branches to `then` |
| `was_lethal` | — | True if the previous effect killed a unit |
| `target_on_tile` | `tile` | True if the target is standing on a tile of the given element |
| `caster_on_terrain` | `terrain` | True if the caster is on a tile of the given terrain type |
| `target_adjacent_to_tile` | `tile` | True if the target is adjacent to a tile of the given element |
| `has_elements_near_caster` | `elements: string[]`, `range` | True if tiles of all listed elements exist within range of the caster |

---

### 5.6 Card Status Lifecycle

Every card JSON file requires a `"status"` field. The loader in `JsonCardLoader.cs` gates on this field before building the card.

| Status | Loads in normal build | Loads with `DevMode = true` | Appears in CardLibrary | Notes |
|--------|----------------------|----------------------------|----------------------|-------|
| `ready` | ✓ | ✓ | ✓ | Card is complete and balanced |
| `wip` | ✗ | ✓ | ✗ | Built but not yet balanced. Use for testing without exposing to players. |
| `stub` | ✗ | ✗ | ✗ | Placeholder. Effect chains may be empty or missing. |

**To enable DevMode** (loads `wip` cards): in `Scripts/Cards/Loader/CardLoaderV2.cs`, set `public static bool DevMode = true;`. Never commit this as `true`.

**Cards missing the `status` field** are treated as stubs with a console warning. Use this PowerShell snippet to add `"status": "stub"` to all cards missing the field:

```powershell
Get-ChildItem "Data\Cards\*.json" | ForEach-Object {
    $content = Get-Content $_.FullName -Raw | ConvertFrom-Json
    if (-not $content.PSObject.Properties['status']) {
        $json = Get-Content $_.FullName -Raw
        $json = $json -replace '(\s*"id"\s*:\s*"[^"]*")', '$1,
  "status": "stub"'
        Set-Content $_.FullName $json
    }
}
```

---

## 6. Systems Reference

### Elemental Attunement (Elementalist)

The Elementalist tracks four elemental counters: Fire, Ice, Storm, Earth. Opposition pairs: Fire/Ice and Storm/Earth — casting one reduces the opposing counter by 1. All counters decay by 1 at start of each turn (minimum 0). Counters are capped at 4.

| Charges | Tier | Bonus |
|---------|------|-------|
| 1 | Minor | +1 bonus damage on spells of that element |
| 2 | Imbue | Auto-imbue the target tile with that element |
| 3 | Enhanced | Element-specific bonus: Fire=Burn, Ice=Slow, Storm=Chain, Earth=Armor |
| 4 | Burst | Big AoE, then counter resets to 0 |

Attunement is read from the card half's `tags` field at cast time. A half tagged `["fire", "ice"]` increments both counters.

### Save System

Three save slots. Saves automatically after every run and every campus change. Add a migration case to `SaveManager.cs` when changing the shape of `GuildSaveData` — the version field is there for this purpose.

### Per-Unit Deck Management

Each unit owns its own draw pile, hand, and discard. `DeckManager` acts as a view controller; `SetActiveDeck()` swaps the visible hand to the selected unit's deck. This is called automatically by `GameRunner` when a player unit is selected.

### Persistent Effects

Effects that last across turns (`MaelstromEffect`, `AvatarAuraEffect`) are stored in `GameState.ActiveEffects` and ticked each turn. Do not store turn-persistent state inside the effect object itself.

### Summons

Summon effects fire `GameState.OnSummonRequested` delegate rather than calling `AddChild` directly. `GameRunner` subscribes to this delegate and handles instantiation. Never call `AddChild` from inside an effect class.

---

## 7. Known Gotchas & Recurring Bugs

These have caused real bugs. Read them once.

### JSON Property Names

The loaders use `JsonNamingPolicy.CamelCase` for most data classes. Card JSON has a few exceptions.

| ❌ Wrong | ✓ Correct | Where |
|---------|-----------|-------|
| `"targetting"` | `"targeting"` | Card JSON |
| `"amount"` | `"tiles"` | Push/move effects — the field is `tiles`, not `amount` |
| `"rules_text"` as camelCase | `"rules_text"` | Keep as snake_case — it's matched explicitly |
| `"Stone"` | `"stone"` | Element tags are lowercase strings |
| `"Elementalist"` | `"elementalist"` | Effect type keys in `RegisterBuiltins` are lowercased |

### Missing `using System;`

Any C# file using `Action<T>`, `Dictionary<,>`, `Math`, `Func<>`, or `Enum` needs `using System;`. Godot templates don't always include it. The error is a cryptic type-not-found at compile time, not a hint about the missing directive.

### Effect Types Must Be Registered

New effect classes do nothing until registered in `CardScriptRegistry.RegisterBuiltins()` inside `JsonCardLoader.cs`. The loader will print `[CardLoader] Unknown effect type 'your_type'. Using EmptyEffect.` if you forget.

### `attunement_set_to` vs `attunement_counters`

The `elemental_convergence` effect reads `"attunement_set_to"` in `RegisterBuiltins`, but some card JSON files were authored with `"attunement_counters"`. Pick one name and make both the JSON and the registry entry match. Check `elementalist_ragnarok.json` specifically.

### Card School Enum Casing

The `school` field in card JSON is parsed against `CardSchool` (case-insensitive via `Enum.TryParse`), but a mismatch silently defaults to `Tinker`. Always use the exact enum name: `Elementalist`, not `elementalist`.

### `CardData.cs` vs `CardRuntime.cs`

The project has two card-related type files. `CardData.cs` contains legacy `CardData : Node2D` and the core enums (`CardSchool`, `CardRarity`, etc.). `CardRuntime.cs` contains the actual runtime types used by combat (`Card`, `CardHalf`, `PlaySpeed`). Do not confuse them. When adding fields to the card runtime, edit `CardRuntime.cs`.

---

## 8. Godot 4.6 Compatibility Rules

These rules exist for Godot 4.6.2 Mac + Windows cross-platform compatibility. Violating them causes crashes on one platform that don't appear on the other.

| Rule | Do this | Not this |
|------|---------|----------|
| Code-built UIs | `CallDeferred(nameof(BuildUI))` from `_Ready()` | Build children directly in `_Ready()` |
| Root Control anchors | Set in `.tscn` editor | Set `AnchorRight`/`AnchorBottom` in C# |
| ScrollContainer children | Manual button + `Visible` tab switching | `ScrollContainer` as direct `TabContainer` child |
| MarginContainer in ScrollContainer | `SizeFlagsVertical = ShrinkBegin` | Default `Fill` sizing |
| Cameras | `CallDeferred("make_current")` | `MakeCurrent()` directly in `_Ready()` |
| Adding to scene root | `CallDeferred("add_child", node)` | `AddChild()` on root directly |

---

## 9. Git Conventions

### Branching

- `main` is always playable. Every commit on `main` must leave the game in a runnable state.
- Use feature branches for anything non-trivial: `feature/necromancer-school`, `fix/attunement-decay`, `data/frontier-wilds-region`.
- Merge via pull request so both contributors see changes before they land on `main`.

### Commit Message Prefixes

```
[feat]      New gameplay feature or system
[fix]       Bug fix
[data]      JSON content only — cards, regions, encounters, buildings
[refactor]  Code restructure, no behavior change
[ui]        UI-only change
[docs]      README, comments, documentation
[chore]     Build config, .gitignore, project settings
```

Examples:
```
[data] Add Necromancer starter deck (12 cards)
[feat] Implement FogOfWarManager with 3 hex visibility states
[fix] Attunement counter not decaying on enemy turn end
[refactor] Move JsonCardLoader to Scripts/Cards/Loader/
[docs] Update README with accurate Scripts/ paths
```

### Ownership

Phase 3 parallel work splits cleanly by layer:

- **Content work** (`Data/Cards/`, `Data/Regions/`, `Data/Encounters/`) — near-zero merge conflict risk; each card and region is an independent file.
- **Systems/code work** — low conflict risk if you're in different script files.
- **High-traffic files** (coordinate before touching both at once): `GameRunner.cs`, `JsonCardLoader.cs`, `GameState.cs`, `CardRuntime.cs`.

---

## 10. Code Style & Comment Conventions

Adopted at the start of Phase 3 — Horizontal Content, as the codebase opens up to additional contributors. The convention applies to all project-owned C# under `Scripts/` and to JSON Schemas under `Schemas/`. Existing files are being migrated; any file that lacks the file header (§10.1) is either third-party / generated or has not yet been migrated.

### Guiding principle

Comments earn their space by carrying information the code itself cannot. A reader fluent in C# can already see what a method does from its identifiers and call shape — what they can't see is *why* it exists, *where it sits* in the larger system, and *which other files it talks to*. That is the information this convention captures.

The convention does NOT require commenting every member, every body, or every line. Auto-generated narration on obvious code adds visual noise, balloons the diff for every refactor, drifts out of sync with the code, and trains readers to ignore comments because most of them are useless. We comment the public API surface, the file-level orientation, and the genuinely non-obvious internals — nothing else.

### Excluded paths

The convention does NOT apply to:

- `addons/` — third-party Godot plugins. Leave untouched.
- `.godot/` — engine cache. Never edit.
- `*.uid` files — Godot-generated. Never edit.
- Anything under `.godot/mono/temp/`.
- `.tscn` scene files — editor-authored; rely on the editor's node naming.

If you find yourself wanting to comment generated or third-party code, wrap it in your own type and comment that wrapper instead.

### 10.1 File Header Block

Every `.cs` file in `Scripts/` begins with this banner, placed below the `using` directives:

```csharp
// ============================================================
// <FileName.cs>
//
// Purpose:        One sentence on what this file owns.
// Layer:          UI | Data | Runtime | System | Loader | Targeting | Effects | Predicates | Tiles | Style
// Collaborators:  <FileA.cs>, <FileB.cs>          (files this one talks to directly)
// See:            README §<N>.<M>                 (optional — link to relevant doc section)
// ============================================================
```

Rules:

- The `=` separators are 60 columns wide so the banner is greppable: `grep -rn "^// ===" Scripts/` lists every file's header.
- `Purpose` is one sentence. If you can't compress it, the file is doing too much — split it.
- `Layer` is one of the values listed above. If your file genuinely doesn't fit, propose a new layer in PR review rather than inventing one.
- `Collaborators` lists *direct* collaborators only. Indirect dependencies (e.g., everything under `Cards/Effects/` because you're a card-related file) do not belong here.
- `See` is optional but encouraged whenever the README has a section that frames this file's purpose.

### 10.2 XML Doc Comments — Public API Only

C# XML doc comments (`/// <summary>`) appear on:

- Every `public` type (class, struct, enum, interface, delegate) in `Scripts/`.
- Every `public` member of those types: properties, fields, methods, events, signals.
- Every `public` enum value whose meaning is not self-evident from the name.

They do NOT appear on:

- `private`, `internal`, or `protected` members.
- Trivial property getters/setters that wrap a backing field with no extra meaning.
- Godot lifecycle overrides (`_Ready()`, `_Process()`, `_PhysicsProcess()`, etc.) — the framework already defines what they do.
- Unity-style implementation details that the consumer should never need to know about.

Format:

```csharp
/// <summary>One-sentence description of what this represents or does.</summary>
/// <param name="x">Only when the parameter's meaning isn't obvious from name and type.</param>
/// <returns>Only when the return value's meaning isn't obvious.</returns>
/// <remarks>Multi-paragraph context. Use sparingly.</remarks>
```

Bad — restates the code, adds no information:

```csharp
/// <summary>Gets or sets the card name.</summary>
public string CardName { get; set; }
```

Good — carries information that isn't in the signature:

```csharp
/// <summary>
/// Stable identifier shown in the UI and used by save data. Distinct from
/// <see cref="InstanceId"/>, which is unique per shuffled-into-deck instance.
/// </summary>
public string CardName { get; set; }
```

### 10.3 Inline `//` Comments

Use inline `//` only when at least one of these is true:

- The next few lines do something the reader cannot infer from identifiers and call shape.
- A specific Godot or .NET quirk forces an unusual idiom (cross-reference §8 when applicable).
- You're tagging a deliberate compromise: `// HACK:`, `// TODO(name):`, `// FIXME:`, `// NOTE:`.

Tag prefixes are case-insensitive but greppable. The `(name)` on `TODO` is the person who owns it.

```csharp
// TODO(magos): replace with proper status duration system once it lands
// HACK: CallDeferred required — see README §8 (Godot 4.6 compat rules)
// FIXME: this loses precision for elements with > 4 attunement counters
// NOTE: order matters — Conditions must run before Costs (see Ability.CanPlay)
```

Do not narrate obvious flow. `// loop through cards` above a `foreach (var c in cards)` adds no information.

### 10.4 Region Separators

For files over ~150 lines (typically large UI classes like `CombatUI.cs`, manager classes like `DeckManager.cs`, factory classes like `JsonCardLoader.cs`), group related members with a region separator:

```csharp
// ── Selected Unit Panel ─────────────────────────────────────────────────
```

Rules:

- Use `──` (U+2500 box-drawing horizontal) — not `--` — so the separator is visually distinct from inline `//` comments.
- Pad each separator out to roughly 72 columns. Consistent length makes the file scan-friendly when you scroll past.
- Use the same separator for both field groups and method groups; don't introduce a different style for one or the other.
- Do not use C#'s `#region` / `#endregion`. They collapse in IDEs and hide structure from readers using non-folding editors or `git diff`.

### 10.5 JSON Schemas

JSON itself doesn't support comments. For schemas, use the schema's `description` field — VS Code surfaces it as a tooltip in editors that have the schema bound. `Schemas/card.schema.json` already uses this pattern; new schemas should follow it.

Every property in a schema should have a `description` that explains *why* the property exists or *how* it interacts with other properties — not just what type it is. The type already tells the reader the type.

### 10.6 Quick Reference

| Surface | Convention | Notes |
|---|---|---|
| Top of every `.cs` in `Scripts/` | File header banner (§10.1) | Required |
| `public` types and members | `/// <summary>` XML docs (§10.2) | Required if non-obvious |
| `private` / `internal` members | Nothing, unless non-obvious | Use `//` if so |
| Large files | `// ── Section ──` separators (§10.4) | Encouraged > ~150 lines |
| Compromise / deferred work | `// TODO(name):` / `// HACK:` / `// FIXME:` | Greppable tags |
| JSON schemas | `description` fields (§10.5) | Required on every property |
| `addons/`, `.godot/`, `*.uid`, `.tscn` | Leave alone | Not project-owned |
