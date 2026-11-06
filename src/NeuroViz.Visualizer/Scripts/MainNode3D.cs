using Godot;
using NeuroViz.Visualizer.Scripts;

public partial class MainNode3D : Node3D
{
    private IpcClient _ipc = null!;
    private MeshInstance3D _sphere = null!;
    private ShaderMaterial _material = null!;
    private Label _fpsLabel = null!;
    private Camera3D _camera = null!;
    // Configurable via Settings (2.4f). Defaults match AppSettings; updated
    // when the App sends SetVisualConfig at Launch.
    private float _cameraOrbitSpeed = 0.05f;
    private float _cameraYAmplitude = 0.4f;

    private float _contLow;
    private float _contHarmonicity;
    private float _smoothLow;
    private float _smoothHarmonicity;
    private float _contHigh;
    private float _smoothHigh;
    private ParamValueRef _shiverState = new();
    private ParamValueRef _cortexFlashState = new();
    private ParamValueRef _pitchState = new();
    private ParamValueRef _pulseState = new();
    private float _contMid;
    private float _smoothMid;
    private float _contFlux;
    private float _smoothFlux;
    private double _lastBurstSeconds = -1.0;

    private GpuParticles3D _ambientParticles = null!;
    private GpuParticles3D _burstParticles = null!;

    private ParticleProcessMaterial _ambientPM = null!;
    private ParamValueRef _panState = new();
    private float _smoothPan;

    private enum TrajectoryVariant { Idle = 0, Fast = 1, Reverse = 2 }
    private TrajectoryVariant _currentTrajectory = TrajectoryVariant.Idle;
    private TrajectoryVariant _lastNonIdleTrajectory = TrajectoryVariant.Fast;

    private double _lastClock;
    private double _visualTime;

    private bool _previewFirstCaptureLogged;

    public override void _Ready()
    {
        // ── Sphere ─────────────────────────────────────────────────
        // Higher resolution than spike (was 64x32 = 2048 verts) — needed
        // so vertex-level noise reads as smooth curvature, not facets.
        // 96x48 = 4608 verts, still trivial for a single sphere.
        var mesh = new SphereMesh
        {
            Radius = 0.5f,
            Height = 1.0f,
            RadialSegments = 96,
            Rings = 48,
        };

        var shader = GD.Load<Shader>("res://spike_sharp.gdshader");
        if (shader == null)
        {
            GD.PrintErr("[MainNode3D] Could not load res://spike_sharp.gdshader");
            return;
        }
        _material = new ShaderMaterial { Shader = shader };

        _sphere = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = _material,
        };
        AddChild(_sphere);

        // ── Camera ─────────────────────────────────────────────────
        // Initial position is the orbit's start point (_visualTime = 0 at
        // _Ready), so there is no visible "jump" when _Process begins moving
        // it. LookAt is re-issued each frame in _Process, no need here.
        _camera = new Camera3D
        {
            Position = new Vector3(
                2.5f,   // cos(0) * radius
                0.0f,   // sin(0) * yAmp
                0.0f),  // sin(0) * radius
        };
        AddChild(_camera);
        _camera.LookAt(Vector3.Zero);

        // ── Light ──────────────────────────────────────────────────
        // Directional light with shadows. Displaced spikes cast into the
        // shadow map (Godot's shadow depth pass runs the vertex shader), so
        // spikes shadow the sphere and each other. Bias tuned for displaced
        // geometry — default 0.1 caused acne on 0.35-amplitude spikes.
        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(-0.5f, 0.7f, 0),
            LightEnergy = 1.2f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 5.0f,
            ShadowBias = 0.04f,
            ShadowNormalBias = 1.5f,
        });

        // ── Environment ────────────────────────────────────────────
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.05f, 0.05f, 0.08f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.1f, 0.1f, 0.15f),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        _fpsLabel = new Label { Position = new Vector2(10, 10), Text = "Waiting..." };
        AddChild(_fpsLabel);

        SetupAmbientParticles();
        SetupBurstParticles();

        _ipc = new IpcClient();
        _ipc.Start();
        _ipc.VisualConfigReceived += OnVisualConfigReceived;
    }

    public override void _Process(double delta)
    {
        _ipc.DrainMainThreadActions();

        if (!_ipc.IsConnected)
        {
            _fpsLabel.Text = $"Waiting... FPS: {Engine.GetFramesPerSecond():F0}";
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
            if (frame.ContinuousParams.TryGetValue("harmonicity", out var h))
                _contHarmonicity = h;
            if (frame.ContinuousParams.TryGetValue("band_energy.high", out var bh))
                _contHigh = bh;
            if (frame.ContinuousParams.TryGetValue("band_energy.mid", out var bm))
                _contMid = bm;
            if (frame.ContinuousParams.TryGetValue("spectral_flux", out var fl))
                _contFlux = fl;
            if (frame.Params.TryGetValue("pulse", out var p))
                _pulseState = new ParamValueRef { Value = p };
            if (frame.Params.TryGetValue("shiver", out var sv))
                _shiverState = new ParamValueRef { Value = sv };
            if (frame.Params.TryGetValue("cortex_flash", out var cf))
                _cortexFlashState = new ParamValueRef { Value = cf };
            if (frame.Params.TryGetValue("pitch_height", out var ph))
                _pitchState = new ParamValueRef { Value = ph };
            if (frame.Params.TryGetValue("pan_x", out var px))
                _panState = new ParamValueRef { Value = px };

            // Trajectory events: stream_boundary is an AudioEvent (Type + Params),
            // not a param-value. frame.Events contains events emitted since the
            // last drain, so an open/close fires exactly once per transition.
            // On replay (LoadProject) the event is delivered from the timeline
            // window; seeking back-and-forth re-fires it, which is fine — the
            // trajectory follows what the track does.
            foreach (var ev in frame.Events)
            {
                if (ev.Type != "stream_boundary") continue;
                var isOpen = ev.Params.GetValueOrDefault("open", 0f) >= 0.5f;
                if (isOpen) AdvanceTrajectoryVariant();
                else { _currentTrajectory = TrajectoryVariant.Idle; ApplyTrajectory(); }
            }
        }
        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;
        float currentPulse = _pulseState.EvaluateAt(nowClock);
        float currentShiver = _shiverState.EvaluateAt(nowClock);
        float currentFlash = _cortexFlashState.EvaluateAt(nowClock);
        float currentPitch = _pitchState.EvaluateAt(nowClock);
        float currentPan = _panState.EvaluateAt(nowClock);

        // Same smoothing as 2D MainNode for band_energy (kb, τ ≈ 83 ms).
        // Harmonicity also k b — spikiness should lag behind attacks
        // slightly so it feels organic, not twitchy.
        float kb = 1f - Mathf.Exp(-12f * (float)delta);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLow, kb);
        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kb);
        _smoothHigh = Mathf.Lerp(_smoothHigh, _contHigh, kb);
        _smoothMid = Mathf.Lerp(_smoothMid, _contMid, kb);

        float kf = 1f - Mathf.Exp(-30f * (float)delta);
        _smoothFlux = Mathf.Lerp(_smoothFlux, _contFlux, kf);

        float kc = 1f - Mathf.Exp(-15f * (float)delta);   // τ ≈ 67 ms
        _smoothPan = Mathf.Lerp(_smoothPan, currentPan, kc);
        // Visual time advances regardless of audio state — spikes keep
        // drifting on pause. Independent of audio clock so seek does not
        // "rewind" the noise field (would be jarring).
        _visualTime += delta;
        _material.SetShaderParameter("time", (float)_visualTime);
        _material.SetShaderParameter("displacement", _smoothLow);
        _material.SetShaderParameter("pulse", currentPulse);
        _material.SetShaderParameter("spikiness", 1f - _smoothHarmonicity);

        // Palette (2.4e)
        _material.SetShaderParameter("shiver", currentShiver);
        _material.SetShaderParameter("white_flash", currentFlash);
        _material.SetShaderParameter("pitch_hue", currentPitch);
        _material.SetShaderParameter("band_high", _smoothHigh);
        // Ambient density: smoothMid maps to AmountRatio [0.05 .. 1.0].
        // Minimum 5% keeps a faint halo even on instrumental-intro; maximum
        // 100% at fully saturated mid (vocal / melody sections).
        float targetRatio = Mathf.Clamp(0.1f + _smoothMid * 0.9f, 0.05f, 1.0f);
        _ambientParticles.AmountRatio =
            Mathf.Lerp(_ambientParticles.AmountRatio, targetRatio, kb);

        // Burst trigger: rising edge of spectral_flux above 0.18, refractory
        // 200 ms (max 5 bursts/s). Uses smoothed flux so a single 40 Hz
        // outlier frame doesn't fire; the 30 Hz smoothing still catches real
        // attacks (10-50 ms duration).
        if (_smoothFlux > 0.18f && nowClock - _lastBurstSeconds > 0.2)
        {
            _burstParticles.Restart();
            _lastBurstSeconds = nowClock;
        }

        // Pan offset: shift both emitters horizontally by the smoothed pan.
        // Setting Position on the emitter moves ALL particles (already-spawned
        // and future) — the whole cloud slides sideways while the sphere stays
        // centred. 0.5 amplitude keeps pan inside the VisibilityAabb.
        var panOffset = new Vector3(_smoothPan * 0.5f, 0, 0);
        _ambientParticles.Position = panOffset;
        _burstParticles.Position = panOffset;

        float spikiness = 1f - _smoothHarmonicity;
        _material.SetShaderParameter("spikiness", spikiness);

        _fpsLabel.Text =
            $"FPS: {Engine.GetFramesPerSecond():F0} | " +
            $"low: {_smoothLow:F3} | harm: {_smoothHarmonicity:F3} | high: {_smoothHigh:F3} | " +
            $"Cont: {contCount}\n" +
            $"pulse: {currentPulse:F2} | shiver: {currentShiver:F2} | flash: {currentFlash:F2} | " +
            $"mid: {_smoothMid:F2} | flux: {_smoothFlux:F2} | pan: {_smoothPan:F2} | " +
            $"traj: {_currentTrajectory} | parts: {_ambientParticles.AmountRatio:F2}\n" +
            $"cam: x={_camera.Position.X:F2} y={_camera.Position.Y:F2} z={_camera.Position.Z:F2}";

        UpdateIdleCamera();

        double now = Time.GetTicksMsec() / 1000.0;
        if (_ipc.ShouldCapturePreview(now))
            CapturePreviewAfterDraw();
    }

    private async void CapturePreviewAfterDraw()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!IsInsideTree() || !_ipc.IsPreviewConnected) return;

        var img = GetViewport().GetTexture().GetImage();
        if (img == null || img.IsEmpty()) return;

        if (!_previewFirstCaptureLogged)
        {
            _previewFirstCaptureLogged = true;
            int nonBlack = 0;
            for (int y = 0; y < img.GetHeight(); y += 32)
                for (int x = 0; x < img.GetWidth(); x += 32)
                {
                    var c = img.GetPixel(x, y);
                    if (c.R > 0.02f || c.G > 0.02f || c.B > 0.02f) nonBlack++;
                }
            GD.Print($"[Preview] First capture {img.GetWidth()}x{img.GetHeight()}, non-black: {nonBlack}");
        }

        img.Resize(320, 180, Image.Interpolation.Bilinear);
        _ipc.QueuePreview(img);
        img.Dispose();
    }

    private void SetupAmbientParticles()
    {
        var quad = new QuadMesh { Size = new Vector2(0.03f, 0.03f) };   // 0.025 → 0.03

        var mat = new StandardMaterial3D
        {
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.4f, 0.7f, 1.0f, 0.7f),
            VertexColorUseAsAlbedo = true,
        };

        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            // 1.3 > max sphere radius (0.5 base + 0.15 disp + 0.35 pulse
            // + 0.35 spike = 1.35 with 0.05 margin slack). Below this,
            // breathing + spikes occlude the particles.
            EmissionSphereRadius = 1.3f,
            Gravity = Vector3.Zero,
            Direction = Vector3.Up,
            Spread = 180f,
            InitialVelocityMin = 0.03f,
            InitialVelocityMax = 0.10f,
            ScaleMin = 0.5f,
            ScaleMax = 1.5f,
            ColorRamp = MakeFadeGradient(),
            // orbit_velocity rotates particles around the emitter origin;
            // the orbit radius equals EmissionSphereRadius (no separate
            // OrbitRadius property exists in Godot 4).
            OrbitVelocityMin = 0.1f,
            OrbitVelocityMax = 0.3f,
        };
        _ambientPM = pm;

        _ambientParticles = new GpuParticles3D   // GpuParticles3D, not GPUParticles3D
        {
            Amount = 400,   // 250 → 400 (larger sphere volume → more particles)
            Lifetime = 5.0,
            Emitting = true,
            AmountRatio = 0.3f,
            DrawPass1 = quad,
            ProcessMaterial = pm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // AABB must cover orbit at radius 1.3 + spread. 6×6×6 = ±3,
            // comfortably beyond.
            VisibilityAabb = new Aabb(new Vector3(-3, -3, -3), new Vector3(6, 6, 6)),
        };
        AddChild(_ambientParticles);
    }

    private void SetupBurstParticles()
    {
        var quad = new QuadMesh { Size = new Vector2(0.04f, 0.04f) };

        var mat = new StandardMaterial3D
        {
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1.0f, 1.0f, 0.9f, 0.95f),
            VertexColorUseAsAlbedo = true,
        };

        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            // 0.7 → burst starts near sphere surface, not deep inside.
            EmissionSphereRadius = 0.7f,
            Gravity = Vector3.Zero,
            Direction = Vector3.Up,
            Spread = 180f,
            // 1.8–2.8 velocity: reach max sphere radius 1.35 in ~0.25 s,
            // so most of the 0.8 s lifetime is visible.
            InitialVelocityMin = 1.8f,
            InitialVelocityMax = 2.8f,
            ScaleMin = 0.8f,
            ScaleMax = 2.0f,
            ColorRamp = MakeFadeGradient(),
            // Damping ramped up — settles within lifetime without flying off.
            DampingMin = 1.5f,
            DampingMax = 2.5f,
        };

        _burstParticles = new GpuParticles3D   // GpuParticles3D
        {
            Amount = 120,
            Lifetime = 0.8,
            OneShot = true,
            Emitting = false,
            DrawPass1 = quad,
            ProcessMaterial = pm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-3, -3, -3), new Vector3(6, 6, 6)),
        };
        AddChild(_burstParticles);
    }

    private static GradientTexture1D MakeFadeGradient()
    {
        var g = new Gradient();
        // Start opaque white, fade to transparent light blue at end-of-life.
        g.SetColor(0, new Color(1.0f, 1.0f, 1.0f, 1.0f));
        g.SetColor(1, new Color(0.5f, 0.7f, 1.0f, 0.0f));
        return new GradientTexture1D { Gradient = g };
    }

    private void AdvanceTrajectoryVariant()
    {
        // Cycle non-idle variants on each open. Idle is the default at
        // start and returns on close; open skips over it.
        _currentTrajectory = _lastNonIdleTrajectory == TrajectoryVariant.Fast
            ? TrajectoryVariant.Reverse
            : TrajectoryVariant.Fast;
        _lastNonIdleTrajectory = _currentTrajectory;
        ApplyTrajectory();
    }

    private void ApplyTrajectory()
    {
        switch (_currentTrajectory)
        {
            case TrajectoryVariant.Fast:
                _ambientPM.OrbitVelocityMin = 0.5f;
                _ambientPM.OrbitVelocityMax = 0.8f;
                _ambientPM.EmissionSphereRadius = 1.5f;
                _ambientPM.ScaleMin = 0.4f;
                _ambientPM.ScaleMax = 1.2f;
                break;
            case TrajectoryVariant.Reverse:
                // Negative OrbitVelocity — Godot 4 does not document a
                // restriction and the field is a float velocity, so reversed
                // direction is the natural reading. If a future Godot version
                // rejects it, swap to a slow-positive variant with larger
                // scale (Orbit 0.05-0.1, Scale 0.8-2.0) — the visual intent
                // is "different character", not specifically "backwards".
                _ambientPM.OrbitVelocityMin = -0.4f;
                _ambientPM.OrbitVelocityMax = -0.2f;
                _ambientPM.EmissionSphereRadius = 1.3f;
                _ambientPM.ScaleMin = 0.6f;
                _ambientPM.ScaleMax = 1.8f;
                break;
            default: // Idle
                _ambientPM.OrbitVelocityMin = 0.1f;
                _ambientPM.OrbitVelocityMax = 0.3f;
                _ambientPM.EmissionSphereRadius = 1.3f;
                _ambientPM.ScaleMin = 0.5f;
                _ambientPM.ScaleMax = 1.5f;
                break;
        }
        // Changing material params affects only NEW spawns — existing
        // particles keep their spawn radius/scale/velocity. With 5 s
        // lifetime the transition reads as a slow, organic drift, not a
        // cut. This is intentional: trajectory changes should not feel
        // like flipping a switch.
    }

    /// <summary>
    /// Slow idle orbit around the sphere. Independent of audio state —
    /// _visualTime advances in _Process regardless of playback, so the
    /// scene is "alive" even when paused. Radii and frequencies picked so
    /// a full revolution takes ~2 minutes (0.05 rad/s) with a secondary
    /// Y-sway at an unrelated frequency (0.03 rad/s, amplitude 0.4) that
    /// produces a gentle Lissajous drift rather than a flat circle.
    /// </summary>
    private void UpdateIdleCamera()
    {
        const float orbitRadius = 2.5f;
        const float ySpeed = 0.03f;       // keep Y frequency fixed
                                          // orbitSpeed and yAmplitude are fields (2.4f, configurable).

        var angle = (float)_visualTime * _cameraOrbitSpeed;
        var y = Mathf.Sin((float)_visualTime * ySpeed) * _cameraYAmplitude;

        _camera.Position = new Vector3(
            Mathf.Cos(angle) * orbitRadius,
            y,
            Mathf.Sin(angle) * orbitRadius);
        _camera.LookAt(Vector3.Zero);
    }

    private void OnVisualConfigReceived(NeuroViz.Core.Ipc.SetVisualConfigPayload p)
    {
        // Called on main thread (IpcClient enqueues, DrainMainThreadActions
        // runs on main thread). Clamp here so hand-edited settings.json
        // cannot break FPS.
        _cameraOrbitSpeed = Mathf.Clamp(p.CameraOrbitSpeed, 0.01f, 0.2f);
        _cameraYAmplitude = Mathf.Clamp(p.CameraYAmplitude, 0.0f, 1.0f);

        if (_ambientParticles != null)
            _ambientParticles.Amount = Mathf.Clamp(p.AmbientParticleCount, 20, 1000);
        if (_burstParticles != null)
            _burstParticles.Amount = Mathf.Clamp(p.BurstParticleCount, 20, 500);

        GD.Print($"[Visual] Config applied: orbit={_cameraOrbitSpeed:F2} " +
                 $"yAmp={_cameraYAmplitude:F2} ambient={_ambientParticles?.Amount} " +
                 $"burst={_burstParticles?.Amount}");
    }

    public override void _ExitTree()
    {
        _ipc.VisualConfigReceived -= OnVisualConfigReceived;
        _ipc.Dispose();
    }

    // ── Pulse state helper ──────────────────────────────────────────
    // ParamValue is NeuroViz.Core.Ipc.ParamValue (MessagePack object,
    // init-only). It's reused here for the same decay semantics as the
    // 2D scene; the wrapper keeps MainNode3D free of `_pulseState = p`
    // and a nullable field.
    private sealed class ParamValueRef
    {
        public NeuroViz.Core.Ipc.ParamValue? Value { get; init; }

        public float EvaluateAt(double nowSeconds)
        {
            if (Value is null) return 0f;

            if (Value.DecayMs <= 0) return Value.Value;
            var age = nowSeconds - Value.SetAtSeconds;
            if (age < 0) return 0f;
            var k = (float)System.Math.Exp(-age / (Value.DecayMs / 1000.0));
            return Value.Value * k;
        }
    }
}