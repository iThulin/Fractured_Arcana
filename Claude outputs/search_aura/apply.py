import pathlib
def edit(rel, pairs):
    p = pathlib.Path(rel); s = p.read_text()
    for o, n in pairs:
        assert s.count(o) == 1, (rel, o[:70])
        s = s.replace(o, n)
    p.write_text(s)

edit('Scripts/UI/DeckEditorUi.cs', [
 ('''            stashed = stashed.Where(c => c.BlueprintId.Contains(
                _stashSearch, StringComparison.OrdinalIgnoreCase)).ToList();''',
  '''            stashed = stashed.Where(c => MatchesSearch(c.BlueprintId, _stashSearch)).ToList();'''),
 ('''                || DebugCardName(bp).Contains(q, StringComparison.OrdinalIgnoreCase)
                || (bp.Id ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || bp.School.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)))''',
  '''                || DebugCardName(bp).Contains(q, StringComparison.OrdinalIgnoreCase)
                || HalfNamesMatch(bp, q)
                || (bp.Id ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || bp.School.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)))'''),
 ('''    private static string DebugCardName(CardBlueprint bp)
        => bp?.Prebuilt?.CardName ?? bp?.Id ?? "";''',
  '''    private static string DebugCardName(CardBlueprint bp)
    {
        var top = bp?.Prebuilt?.TopHalf?.Name;
        var bottom = bp?.Prebuilt?.BottomHalf?.Name;
        if (!string.IsNullOrEmpty(top) && !string.IsNullOrEmpty(bottom) && top != bottom)
            return $"{top} / {bottom}";
        return bp?.Prebuilt?.CardName ?? bp?.Id ?? "";
    }

    /// <summary>True when either half's spell name contains <paramref name="q"/>. The card
    /// name alone is the top half, so bottom-half spells were unsearchable.</summary>
    private static bool HalfNamesMatch(CardBlueprint bp, string q)
        => (bp?.Prebuilt?.TopHalf?.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
        || (bp?.Prebuilt?.BottomHalf?.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stash search: blueprint id, card name or either half's spell name.</summary>
    private static bool MatchesSearch(string blueprintId, string q)
    {
        if ((blueprintId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            return true;
        var bp = CardDatabase.GetByName(blueprintId);
        return bp != null
            && ((bp.Prebuilt?.CardName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || HalfNamesMatch(bp, q));
    }'''),
])

# Remove the light column: never build it, and drop it from the shader warmup.
edit('Scripts/Systems/Combat/Terrain/HexTile.cs', [
 ('''        var auraShader = GD.Load<Shader>(GlyphAuraShaderPath);
        if (auraShader != null)
        {
            _glyphAuraMaterial''',
  '''        // Light column removed (2026-10-04 playtest). _glyphAura stays null, and every
        // use of it is already null-checked, so nothing else needs to change.
        var auraShader = GlyphAuraEnabled ? GD.Load<Shader>(GlyphAuraShaderPath) : null;
        if (auraShader != null)
        {
            _glyphAuraMaterial'''),
 ('    internal const string GlyphAuraShaderPath = ',
  '    internal const bool GlyphAuraEnabled = false;\n    internal const string GlyphAuraShaderPath = '),
])
edit('Scripts/Systems/Combat/Core/CombatManager.GlyphWarmup.cs', [
 ('            var auraShader = GD.Load<Shader>(HexTile.GlyphAuraShaderPath);',
  '            var auraShader = HexTile.GlyphAuraEnabled ? GD.Load<Shader>(HexTile.GlyphAuraShaderPath) : null;'),
])
print("ok")
