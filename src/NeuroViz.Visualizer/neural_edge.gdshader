shader_type spatial;
render_mode unshaded, blend_mix;
// Unshaded — edges don't need lighting (they're fiber-optic-like).
// Coherence (harmonicity) modulates brightness: 1.0 = crisp, 0.4 = dim
// (noisy material → edges recede into background).
// Base alpha comes from vertex COLOR.a (intra 0.50-0.60, CC 0.78-0.85).

uniform float coherence = 1.0;

varying vec3 v_color;
varying float v_alpha;

void vertex() {
    v_color = COLOR.rgb;
    v_alpha = COLOR.a;
}

void fragment() {
    ALBEDO = v_color * coherence;
    ALPHA = v_alpha;
}