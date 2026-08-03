shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_opaque, cull_disabled, shadows_disabled;

// ============================================================
// imbuement_aura.gdshader
//
// Purpose:        Shades the elemental FORM standing on an imbued hex —
//                 flames, crystals, plates, ribbons.
// Layer:          Tiles / VFX
// Collaborators:  ImbuementForms.cs (builds the geometry this shades),
//                 ImbuementOverlay.cs (element_id + tint_color),
//                 painterly_grass.gdshader (the wind this shares,
//                 and the toon_band it must step identically to)
// See:            docs/imbuement_painterly_redesign_v1.md §3.6, §3.7
// ============================================================
//
// PASS 3 — THE SILHOUETTE CHANGED, AND SO DID THE MODEL
//
// This used to shade a tapered hex cylinder with noise scrolling inside it. It
// read as a translucent dome because that is what it was. Shader work cannot
// fix a silhouette, so the silhouette is now built per element in
// ImbuementForms.cs and this file shades real geometry.
//
// Three consequences, each of which is a genuine reversal of an earlier
// decision in this file:
//
// 1. blend_add -> blend_mix. Additive glow was the single loudest disagreement
//    with the painterly style guide, and it makes half the table impossible:
//    additive cannot express "this ground is wrong now", so Shadow could never
//    have worked. It also blew every colour toward white over lit grass, which
//    is what forced the alpha ceiling down to 0.55 and cost the elements their
//    read at distance. Mix blending gives that back.
//
//    depth_draw_opaque is Godot's default and is stated here only to be
//    explicit: a material this shader makes transparent will NOT write depth,
//    so the crossed shards inside one form blend in draw order rather than
//    occluding each other. That is the conservative choice — forcing
//    depth_draw_always would make the solid elements look better and would
//    punch hard-edged holes through the wispy ones. If Frost ends up wanting
//    it, that is a second material, not a change here.
//
// 2. ALPHA now comes from the MESH, not from noise. ImbuementForms writes
//    solidity into COLOR.a per vertex, because the thing that decided how tall
//    a shard is should also decide where it stops being solid. The old
//    UV-derived fade guessed at CylinderMesh's side-UV winding, which was
//    never verified and did not have to be.
//
// 3. The noise moved from ALPHA to ALBEDO. On a cylinder, noise-in-alpha was
//    the only way to have any texture at all. On real geometry it reads as a
//    dissolving particle cloud, and painted variation across a solid surface
//    is what the style guide actually asks for. `pattern_cut` per element says
//    how much of the noise is still allowed to eat alpha — high for fire,
//    ~zero for frost and earth, which are supposed to be objects.
//
// UNCHANGED AND STILL LOAD-BEARING: the shared world-space wind. The uniforms
// are name-for-name the grass's and the defaults are the values on
// painterly_grass.tres, NOT the grass shader's own defaults, which that
// material overrides — wave_speed alone differs by 6.7x. `wind_noise` must be
// Painterly_wind_noise.tres; a different noise is a different phase.
// ============================================================

// 0=Fire, 1=Frost, 2=Lightning, 3=Earth, 4=Water, 5=Air, 6=Arcane, 7=Shadow
uniform int element_id : hint_range(0, 7) = 0;

/** Element colour. Driven per tile from UITheme.ElementTint* via ImbuementOverlay. */
uniform vec4 tint_color : source_color = vec4(0.86, 0.44, 0.22, 1.0);

/** Overall opacity gain. */
uniform float intensity : hint_range(0.0, 2.0) = 1.0;

/** Hard cap on emitted alpha. Raised from the additive era's 0.55 — mix blending does not blow out. */
uniform float alpha_ceiling : hint_range(0.0, 1.0) = 0.88;

group_uniforms paint;

/** Quantisation steps. 3-4 reads as paint; 8+ reads as a gradient. */
uniform float aura_bands : hint_range(1.0, 8.0) = 3.0;

/** Band edge softness. 0 = hard cut, 1 = the smooth ramp the grass lighting uses. */
uniform float band_softness : hint_range(0.0, 1.0) = 0.35;

/**
 * Fixed key direction for the facet shading, world space. This shader is
 * `unshaded` on purpose — it is a stylised object, not a lit one — so the form
 * gets its planes from a hand-placed key rather than from the scene's sun.
 * Point it roughly where the sun is and the forms sit in the scene; point it
 * anywhere and they still read, which is the advantage of not being lit.
 */
uniform vec3 key_dir = vec3(0.35, 0.78, 0.52);

/** Darkest facet. Above 0 so nothing crushes to black. */
uniform float facet_low : hint_range(0.0, 1.0) = 0.62;

/** Brightest facet. Above 1 lets a lit plane read hotter than the flat tint. */
uniform float facet_high : hint_range(0.5, 2.0) = 1.22;

/**
 * How white-hot an arc burns. Arcs take the element tint like everything else,
 * but a bolt is not the colour of the crystal it leaps between — it is the
 * colour of the air it just ionised. Only geometry flagged COLOR.g = 1 sees it.
 */
uniform float arc_hot : hint_range(0.0, 1.0) = 0.80;

/** Edge brightening on the gem elements. Cheap stand-in for the way a faceted translucent stone catches light on its silhouette. */
uniform float rim_power : hint_range(1.0, 8.0) = 3.0;

/** Hand-wobble on the noise sampling coordinate. Kills mathematically perfect lines. */
uniform float warp_amount : hint_range(0.0, 0.2) = 0.045;

/** Frequency of that wobble. */
uniform float warp_scale : hint_range(0.5, 12.0) = 5.0;

group_uniforms;

group_uniforms wind;

/** MUST be Painterly_wind_noise.tres — the same asset the grass samples. */
uniform sampler2D wind_noise : hint_default_black, repeat_enable, filter_linear;

/** Gust direction in world XZ. Match painterly_grass.tres. */
uniform vec2  wind_dir = vec2(1.0, 0.35);

/** How far the form leans. The one value that SHOULD differ from the grass. */
uniform float wind_bend : hint_range(0.0, 1.0) = 0.22;

/** Constant lean added to the gust. Match painterly_grass.tres. */
uniform float wind_amplitude : hint_range(0.0, 1.0) = 0.10;

/** Main wave world scale. Match painterly_grass.tres. */
uniform float wave_scale : hint_range(0.01, 1.0) = 0.075;

/** Main wave travel speed. Match painterly_grass.tres. */
uniform float wave_speed : hint_range(0.0, 3.0) = 0.075;

/** Main wave weight. Match painterly_grass.tres. */
uniform float wave_strength : hint_range(0.0, 2.0) = 1.0;

/** Across-wind stretch of the wave. Match painterly_grass.tres. */
uniform float wave_stretch : hint_range(0.05, 1.0) = 0.599;

/** Detail flutter world scale. Match painterly_grass.tres. */
uniform float detail_scale : hint_range(0.1, 6.0) = 0.10;

/** Detail flutter speed. Match painterly_grass.tres. */
uniform float detail_speed : hint_range(0.0, 3.0) = 0.035;

/** Detail flutter weight. Match painterly_grass.tres. */
uniform float detail_strength : hint_range(0.0, 1.0) = 0.22;

/** Base pattern speed, before the per-element rate. */
uniform float flow_speed : hint_range(0.0, 2.0) = 0.35;

/**
 * Tallest point of the form, world units. SET FROM CODE — ImbuementOverlay
 * pushes ImbuementForms.HeightOf(element). It only anchors the wind lean at
 * the base; a stale value tilts the whole form instead of bending it.
 */
uniform float column_height = 1.0;

group_uniforms;

// Full gust (moving part + constant lean), as the grass computes it. Shared by
// every element; lightning's flash keys off it.
varying float v_gust;
// Moving part, already weighted by this element's flutter response.
varying float v_flow;
// The two gust layers kept SEPARATE — the big coherent swing and the fine
// flutter are different physical things and elements respond to them by
// different amounts. Air swings and barely jitters; fire jitters and barely
// swings; frost does neither.
varying float v_wave;
varying float v_detail;
// Solidity, authored per vertex by ImbuementForms.
varying float v_solid;
// Per-shard random, 0..1, authored by ImbuementForms into COLOR.r. Constant
// across one shard and different for every other shard in the form. This is a
// PHASE OFFSET, not a second clock — the tempo still comes from the rate column
// of element_motion(). Without it a "flicker" is the whole form pulsing in
// unison, which reads as a heartbeat rather than as fire.
varying float v_seed;
// 1 on arc geometry (the bolts leaping between Storm's crystals), 0 on bodies.
// Lets one element hold two behaviours without a second material.
varying float v_isarc;
// Per-shard life envelope, 0..1. Only elements with a life_rate use it; it is
// pinned at 1.0 for everything else.
varying float v_life;
// World-space normal for the facet shading.
varying vec3  v_normal;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

// Verbatim from painterly_grass.gdshader. Do not "improve" it in one file only —
// the whole value of this function is that every surface steps the same way.
float toon_band(float x, float bands, float softness)
{
    x = clamp(x, 0.0, 1.0);
    float scaled = x * bands;
    float lower = floor(scaled);
    float f = scaled - lower;
    float soft = smoothstep(0.5 - softness * 0.5, 0.5 + softness * 0.5, f);
    return (lower + soft) / bands;
}

// ── Per-element response tables ──────────────────────────────────────────
//
// ONE grammar, eight parameter rows (redesign §3.7). Read this before
// "fixing" it: the ladders select PARAMETERS, never code, and every row draws
// from the SAME phase source. That is the entire difference between
//
//     "each element behaves differently"      <- what we want
//     "each element runs on its own clock"    <- the original bug
//
// Restoring per-element TIME multipliers would look like these tables and is
// not these tables. Change the numbers freely; do not add a second clock.
//
// The SHAPE half of the same table lives in ImbuementForms.cs. Keep the two in
// agreement: a form with Tilt = -42 (frost, growing inward from the edges)
// wants lean = 0 here, because a crystal that sways is not a crystal.

//   x = lean      how hard the form bends in the shared gust (x wind_bend)
//   y = flutter   response to the fine detail layer — texture agitation
//   z = rate      pattern scroll speed (x flow_speed)
//   w = band_mul  facet count (x aura_bands)
vec4 element_motion(int id) {
    if (id == 0) return vec4(0.75, 1.40, 2.40, 1.00);  // FIRE      barely leans, agitates hard
    if (id == 1) return vec4(0.00, 0.00, 0.30, 1.70);  // FROST     RIGID. does not move in wind.
    if (id == 2) return vec4(0.00, 0.30, 3.20, 0.70);  // STORM     lean MUST stay 0 — arcs are built against fixed crystal tips
    if (id == 3) return vec4(0.04, 0.00, 0.22, 0.70);  // EARTH     planted, heavy slabs
    if (id == 4) return vec4(0.55, 0.20, 0.70, 1.30);  // WATER     slow high-amplitude roll
    if (id == 5) return vec4(1.45, 1.60, 1.30, 1.00);  // AIR       leads the gust hardest
    if (id == 6) return vec4(0.30, 0.60, 0.90, 1.30);  // ARCANE    drifts
    return       vec4(0.45, 0.20, 0.40, 1.00);         // SHADOW    slow writhe
}

//   x = pattern_cut  how much the noise is allowed to eat ALPHA.
//                    ~0 means "this is an object"; high means "this is fire".
//   y = albedo_var   how much the noise varies surface BRIGHTNESS
//   z = rim          silhouette brightening. Only the translucent elements want
//                    it: on an opaque form it reads as a bad outline shader.
//   w                reserved
//   x = life_rate  per-shard birth->death cycles per second. 0 = the shard is a
//                  PERSISTENT OBJECT and none of the rest of this row applies.
//   y = whip       lateral tip displacement, world units
//   z = whip_rate  how fast it whips
//   w = rise       how much the shard stretches over its life
//
// Only Fire uses this, and that is the point rather than an omission. Fire is
// not an object that moves — it is a POPULATION OF SHORT-LIVED THINGS. Fourteen
// flames each being born, rising, thinning and dying on their own seeded phase
// is what makes it read as combustion; a fixed set of shards waving is a
// seaweed bed. Everything else on the board genuinely is an object and should
// not be given a lifecycle just because fire has one.
vec4 element_anim(int id) {
    if (id == 0) return vec4(0.55, 0.10, 3.40, 0.55);  // FIRE
    return vec4(0.0);
}

vec4 element_look(int id) {
    if (id == 0) return vec4(0.88, 0.60, 0.00, 0.0);   // FIRE      dissolves, hard
    if (id == 1) return vec4(0.05, 0.30, 0.45, 0.0);   // FROST     solid crystal, faint edge catch
    if (id == 2) return vec4(0.06, 0.45, 0.95, 0.0);   // STORM     amethyst — the most translucent thing on the board
    if (id == 3) return vec4(0.02, 0.35, 0.00, 0.0);   // EARTH     solid stone
    if (id == 4) return vec4(0.35, 0.40, 0.35, 0.0);   // WATER     part-transparent
    if (id == 5) return vec4(0.55, 0.35, 0.00, 0.0);   // AIR       wispy
    if (id == 6) return vec4(0.45, 0.50, 0.55, 0.0);   // ARCANE    shimmering
    return       vec4(0.40, 0.30, 0.00, 0.0);          // SHADOW    smoky
}

void vertex() {
    // Sample the gust at the TILE, not per vertex: the form is one object being
    // pushed by the wind and must lean as one object. Constant across the mesh,
    // which is also what lets lightning's flash fire across the whole form at once.
    vec3 tile_ws = MODEL_MATRIX[3].xyz;

    vec2 ndir = normalize(wind_dir);
    vec2 perp = vec2(-ndir.y, ndir.x);

    float along  = dot(tile_ws.xz, ndir);
    float across = dot(tile_ws.xz, perp);

    vec2 wave_uv = vec2(along * wave_scale + TIME * wave_speed,
                        across * wave_scale * wave_stretch);
    float wave = texture(wind_noise, wave_uv).r * 2.0 - 1.0;

    vec2 detail_uv = vec2(along, across) * detail_scale + ndir * TIME * detail_speed;
    float detail = texture(wind_noise, detail_uv).r * 2.0 - 1.0;

    vec4 m = element_motion(element_id);

    v_wave   = wave * wave_strength;
    v_detail = detail * detail_strength;
    v_flow   = v_wave + v_detail * m.y;
    v_gust   = v_wave + v_detail + wind_amplitude;

    v_solid  = COLOR.a;
    v_seed   = COLOR.r;
    v_isarc  = COLOR.g;
    v_normal = normalize((MODEL_NORMAL_MATRIX * NORMAL));

    // ImbuementForms puts the base of every shard at y = 0, so this is a plain
    // normalised height with no half-offset — unlike the CylinderMesh this used
    // to shade, which was centred on its origin.
    float h01 = clamp(VERTEX.y / max(column_height, 0.0001), 0.0, 1.0);

    // m.x == 0 means the element genuinely does not move in wind. That is not a
    // tuning value for frost, it is the point of frost.
    float sway = (v_flow + wind_amplitude) * wind_bend * m.x * h01 * h01;

    VERTEX.x += ndir.x * sway;
    VERTEX.z += ndir.y * sway;

    // ── Per-shard life ──────────────────────────────────────────────
    vec4 an = element_anim(element_id);
    v_life = 1.0;
    if (an.x > 0.0)
    {
        // Rise fast, die slow — an asymmetric envelope. A symmetric one reads as
        // breathing, which is the failure mode the swelling had.
        float life = fract(TIME * an.x + v_seed * 17.0);
        float grow = smoothstep(0.0, 0.16, life) * (1.0 - smoothstep(0.42, 1.0, life));
        v_life = grow;

        // The shard STRETCHES as it lives. h01 was measured before this, so the
        // colour and solidity gradients stay pinned to position along the shard
        // rather than sliding as it grows.
        VERTEX.y *= mix(1.0 - an.w * 0.55, 1.0 + an.w * 0.30, grow);

        // Licking. Two axes at different rates so the tip traces a figure rather
        // than a line, scaled by h01^2 so the base stays planted in the ground.
        float p = v_seed * 40.0 + h01 * 4.0;
        VERTEX.x += sin(TIME * an.z + p) * an.y * h01 * h01;
        VERTEX.z += cos(TIME * an.z * 0.83 + p * 0.7) * an.y * 0.7 * h01 * h01;
    }
}

void fragment() {
    vec4 m = element_motion(element_id);
    vec4 k = element_look(element_id);

    vec2 uv = UV;

    // Nothing perfect: a lazy two-axis waver on the sampling coordinate, applied
    // BEFORE the element branch so all eight inherit it and no branch is written
    // twice.
    vec2 w = vec2(vnoise(uv * warp_scale + vec2(v_flow, 0.0)),
                  vnoise(uv * warp_scale + vec2(11.3, 5.7) - vec2(0.0, v_flow)));
    uv += (w - 0.5) * warp_amount;

    // The shared clock, scaled by this element's rate. One phase source, eight
    // tempos — the rates live in element_motion(), not scattered through the
    // branches, so the whole spread is legible in one place.
    float t = TIME * flow_speed * m.z + v_flow * 0.9;

    float pattern;

    if (element_id == 0) {
        // FIRE: every flame on its own beat, all on one gust.
        // ft is this shard's clock — the shared tempo, offset by the shard seed.
        float ft = t + v_seed * 21.7;
        float n1 = vnoise(uv * vec2(4.0, 6.0) + vec2(0.0, -ft));
        float n2 = vnoise(uv * vec2(7.0, 10.0) - vec2(0.0, ft * 0.6));
        float body = pow(n1 * 0.6 + n2 * 0.4, 1.5);

        // The guttering itself: a fast per-shard swing that momentarily starves
        // a flame and lets it recover. Multiplicative, so it thins the flame
        // rather than dimming it — a dimming flame reads as a light source
        // being turned down, a thinning one reads as combustion.
        float gutter = 0.45 + 0.55 * vnoise(vec2(v_seed * 37.0, ft * 2.6));
        // And a sharper tip flicker, biased to the top of the shard where the
        // flame is already dissolving.
        float tipflick = mix(1.0, 0.25 + 0.75 * vnoise(vec2(v_seed * 91.0, ft * 5.2)), uv.y);

        pattern = body * gutter * tipflick;

    } else if (element_id == 1) {
        // FROST: slow settle. Nothing here reads the gust — the table zeroes
        // this element's flutter response, so v_flow contributes nothing.
        float n1 = vnoise(uv * vec2(3.5, 4.5) + vec2(0.0, t));
        float n2 = vnoise(uv * vec2(6.0, 7.0) + vec2(t * 0.4, t * 1.2));
        pattern = (n1 * 0.6 + n2 * 0.4) * 0.9;

    } else if (element_id == 2) {
        // STORM: conductor crystals, and lightning arcing BETWEEN them.
        // One element, two behaviours, told apart by the arc flag the mesh
        // authored into COLOR.g. This is why it needed new geometry rather than
        // new numbers — bare filaments gave the eye nothing to hold between
        // flashes, so the tile read as static no matter how fast the noise ran.
        float ft = t + v_seed * 13.3;

        if (v_isarc > 0.5) {
            // THE BOLT. Exists in bursts and is absent the rest of the time.
            // step() on a floor()ed clock snaps; anything smooth here reads as a
            // glowing wire strung between the crystals.
            float burst = step(0.62, vnoise(vec2(v_seed * 61.0, floor(ft * 3.0))));
            // A head racing along the arc, so even a short burst has direction.
            float head = fract(ft * 2.4 + v_seed);
            float trav = smoothstep(0.40, 0.0, abs(uv.y - head));
            pattern = clamp(burst * (0.45 + trav * 1.10), 0.0, 1.0);
        } else {
            // THE CRYSTAL. Persistent and solid — it is what makes the tile
            // readable in the gaps between arcs. A charge climbs it, and it
            // brightens on the shared gust crest with everything else.
            float climb = fract(ft * 0.55 + v_seed);
            float glow  = smoothstep(0.30, 0.0, abs(uv.y - climb));
            pattern = 0.40 + glow * 0.45 + smoothstep(0.35, 0.80, v_gust) * 0.15;
        }
    } else if (element_id == 3) {
        // EARTH: chunky slow blotches. Stone, so this is nearly all albedo.
        float n = vnoise(uv * vec2(3.0, 4.0) + vec2(t, 0.0));
        pattern = smoothstep(0.35, 0.75, n) * 0.9 + 0.1;

    } else if (element_id == 4) {
        // WATER: ripples travelling up the collar.
        float ripple = sin(uv.y * 9.0 - t * 2.0);
        float band = vnoise(uv * vec2(5.0, 3.0) + vec2(t * 0.4, -t * 0.5));
        pattern = (ripple * 0.3 + 0.6) * band;

    } else if (element_id == 5) {
        // AIR: a chase running along each ribbon.
        float spiral = sin(uv.y * 7.0 + uv.x * 4.0 - t * 2.5);
        pattern = smoothstep(0.1, 0.9, spiral * 0.5 + 0.5) * 0.85;

    } else if (element_id == 6) {
        // ARCANE: sparkle field. The twinkle keeps a small independent term —
        // a sparkle that only fires on the gust stops being a sparkle.
        float n = vnoise(uv * vec2(12.0, 16.0));
        float twinkle = step(0.72, n) * (0.5 + 0.5 * sin(t * 5.0 + n * 10.0));
        pattern = 0.25 + twinkle * 0.75;

    } else {
        // SHADOW: dense low writhe.
        float n = vnoise(uv * vec2(4.0, 3.0) + vec2(t, t * 0.5));
        pattern = pow(n, 2.0) * 0.9 + 0.1;
    }

    pattern = toon_band(clamp(pattern, 0.0, 1.0), max(aura_bands * m.w, 1.0), band_softness);

    // ── Facets ──────────────────────────────────────────────────────
    // Two-sided by construction: abs() on the key dot, because these shards are
    // cull_disabled single-sided quads and a back face must shade like a front
    // face. Same rule the painterly light() functions follow.
    float ndl   = abs(dot(normalize(v_normal), normalize(key_dir)));
    float facet = toon_band(ndl, max(aura_bands * m.w, 1.0), band_softness);

    float shade = mix(facet_low, facet_high, facet);
    // The noise now paints the surface instead of dissolving it. On a cylinder
    // there was nothing else it could do; on real geometry, dissolving is what
    // made it read as a particle system.
    shade *= mix(1.0, 0.55 + pattern * 0.75, k.y);

    // ── Alpha ───────────────────────────────────────────────────────
    // Solidity is authored in the MESH. Noise only eats into it by this
    // element's pattern_cut, which is ~0 for the elements that are supposed to
    // be objects.
    // Arc geometry ignores its element's pattern_cut and takes the full noise
    // into alpha — a bolt that is only ever dimmer is a wire, not a bolt.
    float cut = mix(k.x, 1.0, v_isarc);

    // v_life is 1.0 for every element without a lifecycle, so this multiply is
    // free everywhere except fire, where it is what lets a flame go OUT.
    float a = v_solid * mix(1.0, pattern, cut) * intensity * v_life;

    // ── Gem rim ─────────────────────────────────────────────────────
    // NORMAL and VIEW are both view-space here, so this is a plain facing term
    // with no extra plumbing. abs() again because the shards are cull_disabled
    // and a back face has to catch the edge exactly like a front face does.
    float rim = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), rim_power) * k.z;

    vec3 col = tint_color.rgb * shade;
    col += tint_color.rgb * rim * 0.9 + vec3(rim * 0.25);

    // An arc is not the colour of the crystal it leaps between. Blowing it
    // toward white is what separates "lightning" from "a glowing purple wire",
    // and it is the whole reason the arc flag exists as geometry data rather
    // than as a second material.
    col = mix(col, mix(tint_color.rgb, vec3(1.0), arc_hot) * (0.85 + pattern * 0.9), v_isarc);

    ALBEDO = col;
    ALPHA  = clamp(a * tint_color.a, 0.0, alpha_ceiling);
}
