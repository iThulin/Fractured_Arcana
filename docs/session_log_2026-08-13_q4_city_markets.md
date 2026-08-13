# Session log — 2026-08-13 — Q4.1: city markets (item track opens)

Fourth increment of the 2026-08-13 session (K3/K4/K5 all verified in-engine).
**NOT COMPILED — static verification only.** Build is the arbiter.

## Q4 reconciliation against the live code (read this before Q4.2+)

The spec's §7c assumes surfaces that DO NOT EXIST at HEAD:

- **No combat item drop tables.** "v1 tier tables carried" — they were never
  built. Item acquisition at HEAD is encounter `itemReward` choices only.
  Combat drops are now a Q4 work item, not a carried assumption.
- **No Enchanter's Workshop building** (13 buildings; no workshop JSON), and
  **no enchant-slot machinery** on ItemDefinition — so blighted items'
  "sealed enchant slot" has nothing to seal. Blighted + Cleanse WAITS for the
  Workshop (Q5 territory).
- **No Auction House, no Merchant Quarter.** Corrupted-archmage relic routing
  ("relics enter the Auction House") is blocked on the building.

Resequenced Q4: **Q4.1 markets (this log) → Q4.2 archmage relics (Overthrow
drop + Unite anniversary; Corrupted leg blocked) → Q4.3 favor→item redemption
→ Q4.4 combat drop tables.** Then Q5 builds the Workshop and blighted/Cleanse
lands there with it.

## Q4.1 — what shipped

- `Scripts/Data/SaveState/CityMarketState.cs` (new, 1 struct): CityId +
  LastRefreshLunation + StockItemIds. Prices are DERIVED at display, never
  persisted. Additive on `CycleState.CityMarkets`, no version bump.
- `Scripts/Systems/Strategic/CityMarketService.cs` (new): mirror of
  HiringHallService — lazy per-lunation refresh, stable FNV seed, sold-out
  stays sold out. Stock: 3 slots (4 at seats), rarity-weighted
  Common 45 / Uncommon 40 / Rare 15. **Legendaries never stocked** — the
  Auction House stays the only Legendary venue (guild_campus rule), even
  though it isn't built yet; the market must not pre-empt it.
  Pricing: book value ×125%, Steward regard −5%/point toward a 100% floor
  (never below book). `TryBuy` → gold → `Armory.AddItem`.
- `CityServicesHost.BuildMarketSection` — live shelf UI: rarity-colored rows
  (`UITheme.RarityColor`), descriptions, priced Buy buttons, in-place
  repopulate; purchase also refreshes the hall's gold readout.

DEFERRED (logged): §7c terrain-flavored pools (desert→Trinket, tundra→Armor)
need per-kingdom terrain pool data that doesn't exist — stock is
rarity-weighted only until region content work (Phase G).

## Q4.2 — archmage relics (same day)

Eight authored Legendary relics, `Data/Items/relic_<archmageId>.json`, one per
archmage, every passive a LIVE dispatching key (no new handlers, no stubs):

| Archmage | Relic | Kit |
|---|---|---|
| wenna (Adept) | The Scholar's Marginalia | FirstCardCostReduction 1, +1 maxMana |
| aurel (Arcanist) | The Second Draft | RestoreManaOnTurnStart 1, +4 HP |
| astrologer (Chronomancer) | Mantle of the Borrowed Hour | StartCombatWithShield 6, +1 armor |
| hess (Druid) | The Witness's Green Way | pathfinder(Forest) 1, +1 speed |
| joren (Elementalist) | Emberheart of the Vessel | FireSpellBonusDamage 2, +1 spellDamage |
| namer (Enchanter) | The Name, Spoken Rightly | regen_aura 2 |
| conductor (Necromancer) | The Conductor's Cadence | apply_bleed onAttack, +2 attackDamage |
| engineer (Tinker) | The Engineer's Bulwark Frame | DamageReductionPerHit 1, +2 armor, +3 HP |

Routing (`Scripts/Data/FeatureBuilders/ArchmageRelics.cs`, new):

- **Overthrow** → granted at the Step-9 resolution return in ExpeditionManager
  ("torn from the fallen seat" toast). Idempotent: unique-owned is enforced by
  checking `Armory.OwnedItems.DefinitionId` — the Armory is the truth, no flags.
- **Unite** → `CampaignState.UniteLunations` (new additive dict) stamped in
  `OnArchmageUnited`; `TickUniteAnniversaries` on the lunation tick grants the
  gift when the unite MOON returns. **RULING (logged)**: spec's "first
  anniversary lunation" is unreachable in a 12-lunation cycle (a 12-lunation
  year outlives the Conjunction); the calendar's moon names cycle every 8, so
  anniversary = the unite moon's return, 8 lunations later — unite by lunation
  4 or the gift never comes. Patience priced in, payoff possible. The alliance
  must still stand (disposition still Allied) when the moon returns.
- **Corrupted → Auction House**: BLOCKED on the unbuilt building, unchanged.

Relic checklist additions: overthrow an archmage → relic toast + Armory entry,
repeat via debug → no duplicate; unite by lunation ≤4 → relic arrives 8
lunations later in the console log; unite later → never arrives (correct).

## Q4.3 + Q4.4 — favor gifts and combat drops (same day)

**K5 DEFECT FOUND AND FIXED**: `CallInIneligibility` blanket-refused every
Major favor ("cannot be called in from the field yet") BEFORE `ExecuteCallIn`
ran — the K5 Arcane-Major retainer was unreachable dead code. Worse: no mint
anywhere ever set `IsMajor = true`, so Major favors did not exist in play at
all. Both fixed:

- **Majors now mint**: `MintPetitionFavor` mints MAJOR when the creditor is
  the court's Patron (`PatronCourtierId`) — patrons owe major debts. First
  and only Major-minting path; everyone else stays minor.
- **Majors now redeem**: the ineligibility gate passes Majors through
  (territory + expedition checks still apply; minor-effect type gates
  skipped). `ExecuteCallIn`: Major non-Arcane → **the courtier's gift** —
  an item slot-flavored by favor type (Military→Weapon, Passage→Armor,
  rest→Trinket), Rare preferred / Uncommon fallback, never Legendary.
  Arcane Major stays the K5 retainer.

**Q4.4 — combat drop tables** (`CombatLootTable.cs`, new): the primary item
faucet finally exists. On combat victory (ExpeditionManager return, beside
the gold/splinter payout): drop chance 20/28/36% by territory tier 1/2/3;
rarity weights 60/35/5 → 40/45/15 → 25/50/25 (C/U/R); **Siege rolls twice
and skips the chance gate; Boss skips the gate** (spec's "elite double-roll"
mapped onto EncounterTier.Siege). Legendary never drops (relics have their
own routing; Auction House rule stands). Toast + run-log line per drop.

Checklist additions: win an ordinary tier-1 fight repeatedly → items ~1 in 5,
mostly Common; win a Siege → exactly 1–2 items every time; court a Patron →
petition favor mints as "major" in the ledger; call it in inside territory →
courier toast + Rare/Uncommon item in Armory, favor consumed.

## First-launch checklist

1. Build.
2. Enter an NPC city → services → Market shows 3–4 rarity-colored items with
   prices ≥ book value; seat cities show 4.
3. Buy one → gold falls, item in campus Armory, shelf row gone; close/reopen
   menu → same remaining stock (no re-roll).
4. Lunation turn → fresh shelf.
5. Positive Steward regard at that kingdom → visibly lower prices (toward,
   never below, GoldValue).
6. Save/load mid-stock → shelf identical.
