shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;

// 0=Fire, 1=Frost, 2=Lightning, 3=Earth, 4=Water, 5=Air, 6=Arcane, 7=Shadow
uniform int element_id : hint_range(0, 7) = 0;

uniform vec4 tint_color : source_color = vec4(1.0, 0.4, 0.1, 1.0);
uniform float intensity : hint_range(0.0, 4.0) = 1.4;

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
    // UV.y goes 0 (bottom of cylinder) to 1 (top). We use it for vertical
    // motion direction & fade.
    vec2 uv = UV;

    // Bottom-to-top fade so the column dissipates upward like steam/embers.
    float vertical_fade = pow(1.0 - uv.y, 1.4);

    // Per-element noise pattern for the column body.
    float pattern;

    if (element_id == 0) {
        // FIRE: upward-rising flickers, fast vertical scroll
        float n1 = vnoise(uv * vec2(4.0, 6.0) + vec2(0.0, -TIME * 1.2));
        float n2 = vnoise(uv * vec2(7.0, 10.0) - vec2(0.0, TIME * 0.7));
        pattern = pow(n1 * 0.6 + n2 * 0.4, 1.5);

    } else if (element_id == 1) {
        // FROST: slow downward drift (it's falling snow/cold), softer
        float n1 = vnoise(uv * vec2(3.5, 4.5) + vec2(0.0, TIME * 0.25));
        float n2 = vnoise(uv * vec2(6.0, 7.0) + vec2(TIME * 0.1, TIME * 0.3));
        pattern = (n1 * 0.6 + n2 * 0.4) * 0.7;

    } else if (element_id == 2) {
        // LIGHTNING: vertical streaks that flash
        float streak = abs(sin(uv.x * 18.0 + TIME * 8.0));
        float flash = step(0.7, vnoise(vec2(TIME * 3.0, uv.y * 2.0)));
        pattern = pow(1.0 - streak, 6.0) * (0.5 + flash * 0.7);

    } else if (element_id == 3) {
        // EARTH: chunky slow-moving blobs, mostly low
        float n = vnoise(uv * vec2(3.0, 4.0) + vec2(TIME * 0.15, 0.0));
        pattern = smoothstep(0.4, 0.7, n) * 0.9;
        // Earth is heavier, so weight it toward the bottom
        vertical_fade *= pow(1.0 - uv.y, 0.5);

    } else if (element_id == 4) {
        // WATER: horizontal ripples drifting up slowly
        float ripple = sin(uv.y * 14.0 - TIME * 1.5);
        float band = vnoise(uv * vec2(5.0, 3.0) + vec2(TIME * 0.3, -TIME * 0.4));
        pattern = (ripple * 0.3 + 0.5) * band;

    } else if (element_id == 5) {
        // AIR: spirals — uv.x is angle around the cylinder, so a moving
        // sin gives a clean swirl.
        float spiral = sin(uv.x * 12.566 + uv.y * 6.0 - TIME * 2.0);
        pattern = smoothstep(0.2, 0.9, spiral) * 0.8;

    } else if (element_id == 6) {
        // ARCANE: shimmering sparkles — high-frequency dots that twinkle
        float n = vnoise(uv * vec2(20.0, 25.0));
        float twinkle = step(0.85, n) * (0.5 + 0.5 * sin(TIME * 4.0 + n * 10.0));
        pattern = twinkle;

    } else {
        // SHADOW: dark dense base with slow, low writhing tendrils
        float n = vnoise(uv * vec2(4.0, 3.0) + vec2(TIME * 0.2, TIME * 0.1));
        pattern = pow(n, 2.0) * 0.8;
        vertical_fade *= pow(1.0 - uv.y, 0.3);
    }

    float alpha = pattern * vertical_fade * intensity;

    ALBEDO = tint_color.rgb;
    ALPHA  = clamp(alpha * tint_color.a, 0.0, 1.0);
}
