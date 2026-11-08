using System;
using System.Collections.Generic;
using System.Linq;

namespace GraphDBViewerWeb.Code;

///<summary>
///Algorithms over the graph a query brought back (DESIGN.md Part 2, *Graph algorithms*, tier B): run in the browser on
///what is already loaded, so they work on every engine, on an imported file and on a pasted graph, with no analytics
///engine behind them. Pure: vertices and edges go in, a number or a group per vertex comes out, and the page decides
///how to show it.
///
///An edge whose end is not among the vertices, as a result cut off at a limit holds, is left out: it cannot be drawn
///either. Every algorithm visits the vertices in the order they were given, so the same graph always gives the same
///answer, ties included.
///</summary>
public static class GraphAlgorithms
{
    ///<summary>Which way an edge may be followed: its own direction, against it, or either way.</summary>
    public enum Direction
    {
        Out,
        In,
        Both
    }

    public sealed record Edge(string Id, string Source, string Target, double Weight = 1);

    ///<summary>How many vertices betweenness walks from at most before it estimates, here and where a database's library can sample (<see cref="GdsAlgorithms"/>).</summary>
    public const int BetweennessSources = 2000;

    ///<summary>How many rounds PageRank goes at most, here and where a database's library is asked (<see cref="GdsAlgorithms"/>, <see cref="MageAlgorithms"/>).</summary>
    public const int PageRankRounds = 100;

    ///<summary>The graph, indexed once: each vertex's number and the edges at it, out and in.</summary>
    public sealed class Graph
    {
        public IReadOnlyList<string> Nodes { get; }
        public IReadOnlyList<Edge> Edges { get; }

        internal readonly Dictionary<string, int> Index = new(StringComparer.Ordinal);
        internal readonly List<(int To, int Edge)>[] Outgoing;
        internal readonly List<(int To, int Edge)>[] Incoming;

        public Graph(IEnumerable<string> nodes, IEnumerable<Edge> edges)
        {
            var kept = new List<string>();

            foreach (var node in nodes ?? [])
                if (node != null && Index.TryAdd(node, kept.Count))
                    kept.Add(node);

            Nodes = kept;
            Outgoing = new List<(int, int)>[kept.Count];
            Incoming = new List<(int, int)>[kept.Count];

            for (var i = 0; i < kept.Count; i++)
            {
                Outgoing[i] = [];
                Incoming[i] = [];
            }

            var within = new List<Edge>();

            foreach (var edge in edges ?? [])
            {
                if (edge == null || !Index.TryGetValue(edge.Source ?? "", out var from) || !Index.TryGetValue(edge.Target ?? "", out var to))
                    continue;

                Outgoing[from].Add((to, within.Count));
                Incoming[to].Add((from, within.Count));
                within.Add(edge);
            }

            Edges = within;
        }

        public int Count => Nodes.Count;

        ///<summary>The vertices one step from this one, the way the direction allows, with the edge each is reached by.</summary>
        internal IEnumerable<(int To, int Edge)> Next(int node, Direction direction)
        {
            if (direction == Direction.Out)
                return Outgoing[node];
            else if (direction == Direction.In)
                return Incoming[node];

            return Outgoing[node].Concat(Incoming[node]);
        }
    }

    ///<summary>
    ///The loaded graph as the algorithms take it: its vertices, and the ends of its edges that the result does not hold,
    ///since the canvas draws those too; and its edges, weighed by a property when one is named. An edge whose property
    ///is missing or is not a number counts one, and <paramref name="unweighed"/> says how many did.
    ///</summary>
    public static Graph FromTable(GraphDataConverter.GraphTable table, string weightProperty, out int unweighed)
    {
        var nodes = new List<string>();

        foreach (var row in table.Nodes)
            nodes.Add(row.Id);

        foreach (var row in table.Edges)
        {
            nodes.Add(row.Source);
            nodes.Add(row.Target);
        }

        var edges = new List<Edge>();
        var weighed = !string.IsNullOrWhiteSpace(weightProperty);

        unweighed = 0;

        foreach (var row in table.Edges)
        {
            var weight = 1.0;

            if (weighed)
            {
                if (row.Properties.TryGetValue(weightProperty, out var text)
                    && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed))
                    weight = parsed;
                else
                    unweighed++;
            }

            edges.Add(new Edge(row.Id, row.Source, row.Target, weight));
        }

        return new Graph(nodes, edges);
    }

    //── Degree ─────────────────────────────────────────────────────────────────────────────────

    ///<summary>How many edges each vertex has, the way the direction counts them: out, in, or both. A loop counts once each way.</summary>
    public static Dictionary<string, double> Degree(Graph graph, Direction direction)
    {
        var degree = new Dictionary<string, double>(StringComparer.Ordinal);

        for (var i = 0; i < graph.Count; i++)
        {
            var count = 0;

            if (direction != Direction.In)
                count += graph.Outgoing[i].Count;

            if (direction != Direction.Out)
                count += graph.Incoming[i].Count;

            degree[graph.Nodes[i]] = count;
        }

        return degree;
    }

    //── PageRank ───────────────────────────────────────────────────────────────────────────────

    ///<summary>
    ///PageRank along the edges' own direction: a vertex is as important as the vertices that point at it, each sharing
    ///its own importance out among its edges. A vertex with no edges out shares its importance with every vertex, as the
    ///original paper does, so the scores always add up to one. Iterated until no score moves by more than a billionth
    ///in total, or a hundred rounds.
    ///</summary>
    public static Dictionary<string, double> PageRank(Graph graph, double damping = 0.85, int rounds = PageRankRounds)
    {
        var n = graph.Count;
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        if (n == 0)
            return scores;

        var rank = Enumerable.Repeat(1.0 / n, n).ToArray();
        var next = new double[n];

        for (var round = 0; round < rounds; round++)
        {
            var dangling = 0.0;

            for (var i = 0; i < n; i++)
                if (graph.Outgoing[i].Count == 0)
                    dangling += rank[i];

            var shared = (1 - damping) / n + damping * dangling / n;

            for (var i = 0; i < n; i++)
            {
                var sum = 0.0;

                foreach (var (from, _) in graph.Incoming[i])
                    sum += rank[from] / graph.Outgoing[from].Count;

                next[i] = shared + damping * sum;
            }

            var moved = 0.0;

            for (var i = 0; i < n; i++)
            {
                moved += Math.Abs(next[i] - rank[i]);
                rank[i] = next[i];
            }

            if (moved < 1e-9)
                break;
        }

        for (var i = 0; i < n; i++)
            scores[graph.Nodes[i]] = rank[i];

        return scores;
    }

    //── Betweenness ────────────────────────────────────────────────────────────────────────────

    ///<summary>
    ///How many shortest paths between other vertices pass through each vertex (Brandes' algorithm, unweighted), the way
    ///the direction allows: the brokers of a graph, the vertices it would come apart at. Either way, each pair is counted
    ///once. It costs a walk of the whole graph from every vertex, so past <paramref name="sources"/> vertices it walks
    ///from that many, spread evenly, and scales up: an estimate, which the page says it is.
    ///</summary>
    public static Dictionary<string, double> Betweenness(Graph graph, Direction direction = Direction.Both, int sources = BetweennessSources)
    {
        var n = graph.Count;
        var centrality = new double[n];
        var starts = Enumerable.Range(0, n).ToList();

        if (n > sources && sources > 0)
            starts = Enumerable.Range(0, sources).Select(i => (int)((long)i * n / sources)).ToList();

        var stack = new List<int>(n);
        var paths = new double[n];
        var distance = new int[n];
        var dependency = new double[n];
        var before = new List<int>[n];

        for (var i = 0; i < n; i++)
            before[i] = [];

        foreach (var start in starts)
        {
            stack.Clear();

            for (var i = 0; i < n; i++)
            {
                before[i].Clear();
                paths[i] = 0;
                distance[i] = -1;
                dependency[i] = 0;
            }

            paths[start] = 1;
            distance[start] = 0;

            var queue = new Queue<int>();

            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var v = queue.Dequeue();

                stack.Add(v);

                foreach (var (w, _) in graph.Next(v, direction))
                {
                    if (w == v)
                        continue;

                    if (distance[w] < 0)
                    {
                        distance[w] = distance[v] + 1;
                        queue.Enqueue(w);
                    }

                    if (distance[w] == distance[v] + 1)
                    {
                        paths[w] += paths[v];
                        before[w].Add(v);
                    }
                }
            }

            for (var i = stack.Count - 1; i >= 0; i--)
            {
                var w = stack[i];

                foreach (var v in before[w])
                    dependency[v] += paths[v] / paths[w] * (1 + dependency[w]);

                if (w != start)
                    centrality[w] += dependency[w];
            }
        }

        //Walked either way, every pair was counted from both of its ends.
        var scale = 1.0;

        if (direction == Direction.Both)
            scale = 0.5;

        if (starts.Count < n && starts.Count > 0)
            scale *= (double)n / starts.Count;

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        for (var i = 0; i < n; i++)
            scores[graph.Nodes[i]] = centrality[i] * scale;

        return scores;
    }

    //── Groups ─────────────────────────────────────────────────────────────────────────────────

    ///<summary>
    ///The pieces the graph falls into when the edges' direction is ignored, numbered from the largest, 0, down; pieces of
    ///one size in the order their first vertex was given.
    ///</summary>
    public static Dictionary<string, int> ConnectedComponents(Graph graph)
    {
        var n = graph.Count;
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        foreach (var edge in Enumerable.Range(0, n).SelectMany(i => graph.Outgoing[i].Select(o => (From: i, o.To))))
        {
            var a = Find(edge.From);
            var b = Find(edge.To);

            if (a == b)
                continue;

            if (a < b)
                parent[b] = a;
            else
                parent[a] = b;
        }

        return Numbered(graph, Enumerable.Range(0, n).Select(Find).ToArray());
    }

    ///<summary>
    ///Communities: groups of vertices more tightly linked among themselves than to the rest, found by the Louvain method
    ///(Blondel and others, 2008), which moves each vertex into the neighboring group that raises the graph's modularity
    ///most, then folds each group into one vertex and goes again until nothing moves. The edges' direction is ignored,
    ///and their weights count. Numbered from the largest group, 0, down.
    ///</summary>
    public static Dictionary<string, int> Communities(Graph graph)
    {
        var n = graph.Count;

        //The working graph: weighted, undirected, a loop holding the weight inside a vertex once it is a folded group.
        var neighbors = new List<Dictionary<int, double>>(n);

        for (var i = 0; i < n; i++)
            neighbors.Add([]);

        foreach (var i in Enumerable.Range(0, n))
            foreach (var (to, edge) in graph.Outgoing[i])
            {
                var weight = Math.Max(0, graph.Edges[edge].Weight);

                Add(neighbors[i], to, weight);

                if (to != i)
                    Add(neighbors[to], i, weight);
            }

        var member = Enumerable.Range(0, n).ToArray();

        for (var level = 0; level < 32; level++)
        {
            var count = neighbors.Count;
            var group = Enumerable.Range(0, count).ToArray();
            var strength = new double[count];
            var total = 0.0;

            for (var i = 0; i < count; i++)
            {
                foreach (var (to, weight) in neighbors[i])
                    if (to == i)
                        strength[i] += 2 * weight;
                    else
                        strength[i] += weight;

                total += strength[i];
            }

            if (total <= 0)
                break;

            var m2 = total;
            var groupStrength = (double[])strength.Clone();
            var moved = false;

            for (var pass = 0; pass < 100; pass++)
            {
                var movedThisPass = false;

                for (var i = 0; i < count; i++)
                {
                    var current = group[i];
                    var links = new Dictionary<int, double>();

                    foreach (var (to, weight) in neighbors[i])
                        if (to != i)
                            Add(links, group[to], weight);

                    groupStrength[current] -= strength[i];

                    var best = current;
                    var bestGain = Gain(links, current, groupStrength, strength[i], m2);

                    foreach (var (candidate, _) in links.OrderBy(l => l.Key))
                    {
                        var gain = Gain(links, candidate, groupStrength, strength[i], m2);

                        if (gain > bestGain + 1e-12)
                        {
                            best = candidate;
                            bestGain = gain;
                        }
                    }

                    groupStrength[best] += strength[i];

                    if (best != current)
                    {
                        group[i] = best;
                        movedThisPass = true;
                        moved = true;
                    }
                }

                if (!movedThisPass)
                    break;
            }

            if (!moved)
                break;

            //Fold each group into one vertex, renumbered in the order of its first member, and carry the vertices along.
            var renumber = new Dictionary<int, int>();

            for (var i = 0; i < count; i++)
                if (!renumber.ContainsKey(group[i]))
                    renumber[group[i]] = renumber.Count;

            var folded = new List<Dictionary<int, double>>(renumber.Count);

            for (var i = 0; i < renumber.Count; i++)
                folded.Add([]);

            for (var i = 0; i < count; i++)
                foreach (var (to, weight) in neighbors[i])
                {
                    var a = renumber[group[i]];
                    var b = renumber[group[to]];

                    //Each link between two vertices is listed at both, and a loop once: halve the doubles inside a group.
                    if (a == b && to != i)
                        Add(folded[a], a, weight / 2);
                    else
                        Add(folded[a], b, weight);
                }

            for (var v = 0; v < n; v++)
                member[v] = renumber[group[member[v]]];

            neighbors = folded;
        }

        return Numbered(graph, member);
    }

    ///<summary>What moving a vertex into a group gains in modularity, up to a factor every candidate shares.</summary>
    private static double Gain(Dictionary<int, double> links, int group, double[] groupStrength, double strength, double m2)
    {
        links.TryGetValue(group, out var into);

        return into - groupStrength[group] * strength / m2;
    }

    private static void Add(Dictionary<int, double> weights, int key, double weight)
    {
        weights.TryGetValue(key, out var held);
        weights[key] = held + weight;
    }

    ///<summary>Groups numbered from the largest, 0, down, groups of one size in the order their first vertex was given.</summary>
    private static Dictionary<string, int> Numbered(Graph graph, int[] group)
    {
        var order = new Dictionary<int, (int Size, int First)>();

        for (var i = 0; i < group.Length; i++)
            if (order.TryGetValue(group[i], out var held))
                order[group[i]] = (held.Size + 1, held.First);
            else
                order[group[i]] = (1, i);

        var number = order.OrderByDescending(g => g.Value.Size).ThenBy(g => g.Value.First).Select((g, i) => (g.Key, i)).ToDictionary(g => g.Key, g => g.i);
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < group.Length; i++)
            groups[graph.Nodes[i]] = number[group[i]];

        return groups;
    }

    //── A shortest path ────────────────────────────────────────────────────────────────────────

    ///<summary>One shortest path, its vertices and edges in order from the first end, and what it costs.</summary>
    public sealed record Path(IReadOnlyList<string> Nodes, IReadOnlyList<string> Edges, double Cost);

    ///<summary>
    ///A shortest path between two vertices, the way the direction allows, or null when there is none: the fewest edges,
    ///or, when <paramref name="weighted"/>, the least total weight (Dijkstra; an edge's weight below zero is refused by
    ///the caller, who reads it). Of several equally short paths, the one through the vertices given first.
    ///</summary>
    public static Path ShortestPath(Graph graph, string from, string to, Direction direction, bool weighted)
    {
        if (!graph.Index.TryGetValue(from ?? "", out var start) || !graph.Index.TryGetValue(to ?? "", out var end))
            return null;

        var n = graph.Count;
        var cost = Enumerable.Repeat(double.PositiveInfinity, n).ToArray();
        var came = Enumerable.Repeat((Node: -1, Edge: -1), n).ToArray();
        var done = new bool[n];
        var queue = new PriorityQueue<int, (double Cost, int Node)>();

        cost[start] = 0;
        queue.Enqueue(start, (0, start));

        while (queue.Count > 0)
        {
            var v = queue.Dequeue();

            if (done[v])
                continue;

            done[v] = true;

            if (v == end)
                break;

            foreach (var (w, edge) in graph.Next(v, direction))
            {
                var step = 1.0;

                if (weighted)
                    step = graph.Edges[edge].Weight;

                var through = cost[v] + step;

                if (through < cost[w] || (through == cost[w] && !done[w] && came[w].Node > v))
                {
                    cost[w] = through;
                    came[w] = (v, edge);
                    queue.Enqueue(w, (through, w));
                }
            }
        }

        if (double.IsPositiveInfinity(cost[end]))
            return null;

        var nodes = new List<string>();
        var edges = new List<string>();

        for (var at = end; at != start; at = came[at].Node)
        {
            nodes.Add(graph.Nodes[at]);
            edges.Add(graph.Edges[came[at].Edge].Id);
        }

        nodes.Add(graph.Nodes[start]);
        nodes.Reverse();
        edges.Reverse();

        return new Path(nodes, edges, cost[end]);
    }
}
