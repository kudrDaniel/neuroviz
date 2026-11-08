shader_type spatial;
render_mode opaque;
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
    // ALPHA write removed. In Godot 4, writing to ALPHA implicitly
    // switches the shader to the transparent pass (blend_mix), which
    // disables Z-buffer writes. Transparent MultiMesh instances are
    // rendered in instance-index order, so the last-created nodes
    // (High layer, closest to centre) were drawn on top of every
    // earlier node regardless of depth. render_mode opaque + no
    // ALPHA write restores proper Z-testing.

    float glow = v_activation * v_activation;
    EMISSION = v_albedo * (0.15 + glow * 2.0) * global_brightness;
}