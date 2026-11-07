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
    private Label _fpsLabel = null!;

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

        SetupCameraAndLight();
        BuildNodeMesh();
        BuildIntraEdgeMesh();
        BuildCCEdgeMesh();

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
            DirectionalShadowMaxDistance = 6.0f,   // covers the wider X span
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

        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1, 1, 1),   // multiplied by instance color
            VertexColorUseAsAlbedo = true,       // per-instance tint flows to ALBEDO
            EmissionEnabled = true,
            Emission = new Color(0.06f, 0.04f, 0.02f),   // was (0.03, 0.05, 0.10) bluish
            Roughness = 0.55f,
        };

        _nodeMultimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = _topology.Nodes.Count,
        };

        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _nodeMultimesh,
            MaterialOverride = mat,
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

        AddChild(new MeshInstance3D
        {
            Mesh = MakeLineMesh(vertices, colors),
            MaterialOverride = LineMaterial(),
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

        AddChild(new MeshInstance3D
        {
            Mesh = MakeLineMesh(vertices.ToArray(), colors.ToArray()),
            MaterialOverride = LineMaterial(),
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

    private static StandardMaterial3D LineMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1, 1, 1, 1),
    };

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
        _fpsLabel.Text =
            $"FPS: {Engine.GetFramesPerSecond():F0} | " +
            $"Nodes: {_topology.Nodes.Count} | " +
            $"Edges: {_topology.Edges.Count} | " +
            $"CC: {_topology.Edges.Count(e => e.IsCrossCallosum)}";
    }

    public override void _ExitTree() => _ipc.Dispose();
}