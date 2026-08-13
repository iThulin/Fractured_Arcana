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
