using System.Collections.Generic;

// ============================================================
// OverworldSpellDefinition.cs  (S1, 2026-07-15)
//
// Purpose:        Schema for one overworld (noncombat) spell,
//                 loaded from Data/OverworldSpells/*.json. Pure
//                 data. Effects are implemented in
//                 OverworldSpellManager's dispatcher, keyed by
//                 EffectKey (bespoke key per spell, the same
//                 dispatcher shape as the enemy-archetype
//                 pattern). Attunements (passive, always-on) are
//                 definitions too, flagged IsAttunement, so one
//                 registry owns the whole catalog.
// Layer:          Data
// Collaborators:  OverworldSpellRegistry.cs (loader/cache),
//                 OverworldSpellManager.cs (dispatch),
//                 GrimoirePanel.cs (display)
// See:            overworld_spell_system_v1_1.docx §6, §7, §13
//
// House convention: System.Text.Json, CamelCase, IncludeFields,
// public fields throughout.
// ============================================================

/// <summary>One overworld spell (or Attunement). Loaded from JSON;
/// behavior lives behind EffectKey in OverworldSpellManager.</summary>
public class OverworldSpellDefinition
{
    /// <summary>Unique id, snake_case (e.g. "force_path").</summary>
    public string Id = "";

    public string Name = "";

    /// <summary>Owning school ("Elementalist", …) or "General". School
    /// innates/Attunements derive availability from this; General spells
    /// fill prepared slots.</summary>
    public string School = "General";

    /// <summary>Taxonomy category (§6): Traversal / Divination / Warding /
    /// Evasion / Conjuration / Communion. Informational in S2.</summary>
    public string Category = "";

    /// <summary>Base Essence cost. Corrupted-ground surcharge (+tier) and the
    /// S3 off-caster tax are applied at cast time, never authored here.</summary>
    public int EssenceCost = 0;

    /// <summary>Subtle / Overt / Grand (§6a). Drives echo emission in S5;
    /// displayed pre-cast from S2.</summary>
    public string Magnitude = "Subtle";

    /// <summary>None / Tile / Path / PatrolToken. S2 implements None + Tile;
    /// Path (Bone Scout, Beast Envoy) and PatrolToken land in S3.</summary>
    public string TargetingType = "None";

    /// <summary>Max hex distance from the party for targeted spells.</summary>
    public int Range = 0;

    /// <summary>School innates are always prepared and occupy no slots.</summary>
    public bool IsInnate = false;

    /// <summary>Always-on passive; no cost, no cast. One per school.</summary>
    public bool IsAttunement = false;

    /// <summary>Hard once-per-expedition cap (Retrace, Parley Compulsion).</summary>
    public bool OncePerExpedition = false;

    /// <summary>Dispatcher key, bespoke per spell (see OverworldSpellManager).
    /// A definition whose key the dispatcher doesn't know renders greyed out
    /// with "(not yet implemented)" rather than failing at cast.</summary>
    public string EffectKey = "";

    /// <summary>Numeric tuning knobs read by the effect (radius, steps, heal…).
    /// Keeps magnitudes in data so tuning passes don't touch code.</summary>
    public Dictionary<string, float> EffectParams = new();

    /// <summary>One-line effect text for the Grimoire panel.</summary>
    public string Description = "";

    // ── Convenience ──────────────────────────────────────────────────────
    /// <summary>Param lookup with default. Effects should always read
    /// through this so a missing key is a tuned default, not a crash.</summary>
    public float Param(string key, float fallback)
        => EffectParams != null && EffectParams.TryGetValue(key, out float v) ? v : fallback;
}
