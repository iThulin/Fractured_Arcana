# Session log — 2026-08-13 — K4: loyalty hooks, Trusted perks, ArcStage signatures

Same Cowork session as the K3 log (`session_log_2026-08-13_k3_hiring_halls.md`).
**NOT COMPILED — static verification only** (brace/paren delta vs HEAD = 0 on all
nine files; symbols grepped to declarations). Build is the arbiter.

## THE STANDING CAVEAT — fresh-authored values

`companion_item_systems_v1` (the locked delta table, Trusted perk matrix, and
signature matrix) could not be located in the repo or project knowledge. Magos
ruled: **author fresh as K4 starting values.** Nothing numeric in this session
is recovered canon; everything is tunable under the empirical pillar. The
tables live in exactly three places: `LoyaltyEvents` constants,
`CompanionPerks` constants, and the ten `sig_*` stances in `StanceRegistry`.

## New files

| File | What |
|---|---|
| `Scripts/Data/FeatureBuilders/LoyaltyEvents.cs` | The delta table + appliers: extraction homecoming +1, heroism (downed in a WON fight, walked out) +2, wipe survivor −2, death ripple −8 roster-wide with Sworn dampened to −4 (counterargument logged in the constant's doc). Clamps 0–100, one console line per movement. |
| `Scripts/Data/FeatureBuilders/CompanionPerks.cs` | Trusted personality perks, trait-keyed so §5c procedurals get them free: Stoic +1 armor · Reckless +1 damage · Curious +1 move (all at unit spawn, both branches) · Loyal +2 pool contribution · Cunning +10g at extraction. No-ops below Trusted. |

## Signatures (ArcStage 4, martial — v1 rules honored)

- `StanceDefinition.IsSignature` (new field) + **ten authored signature stances**
  (`sig_{fighter|ranger}_{trait}`) — elevated versions of the base kit with
  personality-shaped identity, "✦" display suffix.
- `StanceRegistry.SignatureIdFor(c)` — Class × Trait matrix id, or the authored
  override `Companion.SignatureStanceId` (new field, JSON `signatureStanceId`,
  template-copied + backfilled in `CompanionRoster.EnsureRoster`).
- `StanceRegistry.EligibleSignature(c)` — THE single rule site: ArcStage ≥ 4,
  not Wary (v1: "disabled at Wary"), martial only (arcane deferred — v1 lock).
- **Grant is derived, never stored**: appended to `unit.AvailableStances` at
  spawn (CombatManager, after the trained-stance loop). Never in
  `TrainedStanceIds`, never trainable (`CampusTrainingPanel` skips
  `IsSignature`), and **destroyed-on-permadeath costs zero code** — derived
  state, the dead never spawn.

## Hook sites (surgical)

- `ExpeditionManager.Extract()` — `LoyaltyEvents.OnExtraction` BEFORE
  `ApplyExtractionCheck` (which resets `ExpeditionHP`, the heroism evidence);
  Cunning Finder's Fee added to `GoldEarned` before banking.
- `ExpeditionManager.ComputePartyBaseHP()` — `CompanionPerks.PoolBonus` beside
  the existing loyalty bonus; readout line extended.
- `CompanionInjurySystem.ApplyWipe()` — K2's IOU retired: survivors take the
  wipe delta, deaths ripple the roster. **Ordering fix baked in:** all rolls
  resolve against walk-in loyalty; deltas/ripples apply after the loop, so a
  mid-loop ripple can never strip a not-yet-rolled Sworn companion's −10 armor.
  Also: `+ using System.Collections.Generic` (the file imported only Godot +
  System.Text.Json — the marginalia lesson, caught statically).
- `CompanionDefinition` — `GetLoyaltyTier` now delegates to new
  `static TierOfValue(int)`: one threshold site, per its own K1 comment
  ("never re-derive"), which LoyaltyEvents' tier-change reporting needed.

## First-launch checklist

1. Build. (`List<>` using fix and the ten new stances are the likely first
   complaints if anything.)
2. Debug a companion to `ArcStage = 4`, loyalty ≥ 25, field them: spawn log
   prints `[Signature] <name> fields <stance> ✦`, stance switcher shows it.
   Drop loyalty below 25 (Wary): signature gone next spawn.
3. Training tab: no `✦` stances offered for learning.
4. Extract from an expedition: `[Loyalty] <name> +1 → … (came home)` per
   fielded companion; anyone stabilized at 0 also gets `+2 (downed winning…)`;
   a Trusted Cunning fielded adds `+10g` before banking.
5. Force a tier-2 wipe: survivors log `−2 (survived the wipe)`; on a death,
   the whole roster logs the ripple with Sworn at −4.
6. Save/load: `signatureStanceId` round-trips (rides the existing Companion
   serialization — no new save struct this session, no version bump).

## Deferred / next

- **K4 leftover, small**: dossier + campus cards don't yet SHOW the perk or
  signature line — display-only, fold into the K5/Muster UI pass.
- **K5**: fitness vector (still blocked on the CouncilVocab archetype-casing
  verification), favor retainers, Unite adepts, displacement refugees.
- Rescue-arrival "live arc" beat (K3's logged deviation) still belongs to arc
  content work, not this session's hooks.
