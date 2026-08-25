# Session log — 2026-08-23 — Mobile Fortress F2: Hull

Implements **F2** of `mobile_fortress_expedition_spec_v1.md` (v1.1), on top of F1.
Static-verified only (no .NET SDK here): brace/paren/bracket balance = 0 on every
edited file; call-site audit. **You compile + playtest in Godot** before F3.

## The key finding that shaped F2

The expedition `CurrentHP`/`MaxHP` pool is **already** the pure overworld-attrition
pool. The combat-return code (line ~2270) documents that `router.DamageTaken`
"arrived as 0" ever since the K2.5 per-companion carried-HP system landed — combat
runs on companion HP + injury rolls, and does **not** drain this pool. So §7.3
("combat damage does not touch Hull") was already true, and §2.1 ("Hull replaces
party expedition HP as the sortie's health") is a faithful relabel of this pool.

**Therefore F2's real substance is not a rename — it's the Hull-0 consequence.**
Previously an overworld pool-0 called `FailExpedition` (spoils FORFEITED). The spec's
Hull-0 is "damaged, never lost": the castle limps home with **spoils kept**. That is
exactly the existing `EmergencyExtract()` (+1 straggle lunation, §5b tier-2 injury
roll, `BankResources(extracted: true)`). F2 reroutes to it.

## Edits — `Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs`

- **`Hull` / `MaxHull` alias properties** over `CurrentHP`/`MaxHP`. Serialized
  backing keeps its name (§10: no rename, no migration). Later increments read the
  clean name (F5 Reinforced Keel `+25% MaxHull`, F6 Heart HP `= MaxHull share`).
- **Overworld Hull-0 → `EmergencyExtract(reason)`** at all four hazard sites —
  exhaustion (dry-furnace stride), terrain, corruption, supply-leash — plus the two
  other non-combat pool drains (narrative `HPDelta`, tier-3 cast exposure). Each
  passes a castle-flavored recall reason.
- **Combat defeat stays `FailExpedition`** (line ~2290): a whole-party combat wipe
  is a genuine loss, not a Hull breach (§7.3 keeps the two economies separate).
- **`EmergencyExtract(string reason = null)`**: reason now customizes the banner +
  run-log detail; banner rewritten to castle fiction and carries the **turnaround
  narration** (refuel / restock / unload / repair — §2.2's "why one sortie per
  lunation", no new timer).
- **Labels → Hull**: HUD `HP:` → `Hull:`; hazard messages ("The castle takes N
  Hull damage", etc.); field repairs (rest / outpost / waystation / Steward gift)
  say "Repaired N Hull"; `Extract()` banner gains the dock/turnaround line.

## Other files
- `CampusGuildPanel.cs`: last-run result row `HP:` → `Hull:`.

## Deliberate scoping / interpretation calls

1. **Turnaround line placement.** §2.2 says "the Herald's Report gains one flavor
   line." The Herald's Report is council per-lunation plumbing (F7 territory), so
   for F2 the turnaround narration lives on the extraction banners instead —
   self-contained, same fiction. **Formal HeraldReport line deferred to F7.**
2. **Field Hull repair kept.** Rest (+¼), outpost (full), waystation (+¼), and the
   Steward economic gift (+¼) already restored this pool; since the pool is Hull,
   they now repair Hull in the field. §2.1 frames repair as a turnaround step, but
   these are the pre-existing field-recovery economy — I did not remove them. Flag
   if you want field Hull repair gone (repairs turnaround-only).
3. **Combat still can't touch Hull** — confirmed, not just asserted: `DamageTaken`
   is 0, and I did not route any combat path to Hull.

## F2 acceptance (spec §12) — confirm in-editor
- A swamp/marsh/volcanic crossing reduces **Hull** (HUD reads `Hull: …`), not a
  "party HP" bar; the run log shows `terrain_drain`/`corruption_drain`/`leash_drain`.
- Driving Hull to 0 (e.g. press on past fuel into hazards) triggers the **straggle
  recall** — "+1 lunation", injuries rolled, **gold/splinters kept** — NOT a failed
  run with forfeited spoils.
- Extraction banner shows the dock/turnaround line.

## Next: F3 — Castle types (school-keyed movement signatures + operating quirks).
