#!/usr/bin/env python3
"""Exploration information slice 1, fix 1 (2026-10-30): wild sightings only.

Run after apply_exploration_info_slice1.py. The first cut stopped a stride for
every place kind except Settlement, Seat and Outpost. Two problems, both found
reading the generator after the write:
  - a town's studs (Rest, Negotiation, Combat, and Narrative in cities) sit
    inside its footprint, so walking past a town would stop the march at stud
    after stud;
  - wild Outposts are genuine finds (an unclaimed forward base), yet were
    excluded.
Now: any kind but a settlement's or seat's own marker, on a tile outside every
settlement footprint.

Usage: python3 fix1_wild_sightings.py <repo root>
Refuses unless ExpeditionManager.cs is exactly the slice 1 result.
"""
import hashlib
import sys
from pathlib import Path

MANAGER = "Scripts/Systems/Overworld/Expedition/ExpeditionManager.cs"
EXPECTED = "397be7c091ee59484a38b7e6a542fe93f4a2d78c"

EDITS = [
    (
        "    /// <summary>POI kinds a sighting never stops a stride for. Settlements, seats\n"
        "    /// and outposts are infrastructure already on the world map, and the road\n"
        "    /// halt announces them on arrival; stopping to look at them would be the\n"
        "    /// friction the 2026-09-21 ruling removed.</summary>\n"
        "    private static bool IsSightingKind(OverworldHex.POIType kind)\n"
        "        => kind != OverworldHex.POIType.None\n"
        "           && kind != OverworldHex.POIType.Settlement\n"
        "           && kind != OverworldHex.POIType.Seat\n"
        "           && kind != OverworldHex.POIType.Outpost;\n",
        "    /// <summary>Does a place on this tile count as a sighting? Wild places only:\n"
        "    /// any kind but a settlement's or a seat's own marker, on a tile outside every\n"
        "    /// settlement footprint. A town's studs are part of the town you can already\n"
        "    /// see, and the road halt names the town on arrival, so walking past one must\n"
        "    /// not stop the march at every stud (the friction the 2026-09-21 ruling\n"
        "    /// removed). Wild outposts do count: an unclaimed forward base is a find.</summary>\n"
        "    private bool IsSighting(Vector2I local, OverworldHex.POIType kind)\n"
        "    {\n"
        "        if (kind == OverworldHex.POIType.None\n"
        "            || kind == OverworldHex.POIType.Settlement\n"
        "            || kind == OverworldHex.POIType.Seat)\n"
        "        {\n"
        "            return false;\n"
        "        }\n"
        "        return TryTileAt(local, out var st) && st.SettlementIndex < 0;\n"
        "    }\n",
    ),
    (
        "            if (IsSightingKind(kvp.Value.Poi) && !kvp.Value.Consumed\n"
        "                && _fog.FogAt(kvp.Key) == OverworldHex.FogState.Revealed)\n",
        "            if (!kvp.Value.Consumed && IsSighting(kvp.Key, kvp.Value.Poi)\n"
        "                && _fog.FogAt(kvp.Key) == OverworldHex.FogState.Revealed)\n",
    ),
    (
        "            if (!IsSightingKind(ov.Poi) || ov.Consumed || seenBefore.Contains(c))\n",
        "            if (ov.Consumed || seenBefore.Contains(c) || !IsSighting(c, ov.Poi))\n",
    ),
]


def blob_hash(data: bytes) -> str:
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    path = Path(sys.argv[1]) / MANAGER
    data = path.read_bytes()
    got = blob_hash(data)
    if got != EXPECTED:
        print(f"REFUSED {MANAGER}: starting blob {got}, expected {EXPECTED}. Nothing written.")
        return 1
    text = data.decode("utf-8")
    for i, (find, repl) in enumerate(EDITS, 1):
        n = text.count(find)
        if n != 1:
            print(f"REFUSED edit {i}: {n} matches, expected exactly 1. Nothing written.")
            return 1
        text = text.replace(find, repl, 1)
    if "IsSightingKind" in text:
        print("REFUSED: a call to the old IsSightingKind remains. Nothing written.")
        return 1
    out = text.encode("utf-8")
    path.write_bytes(out)
    print(f"wrote {MANAGER}  {EXPECTED[:7]} -> {blob_hash(out)[:7]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
