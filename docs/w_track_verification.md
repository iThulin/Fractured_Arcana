# W-track verification — sliding expedition window + soft leash

Spec: `claude/expedition_window_sliding_v1.md` (project). Session: 2026-07-15.
Files: WorldWindowBuilder.cs · ExpeditionManager.cs · StrategicView.cs ·
OverworldFactionManager.cs · CycleState.cs

## What changed (one paragraph)

The expedition window's hard perimeter is gone. `WorldWindowBuilder` now
streams: `StreamTo()` diffs the loaded hex set against a disc around a new
center — tiles entering radius 12 instantiate from world data, tiles beyond
radius 15 free (hysteresis). The local frame stays anchored at the staging
point forever, so nothing rekeys. `ExpeditionManager` slides the window when
the party drifts ≥3 hexes from its center, applies the W3 supply leash
(+1/+2/+3 HP per step in 3-hex bands beyond 12 hexes of the nearest anchor —
staging tile or any Available staging point; NOT ward-reducible), and rules
extraction: free only ON an anchor, "Emergency Extract" anywhere else =
tier-2 §5b rolls for the whole party + 1 straggle lunation
(`CycleState.PendingStraggleLunations`, consumed with the full lunation tick
by `StrategicView.ProcessPendingStraggle` on return). Silhouetted fringe now
writes `Charted` into the world — expeditions leave a charted corridor on the
strategic map. `HardWindowMode` export restores the old fixed window for A/B.

## Build checks (in order)

1. **Compiles clean.** New symbols: `WorldWindowBuilder.StreamTo/LocalOf/WorldOf`,
   `ExpeditionManager.{OnExtractPressed,EmergencyExtract,RecenterWindow,
   SupplyDistanceAt,SupplyBandAt,OnSupplyAnchor}`, `StrategicView.{RunLunationTick,
   ProcessPendingStraggle}`, `CycleState.PendingStraggleLunations`.

2. **Baseline parity (HardWindowMode = true).** Set the export true on
   ExpeditionScene's manager, deploy: identical to the old build — 469-tile
   window, wall at radius 12, "Extract" always free. Confirms the streamer's
   initial Build is behavior-preserving.

3. **The slide (HardWindowMode = false).** Deploy, walk one direction 20+
   hexes. Expect: no wall — new terrain streams in ahead; with DebugMode on,
   `[Window] Slide → (col,row): +N/−M tiles, K live.` prints every ~3 steps;
   K stays roughly constant (~470–720, never grows unbounded). No hitching at
   slide moments (adds are ~40–90 tiles).

4. **Supply leash.** HUD shows `Supply: in range (d/12)` near staging. At
   distance 13+ from every anchor: `Supply: N beyond the line (−1 HP/step)`
   in warning color, HP drains +1/step (bands 13–15 → 1, 16–18 → 2, 19+ → 3),
   info line names the band. Wards must NOT reduce it (equip wardstone/cloak
   and compare). Return inside 12 → drain stops, "back within your supply line."

5. **Extraction rule.** On the staging tile the button reads "Extract" and
   extracts as before. One hex off it reads "Emergency Extract" and pops the
   confirm; confirming rolls §5b at tier 2 (console `[Injury] Wipe rolls —
   emergency extraction (territory tier 2 …)`), banks gold/splinters, prints
   the straggle banner. Secure an Outpost mid-run → standing on it reads
   "Extract" (anchors extend the line).

6. **Straggle debt.** After an emergency extract, return to the strategic
   map: `[Calendar] The party straggles home — a lunation passes` + council/
   corruption/infirmary tick logs; calendar readout one lunation later; save
   round-trips the debt (quit between extract and return, reload — the
   lunation still charges).

7. **Combat round-trip beyond the base disc.** Walk 16+ hexes out, engage a
   combat POI, win, return: party restored on the same tile, window slid to
   it, patrols/fog/POI state correct. (This exercises the RestoreFromCombat
   recenter — the one crash candidate if wrong: party init on a missing hex.)

8. **Unload/reload illumination.** Walk 20 hexes out, walk back: tiles that
   unloaded behind you return Revealed (Explored persists in world data), the
   strategic map shows an Explored corridor with a Charted fringe along the
   whole route.

9. **Patrol freeze.** Lure a patrol into pursuit, outrun it beyond the shard
   edge: its token vanishes with its tile; walk back — it's where it froze
   and resumes. Patrols now also spawn in ALL quadrants of the window
   (BuildCandidateList fix), so expect them north/west of staging too.

## Predictions

- P1: live hex count never exceeds disc(15) = 721 (assert via the slide log).
- P2: walking a fixed path with HardWindowMode on vs off (inside radius 12)
  produces identical Discovery writes and identical step/HP spends.
- P3: scripted walk to distance 19 from all anchors: total leash loss =
  Σ per-step band (bands 1×3 steps + 2×3 + 3×1 for a straight-line probe from
  12 → 19 = 3+6+3 = 12 HP, terrain drains excluded).
- P4: emergency extract with a 2-companion party: exactly 2 `[Injury]` roll
  lines, each 15% death (5% if Sworn), survivors injured 1–2 lunations.

## Found, NOT fixed (pre-existing, out of W-track scope)

- **Vista-bias neighbor capture off-by-one on odd columns**
  (`ExpeditionManager.CommitCombat`): it converts the grid-local AXIAL combat
  coord with `OffsetToAxial` (treating it as offset), steps, and converts
  back with `AxialToOffset`. For odd local q, the ±q-direction neighbors land
  one row off — the vista border reads the wrong overworld tile. Fix is to
  index `_grid.Hexes[hexCoord + new Vector2I(dq, dr)]` directly (the keys ARE
  axial). Cosmetic (bias is probabilistic), separable one-liner; left alone
  to keep this delta pure W-track.

## Knobs (all exports on ExpeditionManager)

`HardWindowMode` (A/B), `RecenterThreshold` 3, `SupplyRange` 12,
`LeashBandWidth` 3, `LeashDrainPerBand` 1, `LeashBandCap` 3. Tune
{OperatingRange, SupplyRange, leash} as one set — target effective one-way
reach ≈ 18 hexes at meaningful pool cost.
