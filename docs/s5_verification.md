# S5 Verification — Echoes, Exposure, Pre-read (2026-07-16)

Spec: `overworld_spell_system_v1_1.docx` §6a / §7f / §10 / §14-S5, R15.
Files: CouncilEcho, ExpeditionManager, OverworldSpellManager, GrimoirePanel,
NegotiationContext, NegotiationManager + this doc.

## Checks

**Spellcraft echoes (§6a)**

1. **EXIT CRITERION** — Overt necromantic cast in kingdom territory: cast
   Speak with the Fallen (or Bone Scout) inside a kingdom's borders. The cast's
   info line now ends with the real toast ("Word of this will reach the court
   of … — and it will not please them."). Return, deploy again to advance the
   lunation: the Herald's Report carries "necromancy worked openly in the
   kingdom's lands" landing on the Court Wizard (or an Idealist), Regard −1.
2. Same cast in unclaimed wilds: no toast, no echo (no court to hear).
3. Subtle casts (Scrying Lens, Veil) never echo, in or out of territory.
4. SpellcraftAid: cast Purifying Rite within 2 hexes of a kingdom's OWN
   settlement/seat → positive toast; lands on Court Wizard/Idealist with
   "great warding worked over the kingdom's people", Regard +1. The same cast
   in open country (no civic POI within 2) emits nothing — §6a's "near a
   settlement" clause.
5. Other Overt casts (Force Path, Thornwall, Deploy Waystation, Stasis Snare,
   Fulminant Charge) emit no deed — the §6a table is exhaustive in v1.
6. Political call-in still cancels the worst in-flight negative — a
   SpellcraftTransgression in flight qualifies automatically (generic pipeline).
7. Courier Station (tier ≥ 1) drops the delay to 0 — the echo lands at the
   next lunation tick regardless (calendar only advances on deploy).

**Parley Compulsion end-to-end (§7f)** — second EXIT CRITERION

8. Arm Parley Compulsion, get intercepted in kingdom territory: the
   conversion line now carries the compulsion toast (echo emitted at the
   moment of compulsion, routed Chancellor/Commander).
9. Resolve the table **DealAccepted in Cordial**: return line adds "The
   patrol parts on good terms — that story dies here." Next lunation, the
   Herald's Report shows the story was "quietly buried before it landed."
10. Resolve Strained/Hostile, walk away, or collapse: echo lands normally —
    "the kingdom's own patrol bent by enchantment," Regard −1.
11. The negotiation outcome itself still writes stance/deal echoes exactly as
    any negotiation (fleece them and DealExploit fires alongside).
12. Compulsion in unclaimed wilds: no echo either way (no court).

**Tier-3 exposure (R15)**

13. Stand on tier-3 corrupted ground: the detail card adds the warning line
    ("tier-3 ground: every cast here sears the party (−4 HP)") on every
    castable row, spell or scroll.
14. Cast anything from tier-3 ground: HP drops by exactly 4 (deterministic,
    no roll), info line says the ground answers the working. Applies to
    scroll casts too (exposure is not an Essence cost).
15. Exposure at ≤4 HP kills: "Consumed by corruption mid-casting" fails the
    expedition. Tiers 1–2: surcharge only, no exposure (unchanged).

**True Names pre-read (§7f)**

16. As an Enchanter, hover a revealed Negotiation POI: the readout adds
    "· a Merchant holds this table" (or Commander). Non-Enchanters see
    nothing new.
17. **Pin honesty (G5)**: engage that POI — the counterpart archetype matches
    what the hover showed, across saves and combat round-trips within the
    expedition. (Pins are expedition-scoped statics, same lifecycle and same
    app-restart limit as Identify pins.)
18. Non-Enchanter engagement without any prior hover: unchanged random pick
    (the pin is created at engagement — no behavioral difference).

## Predictions

- P1: The combined info line (result + exposure + toast) is long; it's a
  single-line label. Readable in the log; may clip on screen — candidate for
  the info line becoming two-line if it bothers.
- P2: Aid radius 2 and exposure 4 HP are first-guess numbers — the §6a
  radius is unstated in the doc and the exposure "magnitude tuned with the
  attrition axis" (still an open axis). Both are one-line constants
  (CivicPoiNear's radius arg; Tier3CastExposureHP export).
- P3: The Cordial-burial gate is DealAccepted ∧ Cordial (mirrors tuition).
  Walking away AT Cordial leaves the echo — ruled: you compelled them and
  gave nothing. Re-rule if playtests read it as unfair.

## Status after S5

The overworld spell system is feature-complete per v1.1: S1–S5 shipped,
S6 (Grand Rituals) deferred by R14. Remaining: tuning passes, Grimoire
collapse polish (S2), Identify/True-Names pin serialization if the restart
limit bites, settlement on-arrival hooks (campus rework), §15 #10 (enemy
counter-casting) unruled.
