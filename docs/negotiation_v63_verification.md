# Negotiation v6.3 — Verification Checklist

*Created 2026-07-31. Covers every negotiation change since the v2 rebuild that has never been compiled or played: the endgame-receipt pass, the readability pass, the fairness pass (v6/v6.1), and the final layout scheme (v6.3). Code state confirmed at HEAD `331b910` — `MakeTokenChip` absent (v6.3 marker), `NegotiationTuning.PatienceFloorOverPool = 3` present.*

**Nothing below has been run. The Godot build is the arbiter.**

---

## 0. Build gate

- [ ] Project compiles. If it does not, the negotiation files are only one of several unverified batches at this HEAD — bisect by session (07-29 card/post-cast batch vs 07-30 negotiation batch), not by file.

---

## 1. Layout — the v6.3 scheme

The page has **one** flexible region (the portrait|log strip) and **one** scrollbar (inside the conversation log). Everything else is fit-to-content.

- [ ] **L1** — Whole screen fits: `Pass` / `🤝 Shake Hands` / `Walk Away` all visible and clickable, with the live `Signs now for: +Xg · +Y rep · ★★★☆☆` preview inline beside them.
- [ ] **L2** — The conversation log is the only scrolling element. The clause strip does not show a vertical scrollbar; no clause position label is clipped.
- [ ] **L3** — The intent tell sits in the log's header row (tiny, amber, left), with the "Table details" toggle at right. There is no static "THE CONVERSATION" label.
- [ ] **L4** — Spoken-move rows are compact: 44px chips, token name folded into the effect line (`CHARM · They're wavering: …`). A full 8-token pool renders as 4 rows.
- [ ] **L5 — the known floor.** With a full 8-token pool *and* 2-line clause descriptions the page needs ≈1000–1050px of game-viewport height. At 1080p with a typical 5–6 token pool this is fine. Confirm on the machine you actually play on. If it clips, the accepted release valve is re-enabling vertical `Auto` + `Expand` on the moves list only — not adding a second scrollbox.

## 2. Legibility — the fairness pass

- [ ] **G1** — At table open, a card shows `✎ FINE PRINT COMING` on the **lightest movable clause** (not the heaviest). This is the Guile retarget; if it lands on your best clause, the retarget did not take.
- [ ] **G2** — Pull a clause to 0 while their Resolve is up → `⌖ IN THEIR SIGHTS` appears, and their pull lands **exactly there**. Baiting their Resolve is supposed to be a visible, playable line.
- [ ] **G3** — The intent tell never lies. `PredictNpcAction()` is now the single source both `NpcTurn` and the Embassy line consume; if the tell and the move ever disagree, that unification broke.
- [ ] **G4** — Run their Resolve dry: a log line fires on the transition to 0, their pool chip greys to 30% alpha with a "spent" tooltip, the intent reads *press your advantage*, and pulls stick. Refill their Resolve with an Offering → the pool-empty signal re-arms.
- [ ] **G5** — Embassy tier 2 upgrades the category-only soft intent to the precise clause-naming briefing.

## 3. Balance — does it feel fair *and* profitable

Monte Carlo (20k runs/row, sim mirroring the patience floor and Guile retarget) predicted: informed play beats naive on every setup; naive ≈ 20% of table ceiling; skill roughly doubles it; Hostile still loses money; walking away is still 0; informed hits the 3★ skilled-median target.

- [ ] **B1** — Commander and Survivor tables last **≥ pool + 3** exchanges.
- [ ] **B2** — Average-skill runs sign **modestly positive** in Strained and **clearly positive** in Cordial with dry pools.
- [ ] **B3** — Deliberately careless play still loses money. If it doesn't, the fairness pass overshot.

## 4. Table audit follow-ups — measure, don't pre-tune

The `Data/Negotiations/*.json` audit (2026-07-31, all 8 tables) found **no unwinnable table**. The "Commander @4 / Survivor @3" cases the fairness pass was built to rescue were simulation constructs, not shipped data. Effective patience = `max(BasePatience, Resolve + Guile + 3)`:

| Archetype | Effective patience | Note |
|---|---|---|
| Scholar | 10 / 9 | longest |
| Merchant | 8 / 8 | |
| **Opportunist** | **8** (floored up from 6) | **identical to Merchant** |
| Commander | 7 (floored up from 6) | |
| Idealist | 7 | |
| **Survivor** | **6** | **exactly at the floor** |

Two open questions. **Both are deliberately unchanged** — the numbers were validated by a Monte Carlo that no longer exists in the repo, and re-tuning validated balance data from static reasoning is how this project accumulated its verification debt in the first place. Answer them by playing, then change one value.

- [ ] **T1 — Does Opportunist read as a distinct archetype from Merchant?** It has the largest NPC pool in the game (Resolve 2 + Guile 3 = 5), so the floor lifts it from 6 to 8 — the same table length as Merchant. It still differs in start tension (5 vs 4), Guile (3 vs 2), Poise (0 vs 1), and hidden terms (2 vs 1). **If it reads as "Merchant with sharper fine print," that may be correct and the archetype note should change.** If it should genuinely feel *impatient*, the minimal fix is to lengthen Merchant (base 8 → 9, both merchant tables) rather than shorten Opportunist — shortening it below the floor reintroduces exactly the unwinnable-positive case the floor exists to prevent.
- [ ] **T2 — Is Survivor punishing or tense?** It is the tightest table in the game, sitting exactly on the floor at 6, and it is authored that way (base 6, pool 3). Tense is the intent. If it reads as unfair, raise base to 7 — one value, one table.

## 5. Content gap

- [ ] **C1** — `Data/Negotiations/dustreach_commander.json` does not exist. `NegotiationEncounterLoader.PickForTerrain` silently falls back to `generic_merchant`, so the Dustreach region has a merchant where a commander was designed. First logged 2026-07-15; still open. Authoring it is a 10-minute job against `frontier_wilds_commander.json`.
- [ ] **C2** — Coverage is 8 tables / 6 archetypes / **one** Commander table / two faction-bespoke, against 15 regions. Tracked as Phase G item **G5** in `docs/build_order_v4.md`.

## 6. Pre-existing, known, not addressed

- Mouse-wheel over a chip or button won't scroll the actions `ScrollContainer` (minor).
- The 36 negotiation portrait PNGs (6 archetypes × 5 stances + 6 bases) are unpainted; `NegotiationPortrait` falls through to the styled placeholder. Confirm the fallback renders rather than crashing.
- Table-scene backdrops (6), chip objects + tween pass, term-slip art, and the tension object (rope-over-candle vs balance scale — **still undecided**) are all unbuilt.

---

## 7. Tooling regression worth naming

`negotiation_sim.js` — the Monte Carlo harness that produced every balance number in `claude/negotiation_tuning_v1.md` and the 07-30 fairness validation — **is not in the repo.** It lived in a sandbox session and was discarded with it. Every published balance figure for this system is therefore currently unreproducible.

Options, in order of preference:

1. **Rebuild it as a committed tool** (`tools/negotiation_sim.js`), ported from `NegotiationState.cs`, so future tuning is reproducible. Carries the same mirror-drift hazard the fairness pass just eliminated inside the game — mitigate with a fixture test that runs N seeded tables through both the sim and a C# debug harness and asserts identical outcomes.
2. **Retire simulation entirely** and tune from `NegotiationTelemetry` CSV (`user://negotiation_telemetry.csv`) against real play. Slower, but it measures the thing you actually care about, and the sim's own caveat was that bots understate human skill and don't model companion or building tokens.

Do not leave it as-is. A balance system whose numbers cannot be reproduced is one refactor away from being un-retunable.
