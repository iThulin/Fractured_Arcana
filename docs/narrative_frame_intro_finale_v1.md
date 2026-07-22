# Fractured Arcana — Narrative Frame: Intro & Finale (v1, CANONICAL)

**Status:** Canonical as of 2026-07-21. This is the narrative counterpart to
`claude/progression_persistence_model_v1.md` — that doc owns the *mechanics* of
persistence; this doc owns the *fiction* that explains them, plus the two story
bookends: the intro sequence and the Convergence finale. Where earlier narrative
material conflicts (notably parts of `convergence.docx`), reconcile to this doc
per the checklist in §9.

All proper nouns marked ⟨placeholder⟩ are suggestions — swap freely; everything
else is structural and should survive renaming.

---

## 1. Canon rulings (the load-bearing decisions)

**R1 — The anchor was authored by the alternate-reading Chronomancer, NOT a past
version of the Astrologer.** Per `Data/Archmagi/Chronomancer.json`, the Astrologer
(Kassian Vor-Aleth) chose the break after reading the future — he never "fell,"
so a past-self contingency would be something he remembers and would have
dismantled. Instead: the good Chronomancer is the established "version of him who
read the same passage and drew a different conclusion." That divergent self wrote
the anchor contingency into the seat of the university. Because it originates in
a choice the Astrologer did not make, **the anchor is illegible to him** — it is
his own handwriting from a branch he cannot parse.

**R2 — The anchor's illegibility IS the player's plot armor.** The Astrologer's
canonical weakness hint — "you came from outside his timeline; you are the
variable he could not account for" — now has a mechanism. Everyone and everything
inside the anchor's bubble is outside his script. The player is unreadable
*because* they live inside the Long Second. This line of the JSON needs no edit;
it is retroactively load-bearing.

**R3 — The anchor is powered by the Moment Eternal.** The Chronomancer seal
fragment ("a single second stretched into infinity") is what the good
Chronomancer spent to freeze the graduation instant. This merges two redundant
frozen-second set pieces into one object, explains why that fragment is missing
from the world, and gives the Shattered Clock recovery arc a personal stake:
recovering the Moment Eternal reinforces the anchor you live inside.

**R4 — The graduation assault IS the seal-breaking event.** The magisphere and
the Sixfold Seal fragments were housed at the seat of the university. The
Astrologer's assault, enabled by the co-conspirator archmage opening the wards
from inside, shattered the magisphere and scattered the fragments across the
world. Fragment recovery is literally undoing the intrusion.

**R5 — The bubble is the fiction of the permanent layer.** Campus = the
university seat frozen inside the anchor = outside time. Everything inside the
bubble persists (buildings, blueprints, mastery, shards, lore, honored dead —
the `EternalLedger`). Everything beyond its edge is a timeline the Astrologer's
reading can reassert — the `CycleState`. "Let It Unmake" is the anchor snapping
the player back, exactly as the common card `Temporal Anchor` does at tile
scale. The progression doc's theme — *"perfecting the timelines you have lost"*
— is now diegetic, not decorative.

---

## 2. Names ⟨all placeholders⟩

| Thing | Proposed name | Why |
| --- | --- | --- |
| The graduation ceremony | **Commencement** | Double meaning: the ceremony where the game commences, and the ceremony the finale finally completes. |
| The frozen instant | **the Long Second** | Plain, ominous, easy to say in dialogue ("the campus stands inside the Long Second"). |
| The bubble / campus domain | **the Anchorhold** | Reads as both fortress and temporal term. |
| The good Chronomancer | **the Erratum** | The Astrologer reads the sky as fixed text; his divergent self is the correction slip in the margin. Alternates: the Second Reading, the Marginal. |
| The assault | **the Sundering of Commencement** | Formal name used in lore entries. |

---

## 3. Intro sequence — beat by beat

Five beats, one scripted flow, all on existing systems (HexGrid/GameRunner +
narrative panels). `CampaignState.CoConspirator` already documents that its
reveal belongs to "the intro scripted encounter" — **this is that encounter.**

### Beat 1 — The Trial (diegetic tutorial)
Your final examination is a refereed duel in the ceremony hall. An examiner
explains the rules because explaining rules to candidates *is the fiction*.
School choice = your graduating discipline. Small grid, 2–3 turns of basics
(draw, mana, movement, one reaction). Win state is scripted-achievable.

> **EXAMINER:** "The trial is not to defeat me. The trial is to show me you
> know *why* each card leaves your hand. Begin when the bell stills."

### Beat 2 — Commencement (short — resist the pomp)
Two or three lines of ritual, the archmagi assembled on the dais, the
magisphere visible above/behind them. The co-conspirator is **present and
honored** — seated closest to the sphere. Keep this under a minute; the
interrupted-ceremony opening is a known trope and lingering in it is the
mistake, not using it.

> **PROVOST:** "By the Six Schools and the sphere that binds them, we confer
> upon you the name you have earned. Step forward and be written."

*(The conferral of a name matters — see Beat 4.)*

### Beat 3 — The Sundering (scripted collapse, not fake difficulty)
The wards shatter **from inside** — that is the betrayal reveal. A 2–3 turn
combat the player is not meant to win and can visibly tell is not winnable:
faculty fall in ways that incidentally teach reactions and repositioning;
enemy count escalates past absurdity; the co-conspirator walks *through* the
broken wards toward the magisphere. Objective text is honest: "Survive."

Because the co-conspirator is procedurally assigned (highest-tier region
adjacent to The Convergence), the betrayal line must come from data, not
script. **Implementation hook:** add an `introBetrayalLine` field to each
archmage JSON so every possible traitor betrays in their own voice. Generic
fallback:

> **CO-CONSPIRATOR:** "Don't look at me like that. He showed me the ending.
> I am only choosing where to stand when it arrives."

*(Deliberately echoes the Astrologer's own line — "I am not changing what
happens. I am changing where you are standing when it does." The corruption
spreads as borrowed language.)*

### Beat 4 — The Long Second (the freeze)
Mid-deathblow, the second stops. The killing spell hangs half-cast; the
co-conspirator is frozen mid-stride, hand on the sphere; dust motes fixed in
the light. Only the player moves. The Erratum — or his prepared echo; he need
not survive as a character — speaks. The anchor bound to the player because
the anchor needed a fixed point, and at that instant the player was the one
*being named*: mid-conferral, half-written, the only unfinished thing in the
room.

> **THE ERRATUM:** "He read the sky and chose this. I read the same passage
> and could not. So I spent the one second I owned — stretched it wide enough
> to live in — and hid it where he cannot read: inside a choice he never made.
> You were being named when it closed. The writing never finished. You are the
> only sentence in his script that does not end. I am sorry. That is the whole
> of your armor, and it will have to be enough."

### Beat 5 — Waking into the Anchorhold (campus tutorial)
The player walks out of the frozen hall into the university grounds — ruined
where the assault reached, frozen where it hadn't yet. This is the campus.
Rebuilding the campus is diegetic: restoring the seat of magic inside a
stretched second, the one staging ground the enemy cannot read. First quest
beats point outward: the fragments were scattered in the Sundering; the world
beyond the bubble's edge is where the Astrologer's script still runs.

**Scope note:** frozen bystanders are set dressing only in v1 — statues of the
moment. Do not promise interactivity with them yet; it's a content trap.
(A later hook: individual frozen figures as unlockable campus NPCs.)

---

## 4. Loop fiction — mechanic → story mapping

| Mechanic (exists) | Fiction |
| --- | --- |
| Starting a run / expedition | A **sortie** out of the Anchorhold into the running timeline. Crossing the bubble's edge, you become briefly legible — the world can now hurt you. |
| Run death / defeat | The anchor **reels you back** to the Long Second (the `Temporal Anchor` card's snap-back, at world scale). The timeline unravels behind you. |
| "Let It Unmake" (Bank/Reset) | You *choose* to release the timeline before it collapses on its own. The anchor snaps back; the Astrologer's reading closes over the world like water. |
| Fresh gen-1 world | Not "the same world reset" — a **new reading**. The sky is rewritten each time the script re-converges; geography of fortune reshuffles (archmage placement, corruption seeds). |
| Permanent layer survives | It was never in the timeline. It's inside the Long Second. |
| Grand Conjunction deadline | The anchor can hold a given timeline open only so long before the script **re-converges** on it. The Conjunction is the moment the sky finishes re-reading itself. |
| Corruption tide / spread | The Astrologer's reading closing back over the map — his certainty, made weather. |
| "Perfect the Timeline" (Continue) | Holding a timeline open *past* its Conjunction. The strain shows: escalation is the script actively hunting the held timeline ("the world hardens" HUD line already says this). |
| Shard recovery (permanent) | Each recovered fragment is carried inside the bubble, where he cannot read it — his plan erodes in his one blind spot. |
| Moment Eternal recovery (Shattered Clock arc) | Reinforcing the anchor itself. Flavor payoff: after recovery, the Long Second is visibly *stronger* (campus vfx hook). |

---

## 5. Why the Astrologer doesn't just destroy the bubble

Answer once, in lore, and stop worrying about it: **he cannot find it.** The
Anchorhold is not hidden in space; it is hidden in *text* — written in a branch
of his own hand he cannot parse. His forces occupy the university's location in
the running timeline (a fine mid/late-game region: the fallen seat, occupied),
but the Long Second is not *at* that location; it is *beside* it. His entire
campaign of corruption is, in part, a search — forcing timelines toward
configurations where the anchor must reveal itself. It finally does, at the
Convergence: to fight him you must open your own door.

This also cleanly explains why the finale is a defense: the one time the bubble
is open is the one time he can enter it.

---

## 6. The finale — return to the anchor

The framing (Convergence = returning to the anchor moment to fend off the
corrupting forces with gathered allies) maps onto the existing five-phase
Convergence structure with one recast phase. The three-path structure
(Restoration / Dominion / Synthesis) is untouched.

| Phase (convergence.docx) | Recast |
| --- | --- |
| 1 — The Gathering | Unchanged. Allies assemble in the Anchorhold. Council vote, loadout, morale. |
| 2 — The Fracture | **Recast: the anchor releases.** The Long Second resumes. The campus-defense encounter IS the ceremony-hall assault playing out again in the same space — the Sundering, take two. Campus buildings contribute defenses exactly as spec'd. |
| 3 — The Threshold | Per path, as spec'd. The breach opens *from* the resumed moment — the wound in the magisphere is right there, still fresh, because inside the Long Second it is still the instant it happened. |
| 4 — The Reckoning | Per path, as spec'd — with the Astrologer confrontation staged inside the Second (see below). |
| 5 — The Aftermath | Per path — plus the Conferral Completes (see below). |

### Mirror staging (the craft payoff)
Same hall, same blocking, roles swapped. Where the archmagi stood at
Commencement, the player's companions and allied archmagi now stand — **archmage
dispositions literally determine who is on the dais.** The player stands where
the Provost stood. The co-conspirator's tile is either occupied by them
(redeemed — if their region/disposition was resolved) or stands empty until
they arrive with the enemy and betray the moment a second time — and this time
the player sees it coming, which is the difference eleven-odd timelines make.

### The Astrologer, in the Second
He is forced to fight at the precise coordinates of his blindness — inside the
one second he cannot read. His defeat is thematically exact: certainty walking
into the only unwritten room in the world. Voice reference for his finale
lines is already strong in `Chronomancer.json`; extend, don't replace.

> **THE ASTROLOGER (entering the hall):** "So this is where the sentence
> hides. Do you know, I have read every sky but this ceiling. Let us finish
> the passage together, and see which of us was the author."

### The Conferral Completes (the ending beat, all paths)
The entire game is one interrupted graduation; the ending is the diploma.
Whatever the path and whatever it cost, the Aftermath's final scene is the
ceremony finishing — the player's name conferred at last, by whoever survived
to speak it. Tone shifts by path:

- **Restoration:** the surviving archmagi (and the seal-anchor companion's
  empty chair, honored) complete the rite formally. The name is spoken over
  a rebuilt sphere. Elegiac.
- **Dominion:** no provost remains with the standing to confer anything —
  so the player's own guild does it, new rite, new words. The name is spoken
  over the open breach. Triumphant and unsettling.
- **Synthesis:** the ceremony is completed jointly — allies on one side and,
  in some form, the Void intelligence bearing witness on the other. The name
  is spoken into a sky that now reads *back*. Bittersweet.

Credits roll over the guild-history montage, as spec'd.

---

## 7. What this costs to build

- **Intro:** one scripted encounter (Trial) + one scripted collapse (Sundering)
  on existing combat systems, narrative panels between, plus the campus wake-up.
  `CoConspirator` is already wired and waiting for exactly this scene.
- **Data:** `introBetrayalLine` (and optionally `finaleRedemptionLine` /
  `finaleSecondBetrayalLine`) fields per archmage JSON.
- **Finale:** almost entirely writing — the Fracture recast is a re-skin of the
  already-spec'd campus-defense phase into the ceremony hall; mirror staging is
  layout + dialogue, not new systems.
- **Reconciliation pass:** §9 below.

---

## 8. Writing-tone crib (for future copy in this frame)

- The Astrologer never threatens; he *schedules*. Short declaratives, no
  exclamation points, mild regret. He corrects terminology.
- The Erratum speaks in cost: everything he says accounts for what something
  spent. He apologizes; the Astrologer never does.
- Corruption shows in NPCs as **borrowed language** — corrupted characters
  start quoting the Astrologer's phrasings ("where to stand") before any
  visual corruption shows. Cheap, creepy, systemic.
- Never use "time travel." In-world vocabulary: *reading, script, passage,
  sentence, written/unwritten, re-converge, unmake.* Time is a text, not a road.
- The Long Second is described with stillness imagery (hanging dust, a held
  bell, unfinished ink) — never with clock imagery. Clocks belong to the
  Astrologer; stillness belongs to the player. Keep the two visual vocabularies
  segregated so ownership reads at a glance.

---

## 9. Reconciliation checklist

1. **`convergence.docx`** — Phase 2 ("The Fracture"): recast copy from "the
   seal breaks and the first Void incursion hits the campus" to "the anchor
   releases; the Long Second resumes; the Sundering plays out again in the
   hall." Mechanics unchanged.
2. **`convergence.docx`** — Moment Eternal / Shattered Clock entry: add that
   the fragment currently powers the anchor's origin; recovery reinforces the
   Anchorhold (R3). The arc's time-period-hopping structure is untouched.
3. **`Scripts/Systems/Campaign/CampaignState.cs`** — `CoConspirator` comment:
   point at this doc for the intro encounter's content.
4. **`Data/Archmagi/Chronomancer.json`** — no edits required (R1/R2 conform to
   it by design). Optionally add a weakness hint referencing the Long Second.
5. **Archmage JSONs** — add `introBetrayalLine` per archmage (any of them can
   be the co-conspirator).
6. **`progression_persistence_model_v1.md` §1** — no edit needed; this doc
   supplies the fiction that section was missing. Cross-reference added here
   instead.

---

## 10. Open questions (downstream, not blocking)

- Does the player character have a fixed identity/name, or is "the unfinished
  name" kept literal (the player names themselves at the finale's conferral)?
  The latter is a strong New Game+ hook but constrains dialogue writing.
- Frozen-bystander interactivity (Beat 5 scope note) — later content tranche?
- Should the Erratum appear again mid-game (Shattered Clock arc is the natural
  place — the Chronomancer-aligned trial already lets you "speak with the
  chronomancer who forged this fragment"), or stay a voice from the intro only?
- The occupied fallen-seat region (§5) — worth adding to the world gen as a
  fixed high-tier region, or fold into The Convergence region itself?
