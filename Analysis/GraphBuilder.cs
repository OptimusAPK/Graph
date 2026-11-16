using System.Diagnostics;
using CodeGraph.Models;

namespace CodeGraph.Analysis;

/// <summary>
/// Collects nodes and edges from the analyzers, then drops edges to anything outside the solution,
/// runs community detection and produces the final graph.
/// </summary>
public sealed class GraphBuilder(string rootDirectory)
{
    private const int MinCommunitySize = 5;

    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target, string Type), GraphEdge> _edges = new();
    private readonly List<NameEdge> _nameEdges = [];
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _warningSet = new(StringComparer.Ordinal);

    public int SemanticProjects { get; set; }
    public int SyntaxOnlyProjects { get; set; }
    public int SkippedTestProjects { get; set; }

    /// <summary>An edge whose target is only known by simple name (syntax-only projects).</summary>
    private sealed record NameEdge(string Source, string Name, NameEdgeKind Kind, string Project, string? ContainerTypeId);

    public enum NameEdgeKind { BaseType, TypeReference, Call }

    public static string ProjectId(string projectName) => "P:" + projectName;

    public void Warn(string message)
    {
        message = message.Trim();
        if (message.Length > 600) message = message[..600] + "…";
        if (_warningSet.Add(message)) _warnings.Add(message);
    }

    public string? RelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var rel = Path.GetRelativePath(rootDirectory, path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel.Replace('\\', '/');
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>Adds a node; returns false if a node with this id already exists (e.g. a partial class).</summary>
    public bool AddNode(string id, string name, string fullName, string kind, string project,
        string? filePath, int line, string? parentId = null)
    {
        if (_nodes.ContainsKey(id)) return false;
        _nodes[id] = new GraphNode
        {
            Id = id, Name = name, FullName = fullName, Kind = kind, Project = project,
            FilePath = RelativePath(filePath), Line = line, ParentId = parentId,
        };
        return true;
    }

    public void AddProjectNode(string projectName, string csprojPath) =>
        AddNode(ProjectId(projectName), projectName, projectName, NodeKinds.Project, projectName, csprojPath, 1);

    /// <summary>Adds an edge. The target may not exist (yet); unknown endpoints are dropped in Build().</summary>
    public void AddEdge(string source, string target, string type)
    {
        if (source == target) return;
        var key = (source, target, type);
        if (_edges.TryGetValue(key, out var existing)) existing.Weight++;
        else _edges[key] = new GraphEdge { Source = source, Target = target, Type = type };
    }

    public void AddNameEdge(string source, string name, NameEdgeKind kind, string project, string? containerTypeId) =>
        _nameEdges.Add(new NameEdge(source, name, kind, project, containerTypeId));

    public GraphResult Build(string source, Stopwatch stopwatch)
    {
        ResolveNameEdges();

        var nodes = _nodes.Values.ToList();
        var edges = _edges.Values
            .Where(e => _nodes.ContainsKey(e.Source) && _nodes.ContainsKey(e.Target))
            .ToList();

        // Degree = number of distinct relationships touching the node.
        foreach (var e in edges)
        {
            _nodes[e.Source].Degree++;
            _nodes[e.Target].Degree++;
        }

        var communities = DetectCommunities(nodes, edges);

        // Swap the long symbol ids for short ones to keep the JSON small.
        var shortIds = new Dictionary<string, string>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) shortIds[nodes[i].Id] = "n" + i;
        foreach (var e in edges)
        {
            e.Source = shortIds[e.Source];
            e.Target = shortIds[e.Target];
        }
        foreach (var n in nodes) n.Id = shortIds[n.Id];

        return new GraphResult
        {
            Source = source,
            Nodes = nodes,
            Edges = edges,
            Communities = communities,
            Warnings = _warnings,
            Stats = new GraphStats
            {
                Projects = nodes.Count(n => n.Kind == NodeKinds.Project),
                SemanticProjects = SemanticProjects,
                SyntaxOnlyProjects = SyntaxOnlyProjects,
                SkippedTestProjects = SkippedTestProjects,
                Nodes = nodes.Count,
                Edges = edges.Count,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            },
        };
    }

    // ---------------------------------------------------------------- name resolution (syntax-only)

    private void ResolveNameEdges()
    {
        if (_nameEdges.Count == 0) return;

        var types = _nodes.Values.Where(n => NodeKinds.IsType(n.Kind)).ToLookup(n => n.Name, StringComparer.Ordinal);
        var methods = _nodes.Values.Where(n => n.Kind == NodeKinds.Method).ToLookup(n => n.Name, StringComparer.Ordinal);

        foreach (var pending in _nameEdges)
        {
            if (!_nodes.TryGetValue(pending.Source, out var sourceNode)) continue;

            if (pending.Kind == NameEdgeKind.Call)
            {
                var candidates = methods[pending.Name].ToList();
                if (candidates.Count == 0) continue;
                var target = candidates.FirstOrDefault(m => m.ParentId == pending.ContainerTypeId)
                             ?? (candidates.Count == 1 ? candidates[0] : null);
                if (target is not null) AddEdge(pending.Source, target.Id, EdgeTypes.Calls);
                continue;
            }

            var typeCandidates = types[pending.Name].ToList();
            if (typeCandidates.Count == 0) continue;
            GraphNode? type = typeCandidates.Count == 1 ? typeCandidates[0] : null;
            if (type is null)
            {
                var sameProject = typeCandidates.Where(t => t.Project == pending.Project).ToList();
                if (sameProject.Count == 1) type = sameProject[0];
            }
            if (type is null) continue; // ambiguous without a semantic model

            var edgeType = pending.Kind switch
            {
                NameEdgeKind.BaseType when type.Kind == NodeKinds.Interface && sourceNode.Kind != NodeKinds.Interface
                    => EdgeTypes.Implements,
                NameEdgeKind.BaseType => EdgeTypes.Inherits,
                _ => EdgeTypes.References,
            };
            AddEdge(pending.Source, type.Id, edgeType);
        }
    }

    // ---------------------------------------------------------------- communities

    private static double LouvainWeight(GraphEdge edge, Dictionary<string, GraphNode> nodes)
    {
        var occurrences = 1 + Math.Log(edge.Weight); // repeated calls count a little more
        return edge.Type switch
        {
            EdgeTypes.Inherits or EdgeTypes.Implements => 2.0,
            EdgeTypes.Calls => 1.0 * occurrences,
            EdgeTypes.References => 1.0 * occurrences,
            EdgeTypes.ProjectReference => 0.2,
            // Keep methods with their type; let namespaces/projects pull only weakly.
            EdgeTypes.Contains => nodes[edge.Target].Kind switch
            {
                NodeKinds.Method => 2.0,
                NodeKinds.Namespace => 0.2,
                _ => 0.5,
            },
            _ => 1.0,
        };
    }

    private List<GraphCommunity> DetectCommunities(List<GraphNode> nodes, List<GraphEdge> edges)
    {
        if (nodes.Count == 0) return [];

        var index = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) index[nodes[i].Id] = i;

        var membership = Louvain.Detect(
            nodes.Count,
            edges.Select(e => (index[e.Source], index[e.Target], LouvainWeight(e, _nodes))));

        var groups = Enumerable.Range(0, nodes.Count)
            .GroupBy(i => membership[i])
            .Select(g => g.Select(i => nodes[i]).ToList())
            .ToList();

        var large = groups.Where(g => g.Count >= MinCommunitySize)
            .OrderByDescending(g => g.Count)
            .ToList();
        var other = groups.Where(g => g.Count < MinCommunitySize).SelectMany(g => g).ToList();

        // Each community is named after its most connected node.
        var tops = large
            .Select(g => g.OrderByDescending(n => n.Degree).ThenBy(n => n.Name, StringComparer.Ordinal).First())
            .ToList();
        var sharedNames = tops.GroupBy(n => n.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<GraphCommunity>();
        for (var id = 0; id < large.Count; id++)
        {
            foreach (var n in large[id]) n.Community = id;
            result.Add(new GraphCommunity
            {
                Id = id,
                Name = CommunityName(tops[id], sharedNames.Contains(tops[id].Name)),
                Size = large[id].Count,
            });
        }
        if (other.Count > 0)
        {
            var id = result.Count;
            foreach (var n in other) n.Community = id;
            result.Add(new GraphCommunity { Id = id, Name = "other", Size = other.Count });
        }
        return result;
    }

    /// <summary>
    /// A readable community name. A method always carries its type ("Louvain.Detect"), because a
    /// bare method name says little. Anything else is qualified with its parent
    /// ("SyntaxProjectAnalyzer.Walker") only when two communities would otherwise share a name.
    /// </summary>
    private static string CommunityName(GraphNode top, bool nameIsShared)
    {
        if (top.Kind != NodeKinds.Method && !nameIsShared) return top.Name;

        // Drop the parameter list, then keep the last two dot-separated parts,
        // ignoring dots inside generic arguments such as Repo<System.Int32>.
        var full = top.FullName;
        var paren = full.IndexOf('(');
        if (paren >= 0) full = full[..paren];

        int depth = 0, dots = 0;
        for (var i = full.Length - 1; i >= 0; i--)
        {
            var c = full[i];
            if (c == '>') depth++;
            else if (c == '<') depth--;
            else if (c == '.' && depth == 0 && ++dots == 2) return full[(i + 1)..];
        }
        return full.Length > 0 ? full : top.Name;
    }
}
