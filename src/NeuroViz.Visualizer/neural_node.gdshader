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
    EMISSION = v_albedo * (0.15 + glow * 2.0) * global_brightness;
}