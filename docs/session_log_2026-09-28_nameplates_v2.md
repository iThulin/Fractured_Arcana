# Session log, 2026-09-28: unit nameplates v2

Written straight into the Windows working tree (`D:\Development\Fractured Arcana`) on top
of HEAD `e9f8d12` plus the uncommitted loadouts / stance-row work. UNCOMMITTED.

## Problem
The world-space bar (quad meshes + Label3D) showed conditions as symbols from a 15-entry
map. The shipped fonts (Carlito, Caladea) contain none of those glyphs; they only drew
through OS fallback, which differs Windows vs macOS and dropped the shield emoji outright
(armor rendered as a bare "[2]"). Around 45 status keys exist; the bar knew 15 and the
side panel 13, so most conditions (marked, vulnerable, weakened, blinded, hasted, bleed,
shrouded, delayed, ...) were invisible. Conditions only showed on hover.

## Built
- `Scripts/UI/StatusCatalog.cs` (new): one table for every status key plus unit-level
  conditions that never enter the status dict (stagger bool, armed reaction, bodyguard,
  veil, chitin, taunting, heat, spirit undying/invulnerable/vigil). Label, chip code,
  tone family (Debuff / Damage / Mark / Buff / Trait), tooltip. Unknown keys still show.
  `Collect(Unit)` merges and sorts.
- `Scripts/UI/UnitNameplate.cs` (new): screen-space Control drawn in `_Draw`. Intent pill
  (vector glyphs), name (detailed), HP bar in team colour with 5 HP ticks, withered span,
  damage-preview flash, cyan rim when shielded. Defense badges left of the bar in spend
  order: shield (cyan kite), armor (steel hex), cover (moss wall), each with its number.
  Detailed adds HP text, mana strip, AP circles, Edge diamonds, full-name chips.
  Compact shows code chips (2 rows, then "+N"). Hover tooltips on every element by mouse
  polling; MouseFilter stays Ignore so clicks still reach CombatManager._UnhandledInput.
  `NameplateLayer`: one CanvasLayer (layer 0) per viewport, under the HUD.
- `Scripts/UI/HealthBarRoot.cs` (drop-in rewrite): same public API, now an anchor. Hides
  the old meshes, projects its point each frame, PULLS state from the parent Unit every
  0.1 s (pushes mark dirty), mirrors NameLabel / IntentIndicator / CoverMarker Label3Ds
  into the plate and hides them by setting `Layers = 0` (Unit still drives Visible/Text).
  Fixes the "Unit" name: DisplayName wins when the label still holds the node name.
- `Claude outputs/nameplates_v2_apply.py` (applied): UITheme gains Condition* and Plate*
  tokens; CombatUI side panel status row becomes wrapping labelled chips with tooltips
  from StatusCatalog, and its glyph map is deleted.

## Verification
No compiler reachable from the cloud. Independent review pass over Godot API signatures,
Unit member access, language issues and callers found no compile errors; its two real
findings (signature ignoring tooltip text; tooltips firing under HUD panels) were fixed.
`dotnet build` is the gate. Godot will generate `.uid` files for the two new scripts.

## Rules found dead (not fixed; statuses apply but nothing reads them)
`taunted`, `suppressed` (Suppression's "loses 1 move point"), `burning` (AttunementResolver
applies it; only `burn` ticks), `exiled`, and the spirit flags `IsUndying`,
`IsInvulnerable`, `IsVigil` (set by NecromancerEffects, never read). Tooltips describe
the intended effect.

## In-engine confirm owed
1. Plates track units while panning/zooming, no one-frame lag.
2. Clicking a unit through its plate still selects it.
3. Hover a chip: tooltip appears; over the HUD it does not.
4. Brannoc in Guardian shows armor badge distinct from shield; brace armor tooltip.
5. Enemy intent pill replaces the floating glyph; COVER/FLANKED tag beside the bar.

## v2.1 (same day, after first in-engine test)
The build compiled and ran. Three issues came back from play:
1. At close zoom the plate vanished: its anchor above the head left the top of the
   view. HealthBarRoot now decides visibility from the unit's BODY (origin + 0.6) and
   `PlaceAt(anchor, screen)` clamps the plate inside the visible rect, so it pins to the
   top edge instead of disappearing. Units fully off screen still hide their plate.
2. Intent pill was muddy. It now reads `Unit.CurrentIntent` directly (kind, revealed,
   value, shove tiles, imbue element) plus stagger / Poise / Openings / postponed from
   the unit; the Label3D's Visible flag is still the show/hide gate. New file
   `UnitNameplate.Intent.cs`: one drawn icon and hue per IntentKind (sword, arrow,
   swirl, burst, armor plate, droplet, push arrow), white value when revealed, dim "?"
   when not, 2px border when revealed, Poise as steel diamonds, stagger as a red crack.
   The wizard_charging chip is dropped while an intent pill shows (duplicate).
3. The ASCII marker line (MOV4 AP3 SKIRM ...) is no longer drawn. `IntentMarkerText`
   translates every token from LogMarkerLegend into a sentence in the pill tooltip;
   unknown tokens pass through raw.
Also: the name now comes from Unit.DisplayName or the node name, not the NameLabel
(which is written in _Ready before the spawner renames the node, hence "Unit").
UITheme gained Intent* colour tokens (`Claude outputs/nameplates_v2_1_apply.py`, applied).
Note: the v2 review fixes (tooltip suppressed over HUD, signature uses tooltip text)
had not actually reached the working tree in the first delivery; they are in now.
