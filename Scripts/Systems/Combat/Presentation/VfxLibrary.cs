using Godot;
using System.Collections.Generic;

// ============================================================
// VfxLibrary.cs
//
// Purpose:        The one place that turns a VisualEvent into a VFX node.
//                 Phase 1 builds every effect procedurally from primitives so
//                 the pipeline runs with zero authored assets; an authored
//                 .tscn can replace any cell through Register() without
//                 touching the presenter.
//
//                 Palette precedence (phase 2b): explicit style from the card's
//                 "vfx" block → first element tag on the half (ElementColors) →
//                 school (SchoolColors) → neutral. So Elementalist fire and ice
//                 cards already read differently with no art authored.
//
//                 Scene lookup keys: "<archetype>/<style>" (from the vfx block),
//                 then "<archetype>/<School>", then "<archetype>/*". Archetypes:
//                 bolt, arc, burst, impact, cast.
// Layer:          Systems / Combat / Presentation
// Collaborators:  CombatPresenter (caller), ProjectileVfx, BurstVfx,
//                 DamageNumber, SchoolColors, ElementColors
// See:            docs/spell_vfx_pipeline_v1.md §4
// ============================================================

public static class VfxLibrary
{
    public const string ArchBolt = "bolt";     // straight flight (Delivery.Bolt)
    public const string ArchArc = "arc";       // lobbed flight   (Delivery.Arc, Untyped→unit)
    public const string ArchBurst = "burst";   // fills from aim  (Delivery.Burst, Ground, Untyped→tile)
    public const string ArchImpact = "impact"; // lands on a unit (every projectile arrival, Melee)
    public const string ArchCast = "cast";     // caster release flash
    public const string ArchNone = "none";     // "vfx": { "archetype": "none" } suppresses travel visuals

    private static readonly Dictionary<string, PackedScene> _overrides = new();

    /// <summary>Neutral palette for casts with no known school, tag or style
    /// (enemy strikes, triggered effects).</summary>
    public static readonly Color NeutralTint = new Color("#D8D2C4");

    /// <summary>Named palettes for the "style" field of a card's vfx block. These
    /// are deliberately more saturated than the UI pip colours in ElementColors:
    /// they are read as emissive light in the world, not as ink on a card.</summary>
    private static readonly Dictionary<string, Color> _styleTints = new()
    {
        ["fire"] = new Color("#FF7A2E"),
        ["ember"] = new Color("#FF4E1F"),
        ["frost"] = new Color("#9EDBFF"),
        ["ice"] = new Color("#9EDBFF"),
        ["storm"] = new Color("#C9F06A"),
        ["lightning"] = new Color("#FFF3A0"),
        ["earth"] = new Color("#C58A3C"),
        ["stone"] = new Color("#B9AE9A"),
        ["void"] = new Color("#7B3FE0"),
        ["shadow"] = new Color("#4A3B6E"),
        ["blood"] = new Color("#C8102E"),
        ["holy"] = new Color("#FFF1C2"),
        ["light"] = new Color("#FFF1C2"),
        ["nature"] = new Color("#6FE07A"),
        ["growth"] = new Color("#6FE07A"),
        ["spirit"] = new Color("#A8E4FF"),
        ["temporal"] = new Color("#2EE6C8"),
        ["arcane"] = new Color("#9D7BFF"),
        ["gold"] = new Color("#FFD24A"),
        ["glyph"] = new Color("#E0C878"),
        ["oil"] = new Color("#3B2A1A"),
        ["steam"] = new Color("#E8E8F0")
    };

    /// <summary>Element tags that carry a colour in ElementColors. Anything else on a
    /// half's tag list ("arcane" aside, which is both) is a mechanic tag, not a palette.</summary>
    private static readonly HashSet<string> _elementTags = new()
    {
        "fire", "ice", "frost", "storm", "earth", "stone", "arcane", "necrotic",
        "spirit", "temporal", "enchant", "construct", "growth", "glyph"
    };

    /// <summary>Install an authored scene for one matrix cell. The scene root must
    /// derive from ProjectileVfx (bolt/arc) or BurstVfx (burst/impact/cast).</summary>
    public static void Register(string key, PackedScene scene)
    {
        if (string.IsNullOrEmpty(key) || scene == null)
            return;
        _overrides[key] = scene;
    }

    /// <summary>True when <paramref name="tag"/> is a palette-bearing element tag.</summary>
    public static bool IsElementTag(string tag)
        => !string.IsNullOrEmpty(tag) && _elementTags.Contains(tag.ToLowerInvariant());

    /// <summary>Palette for an event: style → element tag → school → neutral.</summary>
    public static Color TintFor(VisualEvent ev)
    {
        if (!string.IsNullOrEmpty(ev.VfxStyle)
            && _styleTints.TryGetValue(ev.VfxStyle.ToLowerInvariant(), out var styled))
            return styled;
        if (!string.IsNullOrEmpty(ev.ElementTag))
        {
            string t = ev.ElementTag.ToLowerInvariant();
            if (t == "frost") t = "ice";
            if (t == "stone") t = "earth";
            if (_styleTints.TryGetValue(t, out var elemStyled))
                return elemStyled;                 // emissive variant when we have one
            return ElementColors.Get(t);           // else the UI pip colour
        }
        if (ev.SchoolKnown)
            return SchoolColors.GetBorderColor(ev.School);
        return NeutralTint;
    }

    public static ProjectileVfx MakeProjectile(string archetype, VisualEvent ev)
    {
        var scene = Find(archetype, ev);
        ProjectileVfx p = scene != null ? scene.Instantiate<ProjectileVfx>() : new ProjectileVfx();
        p.Tint = TintFor(ev);
        p.Lobbed = archetype == ArchArc;
        return p;
    }

    public static BurstVfx MakeBurst(string archetype, VisualEvent ev, float radius)
    {
        var scene = Find(archetype, ev);
        BurstVfx b = scene != null ? scene.Instantiate<BurstVfx>() : new BurstVfx();
        b.Tint = TintFor(ev);
        b.Radius = radius;
        b.GroundRing = archetype == ArchBurst;
        return b;
    }

    public static DamageNumber MakeDamageNumber(int hpLoss, int shieldLoss, int armorLoss)
    {
        var n = new DamageNumber();
        n.Configure(hpLoss, shieldLoss, armorLoss);
        return n;
    }

    private static PackedScene Find(string archetype, VisualEvent ev)
    {
        if (!string.IsNullOrEmpty(ev.VfxStyle)
            && _overrides.TryGetValue($"{archetype}/{ev.VfxStyle}", out var styled))
            return styled;
        if (ev.SchoolKnown && _overrides.TryGetValue($"{archetype}/{ev.School}", out var exact))
            return exact;
        if (_overrides.TryGetValue($"{archetype}/*", out var any))
            return any;
        return null;
    }
}
