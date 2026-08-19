# Scrying Chambers — Design Spec v1

**Status:** proposed, pre-implementation. Written 2026-08-18.
**Building:** `Data/Buildings/scrying_chambers.json` (currently: all three tiers
`"Placeholder — mechanical effects not yet designed"`).
**Authored costs (unchanged):** T1 150g · T2 275g · T3 450g. `category: "Magic"`,
footprint 3 tiles (0,0)(1,0)(0,1).

This is the one genuinely featureless campus building. Every other "inert" building
(Undercroft, Embassy, Teleport Sigil) turned out to already gate real behaviour on its
tier; Scrying has zero implementing code. This spec gives it a mechanical identity that
reuses existing systems rather than inventing one.

---

## 0. Counterarguments, stated first

1. **It overlaps with the Courier Station.** Courier already pre-reveals hexes at run
   start. A second "reveal stuff at run start" building risks being a strictly-better or
   strictly-redundant Courier. *Mitigation:* hard split the domains — Courier reveals
   **terrain** (raw fog hexes), Scrying reveals **information** (POIs, the objective,
   foreknowledge). They answer different questions ("where can I walk" vs "what's out
   there and what's coming"). If that split doesn't hold up in play, one of the two
   should be cut, not both kept.
2. **Information buildings can feel flat.** A "solved map" reduces the exploration
   tension that a roguelike overworld runs on. Revealing everything is not obviously fun.
   *Mitigation:* bounded counts (reveal *N nearest*, not *all*), and push the capstone
   toward **foreknowledge of threats** (the tense, decision-shaping kind of information)
   rather than **loot maps** (the tension-removing kind).
3. **Building this now delays the playtest** and adds net-new surface that won't be
   battle-tested when full-scale testing begins. This was raised and overridden; logged
   here so the cost is on the record. *Confidence this is a real cost: high.*

---

## 1. Identity

**The Scrying Chambers is the guild's divination organ: it converts campus investment
into pre-run intelligence.** Where the Courier Station scouts ground, the Scrying
Chambers sees *sites, objectives, and threats*. Its vocabulary aligns with the existing
`scrying_lens` overworld spell: it **charts** (reveals location without exploring), it
does not **explore** (grant the tile's contents). A charted POI shows as a beacon; the
party still has to walk there and trigger it.

Design rules honoured:
- **Extend, don't parallelise.** No new manager. Every effect routes through an existing
  primitive (`RevealNearestPois`, `FogOfWarManager.RevealHex`, `SpellChartHexRadius`).
- **Two-struct limit.** Zero new structs. New fields are added to the existing
  `BuildingEffectApplier.RunBonuses` and `BuildingTierData`, mirroring how
  `PreRevealHexCount` already works. The one piece of session state (T3) reuses
  `PlayerSession`, which already holds feature/run flags.

---

## 2. Tier ladder

### Tier 1 — "The Still Water" (150g)
At run start, **chart the N nearest hidden POIs** as beacons. N = **2**.

- Hook: `ExpeditionManager.RevealNearestPois(int count)` already exists and already
  produces the "Intel: k sites marked" toast. It is currently only called from event
  choices (`choice.RevealPois`). T1 calls the same method at run start.
- Data: new `BuildingTierData.RevealPoiCount` (int), aggregated into
  `RunBonuses.RevealPoiCount` in `CalculateRunBonuses`, applied alongside the existing
  `PreRevealHexCount` block at run start (`ExpeditionManager:~348`).
- Player value: converts blind opening moves into a routing decision — which of two known
  sites do I hit first, given my step budget.
- **Confidence: high.** Pure reuse of a shipped primitive; one new int field on two
  existing structs.

### Tier 2 — "The Far Sight" (275g)
Everything in T1 (POI count rises to **3**), plus at run start **chart a radius around
the run objective**, so the objective's location is known from turn one.

- Hook: locate the objective POI (the run already knows its objective site), then
  `ExpeditionManager.SpellChartHexRadius(col, row, radius)` (the `scrying_lens` spell's
  own charting call) with radius **2**.
- Data: new `BuildingTierData.ChartObjectiveRadius` (int) → `RunBonuses`.
- Player value: removes "wander until you stumble on the objective," which is the least
  interesting failure mode of a fogged overworld. You still choose the *route* and pay
  the *step cost*; you just aren't blind about the destination.
- **Open dependency:** the objective's map coordinate must be resolvable at run-start
  (before the player has explored to it). *Confidence the objective is locatable at
  start: moderate* — must be verified against the live run-setup code before building
  (see §5). If it is not, T2 degrades to "chart +1 extra POI" and the objective clause
  moves to T3.

### Tier 3 — "The Third Eye" (450g) — capstone
Two options. **Recommended: 3a (overworld-only).** 3b is a stretch that couples to
combat and should not be built pre-playtest.

**3a — Portent (recommended). [IMPLEMENTED v1, 2026-08-18]** POI count rises to **4**, and
once per run the party **foresees and slips the first Ambush**. Shipped behaviour: the
first patrol interception is *avoided entirely* — the patrol passes, no combat — consuming
the per-run flag. (The spec originally proposed *downgrading* Ambush→normal so the fight
still happens without surprise; full avoidance was chosen for v1 because it mirrors the
existing, tested `DebugNoAmbush` early-return and does not touch encounter-tier
composition. Downgrade remains a valid tuning alternative — decide in play.) Implemented at
the **encounter-trigger layer** (`OnPatrolCapturedPlayer`, after the player-armed Parley
check so a deliberate cast still takes precedence), not inside `CombatManager`.
- Data: reuse `PlayerSession` for a per-run `ScryingPortentAvailable` flag, set at run
  start when Scrying tier ≥ 3, consumed on the first Ambush.
- Player value: the tense kind of information — it changes how aggressively you can
  route through dangerous terrain, because you're insured against one bad surprise.
- **Confidence: moderate.** Needs one live-code check: the exact point where an
  encounter is classified Ambush (§5). No new struct; no combat-internal changes.

**3b — Foreknowledge (stretch, NOT for this playtest).** In combat, enemy intents are
revealed one turn earlier than normal. Thematically the strongest capstone, but it
couples to `CombatManager`/`EnemyIntents` (high-churn, effectively save-adjacent during a
fight) and needs a flag read deep in the combat loop. **Deferred:** the risk/reward is
wrong immediately before a full-scale playtest. Revisit after the base loop is validated.
- **Confidence it's worth the coupling now: low.**

---

## 3. Data & code touch-list

New tier-data fields (JSON, mirroring `PreRevealHexCount`):
- `revealPoiCount` (int) — T1:2, T2:3, T3:4
- `chartObjectiveRadius` (int) — T2:2 (0 elsewhere)
- `portent` (bool) — T3:true

`BuildingEffectApplier.RunBonuses`: add `int RevealPoiCount`, `int ChartObjectiveRadius`,
`bool Portent`. Aggregate in `CalculateRunBonuses` (note the existing loop already sums
**all built tiers**, so counts must be authored as per-tier deltas, not cumulative
totals — otherwise a T3 building double-counts T1+T2 reveals; see §5).

`ExpeditionManager` run-start (~line 348, beside the `PreRevealHexCount` application):
- if `RevealPoiCount > 0` → `RevealNearestPois(RevealPoiCount)`
- if `ChartObjectiveRadius > 0` → resolve objective coord → `SpellChartHexRadius(...)`
- if `Portent` → `PlayerSession.ScryingPortentAvailable = true`

`ExpeditionManager` encounter classification: if an encounter would be an Ambush and
`PlayerSession.ScryingPortentAvailable`, downgrade to normal and clear the flag.

`PlayerSession`: add `ScryingPortentAvailable` (bool, per-run — reset at run start, not
persisted to the ledger).

**No new structs. No new manager. No save-schema change** (per-run session state only).

---

## 4. The aggregation gotcha (must-read before coding)

`CalculateRunBonuses` sums every tier from 1..CurrentTier ("a Tier 2 building should carry
Tier 1 flags too"). So tier fields are **deltas**, not totals. For the POI count that
means authoring T1 `revealPoiCount:2`, T2 `revealPoiCount:1`, T3 `revealPoiCount:1` →
totals 2/3/4. Authoring 2/3/4 directly would sum to 2/5/9. This is exactly the class of
"looks right, silently wrong" bug the run-bonus loop invites; call it out in the JSON with
a comment-adjacent field name or a code assertion.

---

## 5. Verify-before-build checklist (live code, on desktop)

1. **Objective locatability at run start** — confirm the run's objective POI has a map
   coordinate resolvable before exploration. Determines whether T2's objective clause is
   viable or slides to T3. *(gates T2)*
2. **Ambush classification hook** — find the single point where an encounter is decided to
   be an Ambush; confirm a per-run flag can veto it cleanly. *(gates T3a)*
3. **`RevealNearestPois` at run start** — it was written for the mid-run event path;
   confirm calling it at run-start (fog freshly initialised) marks correctly and doesn't
   assume prior exploration.
4. **Delta vs total** — encode POI counts as per-tier deltas (§4) and eyeball the summed
   totals in a T3 save.
5. **Build gate** — `tools/build-check.sh`; no dotnet in the design environment.

---

## 6. Sequencing

1. JSON tier data (deltas) + `BuildingTierData` fields.
2. `RunBonuses` fields + `CalculateRunBonuses` aggregation.
3. Run-start application (T1 POI reveal) — smallest end-to-end slice; ship and eyeball first.
4. T2 objective charting (after check §5.1).
5. T3a portent (after check §5.2).
6. T3b foreknowledge — **not now.**

Each step is independently testable; do not batch. T1 alone is a complete, shippable
feature and validates the whole reveal-at-run-start path before any objective/ambush work.

---

## 7. Rulings needed

- **R1 — T3 capstone:** confirm 3a (Portent) over 3b (combat Foreknowledge) for v1.
  Recommendation: 3a. *(Spec assumes 3a.)*
- **R2 — POI counts:** 2/3/4 acceptable, or tune? These are starting values, tune in play.
- **R3 — T2 objective reveal:** keep it at T2 if locatable (§5.1), else accept the slide
  to T3. Recommendation: keep at T2 if the check passes.
- **R4 — overlap ruling:** if Courier (terrain) and Scrying (information) don't feel
  distinct in play, which survives? Recommendation: decide empirically post-playtest, do
  not pre-cut.
