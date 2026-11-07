using Godot;

namespace NeuroViz.Visualizer.Scripts;

public sealed class NeuralNode
{
    public Vector3 Position { get; init; }
    public int Layer { get; init; }         // 0=Low, 1=Mid, 2=High
    public int Hemisphere { get; init; }    // 0=Left, 1=Right
}

public sealed class NeuralEdge
{
    public int A { get; init; }
    public int B { get; init; }
    public bool IsCrossCallosum { get; init; }
}

public sealed class NeuralTopology
{
    public IReadOnlyList<NeuralNode> Nodes { get; }
    public IReadOnlyList<NeuralEdge> Edges { get; }

    public NeuralTopology(IReadOnlyList<NeuralNode> nodes, IReadOnlyList<NeuralEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public static NeuralTopology Generate(
        int nodesPerHemisphere = 800,
        int edgesPerNode = 4,
        float ccDensity = 0.3f,
        int seed = 42)
    {
        var rng = new Random(seed);
        var nodes = new List<NeuralNode>(nodesPerHemisphere * 2);

        AddHemisphere(nodes, rng, nodesPerHemisphere, hemisphere: 0, mirrored: false);
        AddHemisphere(nodes, rng, nodesPerHemisphere, hemisphere: 1, mirrored: true);

        var edges = new List<NeuralEdge>();
        AddIntraEdges(nodes, edges, edgesPerNode, rng);
        AddCCEdges(nodes, edges, ccDensity, rng);

        return new NeuralTopology(nodes, edges);
    }

    private static void AddHemisphere(
        List<NeuralNode> nodes, Random rng, int count, int hemisphere, bool mirrored)
    {
        // 30% Low, 40% Mid, 30% High per hemisphere.
        int low = (int)(count * 0.30f);
        int mid = (int)(count * 0.40f);
        int high = count - low - mid;

        // X ranges (left hemisphere, unmirrored). CC gap [0.48, 0.52]
        // separates the two hemispheres visually; right hemisphere is
        // the mirror (1 - xLeft).
        AddLayer(nodes, rng, low, hemisphere, mirrored, layer: 0, xMin: 0.05f, xMax: 0.28f);
        AddLayer(nodes, rng, mid, hemisphere, mirrored, layer: 1, xMin: 0.28f, xMax: 0.40f);
        AddLayer(nodes, rng, high, hemisphere, mirrored, layer: 2, xMin: 0.40f, xMax: 0.48f);
    }

    private static void AddLayer(
        List<NeuralNode> nodes, Random rng, int count, int hemisphere, bool mirrored,
        int layer, float xMin, float xMax)
    {
        // Ellipsoid: center X = 0.265, semi-axes (0.215, 0.5, 0.5).
        // Ensures the two hemispheres span [0.05, 0.48] and [0.52, 0.95]
        // with the CC gap [0.48, 0.52].
        const float cxLeft = 0.265f;
        const float a = 0.215f;
        const float b = 0.5f;
        const float c = 0.5f;
        float cx = mirrored ? (1f - cxLeft) : cxLeft;

        for (int i = 0; i < count; i++)
        {
            // X uniform within the layer's slab.
            float xLeft = xMin + (float)rng.NextDouble() * (xMax - xMin);
            float x = mirrored ? (1f - xLeft) : xLeft;

            // Ellipsoid cross-section at this X: (y/b)² + (z/c)² ≤ 1 - dx².
            float dx = (x - cx) / a;
            float r2 = 1f - dx * dx;
            if (r2 <= 0f) { i--; continue; } // unreachable with our slabs
            float rf = MathF.Sqrt(r2);

            // Uniform-in-disk sampling for YZ (sqrt on radius, not linear).
            float theta = (float)(rng.NextDouble() * 2 * Math.PI);
            float r = MathF.Sqrt((float)rng.NextDouble());
            float y = r * rf * b * MathF.Cos(theta);
            float z = r * rf * c * MathF.Sin(theta);

            nodes.Add(new NeuralNode
            {
                Position = new Vector3(x, y, z),
                Layer = layer,
                Hemisphere = hemisphere,
            });
        }
    }

    private static void AddIntraEdges(
        List<NeuralNode> nodes, List<NeuralEdge> edges, int k, Random rng)
    {
        // Per-hemisphere K-nearest + cross-layer guarantee.
        // Without the guarantee, 4-nearest often all fall in the same
        // layer (density is uniform within a slab) — the hop cascade
        // in N.3 would never leave Low, which is the whole point of
        // the visualizer. Kept at k edges/node total (Q7 = 4).
        var seen = new HashSet<(int, int)>();

        for (int hemi = 0; hemi < 2; hemi++)
        {
            var hemiIndices = Enumerable.Range(0, nodes.Count)
                .Where(i => nodes[i].Hemisphere == hemi).ToList();

            foreach (int i in hemiIndices)
            {
                var pos = nodes[i].Position;
                int layer = nodes[i].Layer;

                var nearest = hemiIndices
                    .Where(j => j != i)
                    .OrderBy(j => (nodes[j].Position - pos).LengthSquared())
                    .Take(k)
                    .ToList();

                if (nearest.Count == 0) continue;

                bool hasCrossLayer = nearest.Any(j => nodes[j].Layer != layer);
                if (!hasCrossLayer)
                {
                    int cross = hemiIndices
                        .Where(j => nodes[j].Layer != layer)
                        .OrderBy(j => (nodes[j].Position - pos).LengthSquared())
                        .FirstOrDefault(-1);
                    if (cross >= 0)
                        nearest[^1] = cross;
                }

                foreach (int j in nearest)
                {
                    var (lo, hi) = i < j ? (i, j) : (j, i);
                    if (seen.Add((lo, hi)))
                        edges.Add(new NeuralEdge { A = lo, B = hi, IsCrossCallosum = false });
                }
            }
        }
    }

    private static void AddCCEdges(
        List<NeuralNode> nodes, List<NeuralEdge> edges, float ccDensity, Random rng)
    {
        // Cross-callosum: per-layer density (High dense, Low sparse),
        // matched by (Y, Z) proximity — so the bundles read as
        // "highways" between hemispheres rather than random cross-links.
        // Interpolation to arcs happens in the renderer (N-Q1 = variant B).
        var layerDensities = new[] { 0.2f, 0.6f, 1.0f }; // Low, Mid, High

        for (int layer = 0; layer < 3; layer++)
        {
            var leftNodes = Enumerable.Range(0, nodes.Count)
                .Where(i => nodes[i].Hemisphere == 0 && nodes[i].Layer == layer)
                .ToList();
            var rightNodes = Enumerable.Range(0, nodes.Count)
                .Where(i => nodes[i].Hemisphere == 1 && nodes[i].Layer == layer)
                .ToList();
            if (leftNodes.Count == 0 || rightNodes.Count == 0) continue;

            float density = ccDensity * layerDensities[layer];

            foreach (int li in leftNodes)
            {
                if (rng.NextDouble() > density) continue;

                var lPos = nodes[li].Position;
                int best = -1;
                float bestDist2 = float.MaxValue;
                foreach (int ri in rightNodes)
                {
                    var rPos = nodes[ri].Position;
                    float dy = rPos.Y - lPos.Y;
                    float dz = rPos.Z - lPos.Z;
                    float d2 = dy * dy + dz * dz;
                    if (d2 < bestDist2) { bestDist2 = d2; best = ri; }
                }
                if (best >= 0)
                    edges.Add(new NeuralEdge { A = li, B = best, IsCrossCallosum = true });
            }
        }
    }
}