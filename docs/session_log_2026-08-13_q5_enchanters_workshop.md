# Session log — 2026-08-13 — Q5: the Enchanter's Workshop + enchant machinery

**VERIFIED IN-ENGINE (Magos, same day): city-view construction, the upgrade
strip, and Workshop enchanting all confirmed working.** Compile fixes along
the way: BuildingTier.DisplayName field added (JSONs carried it, C# never
parsed it); purchase-before-place ordering (PlaceBuilding refuses Tier 0).

Final increment of the 2026-08-13 session (after K3/K4/K5 and Q4.1–Q4.4).
**NOT COMPILED — static verification only** (balances 0 across all nine files;
symbols grepped; building JSON parses). Q5 STARTING VALUES throughout — v1
§6–7's numeric content is gone (same as K4); everything here is fresh-authored
under the empirical pillar.

## What Q5 is

The building that didn't exist, the enchant slot that had nothing to hold, and
the blight system that had nothing to seal — built as one coherent piece so
each part's rules enforce the others'.

## The machinery

**Building** — `Data/Buildings/enchanters_workshop.json` (hostsSystem
"workshop"): T1 Enchanter's Bench (100g, stat lines) → T2 Scripting Chamber
(220g, scripted effects) → T3 The Unbinding Floor (400g, Cleanse — R23).

**Enchant slot** — `ItemInstance` additive fields (rides the existing Armory
serialization, no new struct, no version bump): `EnchantKey/Value/Param/
Trigger`, `EnchantSealed`, `DrawbackKey/DrawbackValue`, `BlightBonus`,
`IsBlighted`. v1 rules enforced by construction: ONE slot; two-effect ceiling
(innate + 1 enchant); slot identity per enchant's AllowedSlot; the Workshop is
the sole mutation venue; re-enchanting overwrites at full price.

**Catalog** — `WorkshopEnchants.cs`: 8 handcrafted lines (no procedural
affixes), tier-gated. T1 stat lines (Keen Edge, Hardened, Vital Thread, Deep
Well); T2 scripts on LIVE keys only (Warding Script → corruption_ward,
Waymark: Forest → pathfinder, Aegis Script → shield_self/onSpawn, Mending
Verse → regen_aura/aura). No new effect keys anywhere — no new handlers.

**The resolution seam** — `EquipmentLoadout.BuildForRun` is still the ONE
place items become effects (§7a's whole point). Three additions, same loop:
the innate's PassiveValue becomes `def.PassiveValue + instance.BlightBonus`
(the blight's above-floor bump, wherever that value lands — overworld field,
trigger bus, or legacy enum); the enchant routes through a new
`AccumulateEffect` using the same key vocabulary ("stat_*" → bonus fields,
overworld keys → party-summed fields, trigger keys → the Q2 bus; unknown keys
inert, never crashing); blight drawbacks land as negative stat bonuses so
every existing consumer prices them without new plumbing.

**Blight** (§7d) — at the combat-drop site: fights won on corrupted ground
(CorruptionTierAt ≥ 2 at the hex) roll 35% per drop: authored drawback (4-entry
table: −3 maxHP / −1 armor / −1 speed / −1 damage), slot SEALED, +1 innate,
"Blighted" name prefix. **Cleanse** (T3): 150g + 25 Arcane Splinters — strips
the drawback, unseals the slot, restores the name, KEEPS the bonus ("what the
corruption improved, it keeps" — the §7d reward identity).

**UI** — `CampusWorkshopPanel` (new tab, id 9): per-item card with innate
line, blight state, slot state; enchant verb buttons (tier + slot + gold
gated); Cleanse verb on blighted items. Wired: `CampusPanelId.Workshop`,
registry "workshop" key, tabNames + build + refresh in CampusScreen. Uses
`using static CampusUi` (the helpers are NOT on CampusPanel — caught
statically).

## First-launch checklist

1. Build. Then build the Workshop at campus (tier 1).
2. Workshop tab: Armory items listed; a Weapon offers Keen Edge, an Armor
   offers Hardened, everything offers Vital Thread. Enchant one → gold falls,
   slot shows the name, and next expedition the stat visibly lands (loadout
   readout / unit spawn).
3. Tier 2 → scripted enchants appear; Aegis Script on armor → 3 shield at
   combat start via the trigger bus, proc in the combat log grammar.
4. Fight on corrupted ground (tier 2+) until a drop blights: "Blighted" name,
   drawback line, sealed slot, innate reads "(blight-strengthened)".
5. Tier 3 → Cleanse it (needs 150g + 25 splinters): drawback gone, slot
   usable, bonus retained. **This is the Q4/Q5 exit criterion from the spec.**
6. Save/load: enchant + blight state round-trips (rides ArmoryData).

## Addendum — construction from the city view (the gap the Workshop exposed)

Magos: "there's no way to build new buildings in the campus at the strategic
map level." Correct — construction lived only in CampusScreen's Campus tab
(TryBuildOrUpgrade + the 2D placement input); the Phase-2 city view could open
panels for EXISTING buildings but never raise a new one. The Workshop made
this bite immediately. Fixed:

- `Scripts/Systems/Campus/CampusConstruction.cs` (new): the purchase core
  extracted VERBATIM from CampusScreen.TryBuildOrUpgrade (which now
  delegates), plus `Unbuilt(save)` and player-readable `CannotBuildReason`.
  One purchase path, two callers.
- `WorldAtlas3D`: clicking a bare home-grounds hex (not a building, landmark,
  or annex preview) now raises `HomeGroundPicked(coord)` — a building site.
  `TryPlaceHomeBuilding(id, coord)` places at rotation 0 via the same
  `CampusGridManager.PlaceBuilding` the campus uses, then `RefreshCityGrowth()`
  rebuilds the grounds in place.
- `StrategicView`: the construct card (right-docked, CityServicesHost style) —
  the unbuilt ledger with tier-1 costs, greyed rows carrying their reason
  (cost / prerequisite). **Place FIRST, purchase second** — siting is
  revertible, spent gold is not; a failed purchase after siting reverts
  IsPlaced. Multi-tile footprints that don't fit at rotation 0 toast a
  pointer to the campus placement tool (which rotates) rather than failing
  silently. Toasts ride the existing city-explore ToastManager (StrategicView
  has no ShowInfo — that idiom is ExpeditionManager's; caught statically).

Checklist: city view → click empty campus hex → card lists Workshop et al with
costs → build → building appears on the grounds in place, gold/materials fall,
effects live (IsFunctional = Tier>0 && IsPlaced); click it → its panel floats.

## Addendum 2 — upgrades in city view + footprint preview

Two more gaps Magos hit in play, both fixed:

1. **No upgrade path in city view** (clicking a built building only opened its
   menu). `HomeBuildingPanelHost` now takes the buildingId and renders an
   **upgrade strip** under the header — tier N/max + tier name + Upgrade
   button (cost, or the CannotBuildReason greyed) — through the same
   CampusConstruction core. Upgrading refreshes the hosted panel live (a
   Workshop tier-up reveals its new verbs in place). The panel id is now
   nullable: buildings with NO system panel open a strip-only host instead of
   being mute. Workshop added to CanFloat/CreatePanel (no lifecycle deps), so
   it floats instead of falling back to the full overlay.
2. **No footprint visibility before building.** (a) City construct card:
   hovering a row paints the building's would-be footprint on the grounds at
   the chosen anchor — Gold = fits, Danger = doesn't (new
   `CampusGridManager.TintHexes`/`RestoreHexVisuals` +
   `WorldAtlas3D.PreviewHomeFootprint`; cleared on hover-exit and card
   close). (b) Campus tab list + city card rows now print
   "footprint N hexes" up front.

Note for the planned adjacency-bonus work: `TintHexes` + the preview seam in
`PreviewHomeFootprint` are exactly where adjacency highlighting should hang —
tint neighbours by bonus quality at preview time, and the anchor-choice reads
become strategic for free.

## Q track status

Q1–Q5 all built (pending compile + checklists). Still open, deliberately:
signature binding module (v1 optional module — needs a Sworn-companion
binding surface; natural companion to the Muster/dossier UI pass), Auction
House + Corrupted-relic routing (needs the building), terrain-flavored market
pools (Phase G), Legendary market exclusion holds until the Auction House
exists. Next per the session queue: **exploration tranche 3**.
