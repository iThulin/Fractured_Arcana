// ============================================================
// snow_cover.gdshaderinc
//
// Purpose:   Snow that settles on whatever faces the sky. One mask
//            function shared by every shader that should carry snow
//            (terrain_splat, painterly_rock, and any prop shader that
//            includes this file).
// How:       Compare how far a surface points up against a line that
//            drops as snow_cover rises. Noise wobbles the line so the
//            snow edge is ragged, not a ruler-straight contour.
// Cost:      One smoothstep per pixel. No extra pass, texture or light.
// Driver:    The GLOBAL uniform snow_cover (0 = none, 1 = heavy). It
//            is declared in Project Settings > Globals > Shader Globals
//            and set per map by HexGridManager.ApplySnowCover from the
//            recipe atmosphere key "snow_cover".
// Limits:    No shelter test. Ground under an overhang still gets snow.
// See:       docs/snow_ice_ground_v1.md section 3.3
// ============================================================

global uniform float snow_cover;

group_uniforms snow_cover_settings;
/** Colour of settled snow on this material. */
uniform vec3  snow_cover_color : source_color = vec3(0.90, 0.92, 0.95);
/** Steepest face that still holds snow at full cover. 1 = only dead-flat tops, 0 = vertical walls too. */
uniform float snow_cover_threshold : hint_range(0.0, 1.0) = 0.5;
/** Softness of the snow edge. Low = hard painted edge. */
uniform float snow_cover_softness : hint_range(0.01, 0.5) = 0.08;
/** How far noise pushes the snow edge around. 0 = a clean contour. */
uniform float snow_cover_jitter : hint_range(0.0, 1.0) = 0.5;
group_uniforms;

// up      : how far the surface points at the sky, 0..1 (world normal Y).
// noise01 : any 0..1 noise sampled on the surface. Breaks up the edge.
// Returns 0 (bare) to 1 (snow).
float snow_cover_mask(float up, float noise01) {
	float amount = clamp(snow_cover, 0.0, 1.0);
	// With no snow the line sits above anything a surface can reach.
	// At full cover it drops to snow_cover_threshold.
	float line = mix(1.3, snow_cover_threshold, amount);
	float v = clamp(up, 0.0, 1.0) + (noise01 - 0.5) * snow_cover_jitter;
	return smoothstep(line, line + snow_cover_softness, v) * step(0.001, amount);
}
