using Godot;

// ============================================================
// GrassMeshVariant.cs
//
// One weighted entry in the painterly grass mesh palette. Add several
// to HexGridManager.GrassMeshVariants (in the inspector) to mix blade
// shapes; each spawned blade picks a variant at random with probability
// proportional to Weight, then takes a per-variant scale, tint, and
// terrain eligibility.
//
// MUST live in its own file named to match the class so Godot's
// [GlobalClass] registration resolves the script path and offers a
// "New GrassMeshVariant" entry in the inspector array.
//
// Mesh requirements for the painterly_grass shader:
//   - UV.y must run 0 (base) -> 1 (tip) for the height gradient.
//   - Generate tangents on the mesh if you enable use_normal_map.
//   - If you mix variants of DIFFERENT heights, keep the shader on the
//     default UV.y stiffness mode (stiffness_from_model_height OFF):
//     model_height is a single uniform and can't track per-variant height.
// ============================================================

[GlobalClass]
public partial class GrassMeshVariant : Resource
{
    /** The blade mesh for this variant. UV.y should run 0 (base) -> 1 (tip); add tangents if you use the normal map. */
    [Export] public Mesh Mesh;

    /** Relative spawn weight. A variant with Weight 2 appears twice as often as one with Weight 1. Zero/negative = never picked, UNLESS every eligible weight is <= 0, in which case eligible variants spawn uniformly. With clumping on, this sets the variant's purity INSIDE its pockets (higher = fewer other variants mixed in). */
    [Export(PropertyHint.Range, "0.0,10.0,0.05")] public float Weight = 1.0f;

    /** 0 = this variant spreads evenly by Weight (salt-and-pepper, good for FILL grass). 1 = placement is fully gated by a low-frequency clump field, so the variant forms CONNECTED pockets and is absent outside them (good for a tall feature grass). Values between blend the two. */
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float ClumpInfluence = 0.0f;

    /** World-space frequency of this variant's clump field. Lower = larger, broader pockets; higher = smaller, more scattered patches. Each variant gets its own independently-seeded field, so pockets of different variants don't coincide. Only used when ClumpInfluence > 0. */
    [Export(PropertyHint.Range, "0.01,0.5,0.005")] public float ClumpScale = 0.08f;

    /** Roughly the fraction of the grassy area this variant's pockets cover (drives the clump-field threshold). Lower = rarer, more isolated pockets; higher = pockets merge to cover most of the field. Only used when ClumpInfluence > 0. */
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float ClumpCoverage = 0.4f;

    /** Lower bound of this variant's extra scale multiplier (uniform on width AND height), applied on top of GrassScale and before the per-blade jitter. 1.0 = no change. Bake aspect ratio (tall reed vs flat blade) into the MESH; use this only for size spread. */
    [Export(PropertyHint.Range, "0.05,4.0,0.01")] public float ScaleMin = 1.0f;

    /** Upper bound of this variant's extra scale multiplier. Each blade samples uniformly in [ScaleMin, ScaleMax]. Set equal to ScaleMin for a fixed per-variant size (no random draw). */
    [Export(PropertyHint.Range, "0.05,4.0,0.01")] public float ScaleMax = 1.0f;

    /** Flat tint multiplied into this variant's blades via MultiMesh instance colour (shader COLOR hook). White = inert, no instance-colour buffer is created. NOTE: instance colours are renderer-sensitive — verify on the GL Compatibility / Metal-compat target. For broad meadow colour drift prefer the shader's mass_tint instead; use this for discrete cases like a dry/dead-grass mesh. */
    [Export] public Color Tint = Colors.White;

    /** Allow this variant on Grass tiles. */
    [Export] public bool AllowOnGrass = true;

    /** Allow this variant on Forest tiles (only relevant when HexGridManager.GrassOnForest is on). Use this to target ferns/undergrowth meshes to forest only. */
    [Export] public bool AllowOnForest = true;
}
