shader_type spatial;
render_mode unshaded, blend_mix;

uniform float coherence = 1.0;
uniform float bundle_energy = 1.0;

// N.10-fix7: spatial wave for flowing brightness (same idea as the
// node continuous wave, N.10-fix6). Instead of the whole material
// lighting up uniformly when band_energy rises, a slow sine wave
// moves through the mesh, so the CC bundles and intra edges look like
// energy flowing through the network.
// Must be set to 0.0 for materials that should NOT have this effect.
uniform float bundle_wave_time = 0.0;
uniform float bundle_wave_strength = 0.0;

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
varying float v_wave;   // [0, 1], per-vertex wave for flowing brightness

float hash_phase(vec3 pos) {
    return sin(pos.x * 17.0 + pos.y * 23.0 + pos.z * 31.0);
}

void vertex() {
    // Capture the base position BEFORE the release-wave displacement —
    // the flow wave is anchored to physical geometry, not the animated
    // position, so the brightness pattern does not wobble during a
    // release pulse.
    vec3 base_pos = VERTEX;

    // Flow wave — same formula as C# ComputeContinuousWave so edges
    // and their endpoint nodes flow in sync.
    float n1 = sin(base_pos.x * 6.0 + base_pos.y * 4.0 + bundle_wave_time * 1.5);
    float n2 = sin(base_pos.y * 8.0 - base_pos.z * 7.0 + bundle_wave_time * 2.0);
    v_wave = (n1 + n2) * 0.25 + 0.5;

    // Release wave (unchanged from N.4-fix3).
    float dist = length(VERTEX - wave_center);
    float dist_norm = dist / max(wave_max_dist, 0.001);
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
    // waveFactor: 1.0 at strength 0, [0.2, 1.8] at strength 1. Trough
    // gets 20% brightness, peak 180% — visible flow without making
    // the trough invisible. At wave=0.5 (median), factor = 1.0 so
    // the average brightness is unchanged from the no-wave baseline.
    float waveFactor = 1.0 - bundle_wave_strength * 0.8
                     + v_wave * bundle_wave_strength * 1.6;

    ALBEDO = v_color * coherence * bundle_energy * waveFactor;
    ALPHA = v_alpha;
}