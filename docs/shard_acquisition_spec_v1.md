# Fractured Arcana — Shard Acquisition Spec (v1, CANONICAL)

**Status:** Canonical as of 2026-07-21, user-ruled. Companion to
`docs/narrative_frame_intro_finale_v1.md` (the Long Second frame) and
`claude/shard_zone_refactor_plan_v1.md` (the built zone system, P1–P5 done).
This doc is the design of **P6 and beyond**: what shard acquisition *is*,
narratively and structurally.

**User rulings recorded (2026-07-21):**
1. **Provenance affects ONLY the Convergence** — never the shard's permanent
   effect. All six shards are equal in power; they differ in story.
2. **Shards CAN be lost to the Keepers** — seizure is real, and a recovery
   system (the Reliquary, §6) exists to win them back.
3. **Communion opens at high school mastery for any class** — rewarding
   multi-school play across the whole game is exactly the intended
   meta-progression.

---

## 1. The core principle: one zone, many states

Do NOT build multiple bespoke acquisition events per shard. Six shards × N
authored paths is a content explosion. Instead: **one zone per shard, recolored
by campaign state.** The player never picks an acquisition method from a menu —
the method *emerges from how they have played that kingdom* (archmage
disposition, corruption level, school, mastery). The path is earned upstream
over lunations of play, which is what makes it feel rewarding rather than
chosen at the door.

Every shard is a **three-body problem: you, the leak, and the archmage who
lives beside it.** (Zones are already sited 3–7 tiles from archmage seats —
user-confirmed 2026-07-18. The geography was always the integration point.)

---

## 2. The leak (fiction)

The fragments are shrapnel from the Sundering of Commencement. Each landed
like a hot coal and has been **leaking its school's magic into the world ever
since** — the zone is the wound. The entities inside are not stationed
defenders: **the fragment dreams its own guards, in its school's idiom.** The
leak *is* the conjuration. Nobody put them there, which is why no archmage can
order them aside — an Allied archmage can fight the dream beside you, or teach
you approaches, but only fluency in the fragment's own school lets you speak
to it (§4).

Long Second tie-in: closing each wound is testimony. The Convergence reads
back *how* you closed them (§7).

---

## 3. The acquisition matrix

One delve, six colorings. Availability is computed from campaign state at the
gate; multiple paths can be simultaneously available (player picks the delve
style at the gate encounter — but only from what their play has unlocked).

| Kingdom state | Path | How the zone plays | Provenance |
| --- | --- | --- | --- |
| Any (universal spine) | **Take it** | Baseline: lethal approach, guardian fight, sanctum claim. Always available. | *Taken* |
| Player school == fragment school, OR mastery Fluency (§4) | **Commune** | The existing bloodless bypass — you speak its tongue. Also reads as trespass the local archmage never detects. | *Communed* |
| Archmage **Allied** | **Sanctioned delve** | The archmage opens the gate rite and **joins the guardian fight as a unique unit** (`archmage_unique_units_v1_2`), or grants a second entrance past the outer approach. | *Granted* |
| Archmage **Coerced** | **Forced key** | They open the gate under duress. Cheapest delve — but the shard arrives **Corrupted** (condition, §6d) and forcing this raises the `coercedCanFlip` risk. | *Extorted* |
| Archmage **Overthrown** | **Inheritance** | The seat's wards died with them: gate stands open, but the leak destabilizes — guardian enraged, themed interior hazard active (the deferred "vault hazard" finally has its trigger). | *Conquered* |
| Kingdom **Corrupted** (level 3) | **Reclamation** | The Keepers occupy the zone and are harvesting the leak (§5). Fight through their siege, then a Keeper-augmented guardian. Shard arrives **Corrupted**. | *Reclaimed* |
| Shard **Seized** by the Keepers | **The Reliquary raid** (§6) | The shard is gone from the zone entirely. Assault the Keeper Reliquary to win it back. | *Reclaimed* + Corrupted |

Consequence hook for *Taken*: walking out of a kingdom with a piece of the
world's seal, unsanctioned, is noticed. Standing/disposition reaction from the
local court (small, but real — feeds the court layer).

---

## 4. Fluency (mastery communion)

**Rule:** Communion is available when the player's current school matches the
fragment's school, **or** when the player's permanent school mastery in the
fragment's school ≥ `FluencyThreshold` (tunable constant; mastery lives on the
`EternalLedger`, so this is cross-timeline, cross-class meta-progression).

**Fiction:** you learned to speak its tongue in a timeline you have since
lost. The fragment does not know your face; it knows your grammar.

**Why:** this is the intended meta-progression reward for playing multiple
schools across the whole game. A veteran who has mained three schools walks a
different world than a first-cycle player — three of six wounds will open to
words instead of blood.

**Druid note (7 schools, 6 fragments):** a Druid-main has no aligned fragment
— Fluency is their only route to any Communion, which converts a known dead
spot (fragment-arcs session caveat) into a build-around incentive rather than
a hole.

---

## 5. The blight clock (why you can't ignore a shard)

An unclaimed shard is not free. The leak **creeps**:

- Each corruption tick in a zone's kingdom grows a **blight radius** around
  the footprint — themed terrain conversion + themed roamers spilling out
  (living-map roamer system + the parked "blight creep" item; this is their
  job).
- At kingdom corruption 3 (archmage Corrupted), the Keepers arrive and **begin
  the Harvest**: a visible countdown (N lunations, tunable, surfaced on the
  strategic view — the zone beacon changes state). During the Harvest the
  Reclamation delve (§3) is available.
- If the Harvest completes: **Seizure.** The shard is removed from the zone;
  the zone goes dormant (drained scar — blight stops growing but the scar
  remains); the Keepers carry the shard to the Reliquary (§6).

Acquisition stops being errand-running and becomes **triage** — exactly the
pressure a press-your-luck campaign wants.

**Blight themes per school:** arcane geometry overgrowth (axiom) · temporal
stutter zones (moment) · charm-lure, "friendly" roamers with teeth (binding) ·
self-replicating machine sprawl (schema) · necrotic marsh (deathless) ·
standing elemental storm (primal).

---

## 6. Seizure and the Reliquary (the recovery system)

### 6a. Persistence ruling
Fragments are **trans-temporal objects** — that is *why* collection persists
across timeline resets (they are pieces of the pre-break reality; each new
reading re-renders where they lie, it does not remake them). Symmetrically:
**a Keeper Seizure also persists across cycles.** If you let a timeline unmake
with a shard in Keeper hands, the next reading renders it *still in Keeper
hands.* Collection and loss are the same coin. (Implementation: a
`fragment_<key>_seized` metaflag on the EternalLedger, cleared by recovery.)

### 6b. The Reliquary
The Astrologer cannot unmake a fragment (trans-temporal), so he **entombs**
it: a Keeper stronghold — the Reliquary — sited each cycle in Keeper-held
territory near The Convergence (reuses the warfront/stronghold machinery).
Recovering a seized shard is a bespoke assault: breach the stronghold
(warfront siege), then a vault fight against a **caged, farmed version of the
fragment's dream** — the guardian, in chains, being milked. The horror beat is
that you are freeing it as much as fighting it.

### 6c. Pressure while held
While the Keepers hold ≥1 shard, the Astrologer's clock runs hotter: +1
seasonal threat level per held shard (or shorten `CorruptionTickInterval` —
pick ONE knob; do not stack both). Urgency, made mechanical, without a fail
state.

### 6d. Condition: Corrupted shards
Orthogonal to provenance. A shard acquired under duress (*Extorted*,
*Reclaimed*, Reliquary recovery) carries the **Corrupted condition**
(convergence.docx already specs this): it counts fully toward the sixfold
seal, but imposes negative modifiers at the Convergence until **purified** —
via the existing purification paths (companion quests / campus ritual events,
multi-run cost). Purification clears the condition but never rewrites
provenance: the story of how you got it stands.

---

## 7. Provenance (the "rewarding" answer)

**Ruling: provenance affects ONLY the Convergence.** The permanent effect
frame is identical regardless of path — six equal shards, six different
stories.

Stamps: *Taken · Communed · Granted · Extorted · Conquered · Reclaimed*
(one per shard, permanent, `fragment_<key>_provenance` on the EternalLedger).

Convergence echoes (cheap: flags + finale dialogue/modifiers, systems already
spec'd in convergence.docx):

- **Granted** — that archmage gets their unique moment in the Gathering; their
  fragment activates cleanly in the Fracture.
- **Communed** — the fragment *volunteers* during seal assembly; its wave in
  the Fracture is diminished (it isn't fighting you).
- **Taken** — neutral baseline; standard wave.
- **Extorted** — the coerced archmage arrives resentful; elevated flip risk at
  the worst moment; the fragment resists its activation slot.
- **Conquered** — the overthrown archmage's absence is felt: their invocation
  splinter (§8) is available, but their seat's people are missing from the
  dais.
- **Reclaimed** — the fragment's dream remembers its cage; its wave fights
  strangely (wounded, erratic) and its activation needs shepherding.

The finale's mirror staging (narrative frame §6) reads all six stamps back as
testimony: who stands on the dais, which fragments volunteer, which resist.

---

## 8. Terminology unification: archmage "shard invocations"

The codebase has two shard concepts sharing one name:
`ArchmageDefinition.ShardInvocationDescription` (one-use final-battle effect
from Overthrown archmagi) vs the six Seal Fragment shards.

**Ruling: unify them.** Each archmage's seat-power was always drawn from a
**splinter of the fragment leaking in their kingdom** — which is why the seats
sit beside the zones, and why overthrowing an archmage hands you a one-use
chip of the same magic. Rename the archmage object **"splinter"** everywhere
player-facing (invocation = "invoking the splinter"); reserve
**"shard"/"fragment"** for the six seal pieces. The Astrologer's JSON already
fits: "no shard to invoke" — his fragment is the Moment Eternal, and it is
spent holding the Anchorhold open.

Court-layer variant (court_council_system §10, "shard-access-without-
alliance" compact) inherits the same reading: the compact grants splinter
privileges, not the fragment.

---

## 9. The six dreams (per-zone guardian fiction)

Each zone's guardian is the fragment dreaming in its school's idiom. One line
of fiction + one mechanical seed each (seeds are suggestions, not commitments):

- **The Infinite Athenaeum** (axiom, Arcanist) — *a theorem that noticed you.*
  **The Proof:** a guardian that changes one combat rule per phase; killing it
  is winning the argument.
- **The Shattered Clock** (moment, Chronomancer) — *the dream of every arrival.*
  **The Returned:** echoes of the player's own prior attempts (draw from loop
  history / honored dead data) — you fight who you were. The strongest
  personal beat in the set; it uses data no other game has about you.
- **The Charmed City** (binding, Enchanter) — *adoration with teeth.* **The
  Chorus:** the dream loves you and will not let you leave; charm/taunt
  mechanics, "citizens" who must be released, not slain.
- **The Eternal Engine** (schema, Tinker) — *industry without purpose.* **The
  Assembly:** adds a unit every turn until its foundry nodes are broken;
  the fight is against throughput, not a body.
- **The Hollow** (deathless, Necromancer) — *grief that kept its shape.* **The
  Congregation:** the fallen rise again each round until the tolling relic is
  silenced; killing is the wrong verb until the bell stops.
- **The Primal Heart** (primal, Elementalist) — *weather with intent.* **The
  Storm That Stays:** rotating elemental phases; the terrain itself is most of
  the boss.

Keeper-occupied (Reclamation/Reliquary) variants: the same dream, **caged** —
Keeper siege units around it, the guardian fighting in chains, erratic.
Post-Communion flavor: the dream stands down and *watches you leave.*

---

## 10. Build notes (composition, not construction)

Nearly everything composes from built or spec'd systems:

- Zones, gate/sanctum, sanctuary interior, guardian combat, aligned bypass —
  **built** (P1–P5 + guardian session).
- Disposition pipeline, corruption levels, court standing gates, archmage
  unique units, warfront/stronghold siege, roamers, fragment corruption +
  purification, Convergence modifiers — **built or spec'd**.
- **New:** path-availability computation at the gate encounter; blight-creep
  tick + Harvest countdown + Seizure; Reliquary siting + vault fight;
  `fragment_<key>_provenance` + `_seized` metaflags; Fluency threshold check;
  per-zone dream-guardian content (the real content work).

Suggested order: **P6a** gate encounter + path matrix (Take/Commune/Granted/
Forced/Inheritance from existing state) → **P6b** provenance stamps (write
flags now, spend them at the Convergence build) → **P7** blight creep +
Harvest + Seizure → **P8** Reliquary → **P9** bespoke dream guardians
(replacing the ×1.6 scaled archetypes).

---

## 11. Open questions (downstream, not blocking)

- `FluencyThreshold` value — needs the school-mastery scale in front of us;
  pick so a dedicated one-school game earns Fluency around the time that
  school's own fragment is done (i.e., it pays off on the *second* school).
- Harvest length (lunations) — long enough to answer with one prepared
  sortie, short enough to be a real threat. First guess: 3–4.
- Should a *Granted* delve consume an archmage favor / court obligation, or is
  Allied disposition alone the price? (Court layer would say: the rite is a
  Tier-C favor.)
- Reliquary re-raid on failure — retry same cycle, or does a failed raid
  harden it until next cycle?
- Does the drained scar (post-Seizure zone) offer anything — a lesser delve,
  lore, a staging point — or stay a pure wound on the map?
