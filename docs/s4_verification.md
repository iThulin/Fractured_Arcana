# S4 Verification — Acquisition, Prep UI, Scrolls, Identify (2026-07-16)

Spec: `overworld_spell_system_v1_1.docx` §4a / §8a / §11 / §14-S4.
Files: 15 modified + 1 new (`Scripts/Systems/Overworld/Spells/SpellAcquisition.cs`) + this doc.

Boot line to watch: `[OverworldSpellRegistry] Loaded 36 overworld spell definition(s).`
(unchanged from S3 — S4 adds no definitions; it implements the last one, `identify`.)

## Checks

**Un-seeding + grandfathering**

1. **Existing save grandfathered**: load the current cycle — the four Generals are
   still in KnownSpellIds/PreparedSpellIds (they came from the save file, not the
   seed). Nothing lost.
2. **Fresh cycle starts empty**: begin a new cycle — Grimoire panel shows only the
   school innates; the deploy dialog's Prepared line reads `0/2` with the
   "no spells learned yet" hint; the Scriptorium lists only the school's innates.

**Launch-prep UI (deploy dialog)** — S4 exit surface

3. Deploy dialog shows: Innate line (2 spells), Prepared toggle buttons (one per
   known spell, name + base cost), Scrolls line when any are held.
4. Toggling respects the slot cap: with 2 prepared, clicking a third un-presses
   itself and nothing changes. Header count `(k/2)` tracks live.
5. **Adept third slot** (§7h Versatility): an Adept cycle shows `(k/3)` and accepts
   a third spell. Non-Adept stays 2.
6. Prepared selection persists: prepare, cancel the dialog, reopen — selection
   held. Deploy, extract, redeploy — still held (rides the cycle save).
7. Companion-granted line lists fielded off-school companions' schools with
   `(+1✦ off-school)` — or `(no tax — Adept)` on an Adept.

**Lore-POI drops (§11)** — S4 exit criterion (learn persists)

8. Resolve Narrative POIs until one teaches (~30%/POI): info line names the spell;
   it appears in the deploy prep list at next launch. **Save and reload
   mid-expedition after learning — still known.** (KnownSpellIds rides CycleState.)
9. Terrain flavor: a Volcanic-tile lore POI that drops teaches Ember Ward if
   unknown (Forest/Swamp→Thornwall, Ruins→Pallid Bargain, Mountain/Hills→Fulminant
   Charge/Attuned Recall, Grassland/Road→Beguile, Snow/Tundra→Stasis Snare,
   Arcane Ground→Ley Tap); flavored pool exhausted → falls back to any unknown
   learnable (Generals included).
10. Authored seam: add `"spellReward": "mending_cant"` to a test encounter choice —
    that exact spell is granted, no roll.

**Negotiation tuition (§11)**

11. Open tables until the term appears (Merchant/Scholar 75%, others 35%):
    *"They offer to teach ‹spell› — theirs if the deal closes cordially."*
12. Accept in **Cordial**: result panel adds "They honor the cordial terms…", the
    overworld toast confirms, spell is known. Accept in **Strained/Hostile**: panel
    says the tuition offer dies with the tone; NOT learned.
13. The same works on a Parley-Compulsion-converted patrol negotiation (same path).

**Scrolls (§8a)** — S4 exit criterion (zero-Essence cast, consumed)

14. Scriptorium (campus → Expedition tab): scribe Mending Cant for
    `max(30, 25×2) = 50g`; gold deducts, `×1 held` shows, deploy dialog lists it.
15. Afield: the Grimoire panel's Scrolls section shows `Mending Cant ×1 · 0✦`.
    Cast it — **Essence pool unchanged** (try on corrupted ground: still 0 — the
    surcharge is an Essence cost and scrolls pay none), scroll consumed, info line
    says the scroll crumbles. Cancelled targeting (Esc on a tile-scroll) does NOT
    consume.
16. Scroll caps: a scroll of a once-per-expedition spell (scribe Stasis Snare, cast
    spell then scroll — the scroll is blocked with "already cast this expedition").
    Emulate cannot be scribed (Adept: no Scriptorium row for it).

**Identify (§7b)** — closes the S3 G5 gap

17. Prepare/have Identify (Arcanist innate; others learn nothing — it's innate-only,
    so test on an Arcanist or via companion-grant/debug). Cast on a revealed Combat
    POI within 6: 2 Essence deducts, ARCANE SIGHT panel shows the roster read-only
    (no Engage; Close). Silhouetted or consumed POIs are not valid targets.
18. **Pin holds**: walk onto that POI — the scout report shows the SAME composition
    Identify showed. Win the fight; a later Identify of another site re-rolls fresh.
19. Pin survives a combat round-trip (identify site A, fight site B, then walk to
    A — still the identified roster). Pins clear on extraction/failure/new deploy.

**Speak with the Fallen drop**

20. Cast repeatedly at Remnants/ruins across expeditions: ~20% of casts append
    "the dead also yield the working of ‹spell›" and teach it.

## Predictions (uncertain — check, don't assume)

- P1 **Grimoire panel height**: the Scrolls section grows the bottom-left panel
  further; with many scrolls + many spells it may collide with LedgerPanel. Fixed
  list was already flagged in S2 — collapse polish still pending.
- P2 **Deploy dialog size**: widened to 560×430. A 12-spell known list in the
  flow container may still crowd it on small windows.
- P3 **Identify pin vs full restart**: pins are static (scene-swap-proof) but NOT
  serialized — quit-to-desktop mid-expedition and reload re-rolls a previously
  identified site. Accepted limit, documented in code; promote to GrimoireState
  serialization only if it bites in play.
- P4 **Cached negotiation data**: the loader caches shared instances; the tuition
  term is stripped and re-injected per open (idempotent), but a pre-existing
  wrinkle remains — Insight-revealed hidden terms stay revealed on later visits
  to the same encounter id (pre-S4 behavior, unchanged).

## Tuning knobs (all in SpellAcquisition.cs)

NarrativeDropChance 0.30 · SpeakFallenDropChance 0.20 · DealOfferChanceKeen 0.75 /
Other 0.35 · scroll gold = max(30, 25×EssenceCost).
Watch: 50g Mending Cant scrolls vs ~15-50g POI income — if scroll healing starts
substituting for the Essence pool, raise ScrollCostPerEssence first (§8a lever).
