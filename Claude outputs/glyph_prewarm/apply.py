import pathlib
R = pathlib.Path('.')
def edit(rel, pairs):
    p = R/rel; s = p.read_text()
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:60])
        s = s.replace(o, n)
    p.write_text(s)

edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
 ('    private const int GlyphDecalPixels = 512;', '    internal const int GlyphDecalPixels = 512;'),
 ('    private const string GlyphDecalShaderPath = ', '    internal const string GlyphDecalShaderPath = '),
 ('    private const string GlyphAuraShaderPath = ', '    internal const string GlyphAuraShaderPath = '),
])

edit('Scripts/Systems/Combat/Glyphs/GlyphCipherTexture.cs', [
 ('    /// <summary>Drops every cached texture. Call on a resolution change',
  '''    /// <summary>
    /// Bakes the tile-decal masks for a list of blueprint halves ahead of time, one per
    /// frame, so casting a glyph later is a cache hit. A cold bake costs two frames of
    /// latency plus a GPU readback (GetImage) that stalls the render thread, and doing
    /// that at the moment of the cast is the hitch the player sees. Sequential on
    /// purpose: spreading the readbacks out keeps combat start smooth too.
    /// </summary>
    public async void PrewarmMasks(IReadOnlyList<(string id, string half)> halves, int px)
    {
        if (halves == null) return;
        try
        {
            foreach (var (id, half) in halves)
            {
                var done = new TaskCompletionSource<bool>();
                RequestMaskForBlueprint(id, half, px, _ => done.TrySetResult(true));
                await done.Task;
                if (!IsInsideTree()) return;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GlyphCipherTexture] prewarm failed: {ex.Message}");
        }
    }

    /// <summary>Drops every cached texture. Call on a resolution change'''),
])

edit('Scripts/Systems/Combat/Core/CombatManager.cs', [
 ('''            deckManager.SetActiveDeck(null);

        // Post-cast player choice (2026-07-28)''',
  '''            deckManager.SetActiveDeck(null);

        // Glyph hitch fix: bake decal masks and compile the glyph shaders now,
        // not at the first cast. See CombatManager.GlyphWarmup.cs.
        PrewarmGlyphs();

        // Post-cast player choice (2026-07-28)'''),
])
print("ok")
