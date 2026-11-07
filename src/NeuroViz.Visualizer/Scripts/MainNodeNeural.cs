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
    // N.6: continuous inputs for hemisphere bias and node scaling.
    private float _contVad, _smoothVad;
    private float _contSmr, _smoothSmr;
    private ParamValueRef _shiverState = new();
    private ParamValueRef _cortexFlashState = new();
    private ParamValueRef _pitchState = new();
    private ShaderMaterial _nodeMaterial = null!;
    private ShaderMaterial _edgeMaterial = null!;
    private double _lastFluxFlashTime = double.NegativeInfinity;
    private double _lastFrameClock = double.NegativeInfinity;

    // N.6: three separate CC materials (one per layer) so each bundle
    // reacts to its own band_energy independently.
    private ShaderMaterial[] _ccMaterials = new ShaderMaterial[3];
    // N.4-fix3: all materials that must receive wave uniforms (intra + CC).
    // Without this, CC edges use default wave_start = -1e10 → pulse = 0
    // → they never move during a release wave.
    private readonly List<ShaderMaterial> _waveReceivers = new();

    // N.4-fix: release wave state.
    private float[] _nodeDistNorm = null!;
    private Vector3[] _nodeBasePos = null!;
    private float[] _nodePhase = null!;
    private float _maxNodeDist;
    private double _waveStartTime = double.NegativeInfinity;
    private float _waveAmplitude;
    private double _lastReleaseSetAt = double.NegativeInfinity;
    private bool _wasWaveActive;
    private float _lastAppliedScale = 1.0f;
    private bool _loggedBadNodePos;
    private const float WaveDuration = 0.6f;
    private const float WavePulseDuration = 0.4f;
    private const float MaxNonuniformity = 0.3f;
    private const float WaveAmplitude = 0.15f;
    private static readonly Vector3 StructureCenter = new(1.0f, 0, 0);

    // N.5: particles. Neurotransmitters = small meshes flying along one
    // random edge per activated node, visualising signal transfer.
    // Ambient = GpuParticles3D halo around the structure, density from
    // band_energy.mid.
    private MultiMesh _neuroMultiMesh = null!;
    private NeuroParticle[] _neuroParticles = null!;
    private int _neuroWriteIndex;
    private GpuParticles3D _ambientParticles = null!;
    private float _smoothMidOld;   // unused, only to note _smoothMid exists

    private const int NeuroInstanceCount = 200;
    private const float NeuroDuration = 0.12f;
    private const float NeuroMeshRadius = 0.015f;
    private const float TinyScale = 0.0001f;   // effectively invisible, avoids degenerate basis

    private struct NeuroParticle
    {
        public Vector3 FromPos;
        public Vector3 ToPos;
        public double StartTime;
        public bool IsActive;
    }

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
        SetupNeurotransmitters();
        SetupAmbientParticles();
        BuildAdjacency();
        _activation = new float[_topology.Nodes.Count];

        PrecomputeNodeWaveData();   // ← сначала, чтобы _maxNodeDist был готов

        _waveReceivers.Clear();
        _waveReceivers.Add(_edgeMaterial);
        foreach (var m in _ccMaterials)
            if (m != null) _waveReceivers.Add(m);

        foreach (var m in _waveReceivers)
        {
            m.SetShaderParameter("wave_center", StructureCenter);
            m.SetShaderParameter("wave_max_dist", _maxNodeDist);
            m.SetShaderParameter("wave_duration", WaveDuration);
            m.SetShaderParameter("wave_pulse_duration", WavePulseDuration);
        }

        _fpsLabel = new Label { Position = new Vector2(10, 10) };
        AddChild(_fpsLabel);

        _ipc = new IpcClient();
        _ipc.Start();

        // N.4-fix3 diagnostic: confirm the topology and base positions are
        // finite before any rendering happens. If this logs "non-finite: N>0",
        // the topology is corrupt and the rest of the pipeline will collapse
        // the MultiMesh — go back to N.1.
        int badPos = 0;
        for (int i = 0; i < _topology.Nodes.Count; i++)
            if (!IsFinite(_topology.Nodes[i].Position)) badPos++;
        GD.Print($"[Neural] Node sanity: {_topology.Nodes.Count} nodes, " +
                 $"non-finite positions: {badPos}, maxDist={_maxNodeDist:F3}");
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
            // CustomAabb on the MultiMesh overrides Godot's auto-computed AABB.
            // Auto-AABB is derived from the instance transforms; if any of those
            // ever become NaN (e.g. a wave factor goes wrong), the whole AABB
            // becomes NaN and Godot culls the entire MultiMeshInstance3D. With a
            // fixed AABB covering the structure + wave compression range, the
            // mesh stays visible even if individual transforms degenerate.
            // Structure local X: [0.10, 1.90], YZ: [-0.5, 0.5], wave compression
            // pulls inward so the outer bounds never grow. 0.5 margin.
            CustomAabb = new Aabb(
        new Vector3(-0.4f, -1.0f, -1.0f),
        new Vector3(2.8f, 2.0f, 2.0f)),
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
        // Split CC edges into per-layer buckets. Each bucket gets its own
        // MeshInstance3D + ShaderMaterial so band_energy.low/mid/high can
        // drive per-layer bundle brightness without vertex-color rebuilds.
        var ccByLayer = new List<NeuralEdge>[] { new(), new(), new() };
        foreach (var edge in _topology.Edges.Where(e => e.IsCrossCallosum))
        {
            int layer = _topology.Nodes[edge.A].Layer;
            ccByLayer[layer].Add(edge);
        }

        const int segmentsPerArc = 8;

        for (int layer = 0; layer < 3; layer++)
        {
            var edges = ccByLayer[layer];
            if (edges.Count == 0) continue;

            var vertices = new List<Vector3>(edges.Count * segmentsPerArc * 2);
            var colors = new List<Color>(edges.Count * segmentsPerArc * 2);

            foreach (var edge in edges)
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

            var mat = new ShaderMaterial { Shader = _edgeMaterial.Shader };
            // CC uses bundle_energy only; coherence stays at 1.0 so
            // harmonicity doesn't dim the commisure highways — those are
            // structural, not tone-quality-driven.
            mat.SetShaderParameter("coherence", 1.0f);
            mat.SetShaderParameter("bundle_energy", 1.0f);
            _ccMaterials[layer] = mat;

            _sceneRoot.AddChild(new MeshInstance3D
            {
                Mesh = MakeLineMesh(vertices.ToArray(), colors.ToArray()),
                MaterialOverride = mat,
            });
        }
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
            if (frame.ContinuousParams.TryGetValue("vad_state", out var vd)) _contVad = vd;
            if (frame.ContinuousParams.TryGetValue("speech_music_ratio", out var sm)) _contSmr = sm;
        }

        double nowClock = _ipc.GetAudioClockSeconds() ?? _lastClock;
        // Detect seek / track change: a backward jump > 0.3 s or forward
        // jump > 2.0 s cannot be produced by normal playback (16 ms/frame).
        // Without this, _pending holds items with timestamps from the old
        // position — they freeze until the clock catches up, and
        // _lastPulseSetAt blocks new pulses whose SetAtSeconds are less than
        // the previous track's last. Both symptoms reported live.
        if (_lastFrameClock != double.NegativeInfinity)
        {
            var jump = nowClock - _lastFrameClock;
            if (jump < -0.3 || jump > 2.0)
                ResetTimelineState(nowClock);
        }
        _lastFrameClock = nowClock;
        float dt = (float)delta;

        // Smoothing (band kb = 83ms, flux kf = 33ms).
        float kb = 1f - Mathf.Exp(-12f * dt);
        float kf = 1f - Mathf.Exp(-30f * dt);
        _smoothLow = Mathf.Lerp(_smoothLow, _contLow, kb);
        _smoothMid = Mathf.Lerp(_smoothMid, _contMid, kb);
        _smoothHigh = Mathf.Lerp(_smoothHigh, _contHigh, kb);
        _smoothFlux = Mathf.Lerp(_smoothFlux, _contFlux, kf);
        _smoothHarmonicity = Mathf.Lerp(_smoothHarmonicity, _contHarmonicity, kb);
        _smoothVad = Mathf.Lerp(_smoothVad, _contVad, kb);
        // smr is already EMA-smoothed in VadDetector (τ ≈ 2 s); light kb
        // smoothing on top removes per-tick jitter without adding perceptible lag.
        _smoothSmr = Mathf.Lerp(_smoothSmr, _contSmr, kb);

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
                var neighbors = _adjacency[idx];
                foreach (var nbr in neighbors)
                    _pending.Enqueue((nbr, intensity * 0.6f), at + 0.12);

                // One visual neurotransmitter per hop — a single particle on a
                // random edge reads as "signal travels along a path" without
                // visually fanning out. Multiple particles per hop would
                // saturate the 200-slot pool at drum-n-bass tempo.
                // 2 per hop — one is too sparse visually. Two, on different random
                // edges from the same node, reads as "signal branches". Still bounded
                // by the 200-slot pool; at drum-n-bass tempo DropOldest handles the
                // overflow.
                for (int k = 0; k < 1 && neighbors.Count > 0; k++)
                {
                    int chosen = neighbors[_pulseRng.Next(neighbors.Count)];
                    SpawnNeurotransmitter(idx, chosen, at);
                }
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
        UpdateNodeTransforms(nowClock, nonuniformity);

        UpdateNodeColors(pitch);

        // CC bundles: each layer's brightness tracks its band_energy. Range
        // [0.5, 2.0] — dimmer than baseline at silence, brighter than baseline
        // at saturation. Layer 0/1/2 = Low/Mid/High CC commisure.
        for (int layer = 0; layer < 3; layer++)
        {
            if (_ccMaterials[layer] == null) continue;
            float bandEnergy = layer switch
            {
                0 => _smoothLow,
                1 => _smoothMid,
                _ => _smoothHigh,
            };
            float bundleBrightness = 0.5f + bandEnergy * 1.5f;
            _ccMaterials[layer].SetShaderParameter("bundle_energy", bundleBrightness);
        }

        UpdateNeurotransmitters(nowClock);

        // Ambient density from band_energy.mid. Same curve as spike (0.05 floor
        // so halo is visible on instrumental intro; 1.0 at saturated mid).
        float targetRatio = Mathf.Clamp(0.1f + _smoothMid * 0.9f, 0.05f, 1.0f);
        _ambientParticles.AmountRatio =
            Mathf.Lerp(_ambientParticles.AmountRatio, targetRatio, kb);

        int activeCount = 0;
        for (int i = 0; i < _activation.Length; i++)
            if (_activation[i] > 0.05f) activeCount++;

        _fpsLabel.Text =
            $"FPS: {Engine.GetFramesPerSecond():F0} | " +
            $"Pending: {_pending.Count} | Active: {activeCount}\n" +
            $"wave: {(_wasWaveActive ? $"ACTIVE {_waveAmplitude:F2}" : "idle")} | " +
            $"nonunif: {nonuniformity:F2}\n" +
            $"bands: {_smoothLow:F2}/{_smoothMid:F2}/{_smoothHigh:F2} | " +
            $"flux: {_smoothFlux:F2} | harm: {_smoothHarmonicity:F2} | " +
            $"vad: {_smoothVad:F2} | smr: {_smoothSmr:F2}";
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

    /// <summary>
    /// Single place where node instance transforms are written. Combines:
    /// - Release wave (N.4-fix): position moves toward StructureCenter
    ///   during the ~1s window after a release event.
    /// - Speech scale (N.6): speech_music_ratio shrinks nodes by up to 15%
    ///   (voice = focused, dense network; music = dispersed, full-size).
    /// One SetInstanceTransform per node per frame. Same order as the
    /// per-frame SetInstanceColor in UpdateNodeColors — 2 writes per node
    /// total, well within 60 FPS budget.
    /// </summary>
    private void UpdateNodeTransforms(double nowClock, float nonuniformity)
    {
        float nowF = (float)nowClock;
        float startF = (float)_waveStartTime;
        bool waveActive = nowF > startF - MaxNonuniformity
                       && nowF < startF + WaveDuration + WavePulseDuration + MaxNonuniformity;

        float baseScale = 1.0f - _smoothSmr * 0.15f;
        if (float.IsNaN(baseScale) || float.IsInfinity(baseScale))
            baseScale = 1.0f;
        baseScale = Mathf.Clamp(baseScale, 0.5f, 1.5f);

        if (float.IsNaN(nonuniformity) || float.IsInfinity(nonuniformity))
            nonuniformity = 0f;

        bool scaleChanged = Mathf.Abs(baseScale - _lastAppliedScale) > 1e-4f;
        bool needsRewrite = waveActive || _wasWaveActive || scaleChanged;

        if (needsRewrite)
        {
            var scaleBasis = Basis.Identity.Scaled(
                new Vector3(baseScale, baseScale, baseScale));

            for (int i = 0; i < _topology.Nodes.Count; i++)
            {
                // Use the topology position directly — this is what
                // BuildNodeMesh used to place the node initially, so it's
                // known-good by the time _Process runs.
                var basePos = _topology.Nodes[i].Position;
                if (!IsFinite(basePos))
                {
                    if (!_loggedBadNodePos)
                    {
                        _loggedBadNodePos = true;
                        GD.PrintErr($"[Neural] Node[{i}] position not finite: {basePos}");
                    }
                    continue;
                }

                var pos = basePos;

                if (waveActive)
                {
                    // Compute distance on the spot, not from _nodeDistNorm.
                    // _nodeDistNorm came from _maxNodeDist which was computed
                    // in PrecomputeNodeWaveData — a prior bug (N.4-fix4)
                    // showed that _maxNodeDist could end up 0 when
                    // _nodeBasePos was not populated correctly, which made
                    // every _nodeDistNorm[i] equal to 0 and collapsed the
                    // wave into a single global pulse. Computing distance
                    // here from the actual topology position eliminates
                    // that failure mode entirely.
                    float dist = (basePos - StructureCenter).Length();
                    float distNorm = Mathf.Clamp(dist / 1.0f, 0f, 1f);

                    float arrival = startF + distNorm * WaveDuration
                                  + _nodePhase[i] * nonuniformity;
                    float el = nowF - arrival;
                    float pulse = 0f;
                    if (el > 0f && el < WavePulseDuration)
                        pulse = Mathf.Sin(el / WavePulseDuration * Mathf.Pi);

                    float factor = 1f - _waveAmplitude * pulse;
                    if (float.IsNaN(factor) || float.IsInfinity(factor))
                        factor = 1f;
                    factor = Mathf.Clamp(factor, 0.5f, 1.5f);

                    var computed = StructureCenter + (basePos - StructureCenter) * factor;
                    pos = IsFinite(computed) ? computed : basePos;
                }

                _nodeMultimesh.SetInstanceTransform(i, new Transform3D(scaleBasis, pos));
            }

            _lastAppliedScale = baseScale;
        }

        if (waveActive)
        {
            foreach (var m in _waveReceivers)
            {
                m.SetShaderParameter("wave_start", startF);
                m.SetShaderParameter("wave_amplitude", _waveAmplitude);
                m.SetShaderParameter("wave_nonuniformity", nonuniformity);
            }
            _wasWaveActive = true;
        }
        else if (_wasWaveActive)
        {
            foreach (var m in _waveReceivers)
                m.SetShaderParameter("wave_amplitude", 0f);
            _wasWaveActive = false;
            _waveAmplitude = 0f;
        }
        foreach (var m in _waveReceivers)
            m.SetShaderParameter("current_time", nowF);
    }

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

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

            // N.6: vad_state → hemisphere bias. Voice detected (vad=1) → left
            // hemisphere (index 0) gets +0.15 baseline activation, right +0.05.
            // Reads as "when there's voice, the left side of the network glows
            // faintly even without a cascade" — a subtle but distinct channel
            // from pan_x's seed-hemisphere selection.
            float hemiBias = node.Hemisphere == 0
                ? _smoothVad * 0.15f
                : _smoothVad * 0.05f;
            continuousContrib += hemiBias;

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

    /// <summary>
    /// Deterministic pseudo-random phase from position, in [-1, 1].
    /// MUST match neural_edge.gdshader:hash_phase exactly — shared
    /// endpoints of edges and nodes get identical phase, so they move
    /// together through the release wave.
    /// </summary>
    private static float HashPhase(Vector3 pos)
    {
        return MathF.Sin(pos.X * 17f + pos.Y * 23f + pos.Z * 31f);
    }

    private void PrecomputeNodeWaveData()
    {
        int n = _topology.Nodes.Count;
        _nodeBasePos = new Vector3[n];
        _nodeDistNorm = new float[n];
        _nodePhase = new float[n];

        // Deterministic phase — different seed from _pulseRng so cascade
        // seed choice and wave jitter decorrelate across runs.
        float maxDist = 0f;
        for (int i = 0; i < n; i++)
        {
            _nodeBasePos[i] = _topology.Nodes[i].Position;
            float dist = (_nodeBasePos[i] - StructureCenter).Length();
            if (dist > maxDist) maxDist = dist;
        }
        // Hard guard: if _topology.Nodes was empty or every position was
        // (1,0,0), maxDist stays 0 → dist/0 = Infinity in _nodeDistNorm →
        // NaN arrival in the wave → NaN instance transforms → Godot culls
        // the entire MultiMesh. Never let _maxNodeDist be <= 0.
        if (maxDist <= 1e-4f || float.IsNaN(maxDist) || float.IsInfinity(maxDist))
            maxDist = 1.0f;
        _maxNodeDist = maxDist;

        GD.Print($"[Neural] Wave precompute: nodes={n}, maxDist={_maxNodeDist:F3}");
    }

    /// <summary>
    /// Clears everything that carries absolute audio-clock time: pending
    /// cascade queue, seed/release/flux edge-detection timestamps, wave
    /// state, activation array. Invoked on any large clock jump (seek or
    /// track change). After this the visualizer behaves as if it just
    /// started at the new position — pending items only from the current
    /// timeline, pulses always fire their first frame.
    /// </summary>
    private void ResetTimelineState(double nowClock)
    {
        _pending.Clear();
        _lastPulseSetAt = double.NegativeInfinity;
        _lastReleaseSetAt = double.NegativeInfinity;
        _lastSeedTime = double.NegativeInfinity;
        _lastFluxFlashTime = double.NegativeInfinity;
        _waveStartTime = double.NegativeInfinity;
        _waveAmplitude = 0f;

        // Zero activation — a fading cascade from before the seek would
        // confuse against the new position's events.
        for (int i = 0; i < _activation.Length; i++)
            _activation[i] = 0f;

        // Reset wave: nodes back to base, edge shader amplitude to zero so
        // the shader stops applying any in-flight pulse.
        if (_nodeBasePos is not null)
        {
            for (int i = 0; i < _topology.Nodes.Count; i++)
                _nodeMultimesh.SetInstanceTransform(i,
                    new Transform3D(Basis.Identity, _topology.Nodes[i].Position));
        }
        _edgeMaterial.SetShaderParameter("wave_amplitude", 0f);
        _wasWaveActive = false;

        // Kill in-flight neurotransmitters — their StartTime is in the old
        // timeline, would fly through the new position's geometry.
        for (int i = 0; i < NeuroInstanceCount; i++)
        {
            _neuroParticles[i].IsActive = false;
            _neuroMultiMesh.SetInstanceTransform(i,
                new Transform3D(
                    Basis.Identity.Scaled(new Vector3(TinyScale, TinyScale, TinyScale)),
                    Vector3.Zero));
        }
        _neuroWriteIndex = 0;

        GD.Print($"[Neural] Timeline reset at clock={nowClock:F2} (seek or track change)");
    }

    private void SetupNeurotransmitters()
    {
        var mesh = new SphereMesh
        {
            Radius = 0.0075f,             // was 0.015 — ~10 px at typical view, visible
            Height = 0.015f,
            RadialSegments = 6,
            Rings = 4,
        };

        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            // Violet pops against the beige/tan node palette — yellow
            // blended into the base and read as "dust".
            AlbedoColor = new Color(0.75f, 0.35f, 1.0f),
            EmissionEnabled = true,
            Emission = new Color(0.40f, 0.15f, 0.65f),
            EmissionEnergyMultiplier = 0.6f,
        };

        _neuroMultiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            Mesh = mesh,
            InstanceCount = NeuroInstanceCount,
            // VisibilityAabb does NOT exist on MultiMeshInstance3D (that
            // property belongs to GpuParticles3D). MultiMesh has CustomAabb,
            // and it must cover the entire area where any instance might
            // appear — otherwise Godot frustum-culls the whole MultiMesh
            // before any particle spawns. All 200 instances start at
            // (0,0,0) local with tiny scale, so the auto-computed AABB is
            // degenerate until the first particle flies — and by then the
            // cull decision is already made.
            // Structure local X: [0.10, 1.90], YZ: [-0.5, 0.5]; +0.5 margin.
            CustomAabb = new Aabb(
                new Vector3(-0.5f, -1.0f, -1.0f),
                new Vector3(3.0f, 2.0f, 2.0f)),
        };
        _neuroParticles = new NeuroParticle[NeuroInstanceCount];

        var hidden = new Transform3D(
            Basis.Identity.Scaled(new Vector3(TinyScale, TinyScale, TinyScale)),
            Vector3.Zero);
        for (int i = 0; i < NeuroInstanceCount; i++)
            _neuroMultiMesh.SetInstanceTransform(i, hidden);

        _sceneRoot.AddChild(new MultiMeshInstance3D
        {
            Multimesh = _neuroMultiMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,   // see below
                                                                        // No VisibilityAabb — CustomAabb on the MultiMesh does the job.
        });
    }

    private void SetupAmbientParticles()
    {
        var quad = new QuadMesh { Size = new Vector2(0.04f, 0.04f) };

        var mat = new StandardMaterial3D
        {
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.95f, 0.70f, 0.40f, 0.55f),
            VertexColorUseAsAlbedo = true,
        };

        var gradient = new Gradient();
        gradient.SetColor(0, new Color(1.0f, 1.0f, 0.85f, 0.9f));
        gradient.SetColor(1, new Color(0.85f, 0.55f, 0.30f, 0.0f));
        var colorRamp = new GradientTexture1D { Gradient = gradient };

        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 3.0f,   // was 2.5 — world units now, covers structure extent
            Gravity = Vector3.Zero,
            Direction = Vector3.Up,
            Spread = 180f,
            InitialVelocityMin = 0.03f,
            InitialVelocityMax = 0.10f,
            ScaleMin = 0.5f,
            ScaleMax = 1.5f,
            ColorRamp = colorRamp,
            OrbitVelocityMin = 0.05f,
            OrbitVelocityMax = 0.20f,
        };

        _ambientParticles = new GpuParticles3D
        {
            Amount = 300,
            Lifetime = 4.0,
            Emitting = true,
            AmountRatio = 0.3f,
            DrawPass1 = quad,
            ProcessMaterial = pm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-4, -4, -4), new Vector3(8, 8, 8)),
        };
        // Ambient is a background halo — NOT part of the structure. Adding
        // it to MainNodeNeural (not SceneRoot) puts its emission sphere in
        // WORLD space, centred on the camera target (1.0, 0, 0). In
        // SceneRoot it would be at world X=-1.03 (SceneRoot's position),
        // which is exactly why the halo was visibly left-shifted.
        _ambientParticles.Position = new Vector3(1.0f, 0, 0);
        AddChild(_ambientParticles);
    }

    private void SpawnNeurotransmitter(int fromIdx, int toIdx, double startTime)
    {
        // A particle that would have already arrived is dropped — cheaper
        // than spawning and immediately killing it next frame.
        double now = _ipc.GetAudioClockSeconds() ?? _lastClock;
        if (now - startTime >= NeuroDuration) return;

        int slot = _neuroWriteIndex % NeuroInstanceCount;
        _neuroWriteIndex++;

        _neuroParticles[slot] = new NeuroParticle
        {
            FromPos = _nodeBasePos[fromIdx],
            ToPos = _nodeBasePos[toIdx],
            StartTime = startTime,
            IsActive = true,
        };
    }

    private void UpdateNeurotransmitters(double nowClock)
    {
        for (int i = 0; i < NeuroInstanceCount; i++)
        {
            if (!_neuroParticles[i].IsActive) continue;

            float t = (float)((nowClock - _neuroParticles[i].StartTime) / NeuroDuration);
            if (t < 0f || t > 1f)
            {
                _neuroParticles[i].IsActive = false;
                _neuroMultiMesh.SetInstanceTransform(i,
                    new Transform3D(
                        Basis.Identity.Scaled(new Vector3(TinyScale, TinyScale, TinyScale)),
                        Vector3.Zero));
                continue;
            }

            // Straight-line lerp. A curved path (quadratic bezier with a
            // small perpendicular control point) would read slightly more
            // organic; straight is fine for the first pass and cheaper.
            var pos = _neuroParticles[i].FromPos.Lerp(_neuroParticles[i].ToPos, t);

            // Scale profile: sin(pi*t) — 0 at start/end, 1 at midpoint.
            // "Fades in, fades out" without shader involvement.
            float scale = Mathf.Sin(t * Mathf.Pi);
            var basis = Basis.Identity.Scaled(new Vector3(scale, scale, scale));
            _neuroMultiMesh.SetInstanceTransform(i, new Transform3D(basis, pos));
        }
    }

    public override void _ExitTree() => _ipc.Dispose();
}