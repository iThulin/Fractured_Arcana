shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

// Per-element color (set from code)
uniform vec4 tint_color : source_color = vec4(1.0, 0.4, 0.1, 1.0);

// Visual knobs
uniform float scroll_speed : hint_range(0.0, 2.0) = 0.35;
uniform float noise_scale  : hint_range(0.5, 8.0) = 2.5;
uniform float intensity    : hint_range(0.0, 4.0) = 1.6;
uniform float pulse_speed  : hint_range(0.0, 4.0) = 1.2;
uniform float pulse_amount : hint_range(0.0, 1.0) = 0.35;
uniform float edge_falloff : hint_range(0.5, 8.0) = 2.0;

// Cheap value-noise so we don't need a texture asset.
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

void fragment() {
    vec2 uv = UV * noise_scale;
    float t = TIME * scroll_speed;
    float n1 = vnoise(uv + vec2(t, t * 0.5));
    float n2 = vnoise(uv * 1.7 - vec2(t * 0.7, -t * 0.4));
    float shimmer = (n1 * 0.6 + n2 * 0.4);

    float r = distance(UV, vec2(0.5)) * 2.0;
    float radial = pow(clamp(1.0 - r, 0.0, 1.0), edge_falloff);

    float pulse = 1.0 - pulse_amount + pulse_amount * sin(TIME * pulse_speed) * 0.5 + pulse_amount * 0.5;

    float alpha = shimmer * radial * pulse * intensity;

    ALBEDO = tint_color.rgb;
    ALPHA  = clamp(alpha * tint_color.a, 0.0, 1.0);
}
