namespace CodeGraph.Analysis;

/// <summary>
/// Louvain community detection (Blondel et al., 2008) for an undirected weighted graph.
/// Repeats two phases until modularity stops improving:
///   1. local moving  – each node moves to the neighbouring community with the best modularity gain;
///   2. aggregation   – each community becomes a single node of a smaller graph.
/// Deterministic for a given seed.
/// </summary>
public static class Louvain
{
    private const int MaxPassesPerLevel = 100;
    private const double Epsilon = 1e-12;

    /// <returns>For each node 0..nodeCount-1, a community index (contiguous, starting at 0).</returns>
    public static int[] Detect(int nodeCount, IEnumerable<(int A, int B, double Weight)> edges,
        double resolution = 1.0, int seed = 42)
    {
        // adjacency[u][v] = weight between u and v (both directions stored, u != v).
        // selfLoops[u]    = weight u contributes to its own degree (2 × internal weight after aggregation).
        var adjacency = new Dictionary<int, double>[nodeCount];
        for (var i = 0; i < nodeCount; i++) adjacency[i] = new Dictionary<int, double>();
        var selfLoops = new double[nodeCount];

        foreach (var (a, b, weight) in edges)
        {
            if (weight <= 0 || a < 0 || b < 0 || a >= nodeCount || b >= nodeCount) continue;
            if (a == b)
            {
                selfLoops[a] += 2 * weight;
                continue;
            }
            adjacency[a][b] = adjacency[a].GetValueOrDefault(b) + weight;
            adjacency[b][a] = adjacency[b].GetValueOrDefault(a) + weight;
        }

        // membership[original node] = node of the current (aggregated) level it belongs to.
        var membership = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++) membership[i] = i;

        var random = new Random(seed);
        while (true)
        {
            var levelSize = adjacency.Length;
            var community = LocalMoving(adjacency, selfLoops, resolution, random, out var moved);
            if (!moved) break;

            var communityCount = Renumber(community);
            for (var i = 0; i < nodeCount; i++) membership[i] = community[membership[i]];
            if (communityCount == levelSize || communityCount <= 1) break;

            (adjacency, selfLoops) = Aggregate(adjacency, selfLoops, community, communityCount);
        }

        Renumber(membership);
        return membership;
    }

    /// <summary>Phase 1: greedily move nodes between communities while modularity improves.</summary>
    private static int[] LocalMoving(Dictionary<int, double>[] adjacency, double[] selfLoops, double resolution,
        Random random, out bool moved)
    {
        var n = adjacency.Length;
        var community = new int[n];
        var degree = new double[n];        // k_i
        var total = new double[n];         // Σ_tot per community
        var totalWeight = 0.0;             // 2m

        for (var i = 0; i < n; i++)
        {
            community[i] = i;
            var k = selfLoops[i];
            foreach (var w in adjacency[i].Values) k += w;
            degree[i] = k;
            total[i] = k;
            totalWeight += k;
        }

        moved = false;
        if (totalWeight <= 0) return community;

        var order = Enumerable.Range(0, n).ToArray();
        random.Shuffle(order);

        var neighbourWeights = new Dictionary<int, double>();
        bool improved;
        var passes = 0;
        do
        {
            improved = false;
            foreach (var node in order)
            {
                var current = community[node];
                var k = degree[node];

                // Weight from this node to each neighbouring community.
                neighbourWeights.Clear();
                foreach (var (neighbour, weight) in adjacency[node])
                {
                    var c = community[neighbour];
                    neighbourWeights[c] = neighbourWeights.GetValueOrDefault(c) + weight;
                }

                // Take the node out, then find where it fits best (staying put is the baseline).
                total[current] -= k;
                var best = current;
                var bestGain = neighbourWeights.GetValueOrDefault(current) - resolution * total[current] * k / totalWeight;

                foreach (var (candidate, weight) in neighbourWeights)
                {
                    if (candidate == current) continue;
                    var gain = weight - resolution * total[candidate] * k / totalWeight;
                    if (gain > bestGain + Epsilon)
                    {
                        best = candidate;
                        bestGain = gain;
                    }
                }

                total[best] += k;
                if (best != current)
                {
                    community[node] = best;
                    improved = true;
                    moved = true;
                }
            }
        } while (improved && ++passes < MaxPassesPerLevel);

        return community;
    }

    /// <summary>Phase 2: build the graph whose nodes are the communities of the current level.</summary>
    private static (Dictionary<int, double>[] Adjacency, double[] SelfLoops) Aggregate(
        Dictionary<int, double>[] adjacency, double[] selfLoops, int[] community, int communityCount)
    {
        var newAdjacency = new Dictionary<int, double>[communityCount];
        for (var i = 0; i < communityCount; i++) newAdjacency[i] = new Dictionary<int, double>();
        var newSelfLoops = new double[communityCount];

        for (var node = 0; node < adjacency.Length; node++)
        {
            var from = community[node];
            newSelfLoops[from] += selfLoops[node];
            foreach (var (neighbour, weight) in adjacency[node])
            {
                var to = community[neighbour];
                if (from == to)
                    newSelfLoops[from] += weight; // seen from both ends → 2 × internal weight in total
                else
                    newAdjacency[from][to] = newAdjacency[from].GetValueOrDefault(to) + weight;
            }
        }

        return (newAdjacency, newSelfLoops);
    }

    /// <summary>Relabels values to 0..count-1 in order of first appearance; returns count.</summary>
    private static int Renumber(int[] labels)
    {
        var map = new Dictionary<int, int>();
        for (var i = 0; i < labels.Length; i++)
        {
            if (!map.TryGetValue(labels[i], out var id))
            {
                id = map.Count;
                map[labels[i]] = id;
            }
            labels[i] = id;
        }
        return map.Count;
    }
}
