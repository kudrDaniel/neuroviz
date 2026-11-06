shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

uniform float displacement : hint_range(0.0, 1.0) = 0.0;
uniform float pulse : hint_range(0.0, 1.0) = 0.0;
uniform float spikiness : hint_range(0.0, 1.0) = 0.0;
uniform float spike_amplitude = 0.35;
// Accumulated from _Process in MainNode3D. Advances even when audio is
// paused so the scene does not freeze visually.
uniform float time = 0.0;

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

// ── Animation + displacement ──────────────────────────────────────────
vec3 rotate_y(vec3 v, float angle) {
    float c = cos(angle);
    float s = sin(angle);
    return vec3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
}

// Displacement as a function of unit-sphere direction n. Reused by both
// the vertex stage and the fragment normal reconstruction (finite
// differences) — MUST stay a pure function of n and uniforms, so both
// stages agree on geometry.
float compute_displacement(vec3 n_unit) {
    // Time-based rotation makes spikes drift across the sphere surface.
    // 0.1 rad/s → full rotation in ~63 seconds. Slower would look static;
    // faster would feel like a spinning noise texture.
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
    // VERTEX is local space in Godot 4. Keep the undisplaced direction
    // so fragment can reconstruct normals in the same coordinate system.
    v_local_pos = VERTEX;

    vec3 n = normalize(VERTEX);
    float d = compute_displacement(n);
    VERTEX += NORMAL * d;
}

// Orthonormal tangent basis on a sphere, safe at the poles.
void tangent_basis(vec3 n, out vec3 t, out vec3 b) {
    vec3 up = abs(n.y) > 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    t = normalize(cross(up, n));
    b = cross(n, t);
}

void fragment() {
    // Reconstruct displaced normal via finite differences in local space.
    // Three surface points p0/p1/p2; cross((p1-p0), (p2-p0)) is the
    // outward normal of the triangle formed by those points. Cheap
    // approximation but reads correctly for noise-scale displacement.
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

    // NORMAL expects view space in fragment.
    // MODELVIEW_MATRIX is vertex-only in Godot 4; fragment composes it from
    // VIEW_MATRIX (world→view) * MODEL_MATRIX (local→world). The sphere has
    // no scaling in MainNode3D, so skipping the inverse-transpose for the
    // normal is correct here; add transpose(inverse(mat3(MODEL_MATRIX))) if
    // non-uniform scaling is ever applied to the MeshInstance3D.
    NORMAL = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(normal_local, 0.0)).xyz);

    // Amplify valley darkness: low displacement = hidden between spikes.
    // Cheap AO substitute; real SSAO would be a full post-process.
    AO = clamp(0.5 + d0 * 1.5, 0.4, 1.0);

    ALBEDO = vec3(0.2, 0.5, 0.9);
    // Emission lower than before (was 0.4+disp+pulse*0.8) so shadows
    // are visible. Emission still carries the "flash on attack" feel.
    EMISSION = vec3(0.15, 0.3, 0.6) * (0.25 + displacement * 0.5 + pulse * 0.5);
}