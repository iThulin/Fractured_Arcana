# Fractured Arcana: Negotiation Narrative & Legibility Redesign (v1, IMPLEMENTED)

**Status:** Approved and implemented 2026-08-31 (same day). All four open
questions ruled by the user; §10 records the rulings, the implementation
deviations, and the one finding that needs playtest attention.
Companion to `negotiation_system.docx` (original design),
`negotiation_tuning_v1.md` (whose targets stay binding),
`quest_system_narrative_spec_v1.md` (the two-layer law this spec maps deals
onto), and `narrative_frame_intro_finale_v1.md` (the Long Second frame).

**The brief (user, 2026-08-31):** the table is hard to read in play; bark
language is stilted, especially where clause names are substituted; hidden
terms bind against new players who never saw them coming; the closing
squeeze feels tacked on; and the system should carry more of the campaign
arc's narrative. The concept is liked. The application is the problem.

---

## 1. Diagnosis: four faults, with receipts

**D1. The language breaks by construction.** Terms have no display name.
`NegotiationState.ShortName()` takes the first four words of the term's
description, so `amber_downs_commander`'s escort clause renders as
"A levy escort to…" and the Commander pull bark becomes *"The A levy escort
to… is not negotiable at that figure."* Every bark with a `{term}` slot is
grammatically broken for every term whose description does not begin with a
noun phrase, which is most of them. No amount of bark rewriting fixes this;
the substitution contract is wrong.

**D2. Hidden terms are a silent tax.** `GetGoldOutcome()` is explicit:
hidden clauses never flipped "bind at their resting position, the price of
not reading the small print." The board does show face-down cards, and the
tooltip does say "Unread clauses still bind," but nothing prices the risk
(the payout preview quietly includes them), nothing foreshadows their
content, and the receipt is the first place a new player learns they signed
something they never read. The design intent (Insight must matter) is
correct. The delivery reads as the game cheating.

**D3. The squeeze has no setup.** `BeginShake()` fires a one-time modal
with displayed odds the first time the player offers the handshake. It is
mechanically sound (the odds are honest, the knobs are tuned) but nothing
during the table foreshadows that closing is its own beat, the demand comes
from nowhere, and the concede/hold/withdraw choice reads as a bolted-on
gamble rather than the last move of a negotiation.

**D4. The table is narratively sealed off.** Every encounter is a one-off
against a role ("The Levy Captain"). Meanwhile the campaign has exactly the
machinery this system should be feeding on: courts generate named courtiers
WITH negotiation archetypes (`CourtGenerator`), deal outcomes already echo
into courts as deeds (`CouncilEcho.DealFair/DealExploit`), and every
resolution is already written to the **EternalLedger** as a `DealRecord`
whose own doc comment says "timelines die; records don't." The chronicle
remembers every table across every unmake. The negotiation scene never
reads any of it back.

---

## 2. Goals and non-goals

Goals, in priority order:

1. Every line spoken at the table parses as English (D1).
2. A first-time player can predict what an action will do, why the deal
   paid what it paid, and where a hidden cost came from (D2).
3. Closing is a beat the table builds toward, not a modal (D3).
4. The table becomes the place the two-layer law is *felt*: this-timeline
   consequences walk back in the door, and cross-timeline memory is the
   player's private, aching advantage (D4).

Non-goals:

- **No economy retune.** `negotiation_tuning_v1.md` targets stay binding.
  Any mechanical change in this spec (N3's squeeze gating is the only real
  one) gets re-simulated before shipping.
- **No new core mechanics.** Stances, tokens, tension, the term board, and
  the priority ladder all stay. This is a legibility and narrative layer.
- **No tutorial screens.** Onboarding happens in the fiction (§4e), per the
  intro-sequence philosophy ("explaining rules to candidates is the
  fiction").

---

## 3. N1: The language layer

### 3a. Authored short names

`DealTerm` gains an authored field:

```json
{ "id": "column_escort",
  "shortName": "escort",
  "description": "A levy escort to the far boundary. Nothing on the road will trouble you." }
```

**The grammar contract.** A `shortName` is a lowercase noun phrase, one to
three words, no article, no trailing punctuation, that reads correctly in
all three of these frame sentences (the validator's test set):

1. "They pull the {term} back toward their side."
2. "The {term} is not negotiable at that figure."
3. "Their eyes keep returning to the {term}."

Suggested short names for the shipped tables: escort, muster charts,
requisition, exclusivity clause, tuition clause, safe passage, and so on.
Migration touches all 24 files in `Data/Negotiations/` plus any term
injected at runtime (tuition terms get "tuition").

`ShortName()` survives only as a fallback for unauthored data, and
`tools/verify_negotiations.py` gains a check that flags every term without
an authored `shortName` and every authored one that violates the contract
(uppercase first letter, leading "a/the", length over three words,
terminal punctuation).

### 3b. Bark audit rules

With the substitution fixed, one pass over `NegotiationBarks.cs` under
these rules:

- **Dialogue tier speaks; Detail tier counts.** No mechanical vocabulary
  (token names, "+1", pool names) inside quoted speech. Mechanics live in
  the unquoted trailing clause or in a Detail-tier line. Current offenders:
  the gift barks quote a line and then say "+1 {gift}" in the same entry;
  split them.
- **The NPC's resources keep their archetype voice.** `ResolveDisplayName`
  already renders Resolve per archetype. Extend the same treatment to Guile
  and Poise (`GuileDisplayName`: "fine print" for the Merchant,
  "conditions" for the Commander, "footnotes" for the Scholar, and so on)
  and use those words in the empty-pool announcements, so "They're out of
  fine print" stops being a lucky coincidence of one archetype and becomes
  the system.
- **One voice per speaker.** The wizard's spoken lines (Module D) are
  first-person confident; scene narration is second-person present; the
  Detail tier is telegraphic. Lines that mix registers get rewritten.

### 3c. Player-facing vocabulary

The three log tiers get player-facing names in the settings toggle and
filter chips: **Spoken** (Dialogue), **Scene**, and **Table details**
(Detail, off by default, exactly as today). No behavior change; naming
only.

---

## 4. N2: The hidden-term contract

The principle stands: unread clauses still bind. What changes is that the
game now *prices* the risk, *foreshadows* the content, and *explains* the
outcome. Ruled by the user 2026-08-31: telegraph harder, do not stop
binding.

### 4a. Rumors on the card back

Every hidden term gains a required authored field, `rumorText`: one line of
in-fiction innuendo shown on the face-down card in place of today's generic
"A face-down clause." For `she_expects_a_war`:

> *"The escort is generous. Suspiciously generous, for a woman who counts
> her levies twice a day."*

The rumor hints at stakes without revealing mechanics. The validator
requires `rumorText` on every `isHidden` term and forbids it on visible
ones. The tooltip line "Unread clauses still bind" stays, appended under
the rumor.

### 4b. Priced risk in the projection

The live "a handshake signs for…" preview gains an unread-risk chip
whenever face-down clauses remain: **"and N clauses unread"**, styled in
the warning color, with a tooltip: "Face-down clauses bind at their
current position when you sign. Insight turns them over first."
`ProjectGold()` itself is untouched; the number was always honest, it was
the silence around it that lied.

### 4c. The NPC tips their hand

Once per table, when a hidden term exists and the NPC takes a Hold or
Guile move, they bark an archetype-flavored hint instead of the generic
line (Merchant: *"Read it all before you sign. I always say that. Almost
always."*). One bark, once, keyed to the FIRST such move, so it is a tell
for attentive players rather than an alarm.

### 4d. The receipt explains itself

Receipt rows for terms that were still hidden at signing (already marked
with the card-back glyph) gain a note column entry: "signed unread". The
first time a player signs with unread clauses (EternalLedger metaflag),
one footer line appears under the receipt: *"Clauses you never turned
over bind as written. Insight reads them; reading them lets you fight
them."* Once, ever, across all timelines; the chronicle does not nag.

### 4e. First-table onboarding, in fiction

On the player's first table ever (`negotiation_resolved` deed count of 0),
three extra Scene lines fire at the natural moments: at open ("The meter
by their portrait is the temper of the room; read it before you spend
anything"), on the first stance change ("Their mood turns between
exchanges. The same argument lands differently on a different mood"), and
the first time the handshake button lights ("You may offer the handshake
whenever the projection suits you. Expect one last demand before ink").
Framed as guild training surfacing, not UI copy. Never again after the
first table, in any timeline.

---

## 5. N3: The Handshake (the squeeze, earned)

The mechanic survives; its presentation and its trigger change. Ruled by
the user 2026-08-31: rework as a narrative beat.

### 5a. Naming

Player-facing, the whole closing flow is **the Handshake**. "Squeeze"
remains internal vocabulary (code, tuning knobs, telemetry).

### 5b. The squeeze is fueled by what they have left

New rule: the NPC only squeezes if they still hold Resolve or Guile.
Pool-empty counterparts sign as written. This single change does three
things: it makes the existing empty-pool announcements ("Their {Resolve}
is spent…") double as closing intelligence, it turns pool attrition into
closing strategy (grind them down first, or close early and face the
demand), and it makes the squeeze feel like the last act of the person you
have been playing against instead of a scripted toll booth.

**Tuning guardrail:** this makes squeezes avoidable and therefore rarer.
Re-run `negotiation_sim.js` with the gate in place and confirm table
length, star distribution, and squeeze-encounter rate stay inside
`negotiation_tuning_v1.md` §1 targets; if squeeze rates crater, the
compensating knob is squeezing whenever EITHER pool is nonzero (the
proposed rule) versus only when Resolve is (stricter), not new constants.

### 5c. Foreshadowing the demand

The squeeze target is already deterministic: the visible clause the player
has won furthest, the concession they most want back. Two seeds during play: from mid-table on, whenever a handshake
would draw a squeeze, the would-be target's card shows a subtle worried
corner mark (same visual family as the threat markers, which already
foretell NPC moves honestly); and one bark seed, once per table, when that
target first reaches position +1 or better (*"Their eyes keep returning to
the {term}."*). The tell-never-lies principle from `PredictNpcAction`
extends to closing.

### 5d. Odds as a read, not a percentage

The modal keeps the honest number but leads with the fiction. The
Dialogue-tier line renders the odds band as a read of the person: 60%+
"Their grip is firm but their eyes aren't"; 40-59% "You genuinely cannot
tell if they mean it"; below 40% "Every line of them says they will walk."
The exact percentage moves to the Detail tier of the modal, visible but
subordinate. Same number, two registers, consistent with §3b.

### 5e. Copy pass on the three choices

Concede / hold firm / withdraw get scene-register labels and the existing
`ProjectIfConceded` arithmetic stays attached to the concede column:
"Let them have it (signs at: …)", "Hold firm (they blink, or the table
heats)", "Withdraw your hand (back to the table)".

---

## 6. N4/N5: The table remembers

The organizing principle, from `quest_system_narrative_spec_v1.md`: quests
are knowledge, and knowledge is permanent. Deals already obey the law's
storage half (`DealRecord` lives on the EternalLedger with a
`CycleNumber`). This section makes the table READ what it has been writing
all along. The negotiation scene is the one place the player sits face to
face with the world's people; it should be the place where "people reset;
what you know about them does not" stops being a menu screen fact and
becomes a scene.

### 6a. Named counterparts: regional notables

Encounter JSONs become **roles**, not people. A new cycle-layer registry,
`NotableRegistry` (CycleState), is generated at cycle init alongside the
courts, reusing `CourtGenerator`'s name vocabulary: for each region and
each negotiation archetype that region's tables can roll, one named person
holding the role (the Amber Downs levy captain, the Cogwork Reach factor,
and so on).

- On `Load()`, the per-table clone substitutes the notable's name for
  `npcName` and a `{npc}` slot in `openingText`. Bespoke tables that pin a
  canonical character (`npcNamePinned: true`) opt out.
- **Court linkage:** when the table sits in kingdom territory and that
  kingdom's court holds a courtier of the matching archetype, the notable
  IS that courtier. Their Regard then joins faction reputation in the
  starting-tension lookup, and a signed deal at 4★+ moves their Regard
  directly at the table (in addition to the existing deal-deed echo, which
  stays the route for everyone else at court).

### 6b. Same-cycle continuity: consequences walk back in

On table open, query this cycle's `DealRecords` for the same notable. If
the player has sat with this person before in this life, the opening
changes, capped at one line and one small tension adjustment so the effect
is legible:

| Last outcome with them | Opening | Tension |
| --- | --- | --- |
| Signed at 4★+ | Warm callback ("The escort held, as promised. Sit.") | −1 |
| Signed at 1–2★ | Cool callback ("You drive a hard bargain. I remembered.") | 0 |
| WalkedAway | Guarded ("Leaving is your habit, I recall.") | +1 |
| Collapsed | Hostile opening bark; they open on a Guile move | +2 |

Continuity lines are authored per archetype (six lines per row, generic
fallback), not per encounter, so the cost is one bark table. Court-level
ripples stay `CouncilEcho`'s job; this section is only what happens at the
table itself.

### 6c. Cross-timeline familiarity: the chronicle's advantage

After an unmake, the notable is regenerated: same role, new person, or the
same name with no memory (see open question Q2). The player's chronicle,
however, kept every record. When prior-cycle `DealRecords` exist for this
role:

- **Presentation:** a chronicle glyph on the portrait, and one Scene line
  at open: *"You have sat at this table before, in a life she does not
  remember."* The Hall of Records panel gains the counterpart's name and
  role on each row so the ledger reads as people, not filenames.
- **Mechanics, by familiarity tier** (count of prior-cycle tables against
  this role), each reusing an effect that already exists as a
  `ApplyCourierDossier` hook, so implementation is wiring, not systems:
  - 1+ prior table: you remember how they open (starting stance shown).
  - 3+ prior tables: you remember their small print (one hidden term
    pre-flagged: its rumor is replaced by its true description, still
    face-down until flipped).
  - Any prior Collapse: you remember exactly how this goes wrong (their
    walkaway line is shown in the dossier panel before the table starts).
- **Stacking rule:** familiarity and Courier Dossier effects do not stack;
  the better of the two applies per effect. Knowledge is free (it is the
  game's whole thesis); buildings buy it for roles you have never met.
- **Archmage dossier linkage** (quest spec ruling 2: dossiers grant
  mechanical unlocks): a completed dossier on the archmage whose kingdom
  the table sits in adds one extra dialogue seam: a once-per-table free
  Persuade-grade line referencing what you know of their master, rendered
  as its own spoken move.

### 6d. Arc stakes: the war reaches the table

Encounter JSONs gain an optional `openingTextLate`: used when
`CampaignEscalation` has passed its midpoint, so a levy captain who was
counting men in the early game is burying them in the late game. Commander
and Survivor tables are the priority; a table that ignores a world on fire
is the flatness the user is naming. Same pattern is available to
`dialogueWalkaway` (`dialogueWalkawayLate`) where it is cheap.

The Confrontation-tier tables already planned in
`content_buildout_plan_v1.md` §4 are this spec's set pieces: negotiation
as narrative delivery for the campaign spine (an archmage's envoy, a
compelled parley with a court that knows what you did). The compendium's
2.8 envoy hook (Kassian's offer, whose acceptance the game itself refuses)
should be authored as one of them.

---

## 7. Schema and code touch list

**JSON (Data/Negotiations/, 24 files + schema):**

| Field | On | New? | Required |
| --- | --- | --- | --- |
| `shortName` | term | yes | yes (validator) |
| `rumorText` | term | yes | iff `isHidden` |
| `npcNamePinned` | encounter | yes | no (default false) |
| `{npc}` slot support | `openingText` | yes | no |
| `openingTextLate`, `dialogueWalkawayLate` | encounter | yes | no |

**C# (new):** `NotableRegistry` (CycleState + generator, reuses
CourtGenerator vocab); familiarity query over `EternalLedger.DealRecords`;
continuity bark table.

**C# (modified):** `DealTerm` (+2 fields); `NegotiationState`
(`ShortName()` demoted to fallback; squeeze gate in `BeginShake()`;
`GuileDisplayName`); `NegotiationBarks` (audit pass, rumor/foreshadow/
continuity tables, odds-band reads); `NegotiationManager` (unread-risk
chip, corner mark, receipt notes, chronicle glyph, handshake modal copy,
records panel columns); `NegotiationEncounterLoader` (name substitution
at clone time); `NegotiationContext` (notable id in, Regard delta out);
`tools/verify_negotiations.py` (contract checks from §3a, §4a).

**Untouched:** tension model, token economy, stance system, priority
ladder, star thresholds, all of `NegotiationTuning.cs` except any
follow-up the §5b re-simulation demands.

---

## 8. Phasing and acceptance

Each phase ships independently and leaves the game better; N1 and N2 are
pure wins with no tuning risk.

| Phase | Contents | Acceptance |
| --- | --- | --- |
| **N1 Language** | §3: `shortName` + migration, bark audit, tier names | Every `{term}` bark parses in the three frame sentences for all 24 tables; validator green; zero behavior change (telemetry identical) |
| **N2 Legibility** | §4: rumors, risk chip, tip-the-hand bark, receipt notes, first-table lines | A tester who has never seen the system signs a deal with a hidden term and can say afterward where the cost came from |
| **N3 Handshake** | §5: gating, foreshadowing, odds reads, copy | Sim re-run inside tuning targets; squeeze-encounter rate recorded before/after |
| **N4 Continuity** | §6a-b: notables, court linkage, same-cycle callbacks | Re-meeting a counterpart after a walkaway visibly opens colder; Regard moves on 4★ signings |
| **N5 Chronicle** | §6c-d: familiarity tiers, glyph, dossier seam, late-game openings | After an unmake, a repeat table shows the chronicle line and the tier-1 effect; Hall of Records shows names |

---

## 9. Open questions (user rulings requested)

**Q1. Notable regeneration on unmake.** Same name reborn without memory
(stronger tragedy, matches the companion-loss register) or a new person in
the role (cleaner fiction for why familiarity is only partial)? Spec
assumes SAME NAME, no memory, because the chronicle line lands harder when
the face matches the record.

**Q2. Squeeze gate strictness.** §5b proposes squeezing while Resolve OR
Guile remains. If simulation shows squeezes become too rare, is the
stricter Resolve-only gate acceptable, or should pool-empty NPCs keep a
low flat squeeze chance (weakest option; it reintroduces the toll booth)?

**Q3. Familiarity cap.** Should cross-timeline familiarity max out (say,
tier 2) so bespoke canonical characters keep surprises across many cycles,
or run unbounded on the theory that mastery-through-repetition IS the
game's loop?

**Q4. Regard at the table.** §6a lets a 4★+ signing move a courtier's
Regard immediately, on top of the echo system's later ripple. If that
double-counts, the alternative is table-immediate Regard REPLACING the
deal-deed echo for courtier tables only.

---

## 10. Implementation record (2026-08-31)

**Rulings (user):** Q1 same name, reborn without memory. Q2 the stricter
Resolve-only squeeze gate. Q3 familiarity unbounded (mastery through
repetition). Q4 table-immediate Regard replaces the deal-deed echo for
courtier tables.

**Deviations from the draft, and why:**

- **The NotableRegistry was not built.** The authored `npcName` already
  gives every table a stable, persistent counterpart: the same JSON loads
  every cycle, so the same person is reborn without memory, which is Q1's
  ruling exactly and for free. The bespoke tables are also deeply
  name-bound (openings, walkaway lines, term descriptions reference their
  characters), so generated names would have broken the authored voice.
  Continuity and familiarity key off `DealRecord.EncounterId`, which the
  ledger already stores. No new save surface.
- **Court linkage flows Regard, not identity.** The origin court's
  courtier of the counterpart's archetype is treated as the counterpart's
  voice at court rather than the counterpart themselves (courts generate
  random names per cycle; renaming courtiers from negotiation data would
  have inverted CourtGenerator's ownership). A signed deal moves that
  courtier's Regard at the table (+1/−1 with the rep sign, +1 more at
  4★+, clamped ±3), the log attributes it, and per Q4 the deal-deed echo
  is skipped via `NegotiationContext.RegardSettledAtTable`.
- **The dossier seam is +1 Persuade,** granted at table-open when the
  origin kingdom's archmage dossier is complete (every weakness hint
  revealed), rather than a bespoke spoken-move row. Same unlock, a tenth
  of the UI.
- **First-table guidance** fires on `DeedCounts` lacking
  `negotiation_resolved` (first table ever, any timeline), with the
  handshake line delivered after the first exchange rather than "when the
  button lights" (the button is always lit).

**Sim finding to watch (tools/negotiation_squeeze_sim.py):** with any
pool-based gate, squeeze-at-close drops from ~98% to ~11-12% (Resolve-only
and Resolve-or-Guile land within a point of each other), because NPC pools
are usually spent by the time bots close. The beat therefore now fires
mainly on EARLY closes against a counterpart with fight left, which is the
design's intent reading, and real players close earlier than the bots do.
But if playtests show the handshake drama has effectively vanished, the
ready fallback is a reserve model: the NPC banks their last Resolve (pulls
only while Resolve ≥ 2 with the squeeze unspent) and the squeeze spends
it. That restores a common squeeze while keeping it something the player
can reason about, at the cost of the pure attrition counter. Other tuning
targets (table length, star distribution, collapse rate) moved less than a
point under the gate.

**Addendum (2026-08-31, later same day): the verb band.** The user flagged
the screen as word-heavy; the fix shipped as a UI-only pass, mocked first
in "The Quiet Table" artifact and then implemented. The spoken-move rack
(eight rows of quoted line plus preview sentence) is replaced by one
horizontal band of five verbs at 3/2/1/1/2: **Sway** (Charm, Persuade,
Connections: the presses that cool the room), **Force** (Intimidate,
Demonstration: the shows of power), **Offer**, **Read**, and **Bide**,
which pairs the free Pass with the paid Patience token and retires the
Pass button from the action row. Each chip carries a timing glyph (✓/·/✗)
from `NegotiationState.TimingFor`, the stance rules made visible (with
Intimidate-versus-Idealist always ✗); one shared context line under the
band shows the hovered move's spoken line and mechanical read. Clause
cards went quiet: description on the selected card only (tooltip
elsewhere), position label dropped (the slider says it), the lock line
kept. The soft intent sentence is retired in favor of the card markers it
duplicated; the Embassy tier-2 precise briefing remains. The chips debug
toggle and its rack are removed. Ruled same day: the rack keeps a STABLE
shape, all five verbs render every turn; tokens held but unaimable (no
movable clause) dim with the reason, and a verb with nothing left shows
one empty socket in a chip's footprint. First-screenshot fixes, same day:
every card now carries a live payout line (gold/supplies/rep/fuel at the
clause's CURRENT position, moving with the slider), restoring the stakes
the quiet cards had hidden with the prose; the letter-disc token art is
replaced by drawn symbol PNGs (`tools/generate_token_art.py`, re-runnable,
same per-token hues); and color emoji are purged from negotiation UI
strings, since the font stack silently drops them (the handshake button's
missing glyph was the proof). Second-screenshot fixes: chip count tags and
timing badges ride the disc EDGE (mostly outside the circle) so the symbol
keeps its face; and clause cards read as scrolls: dowel bars cap the
parchment, face-down clauses are sealed scrolls, and opening one (click,
or an Insight flip) plays a short unroll animation as the text sweeps down
from the top roller. No rules changed.

**Touched:** `NpcArchetype.cs` (ShortName/RumorText on DealTerm,
OpeningTextLate/DialogueWalkawayLate on the encounter, Guile/Poise display
names), `NegotiationState.cs`, `NegotiationBarks.cs` (continuity table,
small-print hints, gift-bark split), `NegotiationManager.cs`,
`NegotiationContext.cs`, `ExpeditionManager.cs` (echo skip),
`tools/verify_negotiations.py` (contract checks),
`tools/negotiation_squeeze_sim.py` (new), and all 24 encounter JSONs
(shortName everywhere, rumorText on every hidden term, late-war variants
on nine war-facing tables).
