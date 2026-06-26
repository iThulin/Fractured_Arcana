# Fractured Arcana — Painterly Shader Style Guide

**Purpose of this document.** This is the authoritative reference for the "painterly" visual system in Fractured Arcana (Godot 4.6 stable.mono, C#, GL **Compatibility** renderer). Drop it into project knowledge. When starting a new conversation about painterly shaders, point Claude here first. It explains the aesthetic goals, the shared technical model every painterly shader is built on, the conventions, the build process for a new painterly shader, a copy-paste skeleton, and the hard-won gotchas so we never re-derive them.

If a live file is pasted in a session, that file overrides anything here — this is a snapshot and can drift.

---

## 1. Aesthetic goals

The target is a **stylized, hand-painted, Ghibli/anime meadow** read — not photorealism. Cohesion and tactical readability outrank fidelity, because this is a tactical hex-grid game where the board must stay legible.

Concrete principles that define the look:

- **Flowers are the highest-leverage element.** The painterly read is carried mostly by flower color pops (warm yellow / soft violet / off-white) against muted greens. When the scene looks flat, add/tune flowers before anything else.
- **Muted, wide-range greens — not neon.** Saturation lives around ~1.1, with meaningful color *variation* across the field (clump tints, per-blade jitter). Over-saturated single-green = wrong.
- **Bare ground breaks through.** Grass clumps with gaps between them read more naturally than an even carpet. Density and clump contrast matter.
- **Soft, banded toon lighting**, never hard PBR specular. Stylized cel banding with wrapped, floored light so nothing crushes to black.
- **Gentle coherent wind.** The whole field sways together in rolling gusts, not as independent per-blade noise. This coherence is the single most important motion cue.

---

## 2. The shared technical foundation

Every painterly prop shader (grass, flowers, and any future one) is built on the **same three pillars**. Replicating them is what makes a new shader belong to the family.

### 2.1 Two-layer world-space wind (the signature)

This is the heart of the system. Two noise layers sampled in **world space** and scrolled by `TIME`:

- **Wave layer** — the big rolling gust. Low spatial frequency (`wave_scale`), scrolls along `wind_dir` at `wave_speed`, weighted by `wave_strength`. `wave_stretch` stretches it across the wind so gusts read as long streaks.
- **Detail layer** — fine flutter on top. Higher frequency (`detail_scale`), own speed/strength.

The combined `gust` (plus a constant `wind_amplitude` lean) bends the vertex along `wind_dir`, weighted by `stiffness * stiffness` so the **base is anchored and the tips move most**. A small downward `VERTEX.y` nudge fakes foreshortening as it bends.

**Why world space is non-negotiable:** sampling the gust by world position means a grass blade and the flower next to it read the *same* gust value, so they bend together. Sample in object/UV space instead and every prop sways independently — the coherence (the whole point) is lost.

**Phase matching:** any new prop that should sway "with the grass" must use the **same `wind_noise` texture and the same `wave_*` / `detail_*` / `wind_dir` values**. `wind_bend` (amplitude of bend) can differ per prop — stiffer props bend less — but the *wave timing values must match* or props drift out of phase, which reads as wrong.

### 2.2 Custom two-sided toon `light()`

Painterly props are thin, two-sided geometry rendered with `cull_disabled`. A naïve `NdotL` clamps away-facing and back faces to black, darkening the whole field wherever you're not looking down-sun. The fix:

- `light()` uses **`abs(dot(N, LIGHT))`** so both faces catch light.
- **Toon banding** via `toon_bands` / `toon_softness`, with `toon_wrap` to soften the terminator.
- **`ambient_fill`** is a floor on the lit value so shadowed parts keep their color instead of going black.

**Compatibility fallback:** if a shader renders black on the GL Compatibility backend, delete the custom `light()` and add `diffuse_toon` to `render_mode`. (Custom `light()` occasionally misbehaves on Compatibility; this is the escape hatch.)

### 2.3 Render mode + stiffness

- `render_mode cull_disabled, specular_disabled;` — two-sided, no PBR highlight. Grass adds `alpha_to_coverage` for MSAA-resolved distance fade.
- **Stiffness** (`0` at base, `1` at tip) drives both the sway weight and tip color effects. Default source is `UV.y` (author meshes base-at-bottom, tip-at-top with vertical UVs); optional `stiffness_from_model_height` derives it from local Y for meshes without clean UVs.

---

## 3. Color paths

A painterly shader picks one of these per use:

- **Multi-stop height gradient** (grass): base → mid → tip → highlight colors blended by stiffness, plus world-space "mass tint" clumping for whole-region color variation, plus per-blade hash jitter. Rich, but tuned for foliage.
- **Per-instance palette** (flower petals): the color comes from **`INSTANCE_CUSTOM`** (MultiMesh custom data), giving each instance a random color from a palette array. See §5 for why custom data, not instance color.
- **Flat color** (flower stem/center surfaces): one `flat_color`, still swayed and lit, for parts that shouldn't vary.

Common to all: `saturation` control (luma-lerp), optional per-instance brightness jitter from a **world-space hash** so identical meshes don't look cloned.

---

## 4. MultiMesh scatter integration

Painterly props are placed as `MultiMesh` instances by partials of `HexGridManager` (e.g. `HexGridManager.PainterlyGrass.cs`, `HexGridManager.Flowers.cs`):

- One `MultiMesh` **per mesh variant** (a MultiMesh holds only one mesh). A variant pool is supported by bucketing scatter points per chosen mesh.
- Props sit on the blended terrain by sampling the same surface height the grass uses (`SampleGrassSurfaceY`).
- **Shared clump noise:** grass and flowers sample the *same* low-frequency noise field (same seed `MapSeed ^ 0x13577531`, same `GrassClumpFrequency`) so flowers bloom inside the grass masses rather than scattering into bare pockets.
- Hex containment via six half-plane tests; placement is rejection-sampled inside the tile hexagon.
- Rebuilt on map regen (F6); each field is tagged in a group and cleared before respawn.

---

## 5. Per-instance color: use CUSTOM DATA, not instance color

**The trap (learned the hard way):** in Godot, MultiMesh **instance color** and **mesh vertex color** both feed the shader's `COLOR` builtin and are **multiplied together**. So you cannot use instance color for a palette *and* vertex color for a mask — yellow × green-mask = mud, and a black-painted region × any palette = black.

**The fix:** write the per-instance palette to **`SetInstanceCustomData`** (`UseCustomData = true`), read it as `INSTANCE_CUSTOM` in the shader. Custom data does **not** multiply vertex color, so the two are independent. This is how flower petals get their palette while leaving other channels free.

**Per-instance limit:** custom data is one value per instance. A multi-bloom cluster mesh is one instance, so it gets one palette color across all its blooms. Within-cluster color variation requires baking colors into the mesh (see §6).

---

## 6. Multi-part meshes: prefer per-surface materials

For a mesh with distinct parts (petal / stem / center), the robust way to color them differently is **separate material slots in Blender → separate surfaces on import**, each surface getting its own Godot material. This beats vertex-color masks because:

- Surfaces import reliably from `.blend`; **vertex colors do not** (see §8).
- No multiply conflict, no color-space risk, no paint step.

Implementation: assign three material slots in Blender (e.g. Petal/Stem/Center). On import the mesh has three surfaces (named after the Blender materials). Set each surface's material on the **Mesh resource** (`.tres`) — for a MultiMesh, surface materials must live on the resource, since `MultiMeshInstance3D` has no per-surface overrides. In `HexGridManager.Flowers.cs`, enable `UseMeshSurfaceMaterials` so the scatter does **not** force a single `MaterialOverride`.

One shared shader serves all surfaces via a `use_flat_color` toggle: OFF for petals (palette from custom data), ON for stem/center (one `flat_color`), all swaying together because they share the same wind vertex code.

---

## 7. Conventions for painterly shaders

- **Organize the inspector** with `group_uniforms <name>;`. Each group renders as a collapsible section.
- **Inspector tooltips require `/** ... */` doc comments** (two leading asterisks) placed **directly above** the uniform. A `//` comment or a single-asterisk `/* */` is code-only and shows a **blank** tooltip on hover. Follow-up-line asterisks are stripped by the inspector. Always document new uniforms this way.
- **Color uniforms** use the `: source_color` hint so the inspector shows a proper color picker (and handles gamma).
- **Ranged scalars** use `: hint_range(min, max, step)` so they appear as sliders.
- **Reserved words are not allowed as identifiers** — including group names. `flat`, `varying`, `uniform`, `in`, `out`, etc. are off-limits (e.g. use `flat_fill`, not `flat`).
- **Float literals** always have a digit each side of the dot (`1.0`, `0.5`) per Godot style.
- **Match the grass wind values** on any prop meant to share the gust (§2.1).
- **C# conventions** (whole project): no namespaces, Allman braces, `_camelCase` private, `PascalCase` public, 4-space C#. UI colors reference `UITheme`. Live files override the project-knowledge snapshot; ask for the current file before patching large ones.

---

## 8. Gotchas / hard-won learnings

- **`.blend` vertex-color import is fragile** on Blender 5.0 + Godot 4.6 — it can drop the color attribute or bring it in black. Don't rely on painted vertex colors for masks. Use per-surface materials (§6), or if you must use vertex colors, export `.glb` manually (File → Export → glTF 2.0, Data → Mesh → **Color** checked).
- **Blender 5.0 new color attributes initialize BLACK**, not white. If you paint "only the stem" expecting white petals, the petals are black and read wrong. Always fill white first if you go the vertex-color route.
- **Instance color × vertex color multiply** (§5) — the root of the long flower-color debugging saga. Custom data avoids it.
- **Mesh origin at the BASE (Y=0)**, not the center — sway pivots from the base and placement uses the base. **Apply scale** in Blender (`Ctrl+A → Scale`) before export so the C# `*Scale` multiplies a 1.0 baseline.
- **`GenerateTangents()`** on the `SurfaceTool` is required for normal maps to work; triplanar mapping avoids UV-unwrap requirements on tile meshes.
- **MSAA 4× required** for sub-pixel blade-edge quality (Project Settings → Rendering → Anti Aliasing → MSAA 3D). **TAA is unavailable** in the Compatibility renderer.
- **Custom `.tres` materials don't auto-inject `wind_noise`** — set the noise slot manually on each material.
- **Highlighting** uses emission (`EmissionEnabled`, `Emission = color * intensity`), not `AlbedoColor` tint, to preserve texture appearance.
- **Diagnostic: `fallback_color`.** The flower shader renders `fallback_color` (yellow) when no custom data reaches it. Yellow flowers in the live scatter = custom data isn't arriving (check `UseFlowerColorVariation` is on and rebuild). All-one-color or black flowers usually = a material/surface assignment problem, not the shader.

---

## 9. Skeleton for a new painterly shader

Copy this as the starting point. It has the wind vertex model, the two-sided toon `light()`, stiffness, per-instance jitter, and documented uniforms. Add the color path you need in `fragment()`.

```glsl
shader_type spatial;
// Two-sided toon light(); if it renders black on GL Compatibility,
// delete light() and add `diffuse_toon` to render_mode.
render_mode cull_disabled, specular_disabled;

// ---- Wind (copy values from the grass tuner to stay in phase) ----
group_uniforms wind;
/** Seamless noise texture driving the wind. Use the SAME texture as the grass. */
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;
/** Wind direction on the ground plane (x, z). Match the grass. */
uniform vec2 wind_dir = vec2(1.0, 0.35);
/** How far this prop bends. Stiffer props use less. Keep wave_* matched to grass. */
uniform float wind_bend : hint_range(0.0, 1.0) = 0.12;
/** Constant baseline lean so it never stands perfectly still. */
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.4;
/** Spatial size of the big rolling gust. Match the grass. */
uniform float wave_scale : hint_range(0.01, 1.0) = 0.12;
/** Scroll speed of the main gust. Match the grass. */
uniform float wave_speed : hint_range(0.0, 3.0) = 0.5;
/** Strength of the main gust (dominant motion). */
uniform float wave_strength : hint_range(0.0, 2.0) = 0.7;
/** Stretches the gust across the wind into streaks. */
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.35;
/** Spatial size of the fine flutter layer. */
uniform float detail_scale : hint_range(0.1, 6.0) = 1.2;
/** Scroll speed of the fine flutter. */
uniform float detail_speed : hint_range(0.0, 3.0) = 0.25;
/** Strength of the fine flutter. */
uniform float detail_strength : hint_range(0.0, 1.0) = 0.25;

// ---- Toon ----
group_uniforms toon;
/** Number of flat shading steps. */
uniform float toon_bands : hint_range(1.0, 6.0) = 3.0;
/** Softness of the band edges. 0 = hard cel. */
uniform float toon_softness : hint_range(0.0, 1.0) = 0.20;
/** Wraps light around so shadows aren't pure dark. */
uniform float toon_wrap : hint_range(0.0, 1.0) = 0.25;
/** Floor on lit value so shadowed parts keep colour. */
uniform float ambient_fill : hint_range(0.0, 1.0) = 0.40;

// ---- Colour ----
group_uniforms color;
/** Per-instance brightness jitter so clones don't look identical. */
uniform float petal_variation : hint_range(0.0, 0.5) = 0.10;
/** Saturation. 1.0 = unchanged. */
uniform float saturation : hint_range(0.0, 2.0) = 1.05;
/** Base colour (replace with gradient / palette / flat as needed). */
uniform vec3 base_color : source_color = vec3(0.4, 0.7, 0.3);

varying float v_stiffness;
varying float v_jitter;

float hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 345.45));
    p += dot(p, p + 34.345);
    return fract(p.x * p.y);
}

float toon_band(float x, float bands, float softness)
{
    x = clamp(x, 0.0, 1.0);
    float scaled = x * bands;
    float lower = floor(scaled);
    float f = scaled - lower;
    float soft = smoothstep(0.5 - softness * 0.5, 0.5 + softness * 0.5, f);
    return (lower + soft) / bands;
}

void vertex()
{
    float stiffness = clamp(UV.y, 0.0, 1.0); // base = 0, tip = 1
    v_stiffness = stiffness;

    vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_jitter = hash21(floor(world_pos.xz * 7.0)) * 2.0 - 1.0;

    vec2 ndir = normalize(wind_dir);
    vec2 perp = vec2(-ndir.y, ndir.x);
    float along = dot(world_pos.xz, ndir);
    float across = dot(world_pos.xz, perp);

    vec2 wave_uv = vec2(along * wave_scale + TIME * wave_speed,
                        across * wave_scale * wave_stretch);
    float wave = texture(wind_noise, wave_uv).r * 2.0 - 1.0;

    vec2 detail_uv = vec2(along, across) * detail_scale + ndir * TIME * detail_speed;
    float detail = texture(wind_noise, detail_uv).r * 2.0 - 1.0;

    float gust = wave * wave_strength + detail * detail_strength + wind_amplitude;
    float sway = gust * wind_bend * stiffness * stiffness;
    VERTEX.x += ndir.x * sway;
    VERTEX.z += ndir.y * sway;
    VERTEX.y -= abs(sway) * 0.15 * stiffness;
}

void fragment()
{
    // Replace with the color path you need (gradient / INSTANCE_CUSTOM palette / flat).
    vec3 col = base_color * (1.0 + v_jitter * petal_variation);

    if (saturation != 1.0)
    {
        float luma = dot(col, vec3(0.299, 0.587, 0.114));
        col = mix(vec3(luma), col, saturation);
    }

    ALBEDO = clamp(col, 0.0, 1.0);
    ROUGHNESS = 1.0;
    SPECULAR = 0.0;
}

void light()
{
    vec3 N = normalize(NORMAL);
    float ndl = abs(dot(N, normalize(LIGHT)));
    ndl = (ndl + toon_wrap) / (1.0 + toon_wrap);
    float banded = max(toon_band(ndl, toon_bands, toon_softness), ambient_fill);
    DIFFUSE_LIGHT += banded * ATTENUATION * LIGHT_COLOR * ALBEDO;
}
```

---

## 10. Process for building a new painterly shader

1. **Start from the skeleton** (§9). You inherit wind + toon + stiffness + jitter for free.
2. **Choose the color path** (§3): height gradient, per-instance palette via `INSTANCE_CUSTOM`, flat, or a mix.
3. **If multi-part**, decide masks vs surfaces. Default to **per-surface materials** (§6) — they're robust. Only reach for vertex-color masks if surfaces aren't viable, and then expect import pain (§8).
4. **If per-instance color**, route the palette through **custom data** (§5), not instance color.
5. **Wire the wind**: assign the grass `wind_noise` texture and copy the grass tuner's `wave_*` / `detail_*` / `wind_dir`. Tune only `wind_bend` per prop.
6. **Document every uniform** with `/** */` doc comments (§7).
7. **Test on Compatibility.** If it's black, apply the `diffuse_toon` fallback (§2.2).
8. **Tune toward the aesthetic** (§1): saturation ~1.1, real color variation, gentle coherent sway, color from flowers.

---

## 11. Current painterly file inventory

(Snapshot — a pasted live file always wins.)

- `painterly_grass.gdshader` — grass blades: multi-stop height gradient, mass-tint clumping (wind-following), two-layer wind, distance fade (`alpha_to_coverage` / dither fallback), atmospheric depth tint, tip translucency/backlight, AO root-darkening, optional albedo/normal maps.
- `painterly_flower.gdshader` — flower props: shared wind + toon, **petal color from `INSTANCE_CUSTOM` palette**, `use_flat_color` flat path for stem/center surfaces, tip brighten, per-instance jitter, saturation. (Stripped of the earlier mask/height-stem/radial-center paths once the surface-material approach replaced them.)
- `HexGridManager.PainterlyGrass.cs` — grass scatter: clump noise, terrain-follow (`SampleGrassSurfaceY`), per-tile blade counts, density presets.
- `HexGridManager.Flowers.cs` — flower scatter: weighted mesh-variant pool, shared clump noise, palette → custom data, `UseMeshSurfaceMaterials` toggle for per-surface materials.
- `terrain_splat.gdshader` — terrain: per-theme atmosphere, persistent hex grid lines (SDF), hover/move emission highlights, triplanar normals.
- `HexMeshBuilder.cs` — terrain mesh: per-terrain noise amplitude/frequency, barycentric subdivision, seam-safe blending, cliff system, `GenerateTangents()`.
- `PainterlyGrassTuner.cs` — live in-editor grass tuner panel.

---

## 12. How to prompt a future session

Paste something like:

> "Read `painterly_style_guide.md` in project knowledge. I want to build a new painterly [X] shader / extend the [grass/flower] system. Follow the shared wind + toon model and the conventions in the guide."

Then paste the **current live shader(s)** involved, since the guide is a snapshot and live files override it. Ask Claude to lead with the strongest counterargument and attach confidence levels (standard working style for this project).
