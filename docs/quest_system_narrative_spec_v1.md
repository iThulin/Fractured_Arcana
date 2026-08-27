# Fractured Arcana — Quest System Narrative Spec (v1, CANONICAL)

**Status:** Canonical as of 2026-07-21, user-ruled. Companion to
`docs/narrative_frame_intro_finale_v1.md` (the Long Second frame),
`docs/shard_acquisition_spec_v1.md`, and
`claude/progression_persistence_model_v1.md` (the two-layer law this spec
maps quests onto). Builds on the quest infrastructure shipped 2026-07-18
(QuestData/Loader/Tracker, Quests tab, lore-gating, metaflag ladders, toasts,
global topbar).

**User rulings recorded (2026-07-21):**
1. **Remembrance exception: YES** — a high-tier campus building lets ONE
   chosen companion keep their memory across a timeline reset (§5c).
2. **Archmage dossiers grant mechanical unlocks** in later timelines (§4b).
3. **Cross-cycle echoes: YES** — permanent records seed small quests into
   fresh timelines (§6b).
4. **Quest log reorganizes by persistence layer** — Eternal / This Timeline,
   with an Unfinished Business archive (§7).
5. **(2026-07-21, later same day) The Remembrancer's Hall is unlocked by
   recovering the Moment Eternal** — the Chronomancer fragment reinforces the
   anchor until it is strong enough to hold one more person (§5c). The
   shard system and the companion system couple at exactly this point, on
   purpose.

---

## 1. The organizing principle

**Quests are knowledge, and knowledge is permanent.** The quest log is not a
to-do list; it is the **Anchorhold's chronicle**, and the player is the only
one reading it across timelines. People reset on every unmake; what you
*know* about them does not. This is the Outer Wilds loop applied to people
instead of ruins — and deliberately the opposite of the Hades solution
(everyone remembers): here, losing a timeline means losing people who knew
you, and the log is where that loss is visible. The tragedy is the emotional
engine, not a bug to design around.

Consequences of the principle:
- Quest **progress about the world's people and politics** lives on the
  timeline layer (CycleState) and is archived at unmake.
- Quest **knowledge** — everything the player has learned, witnessed, or
  finished anywhere, ever — lives on the EternalLedger and compresses,
  branches, or unlocks future attempts.
- Every quest in the game belongs explicitly to one class (§2). No
  exceptions, no hybrids — a "hybrid" is always two linked quests, one per
  layer (the archmage dossier/resolution pair is the template, §4).

---

## 2. The two classes

| | **Eternal quests** | **Timeline quests** |
| --- | --- | --- |
| Storage | EternalLedger metaflags | CycleState |
| On unmake | Untouched | Archived to Unfinished Business (§7) |
| Content | The Sixfold Seal, Raise the Anchorhold, school Fluency, archmage dossiers, companion remembrance | Archmage resolutions, companion arc stages, incidentals, echoes, kingdom/court chains |
| Emotional register | Accumulation — "what I am building" | Attachment and loss — "who I knew this life" |

Existing quests re-classed: `Restore the Sixfold Seal` and the six fragment
quests are Eternal (already are — permanent flags). `A Debt in Transit`,
`Extend the Guild's Reach`, `Chronicle the Unknown` are Timeline (already
cycle-scoped). The taxonomy formalizes what the seed quests were groping
toward.

---

## 3. Family 1 — Campus meta quests (Eternal)

**Umbrella: `q_raise_the_anchorhold`** — sibling of `q_sixfold_seal`, visible
from run 1. Objectives tick off restored buildings/tiers.

**Per-building restoration quests.** Each significant building gets a short
Eternal quest whose beats answer *what this place was at Commencement* — who
taught in it, what stopped mid-motion inside it. Campus expansion reads as
archaeology of the school you graduated from, not base-building. Gate campus
**tiers** behind quest beats (a discovery, a material from a specific region,
a rite), not gold alone — gold buys construction; the quest supplies the
*permission* of the story.

Sample chain (template for all — 3 beats max):

- **`q_relight_refectory` — "The Refectory Lights"**
  1. *Clear the frost-stilled hall* (small campus interaction/fight vs.
     leaked aether — reuse an encounter).
  2. *Find the kitchen-master's ledger* (region narrative POI — his last
     order was for the graduation feast that never got served).
  3. *Light the ovens* (build/tier action). Completion beat: the frozen
     feast, still warm inside the Second, is finally served — first campus
     morale/loyalty bonus becomes diegetic.

**Fluency quests (per school): `q_second_tongue_<school>`** — the visible
spine of multi-class meta-progression. Objectives track mastery milestones in
a non-main school; completion = Fluency (shard spec §4) + a lore beat about
that school's idiom. The quest exists so cross-class play has a *narrative*
reward surface, not just a hidden threshold.

---

## 4. Family 2 — Archmage quests (the layer-pair template)

Two linked objects per archmage. This pair IS the pattern for anything that
must feel persistent about people who reset.

### 4a. The dossier (Eternal) — `q_dossier_<archmageid>`
A permanent, accumulating quest. Every timeline adds entries: weakness hints
(**already authored in the archmage JSONs, currently unspent — this is where
they get paid off**, revealed one per qualifying interaction), personality
notes, what corrupts them first, how they fell last time. The co-conspirator's
dossier is bespoke and opens at the intro betrayal (their identity is fixed
per campaign).

### 4b. Dossier tiers grant mechanical unlocks (RULED)
Suggested ladder (tune freely):
- **Tier 1 (met them):** their resolution arc pre-reveals on the strategic
  map in future cycles; disposition shown at Unknown.
- **Tier 2 (resolved them once, any outcome):** one weakness hint revealed;
  court intro shortcut (skip the first standing gate with this seat).
- **Tier 3 (seen 2+ different outcomes):** coercion cost reduced / unite
  standing gate lowered one step; foreknowledge dialogue options in the
  resolution arc ("I know what the Astrologer offered you").
- **Tier 4 (full dossier):** betrayal-encounter intel (see their boss
  second phase before committing); their `introBetrayalLine` variant
  foreshadowed if they are this campaign's co-conspirator.

### 4c. The resolution arc (Timeline) — `q_resolve_<archmageid>`
This cycle's Unite / Coerce / Overthrow chain, driven by dispositions, the
corruption clock, and the court layer (standing gates per
court_council_system §10). Archived at unmake with its outcome stamped into
the dossier. The dossier is what makes re-resolving archmagi feel like
progress instead of repetition: you cannot keep the alliance across resets,
but you keep knowing exactly how to win it, faster, every time.

---

## 5. Family 3 — Companion quests (Timeline arcs + Eternal remembrance)

### 5a. Arc stages (Timeline)
Multi-stage personal arcs per companion, staged via metaflag ladders exactly
like fragment arcs (the proven pattern), sourced from companion narrative
POIs (party-present requirement, as run_structure specs). Wiped with the
companion at unmake.

### 5b. Remembrance flags (Eternal)
Every arc stage the player *witnesses* sets a parallel permanent flag:
`remember_<companion>_<stage>`. Remembrance does three things in later
timelines:
- **Compress:** stages you've witnessed offer a "you already know this"
  fast-path (retold in one beat instead of three).
- **Branch:** foreknowledge options open ("tell her about her brother before
  she's ready to say it") — some branches ONLY reachable on a re-run, which
  makes replaying an arc a different story, not the same story faster.
- **Pay off at the Convergence:** the spirits of the permadead and the
  arc-completion states convergence.docx keys companion fates on are read
  from remembrance + honored dead — finished stories count from ANY timeline.

### 5c. The Remembrancer's Hall (RULED — the earnable exception)
A high-tier campus building. At each unmake, the player may anchor **one**
companion inside the bubble: they persist to the next timeline *with their
memory* — relationship, arc stage, loyalty intact. Everyone else resets.
Constraints that keep it honest: one slot, ever (no stacking with tiers);
choosing is explicit and diegetic (a scene in the Hall, not a menu toggle);
an anchored companion **remembers every timeline you spend together**,
including the ones where their friends forgot them — give this its own small
arc content (the anchored companion is the loneliest person in the world
except for you). The choice of WHO remembers should be one of the most loaded
decisions in the game. Companions remain timeline-layer by law; this is one
sanctioned, expensive exception, not a policy change.

---

## 6. Family 4 — Incidental quests (Timeline, two sources)

Small, self-expiring, 1–3 beats, never more than a handful active. These are
the consequence-rendering system — the world visibly reacting to the player.

### 6a. In-cycle ripples
An encounter outcome spawns a follow-up elsewhere later in the same cycle.
`A Debt in Transit` (the letter chain) is the proven template; the
encounter-outcome expansion work is the generator. Categories: a spared enemy
resurfaces (ally or problem), a negotiation's loser retaliates or a winner
sends a gift, a faction shift opens/closes a POI, blight creep displaces a
settlement's people (ties the shard clock to visible human cost).

### 6b. Cross-cycle echoes (RULED)
Fresh timelines render strangely around the player's **trans-temporal
marks**. Seeded at worldgen from permanent records; rewards are lore/flavor
only (breadth, never power). Sources and samples:

- **Deal records:** a merchant dynasty in the new timeline keeps a debt-book
  with your name in it — spelled right, in an entry older than the dynasty.
  Nobody can explain it. (*"The Standing Debt"*)
- **Honored dead:** a shrine in the new reading venerates a local hero whose
  face you knew — a companion who died in a timeline that no longer
  happened. The epitaph quotes something they said to you. (*"A Stranger's
  Shrine"*)
- **Shard provenance:** the drained scar or healed zone of a fragment you
  took carries a mark in this rendering — the wound remembers being closed,
  and how. (*"The Shape of the Scar"*)
- **Astrologer tells:** rarely, an echo is *his* — a corrupted NPC quoting a
  thing you said in a lost timeline. He reads the sky; sometimes the sky
  kept notes. Use sparingly; these should be unsettling, not routine.

Echoes are the cheapest possible proof that the loop is real: flags + a
narrative encounter each.

---

## 7. The quest log, reorganized (RULED)

Top-level groups become the persistence layers — the log itself teaches the
two-layer model:

- **ETERNAL** ("the Chronicle") — Sixfold Seal, Raise the Anchorhold,
  Fluency, dossiers, remembrance. Never resets. Reuses current
  permanent-quest rendering.
- **THIS TIMELINE** — resolution arcs, companion arcs, kingdom/court chains,
  incidentals, echoes. Header shows the campaign year / lunation so its
  mortality is visible.
- **UNFINISHED BUSINESS** (collapsed archive) — at unmake, live Timeline
  quests move here with their final state: *"Mira's Debt — abandoned at
  stage 2 of 4, Timeline VI."* Emotionally load-bearing: this list is the
  cost of every reset, itemized. The Convergence's Gathering phase may read
  it aloud. Hall of Lore stays in the tab, under Eternal.

Rumor/locked rendering, per-objective checklists, and `SyncCompletions`
carry over unchanged. Migration: re-tag the 10 existing quests with a
`Layer` field; grouping logic swaps from category to layer.

---

## 8. Pieces audit — what actually remains to build

**Have (proven):** quest data/loader/tracker, tab UI, toasts + topbar,
lore/metaflag gating, permanent vs cycle flags, ladder pattern, debug rig.

**Missing (the real work, in rough order):**
1. **Quest event sources beyond narrative POIs.** Hooks firing quest-advance
   from: disposition changes, court events, building completion/tier,
   combat outcomes (win/loss/spare), calendar (lunation-triggered beats),
   shard events (Harvest started / Seizure / recovery). Mostly a thin
   `QuestEvents.Raise(id, context)` shim called from ~6 existing sites.
2. **Cycle-end sweep.** On unmake: archive live Timeline quests to
   Unfinished Business (ledger record — it must survive the wipe), clear
   cycle quest state. On Continue: no sweep (timeline persists).
3. **Layer re-tag + log regroup** (§7).
4. **Remembrance flag convention** + fast-path/branch choice gating on
   `remember_*` flags (HasFlag already reads ledger — likely sufficient).
5. **Echo seeding pass at worldgen** — sample N eligible permanent records,
   site echo POIs (`SiteRuntimePoi` precedent from the Prison).
6. **Remembrancer's Hall** building + the anchoring scene + companion
   carry-over path (the one genuinely new persistence mechanic — it moves a
   companion record across `BeginNewCycle`; keep it a single explicit field,
   e.g. `CycleState.AnchoredCompanion` copied forward, NOT a general
   exemption system).
7. **Content:** building restoration quests, dossier entries (weakness hints
   are pre-written — wiring, not writing), companion arcs, echo pool.

Suggested build order: 1 → 3 → 2 → 4 → 7 (incremental) → 5 → 6. The Hall is
last on purpose: it's the only item that touches the persistence law, and it
should land after the wipe/archive loop is proven.

---

## 9. Open questions (downstream, not blocking)

- Unfinished Business volume control — cap the archive display per timeline,
  or full history? (Full history is thematically right; may need pagination.)
- Do echoes ever chain (an echo quest whose completion writes a record that
  seeds a deeper echo next cycle)? Powerful but risks unbounded content.
- Dossier Tier 3's "2+ different outcomes" — does Corrupted (a loss state)
  count as an outcome for tier credit? (Recommend yes: losing an archmage
  teaches you plenty.)
- Lunation-triggered beats (calendar quests) — how loud? A quiet "the sky is
  re-reading itself" flavor tick per lunation may be enough.
