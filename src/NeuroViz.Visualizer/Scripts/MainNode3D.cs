using Godot;
using NeuroViz.Visualizer.Scripts;

/// <summary>
/// Phase 2 spike — proof that Godot 3D + our IPC continuous pipeline
/// works end to end. Not production. Kill switch per PLAN_PHASE_2.md §2.1:
///   1. 60 FPS @ 1920x1080 while receiving continuous @ 20 Hz.
///   2. band_energy.low → displacement is smooth, no jitter.
///   3. Looks like "a 3D visualizer prototype", not "a test cube".
///
/// Everything is built in code (no editor-authored scene tree beyond
/// the root node). Spike deliberately keeps the surface area small —
/// one mesh, one shader, one light, one camera, one label. The full
/// P2-C (spikes, particles, stream_boundary trajectories) comes later
/// and lives in a separate scene, not here.
/// </summary>
public partial class MainNode3D : Node3D
{
    private IpcClient _ipc = null!;
    private MeshInstance3D _sphere = null!;
    private ShaderMaterial _material = null!;
    private Label _fpsLabel = null!;

    private float _contLow;
    private float _smoothLow;
    private double _lastClock;

    public override void _Ready()
    {
        // ── Sphere ──────────────────────────────────────────────────
        // High enough resolution that vertex displacement reads as
        // smooth surface curvature, not faceting. 64x32 = 2048 verts.
        var mesh = new SphereMesh
        {
            Radius = 0.5f,
            Height = 1.0f,
            RadialSegments = 64,
            Rings = 32,
        };

        var shader = new Shader
        {
            Code = @"
shader_type spatial;

uniform float displacement : hint_range(0.0, 1.0) = 0.0;

void vertex() {
    // Push vertices along their normal — breathing sphere.
    VERTEX += NORMAL * displacement * 0.3;
}

void fragment() {
    ALBEDO = vec3(0.2, 0.5, 0.9);
    // Emission scales with displacement so the effect reads even at
    // a distance / small screen size. Cheap way to make it obvious
    // that band_energy.low is doing something.
    EMISSION = vec3(0.1, 0.2, 0.4) * (0.3 + displacement);
}
"
        };
        _material = new ShaderMaterial { Shader = shader };

        _sphere = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = _material,
        };
        AddChild(_sphere);

        // ── Camera ─────────────────────────────────────────────────
        // Static for spike. Idle orbit is P2-C full (§2.4).
        var camera = new Camera3D
        {
            Position = new Vector3(0, 0, 2.5f),
        };
        AddChild(camera);
        camera.LookAt(Vector3.Zero);

        // ── Light ──────────────────────────────────────────────────
        // One directional light is enough to read the silhouette;
        // the sphere's own emission carries the mood.
        var light = new DirectionalLight3D
        {
            Rotation = new Vector3(-0.5f, 0.7f, 0),
            LightEnergy = 1.0f,
        };
        AddChild(light);

        // ── Environment ────────────────────────────────────────────
        // Dark background so emission is legible.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.05f, 0.05f, 0.08f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.1f, 0.1f, 0.15f),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // ── FPS / diagnostics label ────────────────────────────────
        // Kill-switch criterion #1 is "stable 60 FPS" — must be
        // observable without a profiler. This label is spike-only.
        _fpsLabel = new Label
        {
            Position = new Vector2(10, 10),
            Text = "Waiting for Avalonia...",
        };
        AddChild(_fpsLabel);

        // ── IPC ────────────────────────────────────────────────────
        // Reuse the existing 2D client unchanged. It already handles
        // Control pipe handshake, EventStream pipe, MMF audio clock.
        _ipc = new IpcClient();
        _ipc.Start();
    }

    public override void _Process(double delta)
    {
        if (!_ipc.IsConnected)
        {
            _fpsLabel.Text = $"Waiting for Avalonia... FPS: {Engine.GetFramesPerSecond():F0}";
            return;
        }

        var frame = _ipc.GetLatestFrame();
        int contCount = 0;
        if (frame != null)
        {
            _lastClock = frame.AudioClockSeconds;
            contCount = frame.ContinuousParams.Count;
            if (frame.ContinuousParams.TryGetValue("band_energy.low", out var bl))
                _contLow = bl;
        }

        // Same smoothing constant as 2D MainNode (§2.1 criterion #2:
        // "no worse than kb in 2D"). τ ≈ 83 ms.
        float kb = 1f - Mathf.Exp(-12f * (float)delta);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLow, kb);

        _material.SetShaderParameter("displacement", _smoothLow);

        // Kill switch diagnostics: FPS + the raw and smoothed low-band
        // value + how many continuous keys arrived in the last frame.
        // If Cont stays at 0 during playback, the pipeline is not
        // delivering — spike fails functionally regardless of FPS.
        _fpsLabel.Text =
            $"FPS: {Engine.GetFramesPerSecond():F0} | " +
            $"low: {_smoothLow:F3} (raw {_contLow:F3}) | " +
            $"Cont: {contCount} | Clock: {_lastClock:F2}s";
    }

    public override void _ExitTree()
    {
        _ipc.Dispose();
    }
}