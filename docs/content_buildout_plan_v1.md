# Content Buildout Plan — v1

**Date:** 2026-08-21 · **Status:** Proposal, needs your ruling on §8.
**Method:** every "Have" number below was counted from the live working copy
(`Data/`) today, not from session-log memory. Targets are my estimates from
your own consumption rates (run_structure_v2: ~6–10 engaged POIs per 20–35 min
run; assumed 25–40 hr base game ≈ 40–70 runs). Confidence flags per section.

---

## 0. The counterargument first

The premise "the world is a shell, I need to author a mountain of content" is
half wrong, and the half that's wrong is expensive. The shell feeling comes
from three specific, verifiable facts — not from a general content shortage:

1. **14 of 15 regions share one 20-encounter generic pool.** Only
   `frontier_wilds` has bespoke encounters (4). The regions are written; their
   *population* is context-free.
2. **All 20 generic encounters are one-shot** (every one has an `id`, and
   `PickRandom` filters completed ids). The pool exhausts within a cycle and
   the map goes silent. The assembler — built precisely to fix this — has
   exactly **one** production skeleton and 18 fragments.
3. **`Data/Encounters/ripples.json` does not exist.** The loader already loads
   it on every deploy; EchoSeeder already stamps `echo_*` flags on cycle 2.
   Your flagship "the world remembers" system currently pays off in silence.

Meanwhile the companion layer (20/20 arcs, 101-encounter mission pool), the
fragment spine (all 6 arcs), the dossier layer (8/8), and the enemy roster
(~60 non-debug unit archetypes, 25 combat maps) are **at or above** what a
1.0 needs. The gap is narrow and addressable. Do not author 300 encounters;
author the ~120 units below, in this order, with the playtest gate at §6.
Confidence: high on the diagnosis, moderate on every target count.

---

## 1. Verified inventory vs. target

| Category | Have (counted today) | 1.0 target | Gap |
| --- | --- | --- | --- |
| Generic narrative pool | 20 (all one-shot; 1 assembler skeleton) | 25–30, majority repeatable | Convert + add ~10 |
| Assembler fragments | 18 (site 8 / detail 6 / complication 4) | 60–80, incl. region-tagged | ~50 |
| Region-bespoke encounters | 4 (frontier_wilds only) | 4–6 × 14 regions ≈ 60–80 | ~60 |
| Ripples / echoes | **0 — file absent** | 20–30 | 20–30 |
| Negotiation tables | 9 (5 generic archetypes, 2 commanders, 2 region-bespoke) | 20–25 | ~12–15 |
| Quest lines | 18 (10 quests.json + 8 dossiers) | ~45–55 | ~30 (see §5) |
| Companion arcs | 20/20, 101-encounter pool | — | **Done** |
| Fragment arcs | 20 + Primal 3 | — | **Done** |
| Enemy unit archetypes | ~60 non-debug (10 casters, 24 archmage-bespoke, beasts, generics) | 40–60 | **Done** (dispatcher debt, §7) |
| Combat maps | 25 (16 biome + 11 battlefield) | — | **Done** |
| Ambient micro-events | ~0 outside assembler | 30–50 one-liners | 30–50 |

---

## 2. Workstream 1 — Feed the assembler *(highest value per word)*

The engine exists (`EncounterAssembler.cs`); it is starving. One authored
skeleton × M tagged fragments = combinatorial apparent variety. This is the
cheapest fullness in the entire plan.

**Build:**
- **8–10 new skeletons with empty `id`** in `generic_encounters.json` so they
  repeat, assembling fresh each draw. `assembled_wayside` proves the pattern.
  Vary the frame: a crossing, a camp, a corpse, a shrine, a signal, a trade,
  a warning, a wound. Each skeleton: body + 2 choices, ~80–150 words.
- **Grow `fragments.json` to 60–80 fragments.** Current slots (site / detail /
  complication) are thin — 8/6/4. Target ~20 per slot, plus 1–2 new slots
  (`figure`, `weather`). Fragments are one sentence each.
- **Region-tagged fragments.** `Pick` already supports `RegionTags`; zero are
  authored. 2–3 fragments per region (~35 total, included in the count above)
  is what makes the same skeleton read differently in the Hollow Mire vs. the
  Jade Coast — bespoke flavor at fragment cost, not encounter cost.
- **Token-ize choice text** (engine change, deferred in the assembler session:
  display clone must also clone Choices). Small, do it while in the file.

**Effort:** ~4–6k words + one small code change.
**Acceptance:** debug-summon the same skeleton on 5 terrains → 5 distinct
reads; two draws on the same tile differ.
Confidence: high.

## 3. Workstream 2 — Author `ripples.json` *(the silent dead system)*

Machinery fully wired, content absent. These encounters are worth more per
unit than anything else because they make *everything else* feel
consequential — they are the proof the world remembers.

**Build 20–30 encounters keyed to existing `RequiredFlag` seeds:**
- Honored dead echoes (~8): a grave-marker, a song, a debt-collector, a rival
  who blames you — each gated on a `HonoredDead` echo flag.
- Deal echoes (~6): consequences of negotiation deals/walkaways from prior
  cycles (the qualifier flags already exist — `qe_negotiation_*`).
- Loop-record echoes (~6): the Anchorhold's chronicle leaking into the world;
  someone quotes a thing only you should know.
- Unfinished Business echoes (~5): archived timeline quests resurfacing as
  one-beat encounters.

Enumerate the exact flags EchoSeeder emits **before** authoring — write to the
flags that exist, not the flags the spec promises. (I can produce that flag
inventory from `EchoSeeder` + `HonoredDead.cs` as a prep step.)

**Effort:** ~5–7k words. **Acceptance:** unmake once; cycle 2 surfaces ≥3
echoes referencing cycle-1 specifics.
Confidence: high on value; moderate on the flag surface until inventoried.

## 4. Workstream 3 — Regional bespoke pools + negotiation coverage

**Bespoke encounters, 14 regions × 4 each (~56).** Follow the
`frontier_wilds_encounters.json` pattern. Per region: 2 one-shot discoveries
(id'd — the "deep world" units), 1 repeatable flavor piece (no id), 1 that
sets a metaflag or grants something persistent (the gap report's "vending
machine" critique — encounters must leave marks). Write regions in the order
players meet them; `the_convergence` needs finale-adjacent content only.
**Do not write all 14 regions before the §6 gate.**

**Negotiations (~12 tables):**
- 6–7 region-bespoke tables for the regions most defined by their politics
  (amber_downs, boreal_march, cogwork_reach, obsidian_waste, tidewrack_coast,
  verdant_deep, the_crags). Clone-and-flavor from the two existing bespoke
  tables; your own audit called a table "a 10-minute job."
- 2–3 more commander tables (warfront regions currently fall back silently).
- 2 Confrontation-tier tables (the spec'd high-stakes escalate-to-combat
  archetype — currently zero exist).

**Effort:** encounters ~8–12k words; negotiations ~4–6k. Confidence: moderate.

## 5. Workstream 4 — The missing quest families

All spec'd in `quest_system_narrative_spec_v1.md`; none authored:

- **Building restoration quests** (~10 × 3 beats, `q_relight_refectory`
  template). 15 buildings exist in `Data/Buildings/`; pick the ~10
  significant ones. These convert campus-building from base-building into
  archaeology — high emotional value, mostly reuses existing encounter seams.
- **Fluency quests** (8 × `q_second_tongue_<school>`). Mostly objective
  plumbing against mastery milestones + one lore beat each. Blocked on
  mastery actually existing (see §7).
- **Incidental timeline quests** (~8–10). Small 2-beat chains sourced from
  W3's bespoke encounters — author them *together with* the regional pools so
  encounters chain instead of standing alone (the discovery-chain pattern
  run_structure §7 already specs).

**Effort:** ~8–10k words + quest JSON. Confidence: high (it's your own spec).

## 6. The playtest gate *(do not skip)*

After W1 + W2 + **one** region's W3 pool: play 5–8 runs across 2 cycles and
measure when repetition registers — how many runs before you see a verbatim
repeat that annoys you, and whether cycle 2 feels remembered. That measured
rate recalibrates every target in §1 before you commit to 13 more regions.
Authoring all 56 bespoke encounters against an unvalidated weighting is the
classic overproduction failure. Your own pillar: constants are starting
values, tuning is playtesting. Confidence: high.

## 7. Deliberately last / not content problems

- **Combat compositions.** ~60 unit archetypes and 25 maps carry you; deck
  variance absorbs composition repetition. Add compositions opportunistically
  when a region pool wants a themed fight. **Exception:** the wildlife
  behaviour-tag dispatcher (pack/bulwark/charge/scout) is unbuilt — it blocks
  beast-heavy compositions AND the Druid starter deck's deferred beast cards.
  That's a code task, not a content task, and it's load-bearing debt.
- **Ambient micro-events** (30–50 one-liner companion/terrain barks): cheap,
  effective, but they polish a world that already works — slot them into idle
  writing time, not the critical path.
- **Do not touch:** companion arcs, fragment arcs, dossiers. Done. Adding
  more there is procrastination with extra steps.

**Total new authoring, whole plan: roughly 30–40k words** — a novella, not a
mountain, and W1's combinatorics mean the *apparent* content is a multiple of
that.

## 8. Sequencing & rulings needed

Order: **W1 → W2 → W3 (one region) → GATE → W3 remainder + W4 in parallel.**
W1 first because it multiplies everything authored after it; W2 second because
cycle-2 payoff is the game's thesis; quests interleave with regions so chains
form naturally.

Rulings needed from you:
1. **Voice.** The text-inventory sweep exists because you intend to rewrite
   AI-authored player-facing text in your own voice. Ruling: do I draft full
   encounter text for you to rewrite, or draft structure/JSON with placeholder
   bodies you author from scratch? This changes the effort math above by ~2×.
2. **Region order** for W3 — which region after the gate, or accept my
   default (players' likely early-cycle ring first).
3. **Fluency quest timing** — author now against the spec, or hold until the
   mastery/unlock-filter gaps (the inert progression wiring) are fixed so the
   quests have something real to track. My recommendation: hold.
