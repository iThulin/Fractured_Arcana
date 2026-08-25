// ============================================================
// REMOVED (2026-07-15): manifest-driven terrain prop scatter ("Props v2").
//
// Superseded by the painterly scatter family, which owns all prop
// placement now:
//   HexGridManager.PainterlyGrass.cs / .Flowers.cs / .Rocks.cs / .Canopy.cs
//
// Its GenerateMap() call had already been commented out; the legacy
// per-tile fallback (SpawnTerrainProps / SpawnGrassOnTile in Visuals.cs)
// and the GrassTuftScene exports were removed in the same pass, along
// with TilesetManifest.cs / TilesetRegistry.cs.
//
// This file is an intentional tombstone so the removal is visible in
// history. Safe to DELETE from the Godot FileSystem dock whenever
// (Godot will clean up the .uid with it). Recover the old code from git
// if a data-driven prop manifest is ever wanted again.
// ============================================================
