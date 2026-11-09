using Godot;

// ============================================================
// WorldAtlas3D.Rulers.cs: rulers on the map (2026-11-05)
//
// Purpose:        Slice PV3a of the provinces spec (docs/provinces_
//                 v1_8_fronts_per_province.md, PR59). The Political
//                 lens draws each province in its ruler's colour (a
//                 conqueror's kingdom colour, the guild's gold, grey
//                 for land no one rules), so a war reads as one colour
//                 moving into another a province at a time; the realm
//                 ink runs between rulers and the province ink inside
//                 one ruler's land. The rulers are read once a rebuild,
//                 through ProvinceControl, so a province taken shows
//                 when the map next loads.
// Layer:          UI (strategic view)
// Collaborators:  WorldAtlas3D.cs (Rebuild calls RefreshRulers; the
//                 lens colours and the realm ink read RulerKeyAt and
//                 RulerColor), WorldAtlas3D.Provinces.cs (the province
//                 ink), ProvinceControl.cs (RulerOf)
// See:            docs/provinces_v1_8_fronts_per_province.md section 5
// ============================================================

public partial class WorldAtlas3D
{
    /// <summary>The guild's own land on the Political lens: its gold, dimmed toward the realms'
    /// register so it reads as ground.</summary>
    private static readonly Color GuildRulerColor = UITheme.Gold.Darkened(0.18f);

    /// <summary>Land no one rules on the Political lens: a warm grey at the realms' value.</summary>
    private static readonly Color NeutralRulerColor = new Color(0.47f, 0.46f, 0.44f);

    /// <summary>Each province's ruler by its index, read at the last rebuild; null before one,
    /// and on a world without provinces.</summary>
    private string[] _rulerKeys;

    /// <summary>Read every province's ruler once. Rebuild calls it first.</summary>
    private void RefreshRulers()
    {
        _rulerKeys = null;
        if (_world == null || !_world.HasProvinces())
            return;
        _rulerKeys = new string[_world.Provinces.Count];
        for (int i = 0; i < _rulerKeys.Length; i++)
            _rulerKeys[i] = ProvinceControl.RulerOf(_world.Provinces[i], _kingdoms);
    }

    /// <summary>Who rules a tile, as the map colours and inks it: its province's ruler, or, off
    /// the provinces (water, land that joined none), the tile's own kingdom as before.</summary>
    private string RulerKeyAt(in WorldTile t, int col, int row)
    {
        if (_rulerKeys != null && _world != null)
        {
            int pi = _world.ProvinceIndexAt(col, row);
            if (pi >= 0 && pi < _rulerKeys.Length)
                return _rulerKeys[pi] ?? "";
        }
        return t.KingdomId ?? "";
    }

    /// <summary>A ruler's colour on the Political lens: a kingdom's own, the guild's gold, or
    /// grey for no one.</summary>
    private Color RulerColor(string ruler)
    {
        if (ruler == ProvinceControl.Guild)
            return GuildRulerColor;
        if (ruler == ProvinceControl.Neutral)
            return NeutralRulerColor;
        return KingdomColor(ruler);
    }
}
