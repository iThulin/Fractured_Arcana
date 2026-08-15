**ESPIONAGE & THE VEILED CONCORD**

*Informant Networks, the Shadow Marketplace & the Intelligence Layer*

*Design Reference Document — v1 — IMPLEMENTED E1–E6 (see §15 for build status, deviations, and the one open seam)*

This document defines the espionage system — the third tenant of the lunation tick, sitting beneath the Court & Council system (v1.1) the way the underworld sits beneath the throne room. It has two layers. **The Informant Network** is a standing intelligence asset system: you turn cheap, expendable NPCs and plant them inside kingdoms, courts, and warfronts, where they yield intelligence passively each lunation and can be spent on sabotage — the persistent, at-range, into-the-dark counterpart to the one-shot Gather Intelligence mission. **The Veiled Concord** is a faction-neutral cabal of spies and killers that runs as a two-sided marketplace: you buy contracts they can execute that your companions cannot, you sell them the secrets and contraband your network harvests — and the Astrologer buys from the same counter, which is what makes every dealing dangerous. The system is a consumer of the lunation tick (Single-World Refactor v2, Phase 2) and an extension of CouncilState (Court & Council v1.1); it introduces **exactly two new structs** and otherwise reuses Exposure, EchoEvent, the negotiation encounter, the corruption clock, and the warfront machinery.

---

# **0. The Counterargument This Document Must Survive**

**Confidence: high that this is the real risk.** The strongest case against building espionage at all is that the game already has *three* mechanics that reveal hidden things and move court state, and a fourth would be redundant clutter — the exact "checkbox management" rot the Council doc opens by warning against. Specifically:

- **Gather Intelligence** (Council §5) already charts tiles, reveals POIs, and discovers courtier secrets.
- **The Spymaster office** (Council §4a) already sells Intelligence favors: chart packets, reveal-all-POIs, reveal-a-secret.
- **The Insight token** (Negotiation §2a) already reveals hidden terms tactically.

If the Informant Network is just "Gather Intelligence, but standing," it adds a fourth reveal-verb with no new decision space and dilutes all three. This document only earns its place if espionage is *categorically* a different thing from intelligence-gathering, not a bigger dose of it. The design's answer, load-bearing throughout:

1. **The Informant Network is the only layer that operates in the dark with no envoy and no expedition.** Gather Intelligence spends a companion (the scarce envoy pool); expeditions spend phases. Informants spend neither — they are the standing presence that lets the strategic map fill in *between* sorties and warns you about the corruption tick *before* it fires. It occupies the "future intel charts at range" seam the world refactor explicitly reserved (§5, Charted-state source). It is not a reveal-verb; it is a **denial-of-fog-of-war economy** with its own currency (Cover) and its own antagonist (counter-intelligence).
2. **The Veiled Concord is the only layer where actions are irreversible, morally costed, and shared with the enemy.** Court diplomacy is reversible (recover from Hostile via gifts) and legible (the Herald names causes). Concord dealings are neither: an assassinated courtier stays dead, the ledger of who-dealt-with-shadows is permanent within a cycle, and the same cabal that kills for you kills for the Astrologer. It is the game's answer to "I have done the diplomacy and I am losing anyway — what will I pay to win dirty."

If, in playtest, the Informant Network reads as "Gather Intelligence on a timer" and the Concord reads as "a shop," the system has failed and should be cut, not patched. The kill-criteria are in §14.

---

# **1. Core Design Philosophy**

**The Problem.** Intelligence in strategy games is a solved-then-boring resource: you buy vision, you have vision, the interesting decision was made once at purchase. And "dirty options" (assassination, sabotage) are usually free power with a reputation tax — a strictly-better button for players who don't care about being liked.

**The Solution.** Make intelligence a *standing operation under attrition*: informants decay, get hunted, and get turned; keeping the network alive is the game, not acquiring it. And make dirty power a *contested marketplace* where the price is not reputation-you-can-recover but exposure-to-a-third-party-who-also-hires-them — so the question is never "do I want this" but "do I want this badly enough to owe the shadows and be owed against."

**The Principle. Espionage is the layer with no take-backs and no clean hands.** The court layer is medieval-legible; the espionage layer is deliberately its opposite — partial information, delayed betrayal, and consequences the Herald's Report cannot fully explain because the world itself does not fully know what happened. Where the court game is "load the negotiation table in your favor," the espionage game is "see the table before you sit, or flip it before your enemy does."

**The Payoff.** The two layers form a closed loop that the diplomacy layer does not: **network yields intelligence → intelligence sells to the Concord for Favor → Favor buys contracts that reshape the board.** Diplomacy spends companions to earn standing; espionage spends expendable assets and moral standing to earn tempo against the doomsday clock. The two economies are deliberately non-fungible.

---

# **2. Layer One: The Informant Network**

## **2a. Informants Are Not Companions — This Is the Whole Distinction**

**Confidence: high; this is the design's spine.** The Council system's core tension is that every envoy is a companion pulled from the party HP pool — dispatch is sacrifice. If informants were also companions, espionage and diplomacy would compete for the same scarce resource and one would simply dominate. Informants are therefore a **separate, cheap, expendable asset class**: turned NPCs, not party members. They have no arc, contribute no HP, occupy no expedition slot. They are numerous and disposable — you are *meant* to lose them.

Informants are acquired, not recruited into the party:

| **Source** | **How** |
| --- | --- |
| Returned/blackmailed court secrets | A courtier's secret, instead of being spent on blackmail (Council §3a), can be spent to *turn* a client of that courtier into an informant inside the court. |
| Negotiation outcomes | A "Future Alliance → informant" term (Negotiation §7a already lists "an informant" as a Cordial-only alliance reward) plants one in the NPC's kingdom. |
| Concord purchase | Buy a pre-placed informant with instant Cover (§3c). The fast, expensive path. |
| Rescued / aided NPCs | Survivors and freed prisoners can be asked to serve rather than sheltered — a moral fork (Survivor deals have "disproportionate reputation effects," Negotiation §4). |
| Expedition capture | Take a defeated Astrologer agent or kingdom soldier alive; interrogate (a Tier B choice card) into an informant or sell to the Concord. |

## **2b. What an Informant Is (Data)**

An informant has a placement, a role, and one meter that is the entire subgame: **Cover**.

| **Property** | **Range / Values** | **Meaning** |
| --- | --- | --- |
| Placement | KingdomId, and optionally a CourtierId (embedded in a court) or a WarfrontId (embedded in a siege) | Where they operate. Court-embedded informants share that court's **Exposure** meter (no new counter). Kingdom-level informants use their own Cover. |
| Role | Watcher, Cutout, Saboteur | Determines yield type and burn risk (§2c). |
| Cover | 0–10, starts by acquisition source (turned = 6, Concord-bought = 9, captured/coerced = 3) | Inverse of exposure. Decays under counter-intelligence; at 0 the informant is **burned** (§2e). The meter the player manages. |
| Access | 1–3 | What they can reach. Rises with time-in-place (+1 per 3 lunations survived, cap 3); gates the higher-value yields and sabotage. |
| Handler | none / companion / Undercroft | A companion left at campus (not on expedition, not an envoy) or the Undercroft building can *handle* a network, reducing burn rolls (§2e). The handler is a soft sacrifice — smaller than an envoy, but real. |

## **2c. Roles and Standing Yields**

An informant yields on the lunation tick according to role and Access. Yields resolve **Tier A** (silent, delivered in the Herald's Report) unless noted. Yields are the network's product — and the Concord's raw material (§3b).

| **Role** | **Passive Yield (per lunation, by Access)** | **Active Verb (spends Cover)** |
| --- | --- | --- |
| **Watcher** | A1: chart a radius-2 packet in the kingdom (writes **Charted**, not Explored — the reserved world-refactor seam). A2: also preview one echo *in flight* toward any court (Courier-Station effect, at range). A3: also preview the Astrologer's **next** corruption target one lunation early. | — (pure sensor) |
| **Cutout** | A1: +1 Concord Favor equivalent in sellable intel (§3b). A2: reveal one courtier secret over 2 lunations without a mission. A3: forge a **false echo** — manufacture one EchoEvent (valence of your choice, magnitude minor) toward a target court; if traced (§2e), it detonates as a Scandal. | **Plant Evidence** (−2 Cover): set a chosen courtier's Regard toward you or a rival −2 by fabrication. Interactive Tier B. |
| **Saboteur** | A1: −1 patrol pressure in the kingdom (Council-Hostile penalty offset). A2: reveal a warfront's stronghold weakness (feeds WarfrontStrongholdCleared; the expedition arrives pre-briefed). A3: — | **Sabotage** (−3 Cover): degrade a warfront's siege tier by 1 for one lunation; **or** delay the kingdom's next corruption tick by one lunation (§4). Tier B. |

## **2d. Feeding the Network Into the Fog**

The Watcher yield is the mechanical justification for the whole layer: **it is how the strategic map fills in between expeditions.** A single Watcher at Access 2 charts a small packet each lunation and previews in-flight echoes at range — turning the espionage layer into the persistent sensor grid the exploration loop otherwise lacks. This is the "intel later" half of the world refactor's Charted-state decision (Single-World Refactor §9), now built. Deliberately, informants *Chart* but never *Explore*: they tell you where to look, never substitute for going. The dark still has to be entered.

## **2e. Counter-Intelligence: How the Network Dies**

**Confidence: high that attrition is mandatory.** Without a hunter, standing intelligence is a passive buff and §0's counterargument wins. Each lunation, every kingdom with a Spymaster courtier — and every court with an Astrologer agent — rolls counter-intelligence against informants operating there:

- **Burn roll:** the Spymaster's Influence (1–3) plus +1 if the court's Exposure is already ≥4, versus the informant's Cover. On a hit, Cover −1 to −3 by role (Saboteurs bleed fastest — action is loud). Handlers subtract 1–2 from the hit.
- **Cover 0 = burned.** A kingdom-level informant is simply lost (removed). A **court-embedded** informant, when burned, spikes that court's Exposure by +3 and applies the Scandal threshold immediately (Council §8) — the network is traced back to you, and your resident envoy pays for it. This is the coupling that makes court-embedded informants high-risk/high-yield without inventing a new consequence system: it reuses the court Exposure ladder wholesale.
- **Exfiltrate** (player action, no tick cost): pull an informant before they burn, banking their Access as a renown annotation (Hall of Records) so a future cycle's re-placed informant starts at higher Cover. Exfiltration is the disciplined player's tempo trade: take the asset off the board before the enemy takes it off for you.

The single most sensitive number in this layer is the **burn-roll weight**: too low and informants are immortal free vision; too high and the network never survives long enough to yield. Tune against Access ripen-rate as a pair (§13).

---

# **3. Layer Two: The Veiled Concord**

## **3a. What the Concord Is**

The Veiled Concord is a **faction-neutral cabal** — mercenaries, cutthroats, fences, and forgers — that exists in every cycle beneath the courts and answers to no throne. It is not a kingdom, has no Seat, no Regard, and cannot be Allied or Coerced. It has a price list and a memory. It is generated per cycle from the cycle seed like the courts (Council §3), operating from **Concord nodes** — a POI type scattered into the world POI table, the majority undiscovered, found by expedition or revealed by a Watcher. Contact requires reaching one node once (mirrors court "first contact").

The Concord is a **two-sided marketplace**, and both sides are literal:

## **3b. The Sell Side — Fencing the Network's Product**

You sell to the Concord for **Concord Favor** (the layer's currency) and gold:

| **Good Sold** | **Yields** | **Cost / Catch** |
| --- | --- | --- |
| Cutout intel packets (§2c) | Concord Favor | The network keeps producing this passively; the sell side is where informant yield *becomes* buying power. |
| A courtier's secret | High Favor + gold | **Burns your relationship with that court** if the sale is ever traced — the secret is now loose. Ruling flag §15. |
| Contraband / cursed artifacts / captured prisoners | Favor + gold | Prisoners sold (rather than ransomed to their court) are a permanent −Regard if discovered; the Survivor moral axis, monetized. |
| An Astrologer agent taken alive | The most Favor available | The Concord is *very* interested in the enemy's people. |

The sell side is the closed loop's hinge: **Watcher/Cutout yield → sell → Favor → contracts.** Without it, the Concord is a gold sink; with it, espionage funds itself and the player who invests in the network can afford the shadow-work, while the player who doesn't must pay in gold they'd rather spend elsewhere.

## **3c. The Buy Side — Contracts Companions Cannot Fulfill**

You spend Concord Favor (and gold) to commission contracts. These are the verbs the diplomatic layer categorically cannot offer, resolving on the lunation tick at the tier noted:

| **Contract** | **Effect** | **Favor Cost** | **Tier / Risk** |
| --- | --- | --- | --- |
| **Plant Asset** | A pre-placed informant appears with Cover 9. The fast network path. | Low | A |
| **Purchase Intel** | One-shot: reveal all POIs in a window, or one courtier's secret, or the Astrologer's next 2 corruption targets. | Low–Med | A |
| **Sabotage** | Break a warfront siege (clear the stronghold *without* an expedition), or delay a corruption tick 2 lunations. | Med | B |
| **Theft** | Steal a specific secret or a Regalia-tier item from a court's vault. | Med–High | B, +Marked |
| **Extraction** | Free an imprisoned envoy (Council §8, Imprisonment) without mounting the Prison-POI expedition. | High | B, +Marked, +owes the Concord a future contract |
| **Assassination** | Remove a courtier permanently. Their office goes vacant; the court reels (−standing, roster shock). | Very High | C — interactive; **irreversible** |

Assassination and Extraction are **Tier C** interactive when they matter: they load as a negotiation encounter with the Concord's broker, where your leverage tokens set the price and the hidden terms are the strings attached. The Concord always attaches strings.

## **3d. The Twist That Makes It Two-Sided: The Marked Meter**

**Confidence: high that this is what separates the Concord from "a shop."** Every buy-side contract, and every traced sell, raises **Marked** (0–10, one new scalar — see §12). Marked is not reputation you recover with gifts; it is the shadow-world's memory of you, and it has teeth:

| **Marked Threshold** | **Consequence** |
| --- | --- |
| 3 — Noticed | A courtier may discover your Concord dealings and hold them as **blackmail** against you (reuses the secret/blackmail mechanic, Council §3a — now *you* are the one with a secret). |
| 6 — Sold Out | The Concord fences *your* movements: +1 patrol pressure on your expeditions cycle-wide, and the Astrologer's agents get one free burn roll against your network per lunation. |
| 9 — Contracted Against | The Astrologer commissions the Concord **against you**: a killer comes for an envoy (Imprisonment-style, but lethal to the arc for a cycle) or a mass burn hits your network. **Deflectable** by outbidding — spend Concord Favor to buy back the contract (§3e). |

Marked decays −1 per lunation with no Concord dealings, exactly as Exposure decays — the reuse is intentional and the player already understands the shape. The design intent: **the Concord is always available and always the wrong long-term answer** — a tempo loan against the endgame, not a strategy. A player can win dirty, but a player who lives dirty gets a knife.

## **3e. The Bidding War**

Because the Concord serves the highest bidder, the antagonist is a *client at the same counter*. When the Astrologer would commission against you (Marked 9), or when you and the Astrologer both want the same node's exclusive service, resolution is an **outbid**: highest Concord Favor wins the contract; the loser's Favor is partially spent anyway (the Concord charges to *consider* you). This makes hoarded Favor a defensive reserve, not just a shopping wallet — the late-cycle question "do I spend Favor to kill a courtier, or hold it to survive the contract coming for me" is the Concord layer's signature decision. **Confidence: moderate** that the outbid resolves cleanly without a dedicated UI; may need a Tier B interjection card to present it legibly.

---

# **4. Integration With the Corruption Clock**

The Council system already gave the strategic layer a *defensive* role via envoy deflection (Council §9): a resident envoy has a 50% chance to deflect a corruption tick to the Astrologer's next target. Espionage adds two more levers, and the interaction must be capped:

- **Watcher A3 — preview:** see the Astrologer's next target one lunation early (information, not prevention).
- **Saboteur / Concord Sabotage — delay:** actively push a corruption tick back 1–2 lunations (prevention, at Cover or Favor cost).

**Ruling (proposed, confidence moderate): deflection and delay do not stack multiplicatively.** In any single lunation, a kingdom benefits from at most one corruption-mitigation source (deflect *or* delay, whichever the player triggers), and the total mitigation available across the whole map per lunation is capped at two events. Uncapped, the three systems together (envoy deflect + Saboteur delay + Concord Sabotage) would stop the doomsday clock outright and collapse the game's central pressure. The clock must always advance; espionage buys *tempo against it*, never a pause. This is the single most important balance coupling in the document and must be tuned against CorruptionTickInterval as a set of three (§13).

---

# **5. The Lunation Tick — Revised Resolution Order**

Espionage slots into the Council system's 8-step tick (Council §13). Order still matters: intelligence must resolve before the enemy acts on the same board, and sabotage/delay must land before corruption spread. Espionage steps are inserted (marked **NEW**); everything else keeps its Council ordering.

| **#** | **Step** |
| --- | --- |
| 1 | Land echoes (incl. Cutout **false echoes**) whose LandsOnLunation ≤ now; apply Regard; log |
| 2 | Obligation decay on overdue favors |
| 3 | Agent whispers; Corrupted-court decay |
| 4 | Resolve envoy missions; **NEW: resolve informant passive yields (chart/preview/Favor) and Concord contract completions**; roll incident interjections |
| 5 | **NEW: counter-intelligence burn rolls (Spymaster + agent) against informants; apply Cover loss; process burns (court-embedded burns spike Exposure here, feeding the next sub-step)** |
| 6 | Exposure decay + thresholds (Scandal/Expulsion/Imprisonment); **NEW: Marked decay + thresholds; process any Astrologer-vs-you Concord contract, offering the outbid** |
| 7 | Corruption target selection with **capped** mitigation — envoy deflection **or** Saboteur/Concord delay, per §4 — THEN corruption spread |
| 8 | Kingdom drift / border pressure (existing Phase 2 sim) |
| 9 | Compile and present the Herald's Report (now includes a **Shadow Ledger** section: network status, burns, Favor, Marked); flush interjection queue |

The Herald's Report gains a **Shadow Ledger** panel: per-kingdom informant Cover and Access, burns this tick and who caught them, Favor balance and Marked level, and Concord contract status. One screen still, once per lunation — the espionage layer is as legible as the court layer, *except* for the things it is designed to hide (a false echo you planted is not flagged as yours unless traced; a betrayal clause in a Concord contract reads as a locked hidden term until Insight or a Watcher reveals it).

---

# **6. Campus Integration**

Every other strategic system got exactly one spine building (Embassy for the court layer) plus riders on shared buildings. Espionage follows the same shape — **one new building, the rest riders** — to respect the no-proliferation rule.

| **Building** | **Espionage Effect** |
| --- | --- |
| **The Undercroft (I–III)** *(NEW — the spine)* | Concurrency cap on active informants (I: 2, II: 4, III: 6) and Concord contracts (I: 1, II: 2, III: 3). II: acts as a Handler for one network for free. III: −1 Marked gain per contract; unlocks Assassination. Exactly analogous to the Embassy ladder — the cap is the primary economy knob. |
| Library | Informant Access ripens in 2 lunations instead of 3 (your archives brief them faster). |
| Courier Station | Watcher previews and yields arrive same-tick (echo delay 0), consistent with its court effect. |
| War Room | Preview a Concord contract's success band, cost, and *number of* hidden strings before commissioning (does not reveal the strings — that needs Insight/Watcher). |
| Embassy | Diplomatic cover: −1 counter-intelligence burn weight against court-embedded informants (your ambassadors muddy the trail). |
| Hall of Records | Exfiltrated informants bank Access as cross-cycle renown: re-placed networks start at higher Cover next cycle (breadth-unlock, consistent with the ledger). |
| Seance Chamber (Necromancer) | Interrogate a captured/killed enemy asset for free: turn one prisoner into an informant per cycle at no Cover cost (communion with the unwilling dead). |
| Charm Parlor (Enchanter) | Turned-secret informants start at Cover 8 instead of 6 (your enchanters seal loyalties). |
| Temporal Observatory (Chronomancer) | Once per cycle, re-roll one counter-intelligence burn that would kill an informant. |

**Confidence: moderate** on the exact school-building riders; they mirror the court doc's school riders (Council §11) but the specific numbers are first-pass and should be set after the Undercroft cap is tuned.

---

# **7. Data Model**

**Confidence: high that this respects the two-new-structs-max rule.** The espionage layer adds **two structs** — `InformantState` and `ConcordContract` — plus scalar/list fields on the existing `CouncilState` container and one authored JSON data table (the Concord price list, regenerated per cycle from seed). Everything else is reuse: Exposure is CourtState's existing meter; false echoes are `EchoEvent`; Tier C is the negotiation encounter; the corruption/warfront hooks are existing fields.

All state is per-cycle and serializes into CycleState alongside CouncilState/KingdomState. The ledger and network clear at cycle end (loop fiction); exfiltrated-Access renown lives in EternalLedger. JSON via System.Text.Json with the project's CamelCase + IncludeFields options; `additionalProperties: false` on all authored JSON per schema discipline.

```csharp
public class InformantState
{
    public string Id = "";
    public string KingdomId = "";
    public string CourtierId = "";   // non-empty = court-embedded (shares court Exposure)
    public string WarfrontId = "";   // non-empty = siege-embedded
    public string Role = "";         // Watcher, Cutout, Saboteur
    public int Cover = 6;            // 0-10; 0 = burned
    public int Access = 1;           // 1-3; +1 per 3 lunations survived
    public string HandlerCompanionId = ""; // empty = unhandled or Undercroft-handled
    public int LunationPlaced = 0;   // drives Access ripen
}

public class ConcordContract
{
    public string Id = "";
    public string ContractType = ""; // PlantAsset, PurchaseIntel, Sabotage,
                                      // Theft, Extraction, Assassination
    public string TargetKingdomId = "";
    public string TargetId = "";     // courtier / warfront / envoy / POI, by type
    public int LunationsRemaining = 0;
    public int FavorPaid = 0;
    public bool AgainstPlayer = false; // true = Astrologer-commissioned (the outbid path)
}
```

Fields added to the existing **CouncilState** root (no new container):

```csharp
// --- espionage additions to CouncilState ---
public List<InformantState> Informants = new();
public List<ConcordContract> ConcordContracts = new();
public int ConcordFavor = 0;
public int Marked = 0;              // 0-10, decays -1/lunation idle
public bool ConcordContacted = false; // first-node contact flag
// ConcordStanding is DERIVED from lifetime contract count — never stored,
// mirroring CourtState.StandingScore/Band discipline.
```

Every one of these fields is save-adjacent. Per the FactionId-dataflow lesson, **each requires a round-trip serialization assertion before ship** — `InformantState`, `ConcordContract`, and the five CouncilState scalars/lists — authored against the live `CouncilState.cs` as shipped, not a snapshot (§9).

---

# **8. The Interactivity Model**

Espionage reuses the Council system's three-tier interactivity contract (Council §6) verbatim — routine work is silent, stakes are played:

| **Tier** | **Espionage content** | **Presentation** |
| --- | --- | --- |
| A — Automated | Watcher/Cutout passive yields; Plant Asset; Purchase Intel; informant ripen; Favor accrual | Silent on the tick; Shadow Ledger line in the Herald's Report |
| B — Interjection | Sabotage decision points; Plant Evidence; a burn caught mid-network; the Marked outbid; interrogation of a captive | Single-scene choice card, queued to the lunation-boundary summary; never interrupts an active expedition |
| C — Interactive Climax | Assassination, Extraction, and the Broker negotiation for a major contract | Full negotiation encounter vs. the Concord's broker; hidden terms = the strings; the player's leverage tokens set the final price |

Tier C is where espionage fuses with the tactical layer exactly as the court game does: the Concord broker is a negotiation NPC (archetype **Opportunist** by default — numerous, surprising hidden terms; §Negotiation 4), and beating him down on price is the same skill as any negotiation, now with your life on the other side of the table.

---

# **9. Prerequisites and Code Debt (Do Not Skip)**

**Confidence: high; this is a hard gate.** Espionage is the third tenant of the lunation tick and the second extension of CouncilState. It **cannot be built until**:

1. **The Council system is shipped through C5** (Intrigue + consequences: Exposure, Scandal/Expulsion, Imprisonment→Prison-POI, Patron wiring). Espionage reuses all of it; building against a stub reuses nothing.
2. **The outstanding C5 debt is paid first** (per project memory):
   - Prison release key migrated from `PrisonPoiIndex` (mutable list position) to stable coordinates — Extraction contracts free the *same* imprisoned envoy the Prison POI represents, so a mutable index will desync the two.
   - The three owed round-trip assertions land: `HeraldReport`, `CourtState.StandingPenalty`, `ImprisonedEnvoy`.
   - `CouncilPanel` deletion grep for stale references completes.
3. **These live files are pasted current before any patch** (never patched against snapshots — the FactionId-dataflow failure mode): `CouncilScreen.cs`, the CouncilTick handler, the kingdom-drift/border-pressure code, `CouncilState.cs` as shipped in C5, and the `EchoEvent` emission file. New files (`InformantState`, `ConcordContract`, the espionage tick sub-steps as complete drop-ins, the Concord JSON) may be authored now against the interface contracts in §5 and §7; the *patches* that wire them into the live tick may not.

---

# **10. Build Order**

Six phases, E1–E6, mirroring the Council C-series and gated behind it. Each ends at a verifiable state; no stubs — every verb registered in a phase has real resolution logic.

| **Phase** | **Contents** | **Exit Criterion** |
| --- | --- | --- |
| **E1 — Data + generation (headless)** | `InformantState`, `ConcordContract`, CouncilState field additions; Concord node scatter into the POI table; Concord price-list JSON from seed; serialization round-trip assertions | Generate a world; dump the Concord roster and node placements; save/load preserves informants + contracts + Favor + Marked byte-for-byte |
| **E2 — The network (passive)** | Informant acquisition (turn-a-secret, capture); Watcher/Cutout/Saboteur passive yields; Access ripen; tick steps 4–5 (yield + burn rolls); Shadow Ledger v1 in the Herald's Report | Plant an informant, watch it chart tiles and ripen over lunations, watch a Spymaster burn it; a court-embedded burn spikes court Exposure |
| **E3 — The Concord (marketplace)** | Concord node contact; sell side (fence intel/secrets/prisoners → Favor); buy side Tier A/B (Plant Asset, Purchase Intel, Sabotage, Theft); Marked meter + thresholds 3/6 | Sell a Watcher packet, bank Favor, buy a Sabotage that breaks a siege; push Marked to 6 and feel the patrol pressure |
| **E4 — Sabotage & the clock** | Saboteur/Concord corruption **delay**; warfront siege-tier degradation; the §4 mitigation cap wired against envoy deflection; Cutout false echoes | Delay a corruption tick once, confirm it cannot stack with envoy deflect, confirm the clock still advances |
| **E5 — The shadow war** | Marked threshold 9 → Astrologer contracts against you; the outbid (§3e); court blackmail of *your* Concord dealings; Extraction vs. the Prison-POI (stable-coord dependency) | Reach Marked 9, get a contract sent at you, outbid it with hoarded Favor; extract an imprisoned envoy via Concord instead of expedition |
| **E6 — Tier C + the pipeline** | Assassination and the Broker negotiation wired to the negotiation encounter with Concord-state preload; Undercroft I–III caps + Assassination unlock; Hall of Records exfiltration renown | Full arc: fund a network, fence into Favor, assassinate a blocking courtier to open a court, survive the Marked blowback across a cycle |

---

# **11. How the Two Layers Are Meant to Feel Together**

A disciplined run: you turn a courtier's client into a **Watcher**, and the strategic map starts filling in toward a corruption bloom you'd never have staged toward blind. The Watcher previews the Astrologer's next target; you plant a **Saboteur** and delay the tick one lunation — exactly long enough to expedition in. You **fence** the Cutout's take to the Concord, bank Favor, and hold it. Late cycle, a court you cannot win diplomatically has a Chancellor who hates you; you spend the hoarded Favor on an **Assassination**, the office goes vacant, standing lurches, and you Broker the Compact through the gap — but Marked hits 9, and the knife the Astrologer buys is already in the post. You spend your *last* Favor to outbid it, arriving at the Grand Conjunction with an empty shadow-wallet and a scarred network, having bought exactly enough tempo to win. The espionage layer's fantasy is that last sentence: **you did not out-diplomat the world, you out-maneuvered it in the dark, and it cost you clean hands to do it.**

The failure fantasy is equally intended: a network burned faster than it yielded, Favor bled on contracts that attached strings you didn't read, Marked maxed with nothing to show — the shadows took your money and your people and sold your route to the enemy. Both outcomes must be reachable from sensible play, or the layer is either free power or a trap, and both are §14 kill-criteria.

---

# **12. Initial Tuning Values (Playtest Starting Points, Not Commitments)**

Cover 0–10 (turned start 6 / Concord-bought 9 / captured 3); Access 1–3, +1 per 3 lunations survived, cap 3; burn roll = Spymaster Influence (+1 if court Exposure ≥4) vs Cover, damage 1–3 by role, Handler −1..−2; Watcher chart radius 2; Saboteur delay 1 lunation, Concord Sabotage delay 2; Marked 0–10, gains ~+1 Tier-B contract / +2 Theft/Extraction / +3 Assassination, decay −1 idle, thresholds 3/6/9; Undercroft informant caps 2/4/6 and contract caps 1/2/3; corruption mitigation cap = 1 per kingdom-lunation, 2 map-wide (§4). **The three most sensitive numbers, tuned as a set: burn-roll weight (network survival), Concord Favor conversion rate (dirty-power affordability), and the corruption-mitigation cap (doomsday-clock pressure).** None of these may be tuned in isolation.

---

# **13. Open Rulings Needed**

**STATUS: all seven ruled by the designer — every recommendation below was adopted as written.** Kept here as the decision record; the "Recommendation" column is now the ruling. Where implementation deviated in detail, §15 notes it (rulings 2, 6, and 7 have build caveats).

Resolved-where-possible per house style; the following are the decisions I could not settle without your call. My recommendation leads each.

| **#** | **Decision** | **Recommendation (confidence)** |
| --- | --- | --- |
| 1 | **Informants: strictly NPC assets, or can a companion be seconded as one?** | Strictly NPC (high). Letting companions be informants recreates the envoy sacrifice and collapses the layer distinction that §2a is built on. Companions only *handle* networks. |
| 2 | **Corruption mitigation cap** (§4) | One source per kingdom-lunation, two map-wide (moderate). This is a guess at the number; it is the clock's life and must be co-tuned with CorruptionTickInterval and envoy deflection — treat the three as one dial. |
| 3 | **Does selling a court's secret to the Concord burn that court immediately, or only if traced?** | Only if traced, with trace probability rising as Marked rises (moderate). Immediate burn makes the sell side unusable against courts you still want; never-burn makes secrets free money. |
| 4 | **Does Marked persist cross-cycle?** | No — clears at cycle end like Exposure and the ledger (moderate). Persisting it punishes cross-cycle experimentation and fights the loop fiction. Only exfiltrated-Access renown carries over. |
| 5 | **Astrologer-against-you contract lethality** (Marked 9) | Arc-scar for a cycle, not permanent companion death (high). Permadeath from a strategic-layer meter is a feel-bad the game doesn't otherwise do; scar-and-return matches the Imprisonment/defeat-deposit precedent. |
| 6 | **Is the Concord broker always Opportunist, or archetype-varied per node?** | Varied per node from seed (low) — an Idealist-broker Concord node (zealots, not mercenaries) is good texture, but v1 can ship all-Opportunist and add variety later. Non-blocking. |
| 7 | **Undercroft as new building vs. folding espionage caps into the Embassy** | New building (moderate). Folding into Embassy is cheaper but makes the diplomacy building mandatory for espionage and muddies both economies; the one-new-building cost buys a clean second spine. Reversible if campus real-estate is tight. |

---

# **14. Kill-Criteria (When to Cut This System)**

Stated up front so the system is falsifiable, per the empirical-balance pillar. Cut or rebuild the layer if, after E2/E3 playtests:

- The Informant Network reads as "Gather Intelligence on a timer" — i.e., players plant-and-forget and never manage Cover, because burn rolls are too weak to create decisions. (Fix: raise burn weight. If unfixable without making networks unviable, the layer has no subgame and should be cut.)
- The Veiled Concord reads as "a shop" — i.e., Marked never bites, so dirty options are free power. (Fix: strengthen Marked 6/9. If players still never feel the cost, the moral economy has failed.)
- The corruption-mitigation stack lets a careful player *stop* the clock rather than buy tempo against it. (Non-negotiable failure: the doomsday clock is the game's spine; if espionage defuses it, espionage is wrong, not the clock.)

---

# **15. Build Status — Implemented E1–E6**

**STATUS: all six build phases (E1–E6) are implemented in the live repo** (`Scripts/Systems/Campaign/`), authored against the code as shipped (not snapshots). The §9 prerequisites were checked against ground truth and found already met: the C5 debt was paid in code before this build (`ImprisonedEnvoy` uses stable `PrisonX/PrisonY`, `StandingPenalty`/`HeraldReport` exist, `CouncilSaveAssert` present), so E-phases proceeded directly. All seven §13 rulings adopted. Two new save structs total (`InformantState`, `ConcordContract`), everything else reuse — the two-structs-max rule held.

Build verification is desktop-local (`dotnet build`) and the per-phase debug walkthroughs on the Campus guild panel; treat green as unverified until built, per the Cowork-outputs discipline.

## 15a. Files

New: `ShadowState.cs` (the two structs + `ShadowVocab` + `ConcordStandingBand`), `ShadowTick.cs` (the tick + acquisition + sabotage/assassination effects), `ShadowMarket.cs` (sell/commission/outbid), `ShadowOps.cs` (active verbs: saboteur strike, forge echo, exfiltrate), `ConcordGenerator.cs` (node scatter), `ConcordDebug.cs` (dumps + debug verbs).

Edited (surgical): `CouncilState.cs` (espionage fields on the container), `CouncilTick.cs` (tick steps 4b/4c/5a/6b + public `SeizeEnvoyToGaol`), `CouncilSaveAssert.cs` (round-trips for both structs + fields), `CorruptionSpread.cs` (§4 delay hook + cap), `WorldGenerator.cs` (Concord scatter call), `WorldWindowBuilder.cs` (`Concord → None`), `PoiKind.cs` (`Concord` appended), `CampusGuildPanel.cs` (debug buttons).

## 15b. Per-phase status

| Phase | Status | Notes |
| --- | --- | --- |
| E1 — Data + generation | Done | Structs, `CouncilState` fields, round-trip asserts, Concord node scatter (own RNG — world output bit-identical), broker archetype derived per node. |
| E2 — The network (passive) | Done | Watcher/Cutout yields, Access ripen, counter-intelligence burns; court-embedded burn spikes court Exposure and trips the existing Scandal edge-check. |
| E3 — The Concord (marketplace) | Done | Sell a secret (trace→court/Marked), buy Plant/Intel/Theft, Marked meter + thresholds 3/6 (Sold-Out = Astrologer's extra burn roll). |
| E4 — Sabotage & the clock | Done | §4 corruption-delay + cap; Concord Sabotage (siege-break / delay); Saboteur role (passive erosion + active strike); Cutout false echo. |
| E5 — The shadow war | Done | Marked-9 Astrologer contracts (seize/burn), the outbid, court blackmail of the guild's dealings, Concord Extraction reusing the stable-coord release. |
| E6 — Tier C + the spine | Done, one seam | Undercroft caps, Assassination (Inner + Undercroft III), Exfiltrate + Hall-of-Records renown. Interactive broker negotiation deferred — see §15d. |

## 15c. Deviations from the spec (deliberate, no-stubs discipline)

1. **Saboteur moved from E2 to E4.** Patrol pressure is `KingdomState.BorderPressure` (inter-kingdom war, not guild heat) and has no per-kingdom guild scalar; the Saboteur's real hooks (warfront `Advance`, corruption delay) are E4, so registering it in E2 would have been a stub. E2 shipped Watcher + Cutout; the Saboteur landed in E4 with its verbs.
2. **Concord Sabotage moved from E3 to E4** for the same reason — its siege/corruption effects belong with the E4 clock work. E3 shipped Plant/Intel/Theft.
3. **Marked-6 "patrol pressure" → the Astrologer's extra burn roll.** No wired per-kingdom patrol-density scalar exists (council Hostile's "+1 patrol pressure" is design intent, not a number). The extra counter-intelligence roll is the clean, real hook; literal patrol density awaits that scalar being built.
4. **Prisoner/contraband sales deferred.** No capture system exists yet, so the sell side fences **secrets** only.
5. **Ruling 6 (broker archetype):** pinned to Opportunist in v1 with a one-line seam to vary per node — shipped the simple case rather than dead variety.
6. **Ruling 7 (Undercroft):** fully built. `Data/Buildings/undercroft.json` is authored (auto-registered by the directory scan; backfilled at tier 0 by `EnsureBuildings`; buildable at 150/300/500 gold), and all three tiers' effects are implemented: informant/contract caps (2/1 → 6/3), the II free-handler mitigation, the III Assassination unlock, and the III −1 Marked-per-contract discount. Still debug-settable via `Undercroft +1` for testing.
7. **Hall of Records renown** gated on the existing records building (`scriptorum`) since no `hall_of_records` building exists; banked in `EternalLedger.DeedCounts` under a namespaced key (the MarginaliaService pattern), so no new save field.
8. **Fixed in passing:** the E2 Library ripen rider read building id `"library"` (nonexistent) — corrected to `"arcane_library"`.

## 15d. The one open seam — interactive Tier C

Per §8, Assassination and the major Broker are meant to play as **interactive negotiation encounters** with Concord-state preload. The council's own Tier-C launcher (Broker the Compact) is **not built**, and there is no programmatic negotiation-with-callback entry point. Building one solely for espionage would fork shared, unbuilt infrastructure — so Assassination (and Extraction) resolve automatically for now, and a documented SEAM in `ShadowMarket.CommissionAssassination` marks exactly where the interactive path plugs in once the council builds the shared launcher. This is the only intended gap in E1–E6.

## 15e. Tuning — where the knobs live

All starting values are constants in `ShadowVocab` (`ShadowState.cs`) and a few `private const` blocks in `ShadowTick.cs` (burn weights) — empirical, tune in place. The three most sensitive, tuned as a set (§12): the counter-intelligence **burn-roll weight** (`ShadowTick.Burn*`), the **Concord Favor conversion rate** (Cutout yield vs. contract costs in `ShadowVocab`), and the **§4 corruption-mitigation cap** (`CorruptionSpread.MaxDelaysPerLunation`). The mitigation cap is the doomsday-clock guarantee and the §14 non-negotiable — do not tune it in isolation; when C6 envoy deflection lands it must share this cap.
