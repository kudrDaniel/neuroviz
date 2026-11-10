using Godot;
using NeuroViz.Core.Ipc;

namespace NeuroViz.Visualizer.Scripts;

/// <summary>
/// N.2: renders the topology — 1600 node instances (MultiMesh, one
/// icosphere mesh), intra edges and CC arcs (ArrayMesh, PRIMITIVE_LINES).
/// No activation yet (N.3). Palette is layer-coded cold blue; N.4 will
/// replace with an activation-driven shader.
/// </summary>
public partial class MainNodeNeural : VisualizerBase3D
{
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
    private ParamValue? _shiverState = new();
    private ParamValue? _cortexFlashState = new();
    private ParamValue? _pitchState = new();
    private ShaderMaterial _nodeMaterial = null!;
    private ShaderMaterial _edgeMaterial = null!;

    // N.10-fix8: data texture for edge-node activation sync
    private Image _activationImage = null!;
    private ImageTexture _activationTexture = null!;

    private Image _phaseImage = null!;
    private ImageTexture _phaseTexture = null!;

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
    private bool _phaseInitialized;
    private float _maxNodeDist;
    // N.10-fix9: multiple overlapping release waves. Previous impl had
    // three scalars — a new release overwrote the current wave, killing
    // its propagation. Now each release appends to a list; node/edge
    // pulse is the SUM of contributions from all active waves. Old waves
    // are pruned when their propagation window closes.
    private const int MaxConcurrentWaves = 4;
    private readonly List<ReleaseWave> _activeWaves = new();

    private struct ReleaseWave
    {
        public double VisualStart;
        public float Amplitude;
    }
    private double _lastReleaseSetAt = double.NegativeInfinity;
    private bool _wasWaveActive;
    private float _lastAppliedScale = 1.0f;
    private bool _loggedBadNodePos;

    // P2-A preview capture (added to neural in N.9-fix3 — it was in
    // MainNode and MainNode3D from P2-A but never ported to the neural
    // scene, which was created afterwards).
    private bool _previewFirstCaptureLogged;

    // N.7: restructure state.
    private bool _restructureActive;
    private double _restructureStartTime;
    private const float RestructureDuration = 2.5f;
    private double _lastRestructureTime = double.NegativeInfinity;
    private readonly Dictionary<int, Vector3> _restructureTargets = new();
    private readonly Dictionary<int, Vector3> _restructureStartPositions = new();

    // Mesh instances for live rebuild during restructure.
    private MeshInstance3D _intraMeshInstance = null!;
    private MeshInstance3D[] _ccMeshInstances = new MeshInstance3D[3];

    // N.8: idle orbit camera + novelty zoom-out.
    // Defaults match spike (0.05 rad/s, 0.4 Y-amplitude). N.9 will wire
    // these to Settings; for now they are constants.
    private Camera3D _camera = null!;
    private double _visualTime;
    private double _restructureVisualStart = double.NegativeInfinity;
    private float _cameraOrbitSpeed = NeuralSceneConfig.CameraOrbitSpeed;
    private float _cameraYAmplitude = NeuralSceneConfig.CameraYAmplitude;

    private const float WaveDuration = 0.6f;
    private const float WavePulseDuration = 0.4f;
    private const float MaxNonuniformity = 0.10f;
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
    private ParamValue? _panState = new();
    private double _lastClock;

    // N.7-fix3 (B1): in-memory snapshot history for seek-restore and
    // deterministic replay. Persistence in .nvproj (B2) deferred.
    private sealed class RestructureSnapshot
    {
        public double Timestamp;
        public Vector3[] NodePositions = Array.Empty<Vector3>();
        public List<(int a, int b)> IntraEdges = new();
        // N.7-fix7: targets and moved nodes for deterministic replay. On
        // a boundary event near an existing snapshot's timestamp, we apply
        // these exact targets instead of re-running RNG — NoveltyDetector's
        // warm-up drift shifts boundary timestamps by 50-100 ms after seek,
        // so a seed based on the new timestamp would produce a completely
        // different restructure. Replaying stored targets decouples the
        // visual result from timestamp drift.
        public Dictionary<int, Vector3> MovedTargets = new();
    }
    private readonly List<RestructureSnapshot> _restructureHistory = new();
    private RestructureSnapshot? _initialSnapshot;
    private sealed class PendingSnapshot
    {
        public double Timestamp;
        public bool IsNew;
    }
    private PendingSnapshot? _pendingSnapshot;

    // N.7-fix4: the boundary timestamp we restored from during the last
    // seek. If the same boundary event arrives in frame.Events within
    // half a second of playback, we are already at the post-state for it
    // (snapshot restore) — skip re-firing StartRestructure, which would
    // double-restructure on top of the post-state.
    private double _lastRestoreTimestamp = double.NegativeInfinity;

    protected override void _ReadyVisualizer()
    {
        // N.10-fix12: Read topology config from command line before doing
        // anything. This ensures Generate() uses the user's settings even
        // on cold start, where the scene is loaded before IPC connects.
        NeuralSceneConfig.InitializeFromCommandLine();

        _topology = NeuralTopology.Generate(
            nodesPerHemisphere: NeuralSceneConfig.NodesPerHemisphere,
            edgesPerNode: NeuralSceneConfig.EdgesPerNode,
            ccDensity: NeuralSceneConfig.CCDensity,
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

        InitializeNodePhase();
        BuildNodeMesh();
        BuildIntraEdgeMesh();
        BuildCCEdgeMesh();
        SetupNeurotransmitters();
        SetupAmbientParticles();
        BuildAdjacency();
        _activation = new float[_topology.Nodes.Count];

        _activationImage = Image.CreateEmpty(_topology.Nodes.Count, 1, false, Image.Format.Rf);
        _activationTexture = ImageTexture.CreateFromImage(_activationImage);

        _edgeMaterial.SetShaderParameter("activation_tex", _activationTexture);
        _edgeMaterial.SetShaderParameter("texture_width", (float)_topology.Nodes.Count);

        foreach (var m in _ccMaterials)
        {
            if (m != null)
            {
                m.SetShaderParameter("activation_tex", _activationTexture);
                m.SetShaderParameter("texture_width", (float)_topology.Nodes.Count);
            }
        }

        PrecomputeNodeWaveData();   // ← сначала, чтобы _maxNodeDist был готов

        _waveReceivers.Clear();
        _waveReceivers.Add(_edgeMaterial);
        foreach (var m in _ccMaterials)
            if (m != null) _waveReceivers.Add(m);

        UpdateWaveShaderConstants();

        _fpsLabel = new Label { Position = new Vector2(10, 10) };
        AddChild(_fpsLabel);

        Ipc.VisualConfigReceived += OnVisualConfigReceived;
        if (Ipc.LastVisualConfig is { } config)
            OnVisualConfigReceived(config);

        // B1: initial snapshot — state before any restructure. Seek to any
        // position before the first stream_boundary restores to this.
        _initialSnapshot = TakeSnapshot(0.0);
        _lastRestoreTimestamp = 0.0;

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
        // Initial position is the orbit's t=0 point (see UpdateIdleCamera):
        // centre (1, 0, 0) + orbit radius (2.9) along +X, with the Y offset.
        // No visible jump when _Process starts moving the camera.
        _camera = new Camera3D { Position = new Vector3(3.9f, 0.55f, 0.0f) };
        AddChild(_camera);
        _camera.LookAt(new Vector3(1.0f, 0, 0));

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

            // N.10-fix3: glow (bloom). Makes emissive nodes / neurotransmitters
            // bleed into surrounding space — the "radiation" look. Works in
            // gl_compatibility; volumetric fog would require forward_plus (step 2).
            GlowEnabled = true,
            GlowIntensity = 0.8f,
            GlowStrength = 1.0f,
            GlowBloom = 0.1f,           // slight softness, not a blurry blob
            GlowHdrThreshold = 1.0f,    // only HDR pixels (emission > 1.0) bloom
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void InitializeNodePhase()
    {
        if (_phaseInitialized) return;

        int n = _topology.Nodes.Count;
        _nodePhase = new float[n];
        for (int i = 0; i < n; i++)
            _nodePhase[i] = HashPhase(_topology.Nodes[i].Position);

        _phaseInitialized = true;
        GD.Print($"[Neural] Node phase initialized: {n} nodes (frozen for session)");
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
        // N.10-fix7: subtle flow on intra edges. They are dense (3800+
        // lines), so a strong wave would look chaotic — 0.25 gives gentle
        // shimmer that reads as "alive" without becoming noise.
        _edgeMaterial.SetShaderParameter("bundle_wave_strength", 0.25f);

        _intraMeshInstance = new MeshInstance3D
        {
            MaterialOverride = _edgeMaterial,
        };
        _sceneRoot.AddChild(_intraMeshInstance);
        RebuildIntraEdgeMesh();
    }

    private void RebuildIntraEdgeMesh()
    {
        var intra = _topology.Edges.Where(e => !e.IsCrossCallosum).ToList();
        var vertices = new Vector3[intra.Count * 2];
        var colors = new Color[intra.Count * 2];
        var uvs = new Vector2[intra.Count * 2];       // (A, B) — для activation
        var uvs2 = new Vector2[intra.Count * 2];      // (phase, 0) — для wave

        for (int i = 0; i < intra.Count; i++)
        {
            int a = intra[i].A;
            int b = intra[i].B;
            vertices[i * 2] = _topology.Nodes[a].Position;
            vertices[i * 2 + 1] = _topology.Nodes[b].Position;

            var color = EdgeBaseColor(_topology.Nodes[a].Layer);
            colors[i * 2] = color;
            colors[i * 2 + 1] = color;

            uvs[i * 2] = new Vector2(a, b);
            uvs[i * 2 + 1] = new Vector2(a, b);

            // Per-vertex phase: endpoint A uses phase[A], endpoint B uses phase[B].
            // Frozen at generation time, stable across restructure.
            uvs2[i * 2] = new Vector2(_nodePhase[a], 0f);
            uvs2[i * 2 + 1] = new Vector2(_nodePhase[b], 0f);
        }

        _intraMeshInstance.Mesh = MakeLineMesh(vertices, colors, uvs, uvs2);
    }

    // ── CC edges (ArrayMesh with quadratic bezier arcs) ────────────
    // N-Q1 = variant B: edges arc AROUND the geoids, not through them.
    // Without arcs, a Low-Low edge from (0.05, y, z) to (0.95, y, z)
    // passes straight through both geoids (hundreds of internal nodes
    // crossed). Arcs give the CC bundle its visual identity.
    private void BuildCCEdgeMesh()
    {
        // Init materials and empty mesh instances once; RebuildCCEdgeMesh
        // swaps the Mesh on each instance when topology or positions change.
        for (int layer = 0; layer < 3; layer++)
        {
            var mat = new ShaderMaterial { Shader = _edgeMaterial.Shader };
            mat.SetShaderParameter("coherence", 1.0f);
            mat.SetShaderParameter("bundle_energy", 1.0f);
            // N.10-fix7: CC bundles flow strongly — they are the "highways",
            // dramatic flow reads as energy propagating across hemispheres.
            mat.SetShaderParameter("bundle_wave_strength", 0.7f);
            _ccMaterials[layer] = mat;

            _ccMeshInstances[layer] = new MeshInstance3D
            {
                MaterialOverride = mat,
            };
            _sceneRoot.AddChild(_ccMeshInstances[layer]);
        }
        RebuildCCEdgeMesh();
    }

    private void RebuildCCEdgeMesh()
    {
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
            var vertices = new List<Vector3>(edges.Count * segmentsPerArc * 2);
            var colors = new List<Color>(edges.Count * segmentsPerArc * 2);
            var uvs = new List<Vector2>(edges.Count * segmentsPerArc * 2);
            var uvs2 = new List<Vector2>(edges.Count * segmentsPerArc * 2);

            foreach (var edge in edges)
            {
                var a = _topology.Nodes[edge.A];
                var b = _topology.Nodes[edge.B];
                var control = CCControlPoint(a.Position, b.Position, a.Layer);
                var color = CCBaseColor(a.Layer);
                float phaseA = _nodePhase[edge.A];
                float phaseB = _nodePhase[edge.B];

                var prev = a.Position;
                for (int s = 1; s <= segmentsPerArc; s++)
                {
                    float tPrev = (s - 1) / (float)segmentsPerArc;
                    float tCurr = s / (float)segmentsPerArc;
                    var curr = BezierQuadratic(a.Position, control, b.Position, tCurr);

                    vertices.Add(prev); vertices.Add(curr);
                    colors.Add(color); colors.Add(color);

                    // UV0 unchanged: node index pair for activation lookup.
                    uvs.Add(new Vector2(edge.A, edge.B));
                    uvs.Add(new Vector2(edge.A, edge.B));

                    // UV2: phase interpolated along the arc parameter t. This is
                    // what makes the arc deform smoothly from node A's wave to
                    // node B's wave, with no break at either end.
                    uvs2.Add(new Vector2(Mathf.Lerp(phaseA, phaseB, tPrev), 0f));
                    uvs2.Add(new Vector2(Mathf.Lerp(phaseA, phaseB, tCurr), 0f));

                    prev = curr;
                }
            }

            _ccMeshInstances[layer].Mesh = MakeLineMesh(
                vertices.ToArray(), colors.ToArray(), uvs.ToArray(), uvs2.ToArray());
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

    private static ArrayMesh MakeLineMesh(
    Vector3[] vertices, Color[] colors, Vector2[] uvs, Vector2[] uvs2)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = uvs2;

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

    protected override void _ProcessVisualizer(double delta)
    {
        _visualTime += delta;

        if (!Ipc.IsConnected)
        {
            _fpsLabel.Text = $"Waiting... FPS: {Engine.GetFramesPerSecond():F0}";
            return;
        }

        var frame = Ipc.GetLatestFrame();

        double nowClock = Ipc.GetAudioClockSeconds() ?? _lastClock;
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
                _panState = px;

            // N.4 event params
            if (frame.Params.TryGetValue("shiver", out var sv))
                _shiverState = sv;
            if (frame.Params.TryGetValue("cortex_flash", out var cf))
                _cortexFlashState = cf;
            if (frame.Params.TryGetValue("pitch_height", out var ph))
                _pitchState = ph;
            if (frame.Params.TryGetValue("release", out var rel))
            {
                if (rel.SetAtSeconds > _lastReleaseSetAt + 0.001)
                {
                    _lastReleaseSetAt = rel.SetAtSeconds;
                    AddReleaseWave(rel.Value);
                    _wasWaveActive = false;
                }
            }

            // N.7: stream_boundary open → restructure. Cooldown 2 s handled in
            // StartRestructure. Release is processed above so if both fire on
            // the same frame, the wave and restructure start together — the wave
            // hits the moving nodes, and by the time the wave ends the restructure
            // finishes. Reads as "boundary event: wave + reshape".
            foreach (var ev in frame.Events)
            {
                if (ev.Type == "stream_boundary" && ev.Params.GetValueOrDefault("open", 0f) >= 0.5f)
                {
                    float strength = ev.Params.GetValueOrDefault("strength", 0f);
                    StartRestructure(strength, nowClock, ev.TimestampSeconds);
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
        float shiver = _shiverState?.EvaluateAt(nowClock) ?? 0f;
        float flash = _cortexFlashState?.EvaluateAt(nowClock) ?? 0f;
        float pitch = _pitchState?.EvaluateAt(nowClock) ?? 0f;
        // N.4-fix: release wave. Per-node pulse propagates from the structure
        // centre outward. Nonuniformity (some nodes arrive early, some late)
        // is driven by (1 - harmonicity): clean tonal material → ordered front
        // like a water ripple; noisy material → chaotic front like spreading fire.

        float targetFov = 75f + shiver * 8f;
        _camera.Fov = Mathf.Lerp(_camera.Fov, targetFov, 1f - Mathf.Exp(-8f * (float)delta));

        // N.10-fix2: restructure runs BEFORE UpdateNodeTransforms so node
        // instance transforms reflect the SAME topology positions that just
        // rebuilt the edge meshes. Previously: updateNodeTransforms wrote
        // instance transforms from stale positions, then TickRestructure
        // updated topology + rebuilt meshes — nodes lagged edges by 1 frame
        // (visible detachment during the 2.5 s motion).
        if (_restructureActive)
            TickRestructure();

        float nonuniformity = (1f - _smoothHarmonicity) * MaxNonuniformity;
        UpdateNodeTransforms(nonuniformity);
        UpdateNodeColors(pitch);

        // CC bundles: each layer's brightness tracks its band_energy. Range
        // [0.5, 2.0] — dimmer than baseline at silence, brighter than baseline
        // at saturation. Layer 0/1/2 = Low/Mid/High CC commisure.
        // N.10-fix7: shared wave time — same clock as the node continuous
        // wave (N.10-fix6), so edges and their endpoint nodes flow in sync.
        float waveTime = (float)_visualTime;
        // N.10-fix8: update activation texture from the current array
        var bytes = new byte[_activation.Length * sizeof(float)];
        Buffer.BlockCopy(_activation, 0, bytes, 0, bytes.Length);
        _activationImage.SetData(_topology.Nodes.Count, 1, false, Image.Format.Rf, bytes);
        _activationTexture.Update(_activationImage);

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
            _ccMaterials[layer].SetShaderParameter("bundle_wave_time", waveTime);
        }

        // Intra edges share one material; wave time updated once per frame.
        _edgeMaterial.SetShaderParameter("bundle_wave_time", waveTime);

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
            $"Pending: {_pending.Count} | Active: {activeCount} | " +
            (_restructureActive ? "RESTRUCT" : "idle") + "\n" +
            $"nonunif: {nonuniformity:F2}\n" +
            $"bands: {_smoothLow:F2}/{_smoothMid:F2}/{_smoothHigh:F2} | " +
            $"flux: {_smoothFlux:F2} | harm: {_smoothHarmonicity:F2} | " +
            $"vad: {_smoothVad:F2} | smr: {_smoothSmr:F2}";

        UpdateIdleCamera();
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

        float panX = _panState?.EvaluateAt(eventTime) ?? 0f;
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
    private void UpdateNodeTransforms(float nonuniformity)
    {
        PruneExpiredWaves();
        float nowVisual = (float)_visualTime;
        bool hasActiveWaves = _activeWaves.Count > 0;

        float baseScale = 1.0f - _smoothSmr * 0.15f;
        if (float.IsNaN(baseScale) || float.IsInfinity(baseScale))
            baseScale = 1.0f;
        baseScale = Mathf.Clamp(baseScale, 0.5f, 1.5f);

        if (float.IsNaN(nonuniformity) || float.IsInfinity(nonuniformity))
            nonuniformity = 0f;

        bool scaleChanged = Mathf.Abs(baseScale - _lastAppliedScale) > 1e-4f;
        bool needsRewrite = hasActiveWaves || _wasWaveActive || scaleChanged || _restructureActive;

        if (needsRewrite)
        {
            var scaleBasis = Basis.Identity.Scaled(
                new Vector3(baseScale, baseScale, baseScale));

            for (int i = 0; i < _topology.Nodes.Count; i++)
            {
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

                if (hasActiveWaves)
                {
                    float dist = (basePos - StructureCenter).Length();
                    float maxDist = Mathf.Max(_maxNodeDist, 0.1f);
                    float distNorm = Mathf.Clamp(dist / maxDist, 0f, 1f);

                    // Sum contributions from all active waves. Each wave
                    // contributes amplitude × sin(π·elapsed/pulseDur) at the
                    // moment it reaches this node. Overlapping waves
                    // compound; waves offset in time produce distinct dips.
                    float totalPulse = 0f;
                    foreach (var wave in _activeWaves)
                    {
                        float arrival = (float)wave.VisualStart
                                      + distNorm * WaveDuration
                                      + _nodePhase[i] * nonuniformity;
                        float el = nowVisual - arrival;
                        if (el > 0f && el < WavePulseDuration)
                            totalPulse += wave.Amplitude
                                        * Mathf.Sin(el / WavePulseDuration * Mathf.Pi);
                    }

                    float factor = 1f - totalPulse;
                    if (float.IsNaN(factor) || float.IsInfinity(factor))
                        factor = 1f;
                    // Floor at 0.4 (was 0.5 with single wave). Multiple
                    // overlapping waves at 0.15 each can sum to 0.6+; floor
                    // prevents the structure collapsing to a point while
                    // still allowing visible compound compression.
                    factor = Mathf.Clamp(factor, 0.4f, 1.5f);

                    var computed = StructureCenter + (basePos - StructureCenter) * factor;
                    pos = IsFinite(computed) ? computed : basePos;
                }

                _nodeMultimesh.SetInstanceTransform(i, new Transform3D(scaleBasis, pos));
            }

            _lastAppliedScale = baseScale;
        }

        // Pack active waves into two vec4s for the shader. Empty slots stay
        // at 0 amplitude — the shader's per-wave check skips them.
        var starts = new Vector4(0, 0, 0, 0);
        var amps = new Vector4(0, 0, 0, 0);
        for (int w = 0; w < _activeWaves.Count && w < MaxConcurrentWaves; w++)
        {
            float s = (float)_activeWaves[w].VisualStart;
            float a = _activeWaves[w].Amplitude;
            switch (w)
            {
                case 0: starts.X = s; amps.X = a; break;
                case 1: starts.Y = s; amps.Y = a; break;
                case 2: starts.Z = s; amps.Z = a; break;
                case 3: starts.W = s; amps.W = a; break;
            }
        }

        foreach (var m in _waveReceivers)
        {
            m.SetShaderParameter("wave_starts", starts);
            m.SetShaderParameter("wave_amplitudes", amps);
            m.SetShaderParameter("wave_nonuniformity", nonuniformity);
            m.SetShaderParameter("current_time", nowVisual);
        }

        _wasWaveActive = hasActiveWaves;
    }

    private void AddReleaseWave(float releaseValue)
    {
        float amplitude = Mathf.Clamp(releaseValue * WaveAmplitude, 0f, 0.25f);
        if (amplitude <= 0.001f) return;

        // Prune expired waves first so we don't accidentally cap out
        // against already-dead entries.
        float nowVisual = (float)_visualTime;
        double cutoff = nowVisual - (WaveDuration + WavePulseDuration + MaxNonuniformity + 0.1);
        _activeWaves.RemoveAll(w => w.VisualStart < cutoff);

        _activeWaves.Add(new ReleaseWave
        {
            VisualStart = _visualTime,
            Amplitude = amplitude,
        });

        // Cap: drop oldest. Realistically release fires < 4 Hz, wave lasts
        // ~1 s, so 4 slots are plenty. If a user slams releases at 10 Hz
        // during a break, the oldest 6 are lost — acceptable (their
        // remaining propagation at this point is negligible anyway).
        while (_activeWaves.Count > MaxConcurrentWaves)
            _activeWaves.RemoveAt(0);

        GD.Print($"[Neural] Wave launched (active: {_activeWaves.Count})");
    }

    private void PruneExpiredWaves()
    {
        float nowVisual = (float)_visualTime;
        double cutoff = nowVisual - (WaveDuration + WavePulseDuration + MaxNonuniformity + 0.1);
        _activeWaves.RemoveAll(w => w.VisualStart < cutoff);
    }

    /// <summary>
    /// Slow idle orbit around the structure centre in world coordinates.
    /// Structure lives at world (1, 0, 0) (SceneRoot.Position = -1.03,
    /// scale 2, local centroid at X = 1.0 → world 1.0). Orbit radius 2.9
    /// = the N.2-fix2 camera distance, so the initial view is preserved
    /// and the structure fills ~46% of the frame throughout the orbit.
    ///
    /// Driven by _visualTime (advanced from _Process, independent of
    /// playback), matching spike: the camera drifts on pause and does not
    /// jump on seek.
    /// </summary>
    private void UpdateIdleCamera()
    {
        const float orbitRadius = 2.9f;
        const float ySpeed = 0.03f;   // independent from orbit speed
        var center = new Vector3(1.0f, 0f, 0f);

        float angle = (float)_visualTime * _cameraOrbitSpeed;
        float yOffset = 0.55f + Mathf.Sin((float)_visualTime * ySpeed) * _cameraYAmplitude;

        _camera.Position = new Vector3(
            center.X + Mathf.Cos(angle) * orbitRadius,
            center.Y + yOffset,
            center.Z + Mathf.Sin(angle) * orbitRadius);
        _camera.LookAt(center);
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

            // Modulate the layer's continuous energy by a moving 3D wave. The
            // trough of the wave gets 20% of the energy, the peak gets 100%.
            // This turns the uniform "whole layer glows red" into "waves of
            // energy flow through the layer".
            float wave = ComputeContinuousWave(node.Position, _visualTime);
            float continuousContrib = layerEnergy * 0.4f * (0.2f + 0.8f * wave);

            // N.6: vad_state → hemisphere bias.
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
    /// Computes a spatially coherent, time-varying wave in [0, 1] based on
    /// the node's position and the visual clock. Used to modulate the
    /// continuous per-layer activation so it looks like energy flowing
    /// through the network rather than the whole layer glowing in unison.
    ///
    /// Two overlapping sine waves (different frequencies, different axes)
    /// create an organic, non-repeating pattern that drifts over time.
    /// Spatially coherent: neighbouring nodes have similar values, so the
    /// result reads as a wave, not as noise.
    /// </summary>
    private static float ComputeContinuousWave(Vector3 pos, double time)
    {
        float t = (float)time;
        float n1 = Mathf.Sin(pos.X * 6f + pos.Y * 4f + t * 1.5f);
        float n2 = Mathf.Sin(pos.Y * 8f - pos.Z * 7f + t * 2.0f);
        // Sum of two sines is in [-2, 2]; remap to [0, 1].
        return (n1 + n2) * 0.25f + 0.5f;
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
    /// Deterministic phase from position, in [-1, 1]. LOW-frequency so the
    /// ripple is smooth (neighbouring nodes have similar phase) rather than
    /// chaotic. ~1 cycle across the whole structure.
    ///
    /// Computed ONCE at topology generation and never recomputed — phase is
    /// a property of the node, not of its current position (N.11: recomputing
    /// on restructure created a visible seam between moved and unmoved nodes).
    /// </summary>
    private static float HashPhase(Vector3 pos)
    {
        return Mathf.Sin(pos.X * 5.0f + pos.Y * 3.0f) * 0.5f
             + Mathf.Sin(pos.Z * 4.0f + pos.X * 2.5f) * 0.5f;
    }

    private void PrecomputeNodeWaveData()
    {
        int n = _topology.Nodes.Count;
        _nodeBasePos = new Vector3[n];

        float maxDist = 0f;
        for (int i = 0; i < n; i++)
        {
            _nodeBasePos[i] = _topology.Nodes[i].Position;
            float dist = (_nodeBasePos[i] - StructureCenter).Length();
            if (dist > maxDist) maxDist = dist;
        }
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
        // B1: restore topology to the snapshot at or before the seek target.
        // B2 (persistence in .nvproj) is deferred — this covers seek within
        // the current session.
        double seekTarget = nowClock;   // already the new position
        RestructureSnapshot? restore = _initialSnapshot;
        foreach (var s in _restructureHistory)
        {
            if (s.Timestamp <= seekTarget + 0.001) restore = s;
            else break;
        }
        if (restore != null)
        {
            RestoreSnapshot(restore);
            _lastRestoreTimestamp = restore.Timestamp;
        }

        // N.7-fix4: reset the restructure cooldown relative to the new
        // position. Without this, a backward seek leaves _lastRestructureTime
        // in the future; every boundary crossed after the seek is blocked by
        // "nowClock - _lastRestructureTime < 2.0" (which evaluates true for
        // any negative difference). Same class of bug as _pending before
        // N.4-fix2. Offset by -10 s so the first boundary after the seek can
        // fire immediately — cooldown is a debounce, not a "must be idle for
        // 2 s" gate.
        _lastRestructureTime = nowClock - 10.0;

        _restructureActive = false;
        _restructureTargets.Clear();
        _restructureStartPositions.Clear();
        _pendingSnapshot = null;

        _pending.Clear();
        _lastPulseSetAt = double.NegativeInfinity;
        _lastReleaseSetAt = double.NegativeInfinity;
        _lastSeedTime = double.NegativeInfinity;
        _lastFluxFlashTime = double.NegativeInfinity;
        _activeWaves.Clear();

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

    private void StartRestructure(float strength, double nowClock, double eventTimestamp)
    {
        if (_restructureActive) return;
        if (nowClock - _lastRestructureTime < 2.0) return;
        if (eventTimestamp <= _lastRestoreTimestamp + 0.5) return;
        _lastRestructureTime = nowClock;

        // N.7-fix7: if a restructure already exists near this event's
        // timestamp, replay its targets verbatim. This handles the
        // NoveltyDetector warm-up drift (event timestamps shift 50-100 ms
        // after seek-back) — we don't want a fresh RNG sequence just
        // because the timestamp moved a few frames.
        //
        // Tolerance 1.5 s covers worst-case warm-up drift with margin;
        // combined with strength proximity check it's tight enough to
        // distinguish genuine fast section changes (which are > 1.5 s
        // apart in practically all music).
        var historical = _restructureHistory
            .Where(s => s.MovedTargets.Count > 0)
            .Where(s => Math.Abs(s.Timestamp - eventTimestamp) < 1.5)
            .OrderBy(s => Math.Abs(s.Timestamp - eventTimestamp))
            .FirstOrDefault();

        _pendingSnapshot = new PendingSnapshot
        {
            Timestamp = eventTimestamp,
            IsNew = historical == null,
        };

        _restructureActive = true;
        _restructureStartTime = nowClock;              // audio clock — cooldown check
        _restructureVisualStart = _visualTime;          // visual clock — animation
        _restructureTargets.Clear();
        _restructureStartPositions.Clear();

        if (historical != null)
        {
            // Replay mode: use stored targets, skip RNG entirely.
            foreach (var kvp in historical.MovedTargets)
            {
                _restructureStartPositions[kvp.Key] = _topology.Nodes[kvp.Key].Position;
                _restructureTargets[kvp.Key] = kvp.Value;
            }
            GD.Print($"[Neural] Restructure: {historical.MovedTargets.Count} nodes " +
                     $"replaying targets from boundary at t={historical.Timestamp:F2} " +
                     $"(event at t={eventTimestamp:F2})");
            return;
        }

        // Fresh restructure: generate new targets.
        float fraction = strength > 0.01f
            ? Mathf.Clamp(0.15f + strength * 0.02f, 0.15f, 0.45f)
            : 0.30f;
        int moveCount = (int)(fraction * _topology.Nodes.Count);

        var rng = new Random(DeterministicSeed(eventTimestamp));
        var indices = Enumerable.Range(0, _topology.Nodes.Count)
            .OrderBy(_ => rng.Next())
            .Take(moveCount);

        foreach (int i in indices)
        {
            var node = _topology.Nodes[i];
            _restructureStartPositions[i] = node.Position;
            _restructureTargets[i] = ComputeRestructureTarget(node, rng);
        }

        GD.Print($"[Neural] Restructure: {moveCount} nodes moving " +
                 $"(strength={strength:F1}, fraction={fraction:F2}, new)");
    }

    private static int DeterministicSeed(double timestamp)
    {
        long ms = (long)Math.Round(timestamp * 1000.0);
        return (int)(ms ^ 0x5DEECE66D);
    }

    private Vector3 ComputeRestructureTarget(NeuralNode node, Random rng)
    {
        bool left = node.Hemisphere == 0;
        float xMin, xMax;
        switch (node.Layer)
        {
            case 0: xMin = 0.10f; xMax = 0.56f; break;
            case 1: xMin = 0.56f; xMax = 0.80f; break;
            default: xMin = 0.80f; xMax = 0.96f; break;
        }
        if (!left) (xMin, xMax) = (2f - xMax, 2f - xMin);

        float xRange = xMax - xMin;
        var current = node.Position;

        // Free random delta.
        float dx = (float)(rng.NextDouble() * 2 - 1) * xRange * 0.5f;
        float dy = (float)(rng.NextDouble() * 2 - 1) * 0.25f;
        float dz = (float)(rng.NextDouble() * 2 - 1) * 0.25f;

        // ── X: reflect off layer-slab boundaries (was Clamp) ────────────
        float targetX = current.X + dx;
        if (targetX < xMin) targetX = 2f * xMin - targetX;
        if (targetX > xMax) targetX = 2f * xMax - targetX;
        targetX = Mathf.Clamp(targetX, xMin, xMax);   // single reflection is
                                                      // exact given |dx| ≤ xRange/2
                                                      // and current ∈ [xMin, xMax]

        // ── YZ: reflect off circle ──────────────────────────────────────
        // Circle at plane X = targetX, centre (cx, 0). Radius from the
        // triangle the user described: r = sqrt(R² − d²), R = 0.5 (YZ
        // semi-axis), d = |targetX − cx| (perpendicular from hemisphere
        // centre). +15 % buffer prevents clustering at the boundary itself.
        float cx = left ? 0.53f : 1.47f;
        const float R = 0.5f;
        float d = Mathf.Abs(targetX - cx);
        float rInner = Mathf.Sqrt(Mathf.Max(0f, R * R - d * d));
        float rCircle = rInner * 1.15f;

        if (rCircle < 0.01f)
            return new Vector3(targetX, current.Y, current.Z);   // no room

        var posYZ = new Vector2(current.Y, current.Z);
        var deltaYZ = new Vector2(dy, dz);
        var rawTargetYZ = posYZ + deltaYZ;

        if (rawTargetYZ.Length() <= rCircle)
            return new Vector3(targetX, rawTargetYZ.X, rawTargetYZ.Y);

        // Crossing: P + t·Δ hits the circle. Solve |P + t·Δ|² = r².
        float a = deltaYZ.LengthSquared();
        float b = 2f * posYZ.Dot(deltaYZ);
        float c = posYZ.LengthSquared() - rCircle * rCircle;
        float disc = b * b - 4f * a * c;

        if (disc < 0f || a < 1e-8f)
        {
            // Degenerate (shouldn't happen if start inside + target outside).
            var fallback = rawTargetYZ.Length() > 1e-6f
                ? rawTargetYZ.Normalized() * rCircle
                : new Vector2(current.Y, current.Z);
            return new Vector3(targetX, fallback.X, fallback.Y);
        }

        float t = (-b + Mathf.Sqrt(disc)) / (2f * a);
        t = Mathf.Clamp(t, 0f, 1f);
        var cross = posYZ + deltaYZ * t;
        var n = cross.Length() > 1e-6f ? cross.Normalized() : Vector2.Right;
        var outward = rawTargetYZ - cross;
        var reflected = outward - 2f * outward.Dot(n) * n;
        var finalYZ = cross + reflected;

        return new Vector3(targetX, finalYZ.X, finalYZ.Y);
    }

    private void TickRestructure()
    {
        float t = Mathf.Clamp(
            (float)(_visualTime - _restructureVisualStart) / RestructureDuration,
            0f, 1f);
        float eased = t * t * (3f - 2f * t);

        float maxDist = 0f;
        foreach (var kvp in _restructureTargets)
        {
            int i = kvp.Key;
            _topology.Nodes[i].Position =
                _restructureStartPositions[i].Lerp(kvp.Value, eased);
            _nodeBasePos[i] = _topology.Nodes[i].Position;
            _nodePhase[i] = HashPhase(_topology.Nodes[i].Position);
        }

        for (int i = 0; i < _topology.Nodes.Count; i++)
        {
            float d = (_topology.Nodes[i].Position - StructureCenter).Length();
            if (d > maxDist) maxDist = d;
        }
        _maxNodeDist = Mathf.Max(maxDist, 1.0f);
        foreach (var m in _waveReceivers)
            m.SetShaderParameter("wave_max_dist", _maxNodeDist);

        RebuildIntraEdgeMesh();
        RebuildCCEdgeMesh();

        if (t >= 1f) FinishRestructure();
    }

    private void FinishRestructure()
    {
        var moved = new HashSet<int>(_restructureTargets.Keys);

        // N.7-fix2: full re-formation for moved nodes. The threshold-based
        // remove + add-only-if-missing logic let edges accumulate across
        // iterations: a moved node added K new neighbors but old edges to
        // now-distant nodes stayed because neither endpoint was "too far"
        // in a subsequent iteration. Average degree grew 5.6 → 7.5 over
        // four restructures, meshes grew, FPS dropped 60 → 47. Full
        // removal caps avg degree at K for every node that ever moves,
        // stabilising mesh size and FPS after the first restructure.
        //
        // Loss: near-threshold edges that survived in the old scheme are
        // now broken. Semantically correct — a moved node's neighborhood
        // has changed, all its edges are stale.
        var toRemove = new List<(int a, int b)>();
        foreach (var edge in _topology.Edges)
        {
            if (edge.IsCrossCallosum) continue;
            if (moved.Contains(edge.A) || moved.Contains(edge.B))
                toRemove.Add((edge.A, edge.B));
        }
        foreach (var (a, b) in toRemove) _topology.RemoveEdge(a, b);

        int addedCount = 0;
        foreach (int i in moved)
        {
            var pos = _topology.Nodes[i].Position;
            int layer = _topology.Nodes[i].Layer;
            int hemi = _topology.Nodes[i].Hemisphere;

            var nearest = Enumerable.Range(0, _topology.Nodes.Count)
                .Where(j => j != i
                         && _topology.Nodes[j].Layer == layer
                         && _topology.Nodes[j].Hemisphere == hemi)
                .OrderBy(j => (_topology.Nodes[j].Position - pos).LengthSquared())
                .Take(NeuralSceneConfig.EdgesPerNode)
                .ToList();

            foreach (int j in nearest)
            {
                if (!_topology.HasEdge(i, j))
                {
                    _topology.AddEdge(i, j, isCC: false);
                    addedCount++;
                }
            }
        }

        BuildAdjacency();
        PrecomputeNodeWaveData();
        UpdateWaveShaderConstants();
        _pending.Clear();

        _restructureActive = false;
        _restructureVisualStart = double.NegativeInfinity;
        _restructureTargets.Clear();
        _restructureStartPositions.Clear();

        // B1: snapshot represents state AFTER this restructure. Timestamp is
        // the event's, not nowClock — on seek we look for max
        // snapshot.Timestamp <= seek position; finding this snapshot means
        // "position is past the boundary, network should be in post-restructure
        // state".
        if (_pendingSnapshot is { IsNew: true } p)
        {
            // Save both post-state and the targets that produced it. On replay
            // (boundary near p.Timestamp), StartRestructure uses MovedTargets
            // directly instead of re-running RNG.
            _restructureHistory.Add(TakeSnapshot(p.Timestamp, _restructureTargets));
            _restructureHistory.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        _pendingSnapshot = null;

        int intraTotal = _topology.Edges.Count(e => !e.IsCrossCallosum);
        GD.Print($"[Neural] Restructure done: {moved.Count} nodes moved, " +
                 $"{toRemove.Count} edges broken, {addedCount} edges formed, " +
                 $"{intraTotal} intra total (FPS stabilisation)");
    }

    private RestructureSnapshot TakeSnapshot(
    double timestamp,
    Dictionary<int, Vector3>? targets = null)
    {
        var positions = new Vector3[_topology.Nodes.Count];
        for (int i = 0; i < _topology.Nodes.Count; i++)
            positions[i] = _topology.Nodes[i].Position;

        var intra = _topology.Edges
            .Where(e => !e.IsCrossCallosum)
            .Select(e => (e.A, e.B))
            .ToList();

        return new RestructureSnapshot
        {
            Timestamp = timestamp,
            NodePositions = positions,
            IntraEdges = intra,
            MovedTargets = targets != null
                ? new Dictionary<int, Vector3>(targets)
                : new Dictionary<int, Vector3>(),
        };
    }

    private void RestoreSnapshot(RestructureSnapshot snap)
    {
        for (int i = 0; i < snap.NodePositions.Length; i++)
        {
            _topology.Nodes[i].Position = snap.NodePositions[i];
            _nodeBasePos[i] = snap.NodePositions[i];
            _nodePhase[i] = HashPhase(snap.NodePositions[i]);
        }
        _topology.ReplaceIntraEdges(snap.IntraEdges);

        BuildAdjacency();
        PrecomputeNodeWaveData();
        UpdateWaveShaderConstants();
        RebuildIntraEdgeMesh();
        RebuildCCEdgeMesh();
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
        double now = Ipc.GetAudioClockSeconds() ?? _lastClock;
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

    private void UpdateWaveShaderConstants()
    {
        foreach (var m in _waveReceivers)
        {
            m.SetShaderParameter("wave_center", StructureCenter);
            m.SetShaderParameter("wave_max_dist", _maxNodeDist);
            m.SetShaderParameter("wave_duration", WaveDuration);
            m.SetShaderParameter("wave_pulse_duration", WavePulseDuration);
            // N.10-fix9: array uniforms default to zero waves. Updated
            // per-frame in UpdateNodeTransforms.
            m.SetShaderParameter("wave_starts", Vector4.Zero);
            m.SetShaderParameter("wave_amplitudes", Vector4.Zero);
        }
    }

    private void OnVisualConfigReceived(NeuroViz.Core.Ipc.SetVisualConfigPayload p)
    {
        // Live tuning via SetVisualConfig. Same payload as spike uses, but
        // neural reads the NeuralCamera* fields (N.9) — the two scenes have
        // independent camera tunings so switching scene doesn't drag one's
        // settings into the other.
        //
        // Topology params (node count, edges/node, CC density) travel with
        // SetVisualizerScene, not SetVisualConfig — they only apply at scene
        // rebuild, not live. Ignored here.
        _cameraOrbitSpeed = Mathf.Clamp(p.NeuralCameraOrbitSpeed, 0.01f, 0.2f);
        _cameraYAmplitude = Mathf.Clamp(p.NeuralCameraYAmplitude, 0.0f, 1.0f);

        // Neural ignores ambient/burst particle counts — those are spike-
        // specific. If neural grows its own particle tunables later, extend
        // the payload and wire them here.
    }

    public override void _ExitTree()
    {
        Ipc.VisualConfigReceived -= OnVisualConfigReceived;
    }
}