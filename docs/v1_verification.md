# V1 Verification — Resolution Ruling + Layout Redistribution

*Built 2026-07-09 against combat_ui_v2 §4 (R20/R21 ruling) + §5 layout. Visual verification — eyes in the viewport, not log lines.*

**Change inventory:** project settings were already canvas_items/1920×1080/expand (predates V1 — only the 1280×720 minimum was missing, now enforced in SettingsManager via `GetWindow().MinSize`). UITheme gains the design-space block (DesignWidth/Height, MinWindow*, Hand* fan constants with the bottom-row tiling map). DeckUiManager's four `HandBound*` screen-fraction exports DELETED — they were dead code (PositionHandCards never read them); the real inline constants migrated to UITheme. CombatUI redistributed per §5: phase banner → top center; End Turn + Confirm Deployment → bottom right corner; party chips + 3-line log ticker → bottom left (ticker click opens a 200-line scrollable history popup); hint line + deck/grave counters → bottom center flanking the fan; left panel slimmed to unit card + attunement. The U3 priority prompt moved to top-center below the banner (its old bottom-center slot is the fan's). **Bug fixed in passing: `_hintLabel` was declared but never constructed — SetHintText has been a silent no-op; the drag hints ("Drop to cast · Hold Shift to channel") will appear for the first time.**

**Bottom-row tiling (design px @1920):** ticker 12–452 · deck 466–562 · fan 570–1570 · grave 1578–1674 · End Turn 1728–1908. Hand reserves raised (300→570 left, 240→350 right) so cards can never overlap the new furniture.

## Checklist

1. **Resolution matrix (the exit criterion).** Windowed mode, resize live: 1280×720, 1920×1080, your 3840×2160 fullscreen, and one odd size (e.g. 1600×900, plus an ultrawide stretch if available). At every size: banner centered top, End Turn pinned bottom-right, ticker bottom-left, fan centered between deck/grave, nothing overlapping, nothing clipped. Window refuses to shrink below 1280×720.
2. **Flows unchanged:** deploy (Confirm Deployment appears bottom-right, morph works), select unit (left panel shows card + attunement only), cast, end turn, victory/defeat. Card drag-drop and hover unchanged (fan math is identical, only sourced from UITheme).
3. **Hand geometry:** 5-card hand sits between deck/grave buttons; hover-raise and drag still work; discard animation still exits downward.
4. **New hint line:** drag a card — "Drop to cast · Hold Shift to channel (+1 mana)" appears as the banner's second line (first playtest fix 2026-07-09: above-the-fan placement sat on the card tops; moved into the top banner).
5. **Log ticker:** 3 lines, newest brightest; click opens the full-history popup; history survives past 3 lines.
6. **U3 window regression:** trigger a Deathburst with the stop checkbox on — prompt now appears top-center under the banner, Pass works, no fan overlap.
7. **Mac (when convenient):** same checklist on the Retina window — the design space makes DPI a non-event, but this is the doc's named exit criterion, so it stays on the list until run.

## First playtest fixes (2026-07-09)

- Hint line → banner second line (was mid-hand, unreadable).
- Hand reserves made symmetric (484/484, was 570/350) — fan now centers on x=960 exactly; ticker narrowed to 360 to make room.
- **Enter / keypad-Enter ends the turn** (confirms deployment during deploy — same morph as the button). Suppressed while the priority prompt or any popup is open, so Enter can never blind-pass a U3 stack window. Button tooltip says "Enter".

## Known deferrals

- Context strip (terrain/kingdom/witnessed/corruption line under the banner) is V4 by design.
- Boss frame region reserved below banner — V5.
- The ticker's click-to-expand uses a plain ItemList popup; V3's log grammar pass (FormatLogLine) owns making it pretty.
- Left panel width kept at 280 ("slimmed" = contents, not width) — revisit only if V2's inspect blocks want more room.
