shader_type spatial;
render_mode unshaded, blend_mix;

uniform float coherence = 1.0;

// N.4-fix: release wave uniforms. All in SceneRoot-local space —
// edge meshes are direct children of SceneRoot with identity
// transform, so VERTEX here is in the same space as node.Position.
uniform vec3  wave_center = vec3(1.0, 0.0, 0.0);
uniform float wave_max_dist = 1.145;
uniform float wave_start = -1.0e10;
uniform float wave_amplitude = 0.0;
uniform float wave_duration = 0.6;
uniform float wave_pulse_duration = 0.4;
uniform float current_time = 0.0;

varying vec3 v_color;
varying float v_alpha;

void vertex() {
    // Wave factor from dist-based arrival — no per-node jitter on edges
    // (1px lines, jitter invisible; keeping edges smooth avoids edge/
    // node disconnect during the wavefront).
    float dist = length(VERTEX - wave_center);
    float dist_norm = dist / wave_max_dist;
    float arrival = wave_start + dist_norm * wave_duration;
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
    ALBEDO = v_color * coherence;
    ALPHA = v_alpha;
}