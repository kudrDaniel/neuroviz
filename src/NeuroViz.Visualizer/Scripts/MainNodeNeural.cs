using Godot;

namespace NeuroViz.Visualizer.Scripts;

/// <summary>
/// N.2: renders the topology — 1600 node instances (MultiMesh, one
/// icosphere mesh), intra edges and CC arcs (ArrayMesh, PRIMITIVE_LINES).
/// No activation yet (N.3). Palette is layer-coded cold blue; N.4 will
/// replace with an activation-driven shader.
/// </summary>
public partial class MainNodeNeural : Node3D
{
    private IpcClient _ipc = null!;
    private NeuralTopology _topology = null!;
    private MultiMesh _nodeMultimesh = null!;
    private Node3D _sceneRoot = null!;
    private Label _fpsLabel = null!;

    // N.4: continuous inputs (raw + smoothed) and event states.
    private float _contLow, _smoothLow;
    private float _contMid, _smoothMid;
    private float _contHigh, _smoothHigh;
    private float _contFlux, _smoothFlux;
    private float _contHarmonicity, _smoothHarmonicity;
    private ParamValueRef _shiverState = new();
    private ParamValueRef _cortexFlashState = new();
    private ParamValueRef _pitchState = new();
    private ShaderMaterial _nodeMaterial = null!;
    private ShaderMaterial _edgeMaterial = null!;
    private double _lastFluxFlashTime = double.NegativeInfinity;

    // N.4-fix: release wave state.
    private float[] _nodeDistNorm = null!;
    private Vector3[] _nodeBasePos = null!;
    private float[] _nodePhase = null!;
    private float _maxNodeDist;
    private double _waveStartTime = double.NegativeInfinity;
    private float _waveAmplitude;
    private double _lastReleaseSetAt = double.NegativeInfinity;
    private bool _wasWaveActive;
    private const float WaveDuration = 0.6f;
    private const float WavePulseDuration = 0.4f;
    private const float MaxNonuniformity = 0.3f;
    private const float WaveAmplitude = 0.15f;
    private static readonly Vector3 StructureCenter = new(1.0f, 0, 0);

    // N.3: activation state and hop-cascade queue.
    private float[] _activation = null!;
    private List<int>[] _adjacency = null!;
    private List<int>[] _lowNodesByHemi = null!;
    private readonly PriorityQueue<(int idx, float intensity), double> _pending = new();
    private readonly Random _pulseRng = new(7);
    private double _lastSeedTime = double.NegativeInfinity;
    private double _lastPulseSetAt = double.NegativeInfinity;
    private ParamValueRef _panState = new();
    private double _lastClock;

    /// <summary>
    /// Minimal ParamValue wrapper — same idea as spike's MainNode3D. Kept
    /// here as a nested class so the neural scene has zero coupling to
    /// NeuroViz.Core.Ipc except for the payload types.
    /// </summary>
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

    public override void _Ready()
    {
        _topology = NeuralTopology.Generate(
            nodesPerHemisphere: 800,
            edgesPerNode: 4,
            ccDensity: 0.3f,
            seed: 42);

        int cc = _topology.Edges.Count(e => e.IsCrossCallosum);
        GD.Print($"[Neural] Generated: {_topology.Nodes.Count} nodes, " +
                 $"{_topology.Edges.Count} edges ({cc} CC).");

        var edgeShader = GD.Load<Shader>("res://neural_edge.gdshader");
        if (edgeShader == null)
        {
            GD.PrintErr("[Neural] Could not load res://neural_edge.gdshader");
            return;
        }
        _edgeMaterial = new ShaderMaterial { Shader = edgeShader };

        SetupCameraAndLight();

        // N.2-fix2: uniform 2× scale of the entire structure, without moving
        // the camera. All mesh-bearing nodes (nodes, intra edges, CC edges)
        // become children of SceneRoot; camera/light/env stay on `this`.
        // Position offset -1.03 X keeps the structure's centroid at the same
        // world coordinate the camera already aims at, so the framing is the
        // same as before — the structure just fills more of it.
        _sceneRoot = new Node3D
        {
            Name = "SceneRoot",
            Position = new Vector3(-1.03f, 0, 0),
            Scale = new Vector3(2f, 2f, 2f),
        };
        AddChild(_sceneRoot);

        BuildNodeMesh();
        BuildIntraEdgeMesh();
        BuildCCEdgeMesh();
        BuildAdjacency();
        _activation = new float[_topology.Nodes.Count];

        PrecomputeNodeWaveData();

        // Edge shader constants that never change at runtime.
        _edgeMaterial.SetShaderParameter("wave_center", StructureCenter);
        _edgeMaterial.SetShaderParameter("wave_max_dist", _maxNodeDist);
        _edgeMaterial.SetShaderParameter("wave_duration", WaveDuration);
        _edgeMaterial.SetShaderParameter("wave_pulse_duration", WavePulseDuration);

        _fpsLabel = new Label { Position = new Vector2(10, 10) };
        AddChild(_fpsLabel);

        _ipc = new IpcClient();
        _ipc.Start();
    }

    // ── Scene basics ───────────────────────────────────────────────
    private void SetupCameraAndLight()
    {
        // Camera distance bumped 2.1 → 2.9 to preserve visual fill after the
        // X-span doubled (1.66 → 2.29 bounding diagonal, ratio 1.38×). Camera
        // sits on the midline (X=1.0) so both hemispheres are symmetric in frame.
        var camera = new Camera3D { Position = new Vector3(1.0f, 0.55f, 2.9f) };
        AddChild(camera);
        camera.LookAt(new Vector3(1.0f, 0, 0));

        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(-0.5f, 0.7f, 0),
            LightEnergy = 1.3f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 10.0f,   // covers the wider X span
            ShadowBias = 0.03f,
            ShadowNormalBias = 1.0f,
        });

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.05f),   // keep dark blue for contrast
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            // Warm ambient (was bluish (0.08, 0.09, 0.14)) — beige nodes under
            // blue ambient read cold; warm ambient (0.10, 0.07, 0.05) lets the
            // tan palette actually look tan.
            AmbientLightColor = new Color(0.10f, 0.07f, 0.05f),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    // ── Nodes (MultiMesh, 1 draw call) ─────────────────────────────
    private void BuildNodeMesh()
    {
        // Low-poly sphere: 8×6 radial/rings = 42 verts. 1600 instances =
        // ~67K verts, trivial for any modern GPU.
        var mesh = new SphereMesh
        {
            Radius = 0.012f,
            Height = 0.024f,
            RadialSegments = 8,
            Rings = 6,
        };

        var shader = GD.Load<Shader>("res://neural_node.gdshader");
        if (shader == null)
        {
            GD.PrintErr("[Neural] Could not load res://neural_node.gdshader");
            return;
        }
        _nodeMaterial = new ShaderMaterial { Shader = shader };

        _nodeMultimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = _topology.Nodes.Count,
        };

        _sceneRoot.AddChild(new MultiMeshInstance3D
        {
            Multimesh = _nodeMultimesh,
            MaterialOverride = _nodeMaterial,   // ← was `mat`
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        });

        var identity = new Transform3D(Basis.Identity, Vector3.Zero);
        for (int i = 0; i < _topology.Nodes.Count; i++)
        {
            var node = _topology.Nodes[i];
            _nodeMultimesh.SetInstanceTransform(i,
                identity.TranslatedLocal(node.Position));
            _nodeMultimesh.SetInstanceColor(i, NodeBaseColor(node.Layer));
        }
    }

    // ── Intra edges (single ArrayMesh, PRIMITIVE_LINES) ────────────
    private void BuildIntraEdgeMesh()
    {
        var intra = _topology.Edges.Where(e => !e.IsCrossCallosum).ToList();
        var vertices = new Vector3[intra.Count * 2];
        var colors = new Color[intra.Count * 2];

        for (int i = 0; i < intra.Count; i++)
        {
            var a = _topology.Nodes[intra[i].A];
            var b = _topology.Nodes[intra[i].B];
            vertices[i * 2] = a.Position;
            vertices[i * 2 + 1] = b.Position;
            var color = EdgeBaseColor(a.Layer);
            colors[i * 2] = color;
            colors[i * 2 + 1] = color;
        }

        _sceneRoot.AddChild(new MeshInstance3D
        {
            Mesh = MakeLineMesh(vertices, colors),
            MaterialOverride = _edgeMaterial,
        });
    }

    // ── CC edges (ArrayMesh with quadratic bezier arcs) ────────────
    // N-Q1 = variant B: edges arc AROUND the geoids, not through them.
    // Without arcs, a Low-Low edge from (0.05, y, z) to (0.95, y, z)
    // passes straight through both geoids (hundreds of internal nodes
    // crossed). Arcs give the CC bundle its visual identity.
    private void BuildCCEdgeMesh()
    {
        var cc = _topology.Edges.Where(e => e.IsCrossCallosum).ToList();
        const int segmentsPerArc = 8;
        var vertices = new List<Vector3>(cc.Count * segmentsPerArc * 2);
        var colors = new List<Color>(cc.Count * segmentsPerArc * 2);

        foreach (var edge in cc)
        {
            var a = _topology.Nodes[edge.A];
            var b = _topology.Nodes[edge.B];
            var control = CCControlPoint(a.Position, b.Position, a.Layer);
            var color = CCBaseColor(a.Layer);

            var prev = a.Position;
            for (int s = 1; s <= segmentsPerArc; s++)
            {
                float t = s / (float)segmentsPerArc;
                var curr = BezierQuadratic(a.Position, control, b.Position, t);
                vertices.Add(prev); vertices.Add(curr);
                colors.Add(color); colors.Add(color);
                prev = curr;
            }
        }

        _sceneRoot.AddChild(new MeshInstance3D
        {
            Mesh = MakeLineMesh(vertices.ToArray(), colors.ToArray()),
            MaterialOverride = _edgeMaterial,
        });
    }

    // ── Helpers ────────────────────────────────────────────────────
    private static Vector3 CCControlPoint(Vector3 a, Vector3 b, int layer)
    {
        // Three visually distinct commisure lobes:
        //   High  → arc OVER the top (like real corpus callosum)
        //   Mid   → arc sideways (outward in Z, sign of source Z)
        //   Low   → arc UNDER the bottom
        var mid = (a + b) * 0.5f;
        return layer switch
        {
            2 => mid + new Vector3(0, 0.42f, 0),
            1 => mid + new Vector3(0, 0, 0.42f * Mathf.Sign(mid.Z == 0 ? 1 : mid.Z)),
            _ => mid + new Vector3(0, -0.42f, 0),
        };
    }

    private static Vector3 BezierQuadratic(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    private static ArrayMesh MakeLineMesh(Vector3[] vertices, Color[] colors)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    // Warm palette (N.2-fix): nodes light-beige → dark tan from Low to
    // High; synapses muted red (intra) / brighter muted red (CC). Replaces
    // the cold blue scheme of N.2.

    private static Color NodeBaseColor(int layer) => layer switch
    {
        0 => new Color(0.92f, 0.88f, 0.78f),   // very light warm beige (Low)
        1 => new Color(0.70f, 0.55f, 0.38f),   // medium warm tan (Mid)
        _ => new Color(0.48f, 0.34f, 0.22f),   // darker tan, still warm, not black (High)
    };

    private static Color EdgeBaseColor(int layer) => layer switch
    {
        // "Between pastel and normal red": muted dusty reds, alpha ramps
        // up toward High so deeper layers feel more "synaptic".
        0 => new Color(0.72f, 0.38f, 0.38f, 0.50f),
        1 => new Color(0.78f, 0.35f, 0.35f, 0.55f),
        _ => new Color(0.82f, 0.32f, 0.32f, 0.60f),
    };

    private static Color CCBaseColor(int layer) => layer switch
    {
        // CC bundle slightly brighter / more opaque than intra so it still
        // reads as a distinct "highway" bundle under the new uniform-red
        // palette (previously distinguished by hue — cyan vs blue).
        0 => new Color(0.80f, 0.32f, 0.32f, 0.78f),
        1 => new Color(0.85f, 0.28f, 0.28f, 0.82f),
        _ => new Color(0.90f, 0.24f, 0.24f, 0.85f),
    };

    public override void _Process(double delta)
    {
        _ipc.DrainMainThreadActions();

        if (!_ipc.IsConnected)
        {
            _fpsLabel.Text = $"Waiting... FPS: {Engine.GetFramesPerSecond():F0}";
            return;
        }

        var frame = _ipc.GetLatestFrame();
        if (frame != null)
        {
            _lastClock = frame.AudioClockSeconds;

            if (frame.Params.TryGetValue("pulse", out var p))
            {
                if (p.SetAtSeconds > _lastPulseSetAt + 0.001)
                {
                    _lastPulseSetAt = p.SetAtSeconds;
                    TrySeedPulse(p.SetAtSeconds);
                }
            }

            if (frame.Params.TryGetValue("pan_x", out var px))
                _panState = new ParamValueRef { Value = px };

            // N.4 event params
            if (frame.Params.TryGetValue("shiver", out var sv))
                _shiverState = new ParamValueRef { Value = sv };
            if (frame.Params.TryGetValue("cortex_flash", out var cf))
                _cortexFlashState = new ParamValueRef { Value = cf };
            if (frame.Params.TryGetValue("pitch_height", out var ph))
                _pitchState = new ParamValueRef { Value = ph };
            if (frame.Params.TryGetValue("release", out var rel))
            {
                if (rel.SetAtSeconds > _lastReleaseSetAt + 0.001)
                {
                    _lastReleaseSetAt = rel.SetAtSeconds;
                    _waveStartTime = rel.SetAtSeconds;
                    _waveAmplitude = Mathf.Clamp(rel.Value * WaveAmplitude, 0f, 0.25f);
                    _wasWaveActive = false;  // force reactivation this frame
                }
            }

            // N.4 continuous
            if (frame.ContinuousParams.TryGetValue("band_energy.low", out var bl)) _contLow = bl;
            if (frame.ContinuousParams.TryGetValue("band_energy.mid", out var bm)) _contMid = bm;
            if (frame.ContinuousParams.TryGetValue("band_energy.high", out var bh)) _contHigh = bh;
            if (frame.ContinuousParams.TryGetValue("spectral_flux", out var fl)) _contFlux = fl;
            if (frame.ContinuousParams.TryGetValue("harmonicity", out var hm)) _contHarmonicity = hm;
        }

        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;
        float dt = (float)delta;

        // Smoothing (band kb = 83ms, flux kf = 33ms).
        float kb = 1f - Mathf.Exp(-12f * dt);
        float kf = 1f - Mathf.Exp(-30f * dt);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLow, kb);
        _smoothMid = Mathf.Lerp(_smoothMid, _contMid, kb);
        _smoothHigh = Mathf.Lerp(_smoothHigh, _contHigh, kb);
        _smoothFlux = Mathf.Lerp(_smoothFlux, _contFlux, kf);
        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kb);

        // Decay activation, THEN process pending (order matters — see N.3).
        float decay = Mathf.Exp(-dt / 0.4f);
        for (int i = 0; i < _activation.Length; i++)
            _activation[i] *= decay;

        // Spectral flux → discrete random flashes (small, contained bursts).
        // Cooldown 150 ms: max ~7 flashes/s. Intensity 0.25 → ~3 hops,
        // dies out quickly, reads as "ambient neural noise" on top of the
        // main cascade.
        if (_smoothFlux > 0.15f && nowClock - _lastFluxFlashTime > 0.15)
        {
            _lastFluxFlashTime = nowClock;
            int flashCount = 1 + (int)(_smoothFlux * 3);   // 1..4
            for (int k = 0; k < flashCount; k++)
            {
                int idx = _pulseRng.Next(_topology.Nodes.Count);
                _pending.Enqueue((idx, 0.25f), nowClock);
            }
        }

        while (_pending.TryPeek(out _, out var at) && at <= nowClock)
        {
            var (idx, intensity) = _pending.Dequeue();
            _activation[idx] = Mathf.Min(1.5f, _activation[idx] + intensity);

            if (intensity * 0.6f > 0.05f)
            {
                foreach (var nbr in _adjacency[idx])
                    _pending.Enqueue((nbr, intensity * 0.6f), at + 0.12);
            }
        }

        // Event states.
        float shiver = _shiverState.EvaluateAt(nowClock);
        float flash = _cortexFlashState.EvaluateAt(nowClock);
        float pitch = _pitchState.EvaluateAt(nowClock);
        // N.4-fix: release wave. Per-node pulse propagates from the structure
        // centre outward. Nonuniformity (some nodes arrive early, some late)
        // is driven by (1 - harmonicity): clean tonal material → ordered front
        // like a water ripple; noisy material → chaotic front like spreading fire.
        float nonuniformity = (1f - _smoothHarmonicity) * MaxNonuniformity;
        float waveElapsed = (float)nowClock - (float)_waveStartTime;
        bool waveActive = waveElapsed > -MaxNonuniformity
                       && waveElapsed < WaveDuration + WavePulseDuration + MaxNonuniformity;

        if (waveActive)
        {
            float nowF = (float)nowClock;
            float startF = (float)_waveStartTime;
            for (int i = 0; i < _topology.Nodes.Count; i++)
            {
                float arrival = startF + _nodeDistNorm[i] * WaveDuration + _nodePhase[i] * nonuniformity;
                float el = nowF - arrival;
                float pulse = 0f;
                if (el > 0f && el < WavePulseDuration)
                    pulse = Mathf.Sin(el / WavePulseDuration * Mathf.Pi);

                float factor = 1f - _waveAmplitude * pulse;
                var newPos = StructureCenter + (_nodeBasePos[i] - StructureCenter) * factor;
                _nodeMultimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, newPos));
            }
            _edgeMaterial.SetShaderParameter("wave_start", startF);
            _edgeMaterial.SetShaderParameter("wave_amplitude", _waveAmplitude);
            _wasWaveActive = true;
        }
        else if (_wasWaveActive)
        {
            // Wave just ended — one final reset to base, then idle until next fire.
            for (int i = 0; i < _topology.Nodes.Count; i++)
                _nodeMultimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, _nodeBasePos[i]));
            _edgeMaterial.SetShaderParameter("wave_amplitude", 0f);
            _wasWaveActive = false;
            _waveAmplitude = 0f;
        }

        // current_time must always update — shader computes elapsed against it
        // and stale values would give nonzero pulse after the wave ended.
        _edgeMaterial.SetShaderParameter("current_time", (float)nowClock);

        UpdateNodeColors(pitch);

        int activeCount = 0;
        for (int i = 0; i < _activation.Length; i++)
            if (_activation[i] > 0.05f) activeCount++;

        _fpsLabel.Text =
            $"FPS: {Engine.GetFramesPerSecond():F0} | " +
            $"Pending: {_pending.Count} | Active: {activeCount}\n" +
            $"wave: {(_wasWaveActive ? $"ACTIVE {_waveAmplitude:F2}" : "idle")} | " +
            $"nonunif: {nonuniformity:F2}\n" +
            $"bands: {_smoothLow:F2}/{_smoothMid:F2}/{_smoothHigh:F2} | " +
            $"flux: {_smoothFlux:F2} | harm: {_smoothHarmonicity:F2}";
    }

    private void BuildAdjacency()
    {
        int n = _topology.Nodes.Count;
        _adjacency = new List<int>[n];
        _lowNodesByHemi = new List<int>[] { new(), new() };
        for (int i = 0; i < n; i++) _adjacency[i] = new();

        // Only intra edges participate in the hop cascade. CC edges are
        // reserved for stream_boundary reconfiguration (N.7); using them here
        // would merge the two hemispheres' cascades into one — breaks the
        // pan_x → "which hemisphere lights up" mapping.
        foreach (var edge in _topology.Edges)
        {
            if (edge.IsCrossCallosum) continue;
            _adjacency[edge.A].Add(edge.B);
            _adjacency[edge.B].Add(edge.A);
        }

        for (int i = 0; i < n; i++)
        {
            var node = _topology.Nodes[i];
            if (node.Layer == 0) _lowNodesByHemi[node.Hemisphere].Add(i);
        }

        GD.Print($"[Neural] Adjacency: {_adjacency.Sum(a => a.Count) / 2} intra edges, " +
                 $"{_lowNodesByHemi[0].Count}+{_lowNodesByHemi[1].Count} low seeds per hemi");
    }

    /// <summary>
    /// Seeds 1–3 nodes in the Low layer, hemisphere chosen by pan_x:
    /// -1 → always left, +1 → always right, 0 → 50/50.
    /// Cooldown 50 ms prevents drum-n-bass from stacking cascades into mush.
    /// </summary>
    private void TrySeedPulse(double eventTime)
    {
        if (eventTime - _lastSeedTime < 0.05) return;
        _lastSeedTime = eventTime;

        float panX = _panState.EvaluateAt(eventTime);
        float leftProb = (1f - panX) * 0.5f;
        int seedCount = _pulseRng.Next(1, 4);   // 1..3

        for (int s = 0; s < seedCount; s++)
        {
            int hemi = _pulseRng.NextDouble() < leftProb ? 0 : 1;
            var candidates = _lowNodesByHemi[hemi];
            if (candidates.Count == 0) continue;

            int idx = candidates[_pulseRng.Next(candidates.Count)];
            _pending.Enqueue((idx, 1.0f), eventTime);
        }
    }

    private void UpdateNodeColors(float pitch)
    {
        // Subtle violet shift at high pitch — the base beige palette stays
        // recognisable, but high-register notes cool toward lavender.
        var pitchTint = new Color(0.65f, 0.45f, 0.85f);
        float pitchBlend = pitch * 0.35f;

        for (int i = 0; i < _topology.Nodes.Count; i++)
        {
            var node = _topology.Nodes[i];
            var baseCol = NodeBaseColor(node.Layer).Lerp(pitchTint, pitchBlend);

            // Continuous layer contribution: band_energy of this node's
            // layer adds a *baseline* activation. Matches spike's "sphere
            // breathes with bass" but as glow, not scale (scale felt like
            // growing nodes = buggy; glow reads as "network lights up").
            float layerEnergy = node.Layer switch
            {
                0 => _smoothLow,
                1 => _smoothMid,
                _ => _smoothHigh,
            };
            float continuousContrib = layerEnergy * 0.4f;

            // Max, not sum — cascade and continuous don't double up.
            float effAct = Mathf.Clamp(Mathf.Max(_activation[i], continuousContrib), 0f, 1f);

            var finalColor = ActivationColor(baseCol, effAct);

            // Encode activation in alpha (1 - act) so the shader can decode
            // it. RGB carries the already-blended albedo, so albedo + act
            // are both derived from one SetInstanceColor call.
            _nodeMultimesh.SetInstanceColor(i,
                new Color(finalColor.R, finalColor.G, finalColor.B, 1f - effAct));
        }
    }

    /// <summary>
    /// Activation palette (N.4 user decision): base → red (0.3) →
    /// orange (0.7) → yellow (1.0). The three breakpoints give the wave
    /// a "heat" feeling — cool base, hot peaks, warm transition.
    /// </summary>
    private static Color ActivationColor(Color baseCol, float act)
    {
        if (act < 0.3f)
            return baseCol.Lerp(new Color(0.90f, 0.15f, 0.15f), act / 0.3f);
        if (act < 0.7f)
            return new Color(0.90f, 0.15f, 0.15f)
                .Lerp(new Color(1.00f, 0.55f, 0.10f), (act - 0.3f) / 0.4f);
        return new Color(1.00f, 0.55f, 0.10f)
            .Lerp(new Color(1.00f, 0.95f, 0.25f), (act - 0.7f) / 0.3f);
    }

    private void PrecomputeNodeWaveData()
    {
        int n = _topology.Nodes.Count;
        _nodeBasePos = new Vector3[n];
        _nodeDistNorm = new float[n];
        _nodePhase = new float[n];

        // Deterministic phase — different seed from _pulseRng so cascade
        // seed choice and wave jitter decorrelate across runs.
        var waveRng = new Random(1337);

        float maxDist = 0f;
        for (int i = 0; i < n; i++)
        {
            _nodeBasePos[i] = _topology.Nodes[i].Position;
            float dist = (_nodeBasePos[i] - StructureCenter).Length();
            if (dist > maxDist) maxDist = dist;
        }
        _maxNodeDist = maxDist > 1e-6f ? maxDist : 1.0f;

        for (int i = 0; i < n; i++)
        {
            _nodeDistNorm[i] = (_nodeBasePos[i] - StructureCenter).Length() / _maxNodeDist;
            _nodePhase[i] = (float)(waveRng.NextDouble() * 2.0 - 1.0);   // [-1, 1]
        }

        GD.Print($"[Neural] Wave precompute: maxDist={_maxNodeDist:F3}");
    }

    public override void _ExitTree() => _ipc.Dispose();
}