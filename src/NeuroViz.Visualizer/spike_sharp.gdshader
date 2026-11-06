shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform float displacement : hint_range(0.0, 1.0) = 0.0;
uniform float pulse : hint_range(0.0, 1.0) = 0.0;
uniform float spikiness : hint_range(0.0, 1.0) = 0.0;
uniform float spike_amplitude = 0.35;
uniform float time = 0.0;

// Palette — 2.4e.
uniform float shiver = 0.0;         // 0..1, orange tint (novelty, 1000ms decay)
uniform float white_flash = 0.0;    // 0..1, cortex_flash (novelty, 400ms decay)
uniform float pitch_hue = 0.0;      // 0..1, pitch_height (blue → cyan → white)
uniform float band_high = 0.0;      // 0..1, band_energy.high (base brightness)

// ── 3D simplex noise (Ashima Arts / Stefan Gustavson, MIT) ─────────────
vec3 mod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 1.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float snoise(vec3 v) {
    const vec2 C = vec2(1.0 / 6.0, 1.0 / 3.0);
    const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
    vec3 i  = floor(v + dot(v, C.yyy));
    vec3 x0 = v - i + dot(i, C.xxx);
    vec3 g = step(x0.yzx, x0.xyz);
    vec3 l = 1.0 - g;
    vec3 i1 = min(g.xyz, l.zxy);
    vec3 i2 = max(g.xyz, l.zxy);
    vec3 x1 = x0 - i1 + C.xxx;
    vec3 x2 = x0 - i2 + C.yyy;
    vec3 x3 = x0 - D.yyy;
    i = mod289(i);
    vec4 p = permute(permute(permute(
        i.z + vec4(0.0, i1.z, i2.z, 1.0))
        + i.y + vec4(0.0, i1.y, i2.y, 1.0))
        + i.x + vec4(0.0, i1.x, i2.x, 1.0));
    float n_ = 0.142857142857;
    vec3 ns = n_ * D.wyz - D.xzx;
    vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
    vec4 x_ = floor(j * ns.z);
    vec4 y_ = floor(j - 7.0 * x_);
    vec4 x = x_ * ns.x + ns.yyyy;
    vec4 y = y_ * ns.x + ns.yyyy;
    vec4 h = 1.0 - abs(x) - abs(y);
    vec4 b0 = vec4(x.xy, y.xy);
    vec4 b1 = vec4(x.zw, y.zw);
    vec4 s0 = floor(b0) * 2.0 + 1.0;
    vec4 s1 = floor(b1) * 2.0 + 1.0;
    vec4 sh = -step(h, vec4(0.0));
    vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    vec3 p0 = vec3(a0.xy, h.x);
    vec3 p1 = vec3(a0.zw, h.y);
    vec3 p2 = vec3(a1.xy, h.z);
    vec3 p3 = vec3(a1.zw, h.w);
    vec4 norm = taylorInvSqrt(vec4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
    p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
    vec4 m = max(0.6 - vec4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 42.0 * dot(m * m, vec4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

// ── Animation + displacement (pure function — vertex and fragment must
//    agree on geometry). ────────────────────────────────────────────────
vec3 rotate_y(vec3 v, float angle) {
    float c = cos(angle);
    float s = sin(angle);
    return vec3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
}

float compute_displacement(vec3 n_unit) {
    vec3 sample_pos = rotate_y(n_unit, time * 0.1);
    float n_low  = snoise(sample_pos * 2.0) * 0.5 + 0.5;
    float n_high = snoise(sample_pos * 8.0) * 0.5 + 0.5;
    float blend = mix(n_low, n_high, spikiness);
    float threshold = 1.0 - spikiness * 0.3;
    float spike_mask = smoothstep(threshold, 1.0, blend);
    float spike_height = spike_mask * spike_amplitude;
    float base = displacement * 0.15 + pulse * 0.35;
    return base + spike_height;
}

varying vec3 v_local_pos;

void vertex() {
    v_local_pos = VERTEX;
    vec3 n = normalize(VERTEX);
    VERTEX += NORMAL * compute_displacement(n);
}

void tangent_basis(vec3 n, out vec3 t, out vec3 b) {
    vec3 up = abs(n.y) > 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    t = normalize(cross(up, n));
    b = cross(n, t);
}

void fragment() {
    // ── Normal reconstruction (2.4a-fix, unchanged) ────────────────────
    vec3 n = normalize(v_local_pos);
    vec3 t, b;
    tangent_basis(n, t, b);

    float eps = 0.015;
    vec3 n_u = normalize(n + t * eps);
    vec3 n_v = normalize(n + b * eps);

    float d0 = compute_displacement(n);
    float du = compute_displacement(n_u);
    float dv = compute_displacement(n_v);

    vec3 p0 = n   * (0.5 + d0);
    vec3 p1 = n_u * (0.5 + du);
    vec3 p2 = n_v * (0.5 + dv);

    vec3 normal_local = normalize(cross(p1 - p0, p2 - p0));
    NORMAL = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(normal_local, 0.0)).xyz);
    AO = clamp(0.5 + d0 * 1.5, 0.4, 1.0);

    // ── Palette (2.4e) ─────────────────────────────────────────────────
    // Composition order: base blue → brightness (band_high) →
    // hue shift (pitch) → orange tint (shiver). Each layer is a mix()
    // so the identity of "the sphere" is preserved as a stable blue
    // centre even when all inputs are near zero.
    vec3 base_color = vec3(0.2, 0.5, 0.9);

    // band_energy.high → brightness, 0.7..1.3 multiplier.
    base_color *= 0.7 + band_high * 0.6;

    // pitch → hue shift: low note stays blue (pitch = 0, no change),
    // high note moves toward cyan/white. pitch * 0.6 caps the shift at
    // 60% of the way to the target so blue never disappears entirely.
    vec3 pitch_target = mix(
        vec3(0.4, 0.85, 1.0),   // cyan at pitch = 0
        vec3(0.9, 0.95, 1.0),   // near-white at pitch = 1
        pitch_hue);
    base_color = mix(base_color, pitch_target, pitch_hue * 0.6);

    // shiver → orange tint. 0.7 max mix keeps a trace of the underlying
    // blue (shiver-tinted orange reads warmer than flat orange).
    base_color = mix(base_color, vec3(1.0, 0.55, 0.15), shiver * 0.7);

    ALBEDO = base_color;

    // ── Emission ───────────────────────────────────────────────────────
    // Base emission uses the palette colour, so pulse/shiver/band_high
    // all change the glow tone, not just the albedo. White flash adds
    // on top unconditionally (short, bright, doesn't need palette).
    vec3 emission = base_color * (0.3 + displacement * 0.5 + pulse * 0.6);
    emission += vec3(1.0, 0.95, 0.85) * white_flash;
    EMISSION = emission;
}