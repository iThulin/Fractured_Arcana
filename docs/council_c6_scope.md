# Council C6 — Tier C & the Pipeline — Scope

*2026-07-08 · anchored in court_council_system_v1_1 §5, §6, §9, §10, §11, §13, §14 · successor phase to C5 (closed 2026-07-08, see build_order_v3 §1).*

C6 is where the court game and the tactical negotiation system **fuse**: council standing becomes the pipeline that loads a real negotiation table, and winning that table moves the existing `ArchmageDisposition`. Unite and Coerce stop being abstract expedition outcomes and become the culmination — or the missed window — of lunations of court work.

---

## 0. Dependency status — C6 is buildable now

Every hard dependency is already in the codebase, so C6 does **not** need Phase B/D's unit, spell, or environment tracks first:

| Needs | Status | Where |
| --- | --- | --- |
| Negotiation encounter system | present | `NegotiationManager` / `NegotiationState` / `NegotiationContext` / `NegotiationEncounterLoader` |
| Archmage disposition write path | present | `CampaignState.SetDisposition(id, ArchmageDisposition.Allied)`; enum {Unknown, Neutral, Allied, Coerced, Overthrown, Corrupted} |
| Court standing + bands | present | `CourtState.Band()` / `StandingScore()` (C1–C5) |
| Corruption spread + target selection | present | `CorruptionSpread` / `SelectRegionToCorrupt` (deflection hooks here) |
| Cross-cycle ledger | present | `EternalLedger.RenownAnchors` (`List<RenownAnchor>`) |
| Embassy tiers | present | `CouncilQueries.EmbassyTier` (Tier C gated at Embassy I; Broker at Embassy III per §2) |

**Placement note:** build_order_v3 files C6 under Phase D behind spells/units. Its dependencies are met today, so the council track can continue straight into C6 if it keeps priority; nothing downstream of C5 blocks it.

---

## 1. Rulings due BEFORE code (design gates)

Per the "rulings are gates, not chores" principle, settle these first — the whole phase's shape depends on them:

- **R-C6a — Tier C encounter sourcing.** Does each climax (Patron Oath, Expose the Agent, Broker the Compact, post-rescue Reconciliation, major Petition) load an **authored `NegotiationEncounterData` template** whose parameters are injected from court state, or is the encounter **procedurally synthesized** from court state? *Recommendation: authored template per climax type + court-state parameter injection* — keeps the negotiation designer-authorable while §6's "court state pre-loads it" still holds.
- **R-C6b — Regard → hidden terms.** The rule that seeds hidden terms from courtier Regard (how many terms, which courtiers, favorable vs. costly).
- **R-C6c — Envoy affinities → tokens.** What "envoy affinity" maps to. Cheapest: reuse `BuildTokenPool`'s existing companion-trait → `LeverageToken` switch (Reckless→Intimidate, Stoic→Patience, …) for the resident envoy.
- **R-C6d — The Compact's price.** "Broker the Compact | Varies (the court's price)". What the court demands (gold, a favor, a standing hit, a companion secondment) and whether an **Arcane compact** (shard access without alliance) is a distinct selectable outcome.

---

## 2. Deliverables (ordered)

### C6.1 — Court-state negotiation preload
Extend the preload in `NegotiationState.Initialize` / `NegotiationContext` so a council-launched Tier C negotiation opens with:
- **Starting tension from band** — Trusted → Cordial, Welcome → Strained (§6). Refines the current `factionRep`→tension map, which already exists for kingdom NPCs.
- **Token pool** — resident envoy affinities (R-C6c) + Patron bonus (**already wired, archetype-typed, C5**).
- **Hidden terms from Regard** (R-C6b).

*Already present:* `factionRep`→tension, Patron token. *New:* band→tension refinement, envoy-affinity tokens, Regard→hidden-terms seeding.
**Exit:** a debug-launched council negotiation opens at band-derived tension with seeded hidden terms and the envoy's affinity tokens in the pool.

### C6.2 — Tier C launch + result routing pipeline
A council→negotiation launch path parallel to `ExpeditionManager`'s POI launch:
- New `NegotiationContext` input fields: `ClimaxType`, `KingdomId`, `ArchmageId`.
- Set context → synthesize/load the encounter (R-C6a) → scene-swap → on return, route the result to court/disposition mutations via a single **council-climax result handler** (extends the existing `NegotiationContext` output block).
- Gated at **Embassy I** (Tier C unavailable with no Embassy, per §2).
**Exit:** a Tier C negotiation launched from the council layer writes its outcome back to court state and returns cleanly.

### C6.3 — Astrologer agent + deflection (§9)
- **Manifest:** when a kingdom's corruption first reaches 2, flag one courtier `IsCorruptedAgent` (weighted toward Favorite / Court Wizard). `CourtierState.IsCorruptedAgent` already exists and is round-trip asserted — **no new save struct**.
- **Whisper:** −1 standing score per lunation while present (CouncilTick step).
- **Deflection:** in the tick's corruption-target step — evaluated **before** corruption spread (§13 step 6) — if an envoy is resident at the targeted kingdom, 50% chance (tunable) the tick redirects to the next-priority target. Redirects pressure; never deletes it.
**Exit:** corruption reaching 2 flags an agent; a resident envoy deflects ~50% of ticks over a logged sample; the agent applies −1/lunation.

### C6.4 — Expose the Agent (Tier C) — depends on C6.2, C6.3
Gate: **Favored** + (a known secret OR a Spymaster favor). Interactive via C6.2. On success: clear `IsCorruptedAgent`, −1 kingdom corruption, +2 Regard court-wide.
**Exit:** exposing removes the agent and applies both refunds.

### C6.5 — Broker the Compact → Allied (Tier C) — depends on C6.2
Gate: **Trusted + Embassy III + corruption ≤ MaxCorruptionForUnite**. The climax negotiation. On success: `CampaignState.SetDisposition(archmageId, ArchmageDisposition.Allied)` — or an **Arcane compact** alternate outcome (shard access, no alliance) per R-C6d.
**Exit:** brokering sets the Seat Allied entirely through the council layer; verified via `GetDisposition`.

### C6.6 — Standing gates on Unite/Coerce (§10) — independent
Add standing gates in front of the existing corruption gates on the **expedition** paths:
- In-region Unite encounter now requires **Favored** (was standing-ungated) — so some court work is mandatory for any Unite.
- Coerce requires **Welcome**.
- Allied-via-Broker (C6.5) is the Trusted court path.
Locate the current expedition Unite/Coerce trigger and add `CourtState.Band()` checks.
**Exit:** the in-region Unite encounter refuses below Favored; Coerce below Welcome; existing corruption gates unchanged.

### C6.7 — Hall of Records renown (§11) — independent, save-adjacent
Cross-cycle renown in `EternalLedger`: courts in later cycles begin at **Received** instead of Unknown once a renown threshold is met.
- Accrue renown (on reaching Trusted / on a successful Unite) into a `RenownAnchor`.
- At court generation (`CourtGenerator`), consult `EternalLedger` renown to set the initial band / `HasContact`.
- **Save-file paranoia:** any new/changed `EternalLedger` field gets a round-trip assertion (extend `CouncilSaveAssert` or a ledger-specific assert), including a mid-expedition save.
**Exit:** a court in cycle N+1 starts at Received after the renown threshold was met in cycle N; round-trip asserted.

---

## 3. Dependency graph

```
R-C6a..d (rulings)
        │
      C6.1 ──► C6.2 ──► C6.4   (Expose)   ◄── C6.3 (agent must exist to expose)
                     └─► C6.5   (Compact → Allied)
      C6.3 ─────────────────────────────► (whisper + deflection stand alone too)
      C6.6  (independent — expedition/disposition gates)
      C6.7  (independent — ledger, save-adjacency)
```

Suggested build order: **rulings → C6.1 → C6.2 → C6.3 → C6.4 → C6.5 → C6.6 → C6.7.**

---

## 4. Exit criterion (doc §14)

> Full arc: build a court from Unknown to Trusted across a cycle and **Unite an archmage entirely through the council layer** (Broker the Compact).

Plus, for phase completeness: an Astrologer agent manifests at corruption 2, is deflected by a resident envoy, and is removed by Expose the Agent; and a later-cycle court begins at Received via a renown anchor.

---

## 5. Verification queue (per the established discipline)

- **Named-court arc test.** Drive one named court Unknown → Trusted over a scripted cycle; Broker; assert `GetDisposition(archmageId) == Allied`. Log the standing at each lunation boundary.
- **Astrologer.** Force a kingdom to corruption 2; assert exactly one courtier flagged (Favorite/Court Wizard weighting); with a resident envoy, assert deflection rate ≈ 50% over N ticks with log evidence; assert −1 standing/lunation.
- **Expose.** Assert agent cleared, kingdom corruption −1, Regard +2 across all courtiers.
- **Save.** Round-trip assertion for any new save-adjacent field (renown), incl. a mid-expedition save (the `EchoesInFlight` precedent).
- **Tier C preload.** Snapshot band→tension and Regard→hidden-terms for a Welcome vs. Trusted court; confirm the pre-load differs as specified.

---

## 6. Risks specific to C6

| Risk | Note |
| --- | --- |
| R-C6a (encounter sourcing) is load-bearing | Every climax consumes it. Settle it before C6.2, or C6.4/C6.5 get rebuilt. |
| New council→CampaignState write-back seam | The negotiation return now mutates disposition/court state. Keep the routing single-sourced in one council-climax result handler; do not scatter `SetDisposition` calls across scenes. |
| Tick ordering | Deflection must run **before** corruption spread (§13 step 6). Getting it after spreads first, then deflects nothing. |
| Unite gate changes existing balance | Requiring Favored for the expedition Unite path retunes the endgame reachability — coordinate with the demand-economy tuning (K2+C5 watch item) rather than shipping blind. |
| "One authoritative doc" | The court_council design doc + memory are read-only imports; this scope lives in-repo. If the design doc gains a C6 detail pass, reconcile it here. |
