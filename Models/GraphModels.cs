using System.Text.Json.Serialization;

namespace CodeGraph.Models;

/// <summary>Body of POST /api/analyze.</summary>
public sealed record AnalyzeRequest(string? Path, bool ExcludeTests = true, bool Refresh = false);

public static class NodeKinds
{
    public const string Project = "project";
    public const string Namespace = "namespace";
    public const string Class = "class";
    public const string Interface = "interface";
    public const string Record = "record";
    public const string Struct = "struct";
    public const string Enum = "enum";
    public const string Method = "method";

    public static bool IsType(string kind) =>
        kind is Class or Interface or Record or Struct or Enum;
}

public static class EdgeTypes
{
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string Calls = "calls";
    public const string References = "references";
    public const string ProjectReference = "projectReference";
    /// <summary>Structural edge: project → namespace → type → method.</summary>
    public const string Contains = "contains";
}

public sealed class GraphNode
{
    public required string Id { get; set; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Kind { get; init; }
    public required string Project { get; init; }
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public int Community { get; set; }
    public int Degree { get; set; }

    /// <summary>Containing node (used only for name resolution in syntax-only mode).</summary>
    [JsonIgnore] public string? ParentId { get; init; }
}

public sealed class GraphEdge
{
    public required string Source { get; set; }
    public required string Target { get; set; }
    public required string Type { get; init; }
    /// <summary>How many times this relationship occurs in the code.</summary>
    public int Weight { get; set; } = 1;
}

public sealed class GraphCommunity
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public int Size { get; init; }
}

public sealed class GraphStats
{
    public int Projects { get; init; }
    public int SemanticProjects { get; init; }
    public int SyntaxOnlyProjects { get; init; }
    public int SkippedTestProjects { get; init; }
    public int Nodes { get; init; }
    public int Edges { get; init; }
    public long ElapsedMs { get; set; }
}

public sealed class GraphResult
{
    public required string Source { get; init; }
    public required List<GraphNode> Nodes { get; init; }
    public required List<GraphEdge> Edges { get; init; }
    public required List<GraphCommunity> Communities { get; init; }
    public required List<string> Warnings { get; init; }
    public required GraphStats Stats { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.Now;
}
