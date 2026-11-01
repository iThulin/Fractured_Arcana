#!/usr/bin/env python3
"""Exploration information slice 1 (2026-10-30).

Three behaviours and one debug switch:
  1. Frontier lures draw in the 3D sortie view as a column of light with the
     place's signal over it (they were plain dabs, the beacon lived on the
     retired 2D hex only).
  2. A stride stops when a place comes into view ("Something sighted").
  3. High ground widens sight: Hills +1, Mountain +2 on the one-tile base.
  4. Debug > EXPEDITION > "Classic Exploration (A/B baseline)" turns 1 to 3 off,
     so the decision audit can compare both in one build.

Usage: python3 apply_exploration_info_slice1.py <repo root>
Every file must match its expected starting blob; every edit must match exactly
once. Nothing is written unless every edit in every file succeeds. A second run
refuses (the starting hashes no longer match).
"""
import hashlib
import sys
from pathlib import Path


def blob_hash(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


PLAYER_SESSION = "Scripts/Data/SaveState/PlayerSession.cs"
GUILD_PANEL = "Scripts/Systems/Campus/CampusGuildPanel.cs"
FOG = "Scripts/Systems/Overworld/Expedition/FogOfWarManager.cs"
WINDOW3D = "Scripts/Systems/Overworld/Expedition/ExpeditionWindow3D.cs"
MANAGER = "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs"

EXPECTED = {
    PLAYER_SESSION: "7f31d878a9a9eaa7c70f0bdf1ed7f46ed84d457c",
    GUILD_PANEL: "2cd48c3c873c8dd32cc86498b7c5c60274ae8463",
    FOG: "461fe2eb4b74ba2a32bb8d18c3f502656c0f31aa",
    WINDOW3D: "c071e462730e8b36d541558cd646733bccb8a171",
    MANAGER: "183d79548e56171cf4df48b50d1842c1001ef289",
}

EDITS = {
    # ── PlayerSession: the A/B switch ─────────────────────────────────────
    PLAYER_SESSION: [
        (
            "    public static bool DebugNoAmbush = false;\n",
            "    public static bool DebugNoAmbush = false;\n"
            "\n"
            "    /// <summary>Debug A/B switch for the 2026-10-30 exploration information slice\n"
            "    /// (claude/exploration_information_gap_2026-10-30.md). When true, a sortie plays\n"
            "    /// as it did before the slice: frontier lures draw as ordinary markers in the 3D\n"
            "    /// view, a stride does not stop on a sighting, and high ground adds no sight. It\n"
            "    /// exists so the decision audit can compare the two in one build. Not saved.</summary>\n"
            "    public static bool DebugClassicExploration = false;\n",
        ),
    ],
    # ── Campus debug panel: the checkbox ──────────────────────────────────
    GUILD_PANEL: [
        (
            '        Check(expedition, "No Enemy Ambushes", PlayerSession.DebugNoAmbush, on => PlayerSession.DebugNoAmbush = on);\n',
            '        Check(expedition, "No Enemy Ambushes", PlayerSession.DebugNoAmbush, on => PlayerSession.DebugNoAmbush = on);\n'
            '        Check(expedition, "Classic Exploration (A/B baseline)", PlayerSession.DebugClassicExploration,\n'
            '            on => PlayerSession.DebugClassicExploration = on);\n',
        ),
    ],
    # ── Fog: high-ground sight, the sight actually used ───────────────────
    FOG: [
        (
            "    public WindowOverlayModel Overlay;\n",
            "    public WindowOverlayModel Overlay;\n"
            "\n"
            "    /// <summary>Terrain at a grid-local coord, read from the world (the authority\n"
            "    /// the move costs read), injected by ExpeditionManager. Null in isolation,\n"
            "    /// where the hex node's terrain is the fallback.</summary>\n"
            "    public System.Func<Vector2I, OverworldHex.TerrainType?> TerrainQuery;\n"
            "\n"
            "    /// <summary>High ground widens sight. Ruled 2026-10-30: the base stays one\n"
            "    /// tile and the bonus comes from where the party stands. Starting values. The\n"
            "    /// point is the decision: a climb costs fuel and buys a look around.</summary>\n"
            "    public const int HillsSightBonus = 1;\n"
            "    public const int MountainSightBonus = 2;\n"
            "\n"
            "    /// <summary>The reveal radius the last UpdateVision used, every modifier\n"
            "    /// included. A stride reads it to tell a sighting from ground that merely\n"
            "    /// streamed in at the window's edge.</summary>\n"
            "    public int LastRevealRange { get; private set; } = 1;\n",
        ),
        (
            "    /// <summary>\n"
            "    /// Call this whenever the party moves. Reveals hexes within vision radius\n"
            "    /// and sets silhouettes on the fringe.\n"
            "    /// </summary>\n",
            "    /// <summary>Sight added by the ground the party stands on: Hills and Mountain\n"
            "    /// only. Zero under the Classic Exploration debug switch, so the A/B audit\n"
            "    /// compares like with like.</summary>\n"
            "    private int HighGroundBonus(Vector2I partyCoord)\n"
            "    {\n"
            "        if (PlayerSession.DebugClassicExploration)\n"
            "        {\n"
            "            return 0;\n"
            "        }\n"
            "        OverworldHex.TerrainType? terrain = TerrainQuery?.Invoke(partyCoord);\n"
            "        if (terrain == null && _grid != null && _grid.Hexes.TryGetValue(partyCoord, out var hex))\n"
            "        {\n"
            "            terrain = hex.Terrain;\n"
            "        }\n"
            "        return terrain switch\n"
            "        {\n"
            "            OverworldHex.TerrainType.Mountain => MountainSightBonus,\n"
            "            OverworldHex.TerrainType.Hills => HillsSightBonus,\n"
            "            _ => 0,\n"
            "        };\n"
            "    }\n"
            "\n"
            "    /// <summary>\n"
            "    /// Call this whenever the party moves. Reveals hexes within vision radius\n"
            "    /// and sets silhouettes on the fringe.\n"
            "    /// </summary>\n",
        ),
        (
            "        int revealRange = Mathf.Max(1, BaseVisionRadius + bonusRadius + VisionModifiers.ScryBonus);\n",
            "        // High ground (2026-10-30) joins the same sum, so a storm on a peak and a\n"
            "        // clear day in a valley trade off on one scale.\n"
            "        int revealRange = Mathf.Max(1, BaseVisionRadius + bonusRadius + VisionModifiers.ScryBonus\n"
            "                                       + HighGroundBonus(partyCoord));\n"
            "        LastRevealRange = revealRange;\n",
        ),
    ],
    # ── 3D sortie view: lure beacons ──────────────────────────────────────
    WINDOW3D: [
        (
            "    public event System.Action<Vector2I> TileHovered;\n"
            "    public event System.Action TileUnhovered;\n",
            "    public event System.Action<Vector2I> TileHovered;\n"
            "    public event System.Action TileUnhovered;\n"
            "\n"
            "    /// <summary>The words a frontier lure carries (world-offset coord in). The live\n"
            "    /// host supplies the signal its tooltip gives, so label and tooltip never\n"
            "    /// disagree. Null in the standalone harness, which falls back to the POI kind.</summary>\n"
            "    public System.Func<Vector2I, string> LureLabel;\n",
        ),
        (
            "                { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.4f, NoDepthTest = true },\n"
            "                Position = pos,\n"
            "            }));\n"
            "        }\n"
            "    }\n"
            "\n"
            "    // ── Party pawn ",
            "                { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.4f, NoDepthTest = true },\n"
            "                Position = pos,\n"
            "            }));\n"
            "\n"
            "            // Frontier lures and the warfront objective (2026-10-30): a column of\n"
            "            // light that stands above the mist, with the place's signal over it.\n"
            "            // The 2D hex drew these as a glow and ring; this view drew them as\n"
            "            // ordinary dabs, so the horizon pull never reached 3D play.\n"
            "            if (ov.Landmark && !PlayerSession.DebugClassicExploration)\n"
            "            {\n"
            "                AddLureBeacon(c, ov, pos);\n"
            "            }\n"
            "        }\n"
            "    }\n"
            "\n"
            "    /// <summary>Height of a lure's column above its marker, in world units. Tall\n"
            "    /// enough to clear the mist deck at the default pitch.</summary>\n"
            "    private const float LureColumnHeight = 5f;\n"
            "\n"
            "    /// <summary>Column plus label for one lure. Both join <see cref=\"_markers\"/>,\n"
            "    /// so the next rebuild frees them with the dabs.</summary>\n"
            "    private void AddLureBeacon(Vector2I c, TileOverlay ov, Vector3 markerPos)\n"
            "    {\n"
            "        Color tint = ov.Objective ? UITheme.POIObjective : PoiColor(ov.Poi);\n"
            "        var column = new MeshInstance3D\n"
            "        {\n"
            "            Mesh = new CylinderMesh\n"
            "            {\n"
            "                TopRadius = 0.04f, BottomRadius = 0.16f, Height = LureColumnHeight,\n"
            "                RadialSegments = 12, Rings = 1,\n"
            "            },\n"
            "            MaterialOverride = new StandardMaterial3D\n"
            "            {\n"
            "                AlbedoColor = new Color(tint, 0.55f),\n"
            "                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,\n"
            "                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,\n"
            "                NoDepthTest = true,\n"
            "            },\n"
            "            Position = markerPos + new Vector3(0f, LureColumnHeight * 0.5f, 0f),\n"
            "            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,\n"
            "        };\n"
            "        AddChild(column);\n"
            "        _markers.Add(column);\n"
            "\n"
            "        string text = LureLabel != null ? LureLabel(c) : ov.Poi.ToString();\n"
            "        if (string.IsNullOrEmpty(text))\n"
            "        {\n"
            "            return;\n"
            "        }\n"
            "        var label = new Label3D\n"
            "        {\n"
            "            Text = text,\n"
            "            Position = markerPos + new Vector3(0f, LureColumnHeight + 0.35f, 0f),\n"
            "            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,\n"
            "            NoDepthTest = true,\n"
            "            RenderPriority = 4,\n"
            "            OutlineRenderPriority = 3,\n"
            "            FontSize = 34,\n"
            "            PixelSize = 0.012f,\n"
            "            Modulate = tint,\n"
            "            OutlineSize = 12,\n"
            "            OutlineModulate = UITheme.RoadChoiceOutline,\n"
            "        };\n"
            "        AddChild(label);\n"
            "        _markers.Add(label);\n"
            "    }\n"
            "\n"
            "    // ── Party pawn ",
        ),
    ],
    # ── ExpeditionManager: wiring, the label, the sighting halt ───────────
    MANAGER: [
        (
            "        _fog.Overlay = _overlay;\n",
            "        _fog.Overlay = _overlay;\n"
            "        // High-ground sight (2026-10-30) reads terrain from the world, the same\n"
            "        // source the move costs read, not from the display hexes.\n"
            "        _fog.TerrainQuery = local => TryTileAt(local, out var vt) ? vt.Terrain : (OverworldHex.TerrainType?)null;\n",
        ),
        (
            "        _window3D.TileUnhovered += OnWindow3DUnhover;\n"
            "        vp.AddChild(_window3D);\n",
            "        _window3D.TileUnhovered += OnWindow3DUnhover;\n"
            "        _window3D.LureLabel = LureLabelAtWorld;\n"
            "        vp.AddChild(_window3D);\n",
        ),
        (
            "    private void OnWindow3DUnhover()\n"
            "    {\n",
            "    /// <summary>The words a frontier lure carries in the 3D view: the signal the\n"
            "    /// tooltip gives, computed from the grid-local coord exactly as the tooltip\n"
            "    /// computes it, or the stronghold for a warfront objective.</summary>\n"
            "    private string LureLabelAtWorld(Vector2I worldCoord)\n"
            "    {\n"
            "        if (_window == null)\n"
            "        {\n"
            "            return \"\";\n"
            "        }\n"
            "        Vector2I local = _window.LocalOf(worldCoord.X, worldCoord.Y);\n"
            "        TileOverlay ov = _overlay.OverlayAt(local);\n"
            "        if (ov.Objective)\n"
            "        {\n"
            "            return \"the stronghold\";\n"
            "        }\n"
            "        return PoiSignal.Label(ov.Poi, TerrainAt(local), local);\n"
            "    }\n"
            "\n"
            "    private void OnWindow3DUnhover()\n"
            "    {\n",
        ),
        (
            "    private int _strideStuck;          // blind steps without improving _strideBestDist\n",
            "    private int _strideStuck;          // blind steps without improving _strideBestDist\n"
            "    /// <summary>A sighting noted on the last step (ruled 2026-10-30: a stride stops\n"
            "    /// when a place comes into view). Read at the top of the next beat, once the\n"
            "    /// pawn has landed; cleared by EndStride so it never leaks into the next\n"
            "    /// order. Null when nothing is pending.</summary>\n"
            "    private string _strideSightNote;\n",
        ),
        (
            "        // Wait for the previous hop's animation to land before issuing the next.\n"
            "        if (_party.IsMoving)\n"
            "        { ScheduleStrideTick(0.05f); return; }\n",
            "        // Wait for the previous hop's animation to land before issuing the next.\n"
            "        if (_party.IsMoving)\n"
            "        { ScheduleStrideTick(0.05f); return; }\n"
            "\n"
            "        // A place came into view on the last step (2026-10-30). The march stops\n"
            "        // so the player chooses: press on, or turn aside. Checked before the\n"
            "        // arrival test so a sighting on the final step is still announced.\n"
            "        if (_strideSightNote != null)\n"
            "        {\n"
            "            string sighted = _strideSightNote;\n"
            "            _strideSightNote = null;\n"
            "            EndStride(sighted, \"Something sighted\", HaltTone.Choice);\n"
            "            return;\n"
            "        }\n",
        ),
        (
            "        var from = _party.CurrentCoord;\n"
            "        if (!_party.TryMoveTo(next))\n"
            "        {\n"
            "            EndStride(\"The way is blocked.\", \"Blocked\", HaltTone.Trouble);\n"
            "            return;\n"
            "        }\n"
            "        _strideLastTile = from;\n",
            "        var from = _party.CurrentCoord;\n"
            "        HashSet<Vector2I> seenBefore = PlayerSession.DebugClassicExploration ? null : VisiblePoiTiles();\n"
            "        if (!_party.TryMoveTo(next))\n"
            "        {\n"
            "            EndStride(\"The way is blocked.\", \"Blocked\", HaltTone.Trouble);\n"
            "            return;\n"
            "        }\n"
            "        if (seenBefore != null)\n"
            "        {\n"
            "            NoteStrideSighting(seenBefore);\n"
            "        }\n"
            "        _strideLastTile = from;\n",
        ),
        (
            "        _striding = false;\n"
            "        _roadFollow = false;   // a halt ends the road order; the player re-issues\n",
            "        _striding = false;\n"
            "        _strideSightNote = null;   // a pending sighting dies with the order\n"
            "        _roadFollow = false;   // a halt ends the road order; the player re-issues\n",
        ),
        (
            "    private void SetHaltButton(bool show)\n"
            "    {\n",
            "    /// <summary>POI kinds a sighting never stops a stride for. Settlements, seats\n"
            "    /// and outposts are infrastructure already on the world map, and the road\n"
            "    /// halt announces them on arrival; stopping to look at them would be the\n"
            "    /// friction the 2026-09-21 ruling removed.</summary>\n"
            "    private static bool IsSightingKind(OverworldHex.POIType kind)\n"
            "        => kind != OverworldHex.POIType.None\n"
            "           && kind != OverworldHex.POIType.Settlement\n"
            "           && kind != OverworldHex.POIType.Seat\n"
            "           && kind != OverworldHex.POIType.Outpost;\n"
            "\n"
            "    /// <summary>Tiles holding a live, visible place: the stride's picture of what\n"
            "    /// it could already see before a step.</summary>\n"
            "    private HashSet<Vector2I> VisiblePoiTiles()\n"
            "    {\n"
            "        var seen = new HashSet<Vector2I>();\n"
            "        foreach (var kvp in _overlay.All)\n"
            "        {\n"
            "            if (IsSightingKind(kvp.Value.Poi) && !kvp.Value.Consumed\n"
            "                && _fog.FogAt(kvp.Key) == OverworldHex.FogState.Revealed)\n"
            "            {\n"
            "                seen.Add(kvp.Key);\n"
            "            }\n"
            "        }\n"
            "        return seen;\n"
            "    }\n"
            "\n"
            "    /// <summary>After a stride step: did the view just take in a place it could not\n"
            "    /// see before? Only tiles inside the sight the step used count, so ground that\n"
            "    /// streams in at the window's edge is never mistaken for a sighting. The\n"
            "    /// party's own tile opens its own encounter, and the goal is already the\n"
            "    /// order, so neither counts. Writes the halt's words to _strideSightNote.</summary>\n"
            "    private void NoteStrideSighting(HashSet<Vector2I> seenBefore)\n"
            "    {\n"
            "        if (_party == null || _fog == null || _grid == null)\n"
            "        {\n"
            "            return;\n"
            "        }\n"
            "        Vector2I here = _party.CurrentCoord;\n"
            "        int sight = _fog.LastRevealRange;\n"
            "        Vector2I? nearest = null;\n"
            "        int nearestDist = int.MaxValue;\n"
            "        int count = 0;\n"
            "        foreach (var kvp in _overlay.All)\n"
            "        {\n"
            "            Vector2I c = kvp.Key;\n"
            "            TileOverlay ov = kvp.Value;\n"
            "            if (!IsSightingKind(ov.Poi) || ov.Consumed || seenBefore.Contains(c))\n"
            "            {\n"
            "                continue;\n"
            "            }\n"
            "            if (c == here || (!_roadFollow && c == _strideGoal))\n"
            "            {\n"
            "                continue;\n"
            "            }\n"
            "            if (_fog.FogAt(c) != OverworldHex.FogState.Revealed)\n"
            "            {\n"
            "                continue;\n"
            "            }\n"
            "            int d = _grid.Distance(here, c);\n"
            "            if (d > sight)\n"
            "            {\n"
            "                continue;\n"
            "            }\n"
            "            count++;\n"
            "            if (d < nearestDist)\n"
            "            {\n"
            "                nearestDist = d;\n"
            "                nearest = c;\n"
            "            }\n"
            "        }\n"
            "        if (nearest == null)\n"
            "        {\n"
            "            return;\n"
            "        }\n"
            "        Vector2I at = nearest.Value;\n"
            "        string what = PoiSignal.Label(_overlay.OverlayAt(at).Poi, TerrainAt(at), at);\n"
            "        if (what.Length > 0)\n"
            "        {\n"
            "            what = char.ToUpperInvariant(what[0]) + what.Substring(1);\n"
            "        }\n"
            "        string range = nearestDist == 1 ? \"one tile off\" : $\"{nearestDist} tiles off\";\n"
            "        string more = count > 1 ? $\" {count - 1} more in view.\" : \"\";\n"
            "        _strideSightNote = $\"{what}, {range}.{more} Press on, or turn aside.\";\n"
            "    }\n"
            "\n"
            "    private void SetHaltButton(bool show)\n"
            "    {\n",
        ),
    ],
}


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    root = Path(sys.argv[1])
    staged = {}
    for rel, expected in EXPECTED.items():
        path = root / rel
        data = path.read_bytes()
        got = blob_hash(data)
        if got != expected:
            print(f"REFUSED {rel}: starting blob {got}, expected {expected}. Nothing written.")
            return 1
        if b"\r\n" in data:
            print(f"REFUSED {rel}: CRLF line endings; the edits are LF. Nothing written.")
            return 1
        text = data.decode("utf-8")
        for i, (find, repl) in enumerate(EDITS[rel], 1):
            n = text.count(find)
            if n != 1:
                print(f"REFUSED {rel} edit {i}: {n} matches, expected exactly 1. Nothing written.")
                return 1
            text = text.replace(find, repl, 1)
        staged[rel] = text.encode("utf-8")
    for rel, data in staged.items():
        (root / rel).write_bytes(data)
        print(f"wrote {rel}  {EXPECTED[rel][:7]} -> {blob_hash(data)[:7]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
