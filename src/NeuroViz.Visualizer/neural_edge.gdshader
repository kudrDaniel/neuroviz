shader_type spatial;
render_mode unshaded, blend_mix;

uniform float coherence = 1.0;
uniform float bundle_energy = 1.0;
uniform float bundle_wave_time = 0.0;
uniform float bundle_wave_strength = 0.0;

// N.10-fix8: activation flow from connected nodes
uniform sampler2D activation_tex;
uniform float texture_width = 1600.0;

uniform vec3  wave_center = vec3(1.0, 0.0, 0.0);
uniform float wave_max_dist = 1.145;
// N.10-fix9: up to 4 concurrent release waves, packed as vec4 for
// simplicity and cross-version safety (array uniforms work but vec4
// is bulletproof across Godot 4.x versions).
uniform vec4 wave_starts;
uniform vec4 wave_amplitudes;
uniform float wave_duration = 0.6;
uniform float wave_pulse_duration = 0.4;
uniform float wave_nonuniformity = 0.0;
uniform float current_time = 0.0;

varying vec3 v_color;
varying float v_alpha;
varying float v_wave;
varying float v_node_activation; // 0..1 from connected nodes

float hash_phase(vec3 pos) {
    return sin(pos.x * 17.0 + pos.y * 23.0 + pos.z * 31.0);
}

void vertex() {
    vec3 base_pos = VERTEX;

    // Flow wave
    float n1 = sin(base_pos.x * 6.0 + base_pos.y * 4.0 + bundle_wave_time * 1.5);
    float n2 = sin(base_pos.y * 8.0 - base_pos.z * 7.0 + bundle_wave_time * 2.0);
    v_wave = (n1 + n2) * 0.25 + 0.5;

    // Release wave
    float dist = length(VERTEX - wave_center);
    float dist_norm = dist / max(wave_max_dist, 0.001);
    float phase = hash_phase(VERTEX);

    // Sum contributions from all active waves.
    float total_pulse = 0.0;

    // Wave 0
    if (wave_amplitudes.x > 0.001) {
        float el = current_time - (wave_starts.x + dist_norm * wave_duration + phase * wave_nonuniformity);
        if (el > 0.0 && el < wave_pulse_duration)
            total_pulse += wave_amplitudes.x * sin(el / wave_pulse_duration * 3.14159265);
    }
    // Wave 1
    if (wave_amplitudes.y > 0.001) {
        float el = current_time - (wave_starts.y + dist_norm * wave_duration + phase * wave_nonuniformity);
        if (el > 0.0 && el < wave_pulse_duration)
            total_pulse += wave_amplitudes.y * sin(el / wave_pulse_duration * 3.14159265);
    }
    // Wave 2
    if (wave_amplitudes.z > 0.001) {
        float el = current_time - (wave_starts.z + dist_norm * wave_duration + phase * wave_nonuniformity);
        if (el > 0.0 && el < wave_pulse_duration)
            total_pulse += wave_amplitudes.z * sin(el / wave_pulse_duration * 3.14159265);
    }
    // Wave 3
    if (wave_amplitudes.w > 0.001) {
        float el = current_time - (wave_starts.w + dist_norm * wave_duration + phase * wave_nonuniformity);
        if (el > 0.0 && el < wave_pulse_duration)
            total_pulse += wave_amplitudes.w * sin(el / wave_pulse_duration * 3.14159265);
    }

    // N.10-fix10: clamp identically to the C# side (MainNodeNeural.
    // UpdateNodeTransforms uses Mathf.Clamp(factor, 0.4f, 1.5f)).
    // Without this, overlapping release waves push total_pulse above
    // 0.6; nodes stop at factor=0.4 but edges keep compressing → edges
    // visually lead nodes during multi-wave overlap.
    float factor = clamp(1.0 - total_pulse, 0.4, 1.5);
    VERTEX = wave_center + (VERTEX - wave_center) * factor;

    // Read node activation from data texture using UV.x (Node A) and UV.y (Node B)
    // We pass node indices as UVs during mesh generation.
    float act_a = texture(activation_tex, vec2((UV.x + 0.5) / texture_width, 0.5)).r;
    float act_b = texture(activation_tex, vec2((UV.y + 0.5) / texture_width, 0.5)).r;
    // Edge lights up if EITHER endpoint is active, but we use max so it doesn't double up
    v_node_activation = clamp(max(act_a, act_b), 0.0, 1.0);

    v_color = COLOR.rgb;
    v_alpha = COLOR.a;
}

void fragment() {
    float waveFactor = 1.0 - bundle_wave_strength * 0.8 + v_wave * bundle_wave_strength * 1.6;
    vec3 base_edge = v_color * coherence * bundle_energy * waveFactor;

    // Active edge color (bright lime). Multiplied by 2.0 for HDR so bloom catches it.
    vec3 active_edge = vec3(0.83, 1.0, 0.21) * 2.0;

    // Mix based on node activation. At act=0, normal edge. At act=1, glowing gold.
    ALBEDO = mix(base_edge, active_edge, v_node_activation);
    ALPHA = v_alpha;
}