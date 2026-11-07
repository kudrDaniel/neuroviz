shader_type spatial;
// Lit (default render_mode) — keeps shadows from N.2-fix2.
// Activation is encoded in COLOR.a by the C# side (1 - activation).
// Albedo comes from COLOR.rgb (already activation-tinted by C#).
// Emissive scales with activation^2 so low activation is subtle and
// peak activation pops.

uniform float global_brightness = 1.0;

varying vec3 v_albedo;
varying float v_activation;

void vertex() {
    v_albedo = COLOR.rgb;
    v_activation = 1.0 - COLOR.a;
}

void fragment() {
    ALBEDO = v_albedo;
    ALPHA = 1.0;

    // Quadratic falloff: act=0.3 → 0.09 → subtle; act=1.0 → 1.0 → pop.
    float glow = v_activation * v_activation;
    // HDR emission: peak values reach ~4.3 (act=1, global_brightness=2),
    // well above the 1.0 glow threshold. Low activation stays below
    // threshold — only active/flashing nodes bloom, not the whole base
    // palette.
    EMISSION = v_albedo * (0.15 + glow * 2.0) * global_brightness;
}