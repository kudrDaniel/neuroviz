shader_type spatial;
render_mode unshaded, blend_mix;

uniform float coherence = 1.0;
uniform float bundle_energy = 1.0;

uniform vec3  wave_center = vec3(1.0, 0.0, 0.0);
uniform float wave_max_dist = 1.145;
uniform float wave_start = -1.0e10;
uniform float wave_amplitude = 0.0;
uniform float wave_duration = 0.6;
uniform float wave_pulse_duration = 0.4;
uniform float wave_nonuniformity = 0.0;
uniform float current_time = 0.0;

varying vec3 v_color;
varying float v_alpha;

// Deterministic pseudo-random phase from position, in [-1, 1].
// Same formula used on the C# side (HashPhase) for node phases —
// shared endpoints of edges and nodes get identical phase, so they
// move together through the wave. Small argument (positions are
// under 2.0 in local coords) keeps sin precision-safe across C# and
// GLSL implementations.
float hash_phase(vec3 pos) {
    return sin(pos.x * 17.0 + pos.y * 23.0 + pos.z * 31.0);
}

void vertex() {
    float dist = length(VERTEX - wave_center);
    // Guard against wave_max_dist = 0 (would produce inf / NaN in arrival
    // and propagate to vertex position). max() keeps the shader safe even
    // if the C# side sets it incorrectly.
    float safe_max = max(wave_max_dist, 0.001);
    float dist_norm = dist / safe_max;
    float phase = hash_phase(VERTEX);
    float arrival = wave_start + dist_norm * wave_duration + phase * wave_nonuniformity;
    float elapsed = current_time - arrival;
    float pulse = 0.0;
    if (elapsed > 0.0 && elapsed < wave_pulse_duration) {
        pulse = sin(elapsed / wave_pulse_duration * 3.14159265);
    }
    float factor = 1.0 - wave_amplitude * pulse;
    VERTEX = wave_center + (VERTEX - wave_center) * factor;

    v_color = COLOR.rgb;
    v_alpha = COLOR.a;
}

void fragment() {
    ALBEDO = v_color * coherence * bundle_energy;
    ALPHA = v_alpha;
}