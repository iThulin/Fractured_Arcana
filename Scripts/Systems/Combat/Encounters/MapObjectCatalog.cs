using Godot;

// ============================================================
// MapObjectCatalog.cs
//
// Purpose:  Static roster for battlefield E3 neutral map objects. One spec
//           per kind: HP, whether it blocks line-of-sight, whether it can be
//           shoved, its body tint, and a display label. On-death / aura
//           behaviour is keyed off MapObjectKind in CombatManager.MapObjects.
// Layer:    Data (pure lookup, no Node/state)
// See:      docs/battlefield_tactics_spec_v1.md §3.2
// ============================================================
public struct MapObjectSpec
{
    public int Hp;
    public bool BlocksLoS;
    public bool Pushable;
    public Color BodyColor;
    public string Label;
}

public static class MapObjectCatalog
{
    /// <summary>Resolve a catalog kind. Returns false for an unknown key so the
    /// spawner can skip and log rather than drop a mystery object.</summary>
    public static bool TryGet(string kind, out MapObjectSpec spec)
    {
        switch ((kind ?? "").ToLowerInvariant())
        {
            case "cracked_pillar":
                spec = new MapObjectSpec { Hp = 8, BlocksLoS = true, Pushable = false,
                    BodyColor = new Color(0.62f, 0.60f, 0.55f), Label = "Cracked Pillar" };
                return true;
            case "resonant_crystal":
                spec = new MapObjectSpec { Hp = 6, BlocksLoS = true, Pushable = false,
                    BodyColor = new Color(0.55f, 0.40f, 0.85f), Label = "Resonant Crystal" };
                return true;
            case "ember_brazier":
                spec = new MapObjectSpec { Hp = 5, BlocksLoS = false, Pushable = true,
                    BodyColor = new Color(0.90f, 0.45f, 0.15f), Label = "Ember Brazier" };
                return true;
            case "boulder":
                spec = new MapObjectSpec { Hp = 12, BlocksLoS = true, Pushable = true,
                    BodyColor = new Color(0.45f, 0.42f, 0.38f), Label = "Boulder" };
                return true;
            case "ward_stone":
                spec = new MapObjectSpec { Hp = 10, BlocksLoS = false, Pushable = false,
                    BodyColor = new Color(0.85f, 0.72f, 0.25f), Label = "Ward Stone" };
                return true;
            case "lever":
                spec = new MapObjectSpec { Hp = 10, BlocksLoS = false, Pushable = false,
                    BodyColor = new Color(0.75f, 0.68f, 0.30f), Label = "Lever" };
                return true;
            case "powder_cask":
                spec = new MapObjectSpec { Hp = 3, BlocksLoS = false, Pushable = true,
                    BodyColor = new Color(0.35f, 0.28f, 0.22f), Label = "Powder Cask" };
                return true;
            default:
                spec = default;
                return false;
        }
    }
}
